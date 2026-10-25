using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using PrimePC.Sensors;

namespace PrimePC.Testing;

public sealed record StorageBenchmarkOptions
{
    /// <summary>Raiz do volume a testar (ex.: "D:\").</summary>
    public string VolumeRoot { get; init; } = "C:\\";

    public long FileSizeBytes { get; init; } = 1L * 1024 * 1024 * 1024;
    public TimeSpan MaxPhaseDuration { get; init; } = TimeSpan.FromSeconds(12);
}

public sealed record StorageBenchmarkProgress(string Phase, double Fraction);

public sealed record StorageBenchmarkResult(
    DateTime At,
    string Volume,
    string? DiskModel,
    long FileSizeBytes,
    double SequentialWriteMBs,
    double SequentialReadMBs,
    double Random4kReadIops,
    double Random4kReadLatencyUs,
    double Random4kWriteIops,
    double Random4kWriteLatencyUs,
    double Random4kReadQ4Iops,
    bool UnbufferedIo,
    bool Cancelled,
    IReadOnlyList<string> Notes);

/// <summary>
/// Teste de velocidade NÃO destrutivo: cria um arquivo temporário próprio, mede leitura/gravação sequencial e
/// aleatória (4 KiB) sem o cache do Windows e apaga o arquivo ao final. Nunca lê ou altera arquivos do usuário.
/// </summary>
public static class StorageBenchmark
{
    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    private const int SeqBlock = 1024 * 1024;
    private const int RndBlock = 4096;

    /// <summary>Diretório onde o arquivo temporário é criado (o volume do sistema usa a pasta TEMP).</summary>
    public static string ResolveTargetDirectory(string volumeRoot)
    {
        string root = Path.GetPathRoot(Path.GetFullPath(volumeRoot)) ?? volumeRoot;
        string tempRoot = Path.GetPathRoot(Path.GetTempPath()) ?? "";
        if (string.Equals(root, tempRoot, StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetTempPath(), "PrimePC");
        return Path.Combine(root, "PrimePC-Teste-Temporario");
    }

    public static async Task<StorageBenchmarkResult> RunAsync(StorageBenchmarkOptions options, string? diskModel,
        IProgress<StorageBenchmarkProgress>? progress, CancellationToken token)
    {
        var drive = new DriveInfo(options.VolumeRoot);
        long size = options.FileSizeBytes / SeqBlock * SeqBlock;
        if (!drive.IsReady)
            throw new InvalidOperationException($"O volume {options.VolumeRoot} não está pronto.");
        if (drive.AvailableFreeSpace < size + 2L * 1024 * 1024 * 1024)
            throw new InvalidOperationException(
                $"Espaço livre insuficiente em {drive.Name}: são necessários {SensorUnits.FormatBytes(size)} para o arquivo de teste mais 2 GB de margem.");

        string dir = ResolveTargetDirectory(options.VolumeRoot);
        bool createdDir = !Directory.Exists(dir);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"primepc-teste-{Guid.NewGuid():N}.tmp");
        var notes = new List<string>
        {
            $"Arquivo temporário de {SensorUnits.FormatBytes(size)} criado em {dir} e apagado ao final. Nenhum arquivo do usuário foi lido ou alterado.",
        };

        return await Task.Run(() =>
        {
            SafeFileHandle handle;
            bool unbuffered = true;
            try
            {
                handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    FileOptions.DeleteOnClose | FileOptions.WriteThrough | NoBuffering, size);
            }
            catch (Exception)
            {
                unbuffered = false;
                handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                    FileOptions.DeleteOnClose | FileOptions.WriteThrough, size);
                notes.Add("O volume não aceitou E/S sem cache; o cache do Windows pode ter influenciado os resultados de leitura.");
            }

            double seqW = 0, seqR = 0, rr = 0, rrLat = 0, rw = 0, rwLat = 0, rrQ4 = 0;
            bool cancelled = false;
            long written = 0;
            try
            {
                using (var seqBuf = AlignedBuffer.Create(SeqBlock))
                {
                    Random.Shared.NextBytes(seqBuf.Span);

                    progress?.Report(new StorageBenchmarkProgress("Gravação sequencial", 0));
                    var sw = Stopwatch.StartNew();
                    while (written < size && sw.Elapsed < options.MaxPhaseDuration)
                    {
                        token.ThrowIfCancellationRequested();
                        RandomAccess.Write(handle, seqBuf.Span, written);
                        written += SeqBlock;
                        if (written % (64L * SeqBlock) == 0)
                            progress?.Report(new StorageBenchmarkProgress("Gravação sequencial", 0.25 * written / size));
                    }

                    seqW = written / sw.Elapsed.TotalSeconds / 1e6;

                    progress?.Report(new StorageBenchmarkProgress("Leitura sequencial", 0.25));
                    sw.Restart();
                    long read = 0;
                    while (read < written && sw.Elapsed < options.MaxPhaseDuration)
                    {
                        token.ThrowIfCancellationRequested();
                        read += RandomAccess.Read(handle, seqBuf.Span, read);
                    }

                    seqR = read / sw.Elapsed.TotalSeconds / 1e6;
                }

                long blocks = written / RndBlock;
                var phase = TimeSpan.FromSeconds(Math.Min(6, options.MaxPhaseDuration.TotalSeconds));

                progress?.Report(new StorageBenchmarkProgress("Leitura aleatória 4K (QD1)", 0.5));
                (rr, rrLat) = RandomPhase(handle, blocks, write: false, phase, token);

                progress?.Report(new StorageBenchmarkProgress("Gravação aleatória 4K (QD1)", 0.65));
                (rw, rwLat) = RandomPhase(handle, blocks, write: true, phase, token);

                progress?.Report(new StorageBenchmarkProgress("Leitura aleatória 4K (4 threads)", 0.8));
                var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() => RandomPhase(handle, blocks, false, phase, token).Iops)).ToArray();
                Task.WaitAll(tasks, token);
                rrQ4 = tasks.Sum(t => t.Result);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            finally
            {
                // Fechar o handle apaga o arquivo (DeleteOnClose); a exclusão abaixo é só uma garantia extra.
                handle.Dispose();
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    if (createdDir && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch
                {
                    notes.Add($"Não foi possível remover {path}; apague-o manualmente.");
                }
            }

            if (written < size)
                notes.Add($"A fase sequencial foi limitada a {options.MaxPhaseDuration.TotalSeconds:0} s; foram gravados {SensorUnits.FormatBytes(written)}.");
            notes.Add("Resultados variam com o espaço livre, a temperatura, o cache SLC do SSD e a atividade em segundo plano. Compare execuções nas mesmas condições.");

            progress?.Report(new StorageBenchmarkProgress("Concluído", 1));
            return new StorageBenchmarkResult(DateTime.Now, drive.Name, diskModel, size, seqW, seqR, rr, rrLat, rw, rwLat, rrQ4,
                unbuffered, cancelled, notes);
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private static (double Iops, double LatencyUs) RandomPhase(SafeFileHandle handle, long blocks, bool write, TimeSpan duration,
        CancellationToken token)
    {
        if (blocks <= 0)
            return (0, 0);
        using var buf = AlignedBuffer.Create(RndBlock);
        Random.Shared.NextBytes(buf.Span);
        var rng = new Random();
        var sw = Stopwatch.StartNew();
        long ops = 0;
        while (sw.Elapsed < duration)
        {
            token.ThrowIfCancellationRequested();
            long offset = rng.NextInt64(blocks) * RndBlock;
            if (write)
                RandomAccess.Write(handle, buf.Span, offset);
            else
                RandomAccess.Read(handle, buf.Span, offset);
            ops++;
        }

        double secs = sw.Elapsed.TotalSeconds;
        return (ops / secs, secs / ops * 1e6);
    }

    /// <summary>Buffer alinhado a 4 KiB, exigido pela E/S sem cache.</summary>
    private sealed unsafe class AlignedBuffer : IDisposable
    {
        private void* _ptr;
        private readonly int _length;

        private AlignedBuffer(int length)
        {
            _length = length;
            _ptr = NativeMemory.AlignedAlloc((nuint)length, 4096);
        }

        public static AlignedBuffer Create(int length) => new(length);

        public Span<byte> Span => new(_ptr, _length);

        public void Dispose()
        {
            if (_ptr != null)
            {
                NativeMemory.AlignedFree(_ptr);
                _ptr = null;
            }
        }
    }
}

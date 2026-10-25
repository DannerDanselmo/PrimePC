using System.Diagnostics;
using ComputeSharp;
using PrimePC.Sensors;

namespace PrimePC.GpuTesting;

/// <summary>
/// Teste da memória de vídeo (VRAM): aloca blocos de 256 MB na GPU, grava padrões variados em todos e depois verifica
/// tudo na própria GPU (sem trafegar os dados pelo barramento). Divergências são registradas com endereço, valor
/// esperado e valor lido. Independente do teste de RAM do sistema.
/// </summary>
public sealed class VramTester
{
    public const long ChunkBytes = 256L * 1024 * 1024;
    private const int ChunkElements = (int)(ChunkBytes / sizeof(uint));
    private const int Slice = 61440 * 64;
    private const int MaxLoggedErrors = 64;

    /// <summary>Padrões: (nome, modo do shader, valor/semente).</summary>
    public static IReadOnlyList<(string Name, uint Mode, uint Value)> Patterns { get; } = new[]
    {
        ("Zeros", 0u, 0x00000000u),
        ("Uns", 0u, 0xFFFFFFFFu),
        ("Xadrez (0x55)", 0u, 0x55555555u),
        ("Xadrez invertido (0xAA)", 0u, 0xAAAAAAAAu),
        ("Endereço próprio", 1u, 0u),
        ("Endereço invertido", 2u, 0u),
        ("Aleatório A", 3u, 0x9E3779B9u),
        ("Aleatório B", 3u, 0x85EBCA6Bu),
    };

    public Task<VramTestResult> RunAsync(VramTestOptions options, IProgress<VramTestProgress>? progress, CancellationToken token) =>
        Task.Run(() => Run(options, progress, token), CancellationToken.None);

    private VramTestResult Run(VramTestOptions o, IProgress<VramTestProgress>? progress, CancellationToken token)
    {
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        string deviceName = "GPU";
        long deviceMemory = 0;
        var errors = new List<VramTestError>();
        long errorCount = 0;
        int passesCompleted = 0, patternsCompleted = 0;
        long tested = 0;
        var notes = new List<string>();
        var chunks = new List<ReadWriteBuffer<uint>>();

        VramTestResult Finish(GpuTestOutcome outcome, string summary)
        {
            notes.Insert(0, $"Foram testados {SensorUnits.FormatBytes(tested)} de {SensorUnits.FormatBytes(deviceMemory)} de VRAM. " +
                            "A parte em uso pelo Windows, pela tela e por outros programas não é testada.");
            notes.Add("O driver de vídeo decide onde cada bloco fica. Se faltar VRAM livre, o Windows pode mover dados para a memória " +
                      "do sistema e o teste passaria a verificar essa memória — por isso a quantidade padrão deixa uma reserva.");
            notes.Add("Os endereços exibidos são relativos à área de teste, não endereços físicos da VRAM.");
            notes.Add("Um teste sem erros reduz a suspeita de defeito, mas não garante que a memória de vídeo esteja livre de falhas.");
            if (errorCount > 0)
                notes.Insert(1, "Erros de VRAM costumam indicar overclock de memória instável, superaquecimento dos chips de memória ou defeito na placa.");
            return new VramTestResult(outcome, deviceName, started, sw.Elapsed, tested, deviceMemory, passesCompleted, patternsCompleted,
                errorCount, errors.ToList(), summary, notes);
        }

        GraphicsDevice device;
        try
        {
            device = GpuDevices.Get(o.DeviceIndex);
            deviceName = device.Name;
            deviceMemory = (long)device.DedicatedMemorySize;
        }
        catch (Exception ex)
        {
            return Finish(GpuTestOutcome.Failed, $"Não foi possível iniciar o teste: {ex.Message}");
        }

        long requested = Math.Max(ChunkBytes, o.BytesToTest / ChunkBytes * ChunkBytes);
        if (requested > deviceMemory)
            return Finish(GpuTestOutcome.Failed, $"A quantidade escolhida excede a memória dedicada da placa ({SensorUnits.FormatBytes(deviceMemory)}).");

        try
        {
            int wanted = (int)(requested / ChunkBytes);
            for (int i = 0; i < wanted; i++)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    chunks.Add(device.AllocateReadWriteBuffer<uint>(ChunkElements));
                }
                catch (Exception ex) when (!GpuDevices.IsDeviceLost(ex))
                {
                    notes.Add($"O driver só permitiu alocar {chunks.Count} de {wanted} blocos de 256 MB ({ex.Message}).");
                    break;
                }

                progress?.Report(new VramTestProgress(0, 1, o.Passes, "Alocando memória de vídeo", "Preparação", 0, sw.Elapsed));
            }

            if (chunks.Count == 0)
                return Finish(GpuTestOutcome.Failed, "Não foi possível alocar memória de vídeo para o teste.");
            tested = chunks.Count * ChunkBytes;

            using var errorCounter = device.AllocateReadWriteBuffer<int>(new int[1]);
            using var errorLog = device.AllocateReadWriteBuffer<uint>(new uint[MaxLoggedErrors * 3]);
            var countBack = new int[1];
            var logBack = new uint[MaxLoggedErrors * 3];
            int passes = Math.Max(1, o.Passes);
            double totalSteps = (double)passes * Patterns.Count * chunks.Count * 2;
            double step = 0;

            for (int pass = 1; pass <= passes; pass++)
            {
                foreach (var (name, mode, value) in Patterns)
                {
                    for (int c = 0; c < chunks.Count; c++)
                    {
                        uint baseIndex = unchecked((uint)((long)c * ChunkElements));
                        for (int off = 0; off < ChunkElements; off += Slice)
                        {
                            token.ThrowIfCancellationRequested();
                            device.For(Math.Min(Slice, ChunkElements - off), new VramWriteShader(chunks[c], off, mode, value, baseIndex));
                        }

                        progress?.Report(new VramTestProgress(++step / totalSteps, pass, passes, name, "Gravando", errorCount, sw.Elapsed));
                    }

                    for (int c = 0; c < chunks.Count; c++)
                    {
                        uint baseIndex = unchecked((uint)((long)c * ChunkElements));
                        for (int off = 0; off < ChunkElements; off += Slice)
                        {
                            token.ThrowIfCancellationRequested();
                            device.For(Math.Min(Slice, ChunkElements - off),
                                new VramVerifyShader(chunks[c], errorCounter, errorLog, off, mode, value, baseIndex));
                        }

                        progress?.Report(new VramTestProgress(++step / totalSteps, pass, passes, name, "Verificando", errorCount, sw.Elapsed));
                    }

                    errorCounter.CopyTo(countBack);
                    long newCount = countBack[0];
                    if (newCount > errorCount)
                    {
                        errorLog.CopyTo(logBack);
                        for (long slot = errorCount; slot < Math.Min(newCount, MaxLoggedErrors); slot++)
                        {
                            long s = slot * 3;
                            errors.Add(new VramTestError(pass, name, (long)logBack[s] * sizeof(uint), logBack[s + 1], logBack[s + 2]));
                        }

                        errorCount = newCount;
                    }

                    patternsCompleted++;
                }

                passesCompleted = pass;
            }
        }
        catch (OperationCanceledException)
        {
            return Finish(GpuTestOutcome.Cancelled, errorCount > 0
                ? $"Teste cancelado. Erros detectados antes do cancelamento: {errorCount:N0}."
                : "Teste cancelado. Nenhum erro detectado até o cancelamento.");
        }
        catch (Exception ex) when (GpuDevices.IsDeviceLost(ex))
        {
            return Finish(GpuTestOutcome.DeviceLost, GpuDevices.DeviceLostMessage);
        }
        catch (Exception ex)
        {
            return Finish(GpuTestOutcome.Failed, $"O teste foi interrompido por um erro: {ex.Message}");
        }
        finally
        {
            foreach (var c in chunks)
                c.Dispose();
        }

        return errorCount > 0
            ? Finish(GpuTestOutcome.ErrorsDetected, $"Erros detectados: {errorCount:N0} divergência(s) entre o valor gravado e o valor lido da VRAM.")
            : Finish(GpuTestOutcome.Completed, "Nenhum erro detectado durante este teste.");
    }
}

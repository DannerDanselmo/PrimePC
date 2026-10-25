using System.Diagnostics;
using PrimePC.Sensors;

namespace PrimePC.Testing;

/// <summary>
/// Teste de estabilidade da memória RAM executado dentro do Windows. Aloca uma região limitada (nunca toca em
/// arquivos ou na memória de outros programas), grava padrões variados, lê de volta e registra qualquer divergência.
/// Inclui proteções: margem de memória livre para o sistema, monitoramento contínuo e cancelamento seguro.
/// </summary>
public sealed class MemoryTester
{
    private const long BlockBytes = 16L * 1024 * 1024;
    private const long RegionBytes = 256L * 1024 * 1024;
    private const int MaxStoredErrors = 500;

    private readonly object _errorSync = new();
    private readonly List<MemoryTestError> _errors = new();
    private long _errorCount;
    private long _bytesProcessed;

    /// <summary>Conjunto de padrões usado em cada modo.</summary>
    public static IReadOnlyList<MemoryPattern> PatternsFor(MemoryTestMode mode)
    {
        var quick = new List<MemoryPattern>
        {
            MemoryPattern.Fill("Zeros", 0UL),
            MemoryPattern.Fill("Uns", ulong.MaxValue),
            MemoryPattern.Fill("Xadrez (0x55)", 0x5555_5555_5555_5555UL),
            MemoryPattern.Fill("Xadrez invertido (0xAA)", 0xAAAA_AAAA_AAAA_AAAAUL),
            MemoryPattern.OwnAddress(),
            MemoryPattern.Random("Aleatório A", 0x5D58_8B65_6C07_8965UL, inverted: false),
        };
        if (mode == MemoryTestMode.Quick)
            return quick;

        var full = new List<MemoryPattern>(quick)
        {
            MemoryPattern.Random("Aleatório A invertido", 0x5D58_8B65_6C07_8965UL, inverted: true),
        };
        for (int bit = 0; bit < 8; bit++)
            full.Add(MemoryPattern.Fill($"Bit móvel 1 (posição {bit})", 0x0101_0101_0101_0101UL << bit));
        for (int bit = 0; bit < 8; bit++)
            full.Add(MemoryPattern.Fill($"Bit móvel 0 (posição {bit})", ~(0x0101_0101_0101_0101UL << bit)));
        full.Add(MemoryPattern.Random("Aleatório B", 0x2545_F491_4F6C_DD1DUL, inverted: false));
        full.Add(MemoryPattern.Random("Aleatório B invertido", 0x2545_F491_4F6C_DD1DUL, inverted: true));
        return full;
    }

    public async Task<MemoryTestResult> RunAsync(MemoryTestOptions options, IProgress<MemoryTestProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        var sw = Stopwatch.StartNew();
        var (total, _, safeMax) = MemoryBudget.Current();
        var patterns = PatternsFor(options.Mode);
        int threads = Math.Clamp(options.Threads, 1, Environment.ProcessorCount);
        long bytes = options.BytesToTest / BlockBytes * BlockBytes;

        MemoryTestResult Fail(string reason) => new(MemoryTestOutcome.Failed, options.Mode, started, sw.Elapsed, 0, total, threads,
            0, 0, Array.Empty<string>(), 0, Array.Empty<MemoryTestError>(), false, $"Não foi possível executar o teste: {reason}",
            Array.Empty<string>());

        if (bytes < MemoryBudget.MinimumTestBytes)
            return Fail($"a quantidade mínima é {SensorUnits.FormatBytes(MemoryBudget.MinimumTestBytes)}.");
        if (bytes > safeMax)
            return Fail($"a quantidade escolhida ({SensorUnits.FormatBytes(bytes)}) excede o limite seguro atual " +
                        $"({SensorUnits.FormatBytes(safeMax)}). Feche programas ou escolha menos memória.");

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? abortReason = null;
        bool durationReached = false;
        int passesCompleted = 0, patternsCompleted = 0;
        int currentPass = 1;
        string currentPattern = "Alocando memória";
        string phase = "Preparação";
        var runPatterns = new List<string>();

        using var region = MemoryRegion.TryAllocate(bytes, out string? allocError);
        if (region is null)
            return Fail(allocError ?? "falha ao alocar memória.");

        var blocks = region.Blocks(BlockBytes);
        int? totalPasses = options.Mode switch
        {
            MemoryTestMode.Quick => 1,
            MemoryTestMode.Full => Math.Max(1, options.Passes),
            _ => null,
        };
        double plannedBytes = totalPasses is null ? 0 : (double)bytes * patterns.Count * 2 * totalPasses.Value;

        var worker = Task.Run(() =>
        {
            var po = new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = stopCts.Token };
            for (int pass = 1; ; pass++)
            {
                currentPass = pass;
                foreach (var pattern in patterns)
                {
                    currentPattern = pattern.Name;
                    phase = "Gravando";
                    Parallel.ForEach(blocks, po, b =>
                    {
                        pattern.Write(b);
                        Interlocked.Add(ref _bytesProcessed, b.Length);
                    });
                    phase = "Verificando";
                    int p = pass;
                    Parallel.ForEach(blocks, po, b =>
                    {
                        pattern.Verify(b, (offset, address, expected, actual) => RecordError(p, pattern.Name, offset, address, expected, actual));
                        Interlocked.Add(ref _bytesProcessed, b.Length);
                    });
                    patternsCompleted++;
                    if (!runPatterns.Contains(pattern.Name))
                        runPatterns.Add(pattern.Name);
                }

                passesCompleted = pass;
                if (totalPasses is not null && pass >= totalPasses)
                    return;
            }
        }, stopCts.Token);

        // Monitor: progresso, duração e proteção contra falta de memória no sistema.
        while (!worker.IsCompleted)
        {
            await Task.WhenAny(worker, Task.Delay(250, CancellationToken.None)).ConfigureAwait(false);
            if (options.Mode == MemoryTestMode.Timed && sw.Elapsed >= options.Duration && !durationReached)
            {
                durationReached = true;
                stopCts.Cancel();
            }

            var (_, available) = Sensors.Sources.WindowsSystemSource.GetPhysicalMemory();
            if (available > 0 && (long)available < options.MinimumSystemFreeBytes && abortReason is null)
            {
                abortReason = $"a memória livre do sistema caiu para {SensorUnits.FormatBytes((long)available)}. " +
                              "O teste foi encerrado para evitar travamentos.";
                stopCts.Cancel();
            }

            long processed = Interlocked.Read(ref _bytesProcessed);
            double fraction = options.Mode == MemoryTestMode.Timed
                ? Math.Min(1, sw.Elapsed.TotalSeconds / options.Duration.TotalSeconds)
                : Math.Min(1, processed / plannedBytes);
            progress?.Report(new MemoryTestProgress(fraction, currentPass, totalPasses, currentPattern, phase, processed, sw.Elapsed,
                Interlocked.Read(ref _errorCount), processed / Math.Max(0.001, sw.Elapsed.TotalSeconds) / 1e9));
        }

        bool cancelled = false;
        try
        {
            await worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }

        sw.Stop();
        long errorCount = Interlocked.Read(ref _errorCount);
        MemoryTestOutcome outcome;
        string summary;
        if (abortReason is not null)
        {
            outcome = MemoryTestOutcome.Aborted;
            summary = $"Teste interrompido para proteger o sistema: {abortReason}" +
                      (errorCount > 0 ? $" Antes disso, {errorCount} erro(s) foram detectados." : " Nenhum erro detectado até a interrupção.");
        }
        else if (cancelled && !durationReached)
        {
            outcome = MemoryTestOutcome.Cancelled;
            summary = errorCount > 0
                ? $"Teste cancelado. Erros detectados antes do cancelamento: {errorCount}."
                : "Teste cancelado. Nenhum erro detectado até o cancelamento.";
        }
        else if (errorCount > 0)
        {
            outcome = MemoryTestOutcome.ErrorsDetected;
            summary = $"Erros detectados: {errorCount} divergência(s) entre o valor gravado e o valor lido.";
        }
        else
        {
            outcome = MemoryTestOutcome.NoErrors;
            summary = "Nenhum erro detectado durante este teste.";
        }

        var notes = new List<string>
        {
            $"Foram testados {SensorUnits.FormatBytes(bytes)} de {SensorUnits.FormatBytes(total)} de memória física utilizável. " +
            "O Windows e os programas abertos usam o restante, que não pode ser testado de dentro do sistema operacional.",
            "Um teste sem erros reduz a suspeita de defeito, mas não garante que a memória esteja livre de falhas.",
            "Os endereços exibidos são virtuais (do processo de teste); o endereço físico não é acessível sem privilégios de kernel.",
            "Para um teste mais profundo, use um teste inicializável, fora do Windows (Diagnóstico de Memória do Windows ou MemTest86).",
        };
        if (errorCount > 0)
        {
            notes.Insert(1, "Erros podem ser causados pelos módulos, pelo controlador de memória da CPU ou por configurações instáveis " +
                            "(perfis XMP/EXPO, overclock, tensões). Repita o teste com as configurações padrão e um módulo por vez para isolar a causa.");
        }

        if (!region.Locked)
        {
            notes.Add("Não foi possível fixar toda a região testada na RAM física; o Windows pode ter movido parte dela para o arquivo de " +
                      "paginação durante o teste.");
        }

        List<MemoryTestError> errors;
        lock (_errorSync)
            errors = _errors.ToList();

        return new MemoryTestResult(outcome, options.Mode, started, sw.Elapsed, bytes, total, threads, passesCompleted, patternsCompleted,
            runPatterns, errorCount, errors, region.Locked, summary, notes);
    }

    private void RecordError(int pass, string pattern, long offset, ulong address, ulong expected, ulong actual)
    {
        long n = Interlocked.Increment(ref _errorCount);
        if (n > MaxStoredErrors)
            return;
        lock (_errorSync)
            _errors.Add(new MemoryTestError(DateTime.Now, pass, pattern, offset, address, expected, actual));
    }
}

/// <summary>Bloco contíguo da região de teste.</summary>
public readonly record struct MemoryBlock(nint Pointer, long Length, long Offset, int Index);

/// <summary>Região de memória alocada e (se possível) fixada na RAM física para o teste.</summary>
internal sealed class MemoryRegion : IDisposable
{
    private readonly List<(nint Ptr, long Len)> _chunks = new();
    private readonly bool _workingSetChanged;
    private readonly nuint _originalMin, _originalMax;

    private MemoryRegion(bool workingSetChanged, nuint originalMin, nuint originalMax)
    {
        _workingSetChanged = workingSetChanged;
        _originalMin = originalMin;
        _originalMax = originalMax;
    }

    public bool Locked { get; private set; }

    public static MemoryRegion? TryAllocate(long bytes, out string? error)
    {
        error = null;
        nint process = Win32Memory.GetCurrentProcess();
        bool wsChanged = false;
        nuint min = 0, max = 0;
        if (Win32Memory.GetProcessWorkingSetSize(process, out min, out max))
        {
            // Aumenta o working set mínimo para permitir VirtualLock da região de teste.
            wsChanged = Win32Memory.SetProcessWorkingSetSize(process, min + (nuint)bytes + 64 * 1024 * 1024,
                max + (nuint)bytes + 128 * 1024 * 1024);
        }

        var region = new MemoryRegion(wsChanged, min, max);
        bool allLocked = wsChanged;
        try
        {
            long remaining = bytes;
            while (remaining > 0)
            {
                long len = Math.Min(256L * 1024 * 1024, remaining);
                nint ptr = Win32Memory.VirtualAlloc(0, (nuint)len, Win32Memory.MEM_COMMIT | Win32Memory.MEM_RESERVE, Win32Memory.PAGE_READWRITE);
                if (ptr == 0)
                {
                    error = $"o Windows recusou a alocação de memória (código {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}).";
                    region.Dispose();
                    return null;
                }

                region._chunks.Add((ptr, len));
                if (allLocked && !Win32Memory.VirtualLock(ptr, (nuint)len))
                    allLocked = false;
                remaining -= len;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            region.Dispose();
            return null;
        }

        region.Locked = allLocked;
        return region;
    }

    public IReadOnlyList<MemoryBlock> Blocks(long blockBytes)
    {
        var list = new List<MemoryBlock>();
        long offset = 0;
        foreach (var (ptr, len) in _chunks)
        {
            for (long o = 0; o < len; o += blockBytes)
            {
                long l = Math.Min(blockBytes, len - o);
                list.Add(new MemoryBlock(ptr + (nint)o, l, offset + o, list.Count));
            }

            offset += len;
        }

        return list;
    }

    public void Dispose()
    {
        foreach (var (ptr, len) in _chunks)
        {
            Win32Memory.VirtualUnlock(ptr, (nuint)len);
            Win32Memory.VirtualFree(ptr, 0, Win32Memory.MEM_RELEASE);
        }

        _chunks.Clear();
        if (_workingSetChanged)
            Win32Memory.SetProcessWorkingSetSize(Win32Memory.GetCurrentProcess(), _originalMin, _originalMax);
    }
}

public delegate void MemoryErrorCallback(long offset, ulong address, ulong expected, ulong actual);

/// <summary>Padrão de teste: grava um bloco e depois verifica, relatando cada divergência.</summary>
public abstract class MemoryPattern
{
    protected MemoryPattern(string name) => Name = name;

    public string Name { get; }

    public abstract void Write(MemoryBlock block);

    public abstract void Verify(MemoryBlock block, MemoryErrorCallback onError);

    public static MemoryPattern Fill(string name, ulong value) => new FillPattern(name, value);

    public static MemoryPattern OwnAddress() => new AddressPattern();

    public static MemoryPattern Random(string name, ulong seed, bool inverted) => new RandomPattern(name, seed, inverted);

    protected static unsafe Span<ulong> AsSpan(MemoryBlock b) => new((void*)b.Pointer, checked((int)(b.Length / sizeof(ulong))));

    private sealed class FillPattern(string name, ulong value) : MemoryPattern(name)
    {
        public override void Write(MemoryBlock block) => AsSpan(block).Fill(value);

        public override void Verify(MemoryBlock block, MemoryErrorCallback onError)
        {
            var span = AsSpan(block);
            int start = 0;
            while (start < span.Length)
            {
                int idx = span[start..].IndexOfAnyExcept(value);
                if (idx < 0)
                    break;
                int i = start + idx;
                onError(block.Offset + (long)i * 8, (ulong)(block.Pointer + (nint)i * 8), value, span[i]);
                start = i + 1;
            }
        }
    }

    /// <summary>Cada palavra recebe o próprio endereço: detecta falhas nas linhas de endereçamento.</summary>
    private sealed class AddressPattern() : MemoryPattern("Endereço próprio")
    {
        public override void Write(MemoryBlock block)
        {
            var span = AsSpan(block);
            ulong baseAddr = (ulong)block.Pointer;
            for (int i = 0; i < span.Length; i++)
                span[i] = baseAddr + (ulong)i * 8;
        }

        public override void Verify(MemoryBlock block, MemoryErrorCallback onError)
        {
            var span = AsSpan(block);
            ulong baseAddr = (ulong)block.Pointer;
            for (int i = 0; i < span.Length; i++)
            {
                ulong expected = baseAddr + (ulong)i * 8;
                if (span[i] != expected)
                    onError(block.Offset + (long)i * 8, expected, expected, span[i]);
            }
        }
    }

    /// <summary>Sequência pseudoaleatória determinística (xorshift64*), opcionalmente invertida.</summary>
    private sealed class RandomPattern(string name, ulong seed, bool inverted) : MemoryPattern(name)
    {
        private ulong Seed(MemoryBlock b) => (seed ^ ((ulong)b.Offset * 0x9E37_79B9_7F4A_7C15UL)) | 1UL;

        private static ulong Next(ref ulong x)
        {
            x ^= x >> 12;
            x ^= x << 25;
            x ^= x >> 27;
            return x * 0x2545_F491_4F6C_DD1DUL;
        }

        public override void Write(MemoryBlock block)
        {
            var span = AsSpan(block);
            ulong state = Seed(block);
            ulong mask = inverted ? ulong.MaxValue : 0;
            for (int i = 0; i < span.Length; i++)
                span[i] = Next(ref state) ^ mask;
        }

        public override void Verify(MemoryBlock block, MemoryErrorCallback onError)
        {
            var span = AsSpan(block);
            ulong state = Seed(block);
            ulong mask = inverted ? ulong.MaxValue : 0;
            for (int i = 0; i < span.Length; i++)
            {
                ulong expected = Next(ref state) ^ mask;
                if (span[i] != expected)
                    onError(block.Offset + (long)i * 8, (ulong)(block.Pointer + (nint)i * 8), expected, span[i]);
            }
        }
    }
}

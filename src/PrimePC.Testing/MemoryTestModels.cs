using System.Numerics;
using PrimePC.Sensors.Sources;

namespace PrimePC.Testing;

public enum MemoryTestMode
{
    /// <summary>Uma passagem com os padrões essenciais.</summary>
    Quick,

    /// <summary>Várias passagens com todos os padrões (inclui bits móveis e inversões aleatórias).</summary>
    Full,

    /// <summary>Repete passagens completas até atingir a duração escolhida.</summary>
    Timed,
}

public sealed record MemoryTestOptions
{
    public long BytesToTest { get; init; }
    public MemoryTestMode Mode { get; init; } = MemoryTestMode.Quick;
    public int Passes { get; init; } = 2;
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(15);
    public int Threads { get; init; } = Environment.ProcessorCount;

    /// <summary>O teste é interrompido se a memória livre do sistema cair abaixo deste valor.</summary>
    public long MinimumSystemFreeBytes { get; init; } = 512L * 1024 * 1024;
}

public sealed record MemoryTestError(
    DateTime Time,
    int Pass,
    string Pattern,
    long Offset,
    ulong VirtualAddress,
    ulong Expected,
    ulong Actual)
{
    public ulong DifferingBits => Expected ^ Actual;
    public int DifferingBitCount => BitOperations.PopCount(DifferingBits);
}

public sealed record MemoryTestProgress(
    double Fraction,
    int Pass,
    int? TotalPasses,
    string Pattern,
    string Phase,
    long BytesProcessed,
    TimeSpan Elapsed,
    long ErrorCount,
    double ThroughputGBs);

public enum MemoryTestOutcome
{
    NoErrors,
    ErrorsDetected,
    Cancelled,
    Aborted,
    Failed,
}

public sealed record MemoryTestResult(
    MemoryTestOutcome Outcome,
    MemoryTestMode Mode,
    DateTime StartedAt,
    TimeSpan Elapsed,
    long BytesTested,
    long TotalPhysicalBytes,
    int Threads,
    int PassesCompleted,
    int PatternsCompleted,
    IReadOnlyList<string> PatternsRun,
    long ErrorCount,
    IReadOnlyList<MemoryTestError> Errors,
    bool MemoryLocked,
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>Calcula quanta memória pode ser testada com segurança agora.</summary>
public static class MemoryBudget
{
    /// <summary>Memória sempre deixada livre para o Windows e os programas abertos.</summary>
    public const long SystemReserve = 2L * 1024 * 1024 * 1024;

    public const long MinimumTestBytes = 256L * 1024 * 1024;
    public const long Granularity = 64L * 1024 * 1024;

    public static (long Total, long Available, long SafeMaximum) Current()
    {
        var (total, available) = WindowsSystemSource.GetPhysicalMemory();
        double safe = Math.Min((double)available - SystemReserve, available * 0.85);
        long safeBytes = Math.Max(0, (long)safe / Granularity * Granularity);
        return ((long)total, (long)available, safeBytes);
    }
}

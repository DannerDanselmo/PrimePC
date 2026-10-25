namespace PrimePC.GpuTesting;

/// <summary>Placa de vídeo disponível para computação (DirectX 12).</summary>
public sealed record GpuDeviceInfo(int Index, string Name, long DedicatedMemoryBytes, long SharedMemoryBytes, int ComputeUnits);

public enum GpuTestOutcome
{
    Completed,
    ErrorsDetected,
    Cancelled,
    StoppedByTemperature,
    DeviceLost,
    Failed,
}

public sealed record GpuStressOptions
{
    public int DeviceIndex { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Temperatura (°C) da GPU que encerra a carga imediatamente. Nulo = sem corte.</summary>
    public float? TemperatureLimit { get; init; } = 88;

    /// <summary>Permite rodar sem leitura de temperatura (o usuário precisa confirmar).</summary>
    public bool AllowWithoutTemperature { get; init; }
}

public sealed record GpuStressProgress(TimeSpan Elapsed, double Fraction, long Dispatches, double Gflops, long Errors, float? Temperature);

public sealed record GpuStressResult(
    GpuTestOutcome Outcome,
    string DeviceName,
    DateTime StartedAt,
    TimeSpan Elapsed,
    TimeSpan PlannedDuration,
    long Dispatches,
    double AverageGflops,
    long Errors,
    float? MaxTemperature,
    string Summary,
    IReadOnlyList<string> Notes);

public sealed record VramTestOptions
{
    public int DeviceIndex { get; init; }
    public long BytesToTest { get; init; } = 2L * 1024 * 1024 * 1024;
    public int Passes { get; init; } = 1;
}

public sealed record VramTestProgress(double Fraction, int Pass, int TotalPasses, string Pattern, string Phase, long ErrorCount, TimeSpan Elapsed);

public sealed record VramTestError(int Pass, string Pattern, long Offset, uint Expected, uint Actual);

public sealed record VramTestResult(
    GpuTestOutcome Outcome,
    string DeviceName,
    DateTime StartedAt,
    TimeSpan Elapsed,
    long BytesTested,
    long DeviceMemoryBytes,
    int PassesCompleted,
    int PatternsCompleted,
    long ErrorCount,
    IReadOnlyList<VramTestError> Errors,
    string Summary,
    IReadOnlyList<string> Notes);

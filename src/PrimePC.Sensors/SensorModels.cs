namespace PrimePC.Sensors;

/// <summary>Categoria do componente ao qual o sensor pertence.</summary>
public enum HardwareCategory
{
    Cpu,
    Gpu,
    Motherboard,
    Memory,
    Storage,
    Network,
    Battery,
    Cooler,
    PowerSupply,
    EmbeddedController,
    System,
    Other,
}

/// <summary>Tipo de grandeza medida. Os nomes coincidem com os tipos do LibreHardwareMonitor quando existem.</summary>
public enum SensorKind
{
    Temperature,
    Load,
    Clock,
    Voltage,
    Current,
    Power,
    Fan,
    Flow,
    Control,
    Level,
    Data,
    SmallData,
    Throughput,
    Frequency,
    Energy,
    Factor,
    TimeSpan,
    Timing,
    Noise,
    Humidity,
    Conductivity,
    Count,
    Other,
}

/// <summary>Estado de uma leitura.</summary>
public enum SensorState
{
    Normal,
    Warning,
    Critical,
    Unavailable,
}

/// <summary>Descrição estável de um sensor (não muda entre leituras).</summary>
/// <param name="Id">Identificador estável, usado para favoritos, alertas e histórico.</param>
/// <param name="Source">Origem do dado (biblioteca, chip ou API do Windows), quando conhecida.</param>
public sealed record SensorDescriptor(
    string Id,
    string Name,
    string HardwareId,
    string HardwareName,
    HardwareCategory Category,
    SensorKind Kind,
    string Unit,
    string Source);

/// <summary>Leitura bruta entregue por uma fonte, antes da avaliação de estado.</summary>
public readonly record struct RawReading(SensorDescriptor Descriptor, float? Value);

/// <summary>Ponto do histórico de um sensor.</summary>
public readonly record struct SensorSample(DateTime Timestamp, float Value);

/// <summary>Leitura avaliada e pronta para exibição.</summary>
public sealed record SensorReading(
    SensorDescriptor Descriptor,
    float? Value,
    float? Min,
    float? Max,
    float? Average,
    SensorState State,
    string? StateReason);

/// <summary>Conjunto de leituras de um ciclo de atualização.</summary>
public sealed class SensorSnapshot
{
    private readonly Dictionary<string, SensorReading> _byId;

    public SensorSnapshot(DateTime timestamp, IReadOnlyList<SensorReading> readings)
    {
        Timestamp = timestamp;
        Readings = readings;
        _byId = new Dictionary<string, SensorReading>(readings.Count, StringComparer.Ordinal);
        foreach (var r in readings)
            _byId[r.Descriptor.Id] = r;
    }

    public DateTime Timestamp { get; }
    public IReadOnlyList<SensorReading> Readings { get; }

    public SensorReading? Find(string id) => _byId.GetValueOrDefault(id);

    public float? ValueOf(string? id) => id is null ? null : _byId.GetValueOrDefault(id)?.Value;

    public static SensorSnapshot Empty { get; } = new(DateTime.MinValue, Array.Empty<SensorReading>());
}

/// <summary>Estatísticas mínimas/médias/máximas de um sensor ao longo de uma sessão.</summary>
public sealed record SensorSessionStats(
    SensorDescriptor Descriptor,
    float? Min,
    float? Average,
    float? Max,
    long SampleCount,
    long UnavailableCount,
    long WarningCount,
    long CriticalCount);

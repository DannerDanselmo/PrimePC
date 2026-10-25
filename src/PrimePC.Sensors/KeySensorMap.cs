using System.Text.RegularExpressions;
using PrimePC.Sensors.Sources;

namespace PrimePC.Sensors;

/// <summary>Sensores relevantes de uma placa de vídeo.</summary>
public sealed record GpuSensorSet(
    string HardwareId,
    string Name,
    bool LikelyIntegrated,
    string? Load,
    string? Temperature,
    string? HotSpot,
    string? MemoryJunction,
    string? CoreClock,
    string? MemoryClock,
    string? Power,
    string? VramUsed,
    string? VramTotal,
    string? MemoryControllerLoad,
    string? Fan,
    string? PowerLimitPercent,
    string? Fps);

/// <summary>Sensores relevantes de um disco.</summary>
public sealed record DiskSensorSet(
    string HardwareId,
    string Name,
    string? Temperature,
    string? TotalActivity,
    string? UsedSpace,
    string? ReadRate,
    string? WriteRate,
    string? AvailableSpare,
    string? PercentageUsed,
    string? RemainingLife,
    string? DataWritten,
    string? DataRead,
    string? PowerOnHours);

/// <summary>Sensor da placa-mãe/refrigeração exibido no painel (nome amigável + identificador).</summary>
public sealed record BoardSensor(string Id, string Name);

/// <summary>
/// Localiza os sensores-chave (uso/temperatura/clock/potência de CPU e GPU, RAM, discos) entre todas as
/// leituras disponíveis. Os nomes variam conforme fabricante e driver, por isso há várias alternativas
/// em ordem de preferência; quando nada corresponde o campo fica nulo ("não disponível").
/// </summary>
public sealed partial class KeySensorMap
{
    public string? CpuName { get; private init; }
    public string? CpuTotalLoad { get; private init; }
    public string? CpuTemperature { get; private init; }
    public string? CpuPower { get; private init; }
    public IReadOnlyList<string> CpuThreadLoads { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<string> CpuCoreClocks { get; private init; } = Array.Empty<string>();
    public IReadOnlyList<GpuSensorSet> Gpus { get; private init; } = Array.Empty<GpuSensorSet>();
    public IReadOnlyList<DiskSensorSet> Disks { get; private init; } = Array.Empty<DiskSensorSet>();
    /// <summary>Nome do chip/placa que fornece os sensores da placa-mãe (ex.: "MSI B650M … › Nuvoton NCT6687D").</summary>
    public string? BoardSensorSource { get; private init; }

    public IReadOnlyList<BoardSensor> BoardTemperatures { get; private init; } = Array.Empty<BoardSensor>();
    public IReadOnlyList<BoardSensor> Fans { get; private init; } = Array.Empty<BoardSensor>();
    public IReadOnlyList<BoardSensor> Rails { get; private init; } = Array.Empty<BoardSensor>();
    public string? RamLoad { get; private init; }
    public string? RamUsed { get; private init; }
    public string? RamAvailable { get; private init; }

    public static KeySensorMap Empty { get; } = new();

    /// <summary>GPU principal: a de maior VRAM informada, ou a primeira que não parece integrada.</summary>
    public GpuSensorSet? PrimaryGpu(SensorSnapshot snapshot, string? preferredHardwareId = null)
    {
        if (preferredHardwareId is not null)
        {
            var preferred = Gpus.FirstOrDefault(g => g.HardwareId == preferredHardwareId);
            if (preferred is not null)
                return preferred;
        }

        return Gpus
            .OrderByDescending(g => snapshot.ValueOf(g.VramTotal) ?? 0)
            .ThenBy(g => g.LikelyIntegrated)
            .FirstOrDefault();
    }

    /// <summary>Média dos clocks por núcleo disponíveis.</summary>
    public float? AverageCpuClock(SensorSnapshot snapshot) => Average(snapshot, CpuCoreClocks);

    /// <summary>Maior uso entre as threads lógicas.</summary>
    public float? BusiestThreadLoad(SensorSnapshot snapshot)
    {
        float? max = null;
        foreach (var id in CpuThreadLoads)
        {
            if (snapshot.ValueOf(id) is { } v)
                max = max is null ? v : Math.Max(max.Value, v);
        }

        return max;
    }

    public static KeySensorMap Build(IReadOnlyList<SensorReading> readings)
    {
        var all = readings.Select(r => r.Descriptor).ToList();
        var cpu = all.Where(d => d.Category == HardwareCategory.Cpu).ToList();

        var threadLoads = cpu
            .Where(d => d.Kind == SensorKind.Load && CoreRegex().IsMatch(d.Name) && !IsAggregate(d.Name))
            .OrderBy(d => NaturalKey(d.Name))
            .Select(d => d.Id)
            .ToList();
        var coreClocks = cpu
            .Where(d => d.Kind == SensorKind.Clock && CoreRegex().IsMatch(d.Name) && !IsAggregate(d.Name))
            .Select(d => d.Id)
            .ToList();

        var gpus = all.Where(d => d.Category == HardwareCategory.Gpu)
            .GroupBy(d => d.HardwareId)
            .Select(g => BuildGpu(g.Key, g.ToList()))
            .ToList();

        var disks = all.Where(d => d.Category == HardwareCategory.Storage)
            .GroupBy(d => d.HardwareId)
            .Select(g => BuildDisk(g.Key, g.ToList()))
            .ToList();

        var board = all.Where(d => d.Category is HardwareCategory.Motherboard or HardwareCategory.EmbeddedController
                                   && !SensorStateEvaluator.IsReferenceValue(d)).ToList();
        var fans = all.Where(d => d.Kind == SensorKind.Fan
                                  && d.Category is HardwareCategory.Motherboard or HardwareCategory.Cooler or HardwareCategory.EmbeddedController)
            .OrderBy(d => NaturalKey(d.Name))
            .Select(d => new BoardSensor(d.Id, d.Name))
            .ToList();
        var rails = board.Where(d => d.Kind == SensorKind.Voltage
                                     && (SensorStateEvaluator.NominalRail(d.Name) is not null || d.Name.Equals("Vcore", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(d => SensorStateEvaluator.NominalRail(d.Name) ?? 0)
            .Select(d => new BoardSensor(d.Id, d.Name))
            .ToList();

        return new KeySensorMap
        {
            BoardSensorSource = board.FirstOrDefault()?.HardwareName,
            BoardTemperatures = board.Where(d => d.Kind == SensorKind.Temperature).Select(d => new BoardSensor(d.Id, d.Name)).ToList(),
            Fans = fans,
            Rails = rails,
            CpuName = cpu.FirstOrDefault()?.HardwareName,
            CpuTotalLoad = Pick(cpu, SensorKind.Load, "CPU Total"),
            CpuTemperature = Pick(cpu, SensorKind.Temperature,
                                 "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Tctl/Tdie", "Core (Tdie)",
                                 "CCDs Max (Tdie)", "Core Max", "Package")
                             ?? cpu.FirstOrDefault(d => d.Kind == SensorKind.Temperature && !d.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase))?.Id,
            CpuPower = Pick(cpu, SensorKind.Power, "Package", "CPU Package")
                       ?? cpu.FirstOrDefault(d => d.Kind == SensorKind.Power)?.Id,
            CpuThreadLoads = threadLoads,
            CpuCoreClocks = coreClocks,
            Gpus = gpus,
            Disks = disks,
            RamLoad = WindowsSystemSource.MemoryLoadId,
            RamUsed = WindowsSystemSource.MemoryUsedId,
            RamAvailable = WindowsSystemSource.MemoryAvailableId,
        };
    }

    private static GpuSensorSet BuildGpu(string hardwareId, List<SensorDescriptor> s)
    {
        string name = s[0].HardwareName;
        bool integrated = IntegratedGpuRegex().IsMatch(name);
        return new GpuSensorSet(
            hardwareId,
            name,
            integrated,
            Load: Pick(s, SensorKind.Load, "GPU Core", "D3D 3D", "GPU"),
            Temperature: Pick(s, SensorKind.Temperature, "GPU Core", "GPU"),
            HotSpot: Pick(s, SensorKind.Temperature, "GPU Hot Spot", "GPU Hotspot"),
            MemoryJunction: Pick(s, SensorKind.Temperature, "GPU Memory Junction", "GPU Memory"),
            CoreClock: Pick(s, SensorKind.Clock, "GPU Core"),
            MemoryClock: Pick(s, SensorKind.Clock, "GPU Memory"),
            Power: Pick(s, SensorKind.Power, "GPU Package", "GPU Power", "GPU Board Power", "GPU Core")
                   ?? s.FirstOrDefault(d => d.Kind == SensorKind.Power)?.Id,
            VramUsed: Pick(s, SensorKind.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used"),
            VramTotal: Pick(s, SensorKind.SmallData, "GPU Memory Total", "D3D Dedicated Memory Total"),
            MemoryControllerLoad: Pick(s, SensorKind.Load, "GPU Memory Controller"),
            Fan: s.FirstOrDefault(d => d.Kind == SensorKind.Fan)?.Id,
            // Em GPUs NVIDIA, "GPU Power" do tipo Load é a potência em % do limite configurado.
            PowerLimitPercent: Pick(s, SensorKind.Load, "GPU Power", "GPU Board Power"),
            // Contador de FPS fornecido pelo driver AMD (ADLX) para o aplicativo em tela cheia.
            Fps: Pick(s, SensorKind.Factor, "Fullscreen FPS"));
    }

    private static DiskSensorSet BuildDisk(string hardwareId, List<SensorDescriptor> s) => new(
        hardwareId,
        s[0].HardwareName,
        Temperature: Pick(s, SensorKind.Temperature, "Composite Temperature", "Temperature")
                     ?? s.FirstOrDefault(d => d.Kind == SensorKind.Temperature)?.Id,
        TotalActivity: Pick(s, SensorKind.Load, "Total Activity"),
        UsedSpace: Pick(s, SensorKind.Load, "Used Space"),
        ReadRate: Pick(s, SensorKind.Throughput, "Read Rate"),
        WriteRate: Pick(s, SensorKind.Throughput, "Write Rate"),
        AvailableSpare: Pick(s, SensorKind.Level, "Available Spare"),
        PercentageUsed: Pick(s, SensorKind.Level, "Percentage Used"),
        RemainingLife: Pick(s, SensorKind.Level, "Remaining Life", "Life"),
        DataWritten: Pick(s, SensorKind.Data, "Data Written", "Total Bytes Written", "Host Writes"),
        DataRead: Pick(s, SensorKind.Data, "Data Read", "Total Bytes Read", "Host Reads"),
        PowerOnHours: Pick(s, SensorKind.Factor, "Power On Hours") ?? Pick(s, SensorKind.TimeSpan, "Power On Hours"));

    private static string? Pick(IEnumerable<SensorDescriptor> sensors, SensorKind kind, params string[] names)
    {
        var ofKind = sensors.Where(d => d.Kind == kind).ToList();
        foreach (var name in names)
        {
            var match = ofKind.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match.Id;
        }

        return null;
    }

    private static float? Average(SensorSnapshot snapshot, IReadOnlyList<string> ids)
    {
        double sum = 0;
        int n = 0;
        foreach (var id in ids)
        {
            if (snapshot.ValueOf(id) is { } v && v > 0)
            {
                sum += v;
                n++;
            }
        }

        return n == 0 ? null : (float)(sum / n);
    }

    private static bool IsAggregate(string name) =>
        name.Contains("Max", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Average", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Total", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Effective", StringComparison.OrdinalIgnoreCase);

    private static string NaturalKey(string name) => NumberRegex().Replace(name, m => m.Value.PadLeft(4, '0'));

    [GeneratedRegex(@"^(CPU |P-|E-)?Core #\d+", RegexOptions.IgnoreCase)]
    private static partial Regex CoreRegex();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"\(TM\) Graphics|UHD Graphics|Iris|Vega \d+ Graphics|Radeon Graphics|Basic Display", RegexOptions.IgnoreCase)]
    private static partial Regex IntegratedGpuRegex();
}

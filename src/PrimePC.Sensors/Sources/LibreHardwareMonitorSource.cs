using LibreHardwareMonitor.Hardware;

namespace PrimePC.Sensors.Sources;

/// <summary>
/// Fonte principal de sensores, baseada na biblioteca LibreHardwareMonitor (código aberto, mantida).
/// Cobre CPU, GPU (NVIDIA/AMD/Intel), placa-mãe (Super I/O), memória, discos (SMART/NVMe), rede,
/// bateria, fontes e controladores compatíveis. Sensores de baixo nível dependem do driver PawnIO
/// e de privilégios de administrador.
/// </summary>
public sealed partial class LibreHardwareMonitorSource : ISensorSource
{
    private readonly List<(ISensor Sensor, SensorDescriptor Descriptor)> _sensors = new();
    private readonly List<string> _limitations = new();
    private Computer? _computer;
    private volatile bool _dirty;

    public string Name => "LibreHardwareMonitor";
    public IReadOnlyList<string> Limitations => _limitations;
    public int Generation { get; private set; }

    public void Open()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = true,
            IsStorageEnabled = true,
            IsBatteryEnabled = true,
            IsPsuEnabled = true,
        };
        _computer.HardwareAdded += _ => _dirty = true;
        _computer.HardwareRemoved += _ => _dirty = true;
        _computer.Open();

        bool admin = PrivilegeInfo.IsAdministrator;
        bool pawnIo = PrivilegeInfo.IsPawnIoInstalled;
        if (!pawnIo)
        {
            _limitations.Add(
                "Driver PawnIO não instalado: temperaturas, clocks e potência da CPU, sensores da placa-mãe " +
                "(ventoinhas, tensões, temperaturas de VRM/chipset) e temperatura dos módulos de RAM ficam indisponíveis.");
        }
        else if (!admin)
        {
            _limitations.Add(
                "O driver PawnIO está instalado, mas só pode ser usado quando o PrimePC é executado como administrador.");
        }

        if (!admin)
        {
            _limitations.Add(
                "Sem privilégios de administrador: dados SMART/saúde dos discos e alguns sensores de baixo nível podem não ser lidos.");
        }

        Rebuild();
    }

    public void Update()
    {
        if (_computer is null)
            return;

        foreach (var hardware in _computer.Hardware)
            UpdateRecursive(hardware);

        if (_dirty)
        {
            _dirty = false;
            Rebuild();
        }
    }

    public IEnumerable<RawReading> Read()
    {
        foreach (var (sensor, descriptor) in _sensors)
        {
            float? value = sensor.Value;
            if (value is { } v && (float.IsNaN(v) || float.IsInfinity(v)))
                value = null;
            yield return new RawReading(descriptor, value);
        }
    }

    public void Dispose()
    {
        try
        {
            _computer?.Close();
        }
        catch
        {
            // Encerramento best-effort: o processo pode estar finalizando.
        }

        _computer = null;
    }

    private static void UpdateRecursive(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch
        {
            // Um dispositivo com falha não deve interromper a coleta dos demais.
        }

        foreach (var sub in hardware.SubHardware)
            UpdateRecursive(sub);
    }

    private void Rebuild()
    {
        _sensors.Clear();
        if (_computer is null)
            return;

        foreach (var hardware in _computer.Hardware)
            Collect(hardware, parent: null);

        Generation++;
    }

    private void Collect(IHardware hardware, IHardware? parent)
    {
        if (hardware.HardwareType == HardwareType.Network && IsVirtualNetworkBinding(hardware.Name))
            return;

        var category = MapCategory(parent?.HardwareType.ToString() ?? hardware.HardwareType.ToString());
        string hardwareName = parent is null ? hardware.Name : $"{parent.Name} › {hardware.Name}";

        foreach (var sensor in hardware.Sensors)
        {
            var kind = Enum.TryParse<SensorKind>(sensor.SensorType.ToString(), out var k) ? k : SensorKind.Other;
            var descriptor = new SensorDescriptor(
                Id: sensor.Identifier.ToString(),
                Name: sensor.Name,
                HardwareId: hardware.Identifier.ToString(),
                HardwareName: hardwareName,
                Category: category,
                Kind: kind,
                Unit: SensorUnits.UnitFor(kind),
                Source: DescribeSource(hardware, kind));
            _sensors.Add((sensor, descriptor));
        }

        foreach (var sub in hardware.SubHardware)
            Collect(sub, hardware);
    }

    /// <summary>
    /// O Windows expõe cada filtro de rede (QoS, WFP...) e adaptadores virtuais de miniporta como
    /// interfaces separadas; elas duplicam o tráfego do adaptador real e só poluem a lista.
    /// </summary>
    private static bool IsVirtualNetworkBinding(string name) =>
        VirtualFilterRegex().IsMatch(name)
        || name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Depurador de Kernel", StringComparison.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"-\d{4}$|\*\s*\d+\s*$")]
    private static partial System.Text.RegularExpressions.Regex VirtualFilterRegex();

    private static HardwareCategory MapCategory(string hardwareType) => hardwareType switch
    {
        "Cpu" => HardwareCategory.Cpu,
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => HardwareCategory.Gpu,
        "Motherboard" or "SuperIO" => HardwareCategory.Motherboard,
        "Memory" => HardwareCategory.Memory,
        "Storage" => HardwareCategory.Storage,
        "Network" => HardwareCategory.Network,
        "Battery" => HardwareCategory.Battery,
        "Cooler" => HardwareCategory.Cooler,
        "Psu" => HardwareCategory.PowerSupply,
        "EmbeddedController" => HardwareCategory.EmbeddedController,
        _ => HardwareCategory.Other,
    };

    private static string DescribeSource(IHardware hardware, SensorKind kind)
    {
        string type = hardware.HardwareType.ToString();
        string detail = type switch
        {
            "Cpu" when kind == SensorKind.Load => "contadores de tempo de processador do Windows",
            "Cpu" => "registradores da CPU (MSR/SMU) via driver PawnIO",
            "GpuNvidia" => "NVIDIA NVAPI/NVML",
            "GpuAmd" => "AMD ADLX/ADL",
            "GpuIntel" => "Intel (D3D/IGCL)",
            "SuperIO" => $"chip Super I/O {hardware.Name} via driver PawnIO",
            "Motherboard" => "firmware da placa-mãe",
            "Memory" when kind is SensorKind.Temperature or SensorKind.Timing => "SPD/SMBus dos módulos via driver PawnIO",
            "Memory" => "Windows (status da memória)",
            "Storage" => "SMART/NVMe e contadores de disco do Windows",
            "Network" => "interfaces de rede do Windows",
            "Battery" => "Windows (bateria)",
            "Cooler" => "controlador USB/HID da refrigeração",
            "Psu" => "fonte com interface USB/HID",
            "EmbeddedController" => "controlador embarcado (EC)",
            _ => type,
        };
        return "LibreHardwareMonitor · " + detail;
    }
}

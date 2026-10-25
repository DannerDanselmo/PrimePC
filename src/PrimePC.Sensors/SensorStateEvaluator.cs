using System.Text.RegularExpressions;

namespace PrimePC.Sensors;

/// <summary>
/// Classifica cada leitura em normal / atenção / crítico / indisponível usando limites padrão
/// conservadores. Leituras implausíveis (canal desconectado, escala desconhecida) são marcadas como
/// indisponíveis em vez de exibidas como se fossem verdadeiras. Limites personalizados são configurados
/// pelo usuário por meio de alertas.
/// </summary>
public sealed partial class SensorStateEvaluator
{
    private (float Warn, float Crit) _cpuTempLimits = (85, 95);
    private string? _cpuModel;

    /// <summary>Modelo da CPU, usado para escolher o limite térmico padrão adequado.</summary>
    public string? CpuModel
    {
        get => _cpuModel;
        set
        {
            _cpuModel = value;
            _cpuTempLimits = CpuTemperatureLimits(value);
        }
    }

    public (float Warn, float Crit) CpuTempLimits => _cpuTempLimits;

    /// <summary>
    /// Indica se o acesso de baixo nível (driver PawnIO + administrador) está ativo. Sem ele, o
    /// LibreHardwareMonitor devolve zeros/valores padrão para clocks, potência, tensões e temperaturas da CPU
    /// e para sensores SPD da memória; essas leituras são então marcadas como indisponíveis.
    /// </summary>
    public bool LowLevelAccess { get; set; } = true;

    private const string NeedsDriver =
        "Esta leitura exige o driver PawnIO instalado e o PrimePC executado como administrador.";

    private const string ReferenceValue =
        "Valor de referência informado pelo dispositivo (limite configurado ou resolução do sensor), não é uma medição.";

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (float Warn, float Crit)> _deviceLimits = new();

    /// <summary>
    /// Valores que o próprio dispositivo informa como limites (ex.: "Warning Temperature" de um SSD NVMe,
    /// "Thermal Sensor Critical High Limit" de um módulo DDR5). Não são medições e não devem gerar alertas.
    /// </summary>
    public static bool IsReferenceValue(SensorDescriptor d) =>
        Has(d.Name, "Limit") || Has(d.Name, "Threshold") || Has(d.Name, "Resolution")
        || d.Name.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase)
        || d.Name.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Aprende os limites térmicos que cada disco informa (NVMe: "Warning/Critical Temperature"), que são mais
    /// precisos que os limites genéricos. Chamado pelo coletor a cada ciclo, antes da avaliação.
    /// </summary>
    public void LearnDeviceLimits(IEnumerable<RawReading> readings)
    {
        foreach (var group in readings.Where(r => r.Descriptor.Category == HardwareCategory.Storage && r.Descriptor.Kind == SensorKind.Temperature)
                     .GroupBy(r => r.Descriptor.HardwareId))
        {
            float? warn = group.FirstOrDefault(r => r.Descriptor.Name.Equals("Warning Temperature", StringComparison.OrdinalIgnoreCase)).Value;
            float? crit = group.FirstOrDefault(r => r.Descriptor.Name.Equals("Critical Temperature", StringComparison.OrdinalIgnoreCase)).Value;
            if (warn is > 30 and < 150 && crit is > 30 and < 150 && crit >= warn)
                _deviceLimits[group.Key] = (warn.Value, crit.Value);
        }
    }

    public static (float Warn, float Crit) CpuTemperatureLimits(string? cpuModel)
    {
        if (string.IsNullOrWhiteSpace(cpuModel))
            return (85, 95);
        // Processadores X3D (cache 3D V-Cache) têm limite térmico menor (ex.: 7800X3D = 89 °C).
        if (cpuModel.Contains("X3D", StringComparison.OrdinalIgnoreCase))
            return (80, 89);
        if (cpuModel.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            return (85, 100);
        return (85, 95);
    }

    public (SensorState State, string? Reason) Evaluate(SensorDescriptor d, float? value)
    {
        if (!LowLevelAccess && RequiresLowLevelAccess(d))
            return (SensorState.Unavailable, NeedsDriver);

        if (value is not { } v || float.IsNaN(v) || float.IsInfinity(v))
            return (SensorState.Unavailable, "O sensor não forneceu leitura neste ciclo (driver, permissão ou dispositivo em repouso).");

        if (IsReferenceValue(d))
            return (SensorState.Normal, ReferenceValue);

        return d.Kind switch
        {
            SensorKind.Temperature => EvaluateTemperature(d, v),
            SensorKind.Load => EvaluateLoad(d, v),
            SensorKind.Level => EvaluateLevel(d, v),
            SensorKind.Voltage => EvaluateVoltage(d, v),
            SensorKind.Fan when v < 0 => (SensorState.Unavailable, Implausible),
            SensorKind.Clock when v <= 0 => (SensorState.Unavailable, "Clock zero: a leitura não foi fornecida pelo driver."),
            SensorKind.Power when v <= 0 && d.Category == HardwareCategory.Cpu && Has(d.Name, "Package")
                => (SensorState.Unavailable, "Potência zero: a leitura não foi fornecida pelo driver."),
            SensorKind.Factor when v < 0 && Has(d.Name, "FPS")
                => (SensorState.Unavailable, "Nenhum aplicativo em tela cheia está sendo medido pelo driver no momento."),
            _ => (SensorState.Normal, null),
        };
    }

    /// <summary>Sensores lidos de registradores/barramentos que só o driver de baixo nível acessa.</summary>
    public static bool RequiresLowLevelAccess(SensorDescriptor d) => d.Category switch
    {
        HardwareCategory.Cpu => d.Kind != SensorKind.Load,
        HardwareCategory.Memory => d.Kind is SensorKind.Temperature or SensorKind.Timing,
        HardwareCategory.Motherboard => true,
        _ => false,
    };

    private const string Implausible =
        "Leitura fora da faixa plausível: o canal provavelmente está desconectado ou não é mapeado para esta placa.";

    private (SensorState, string?) EvaluateTemperature(SensorDescriptor d, float v)
    {
        string name = d.Name;
        if (name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
        {
            // Sensores Intel que informam a margem até o limite térmico (quanto menor, pior).
            if (v < 0 || v > 150) return (SensorState.Unavailable, Implausible);
            if (v < 5) return (SensorState.Critical, "Margem térmica abaixo de 5 °C.");
            if (v < 15) return (SensorState.Warning, "Margem térmica abaixo de 15 °C.");
            return (SensorState.Normal, null);
        }

        if (v < 1 || v > 125)
            return (SensorState.Unavailable, Implausible);

        var (warn, crit) = d.Category switch
        {
            HardwareCategory.Cpu => _cpuTempLimits,
            HardwareCategory.Gpu when Has(name, "Hot Spot") || Has(name, "Junction") || Has(name, "Memory") => (95f, 105f),
            HardwareCategory.Gpu => (83f, 90f),
            HardwareCategory.Storage => _deviceLimits.TryGetValue(d.HardwareId, out var device) ? device : (70f, 80f),
            HardwareCategory.Memory => (70f, 85f),
            HardwareCategory.Battery => (45f, 60f),
            _ => (80f, 95f),
        };
        return Classify(v, warn, crit, "°C");
    }

    private static (SensorState, string?) EvaluateLoad(SensorDescriptor d, float v)
    {
        if (v < -0.5f || v > 100.5f)
            return (SensorState.Unavailable, Implausible);

        bool isPhysicalMemory = (d.Category == HardwareCategory.Memory && d.Name.Equals("Memory", StringComparison.OrdinalIgnoreCase)
                                 && !Has(d.HardwareName, "Virtual"))
                                || d.Name.Equals("Memória física em uso", StringComparison.Ordinal);
        if (isPhysicalMemory)
            return Classify(v, 90, 97, "%");

        if (d.Category == HardwareCategory.Storage && Has(d.Name, "Used Space"))
            return Classify(v, 90, 97, "%");

        return (SensorState.Normal, null);
    }

    private static (SensorState, string?) EvaluateLevel(SensorDescriptor d, float v)
    {
        if (v < -0.5f || v > 255f)
            return (SensorState.Unavailable, Implausible);

        string n = d.Name;
        if (Has(n, "Available Spare") && !Has(n, "Threshold"))
        {
            if (v < 10) return (SensorState.Critical, "Área reserva do SSD abaixo de 10%.");
            if (v < 20) return (SensorState.Warning, "Área reserva do SSD abaixo de 20%.");
            return (SensorState.Normal, null);
        }

        if (Has(n, "Percentage Used"))
            return Classify(v, 80, 100, "% da vida útil estimada usada");

        if (Has(n, "Remaining Life") || n.Equals("Life", StringComparison.OrdinalIgnoreCase))
        {
            if (v <= 10) return (SensorState.Critical, "Vida útil restante informada ≤ 10%.");
            if (v <= 20) return (SensorState.Warning, "Vida útil restante informada ≤ 20%.");
        }

        return (SensorState.Normal, null);
    }

    private static (SensorState, string?) EvaluateVoltage(SensorDescriptor d, float v)
    {
        float? nominal = NominalRail(d.Name);
        if (nominal is null)
            return v < 0 || v > 20 ? (SensorState.Unavailable, Implausible) : (SensorState.Normal, null);

        float deviation = Math.Abs(v - nominal.Value) / nominal.Value;
        if (deviation > 0.25f)
        {
            return (SensorState.Unavailable,
                "Leitura muito distante do valor nominal: o canal provavelmente não tem escala conhecida para esta placa-mãe. " +
                "Não interprete como falha da fonte.");
        }

        if (deviation > 0.10f) return (SensorState.Critical, $"Desvio de {deviation:P0} em relação a {nominal:0.0} V (tolerância ATX: ±5%).");
        if (deviation > 0.05f) return (SensorState.Warning, $"Desvio de {deviation:P0} em relação a {nominal:0.0} V (tolerância ATX: ±5%).");
        return (SensorState.Normal, null);
    }

    /// <summary>Tensão nominal de trilhos conhecidos da fonte (12 V, 5 V, 3,3 V), pelo nome do sensor.</summary>
    public static float? NominalRail(string name)
    {
        var m = RailRegex().Match(name);
        if (!m.Success)
            return null;
        return m.Groups[1].Value switch
        {
            "12" => 12f,
            "5" => 5f,
            "3.3" or "3,3" or "3" => 3.3f,
            _ => null,
        };
    }

    [GeneratedRegex(@"^\s*\+?(12|5|3[.,]3|3)\s*V(?:CC|SB)?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RailRegex();

    private static (SensorState, string?) Classify(float v, float warn, float crit, string unit)
    {
        if (v >= crit) return (SensorState.Critical, $"Acima do limite crítico padrão ({crit:0} {unit}).");
        if (v >= warn) return (SensorState.Warning, $"Acima do limite de atenção padrão ({warn:0} {unit}).");
        return (SensorState.Normal, null);
    }

    private static bool Has(string s, string part) => s.Contains(part, StringComparison.OrdinalIgnoreCase);
}

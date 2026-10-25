using System.Globalization;

namespace PrimePC.Sensors;

public enum TemperatureUnit
{
    Celsius,
    Fahrenheit,
}

/// <summary>Unidades e formatação consistentes para todas as grandezas.</summary>
public static class SensorUnits
{
    /// <summary>Unidade-base de armazenamento de cada tipo de sensor.</summary>
    public static string UnitFor(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "°C",
        SensorKind.Load => "%",
        SensorKind.Clock => "MHz",
        SensorKind.Voltage => "V",
        SensorKind.Current => "A",
        SensorKind.Power => "W",
        SensorKind.Fan => "RPM",
        SensorKind.Flow => "L/h",
        SensorKind.Control => "%",
        SensorKind.Level => "%",
        SensorKind.Data => "GB",
        SensorKind.SmallData => "MB",
        SensorKind.Throughput => "B/s",
        SensorKind.Frequency => "Hz",
        SensorKind.Energy => "mWh",
        SensorKind.TimeSpan => "s",
        SensorKind.Timing => "ns",
        SensorKind.Noise => "dBA",
        SensorKind.Humidity => "%",
        SensorKind.Conductivity => "µS/cm",
        _ => "",
    };

    public static string KindDisplayName(SensorKind kind) => kind switch
    {
        SensorKind.Temperature => "Temperatura",
        SensorKind.Load => "Uso",
        SensorKind.Clock => "Frequência (clock)",
        SensorKind.Voltage => "Tensão",
        SensorKind.Current => "Corrente",
        SensorKind.Power => "Potência",
        SensorKind.Fan => "Ventoinha",
        SensorKind.Flow => "Fluxo",
        SensorKind.Control => "Controle",
        SensorKind.Level => "Nível",
        SensorKind.Data => "Dados",
        SensorKind.SmallData => "Dados (MB)",
        SensorKind.Throughput => "Taxa de transferência",
        SensorKind.Frequency => "Frequência",
        SensorKind.Energy => "Energia",
        SensorKind.Factor => "Fator",
        SensorKind.TimeSpan => "Tempo",
        SensorKind.Timing => "Temporização",
        SensorKind.Noise => "Ruído",
        SensorKind.Humidity => "Umidade",
        SensorKind.Conductivity => "Condutividade",
        SensorKind.Count => "Contagem",
        _ => "Outro",
    };

    public static string CategoryDisplayName(HardwareCategory category) => category switch
    {
        HardwareCategory.Cpu => "Processador",
        HardwareCategory.Gpu => "Placa de vídeo",
        HardwareCategory.Motherboard => "Placa-mãe",
        HardwareCategory.Memory => "Memória",
        HardwareCategory.Storage => "Armazenamento",
        HardwareCategory.Network => "Rede",
        HardwareCategory.Battery => "Bateria",
        HardwareCategory.Cooler => "Refrigeração",
        HardwareCategory.PowerSupply => "Fonte",
        HardwareCategory.EmbeddedController => "Controlador embarcado",
        HardwareCategory.System => "Sistema",
        _ => "Outros",
    };

    public static string StateDisplayName(SensorState state) => state switch
    {
        SensorState.Normal => "Normal",
        SensorState.Warning => "Atenção",
        SensorState.Critical => "Crítico",
        _ => "Indisponível",
    };

    public static float CelsiusToFahrenheit(float c) => c * 9f / 5f + 32f;

    /// <summary>Formata valor com unidade apropriada, convertendo escalas quando faz sentido.</summary>
    public static string Format(SensorKind kind, float? value, TemperatureUnit temperatureUnit = TemperatureUnit.Celsius,
        CultureInfo? culture = null)
    {
        if (value is null || float.IsNaN(value.Value) || float.IsInfinity(value.Value))
            return "Não disponível";

        var v = value.Value;
        culture ??= CultureInfo.CurrentCulture;
        return kind switch
        {
            SensorKind.Temperature when temperatureUnit == TemperatureUnit.Fahrenheit
                => CelsiusToFahrenheit(v).ToString("0.0", culture) + " °F",
            SensorKind.Temperature => v.ToString("0.0", culture) + " °C",
            SensorKind.Load or SensorKind.Control or SensorKind.Level or SensorKind.Humidity
                => v.ToString("0.0", culture) + " %",
            SensorKind.Clock => v.ToString("0", culture) + " MHz",
            SensorKind.Voltage => v.ToString("0.000", culture) + " V",
            SensorKind.Current => v.ToString("0.00", culture) + " A",
            SensorKind.Power => v.ToString("0.0", culture) + " W",
            SensorKind.Fan => v.ToString("0", culture) + " RPM",
            SensorKind.Flow => v.ToString("0.0", culture) + " L/h",
            SensorKind.Data => v.ToString("0.00", culture) + " GB",
            SensorKind.SmallData => v.ToString("0", culture) + " MB",
            SensorKind.Throughput => FormatThroughput(v, culture),
            SensorKind.Frequency => v.ToString("0.0", culture) + " Hz",
            SensorKind.Energy => v.ToString("0", culture) + " mWh",
            SensorKind.TimeSpan => FormatDuration(TimeSpan.FromSeconds(v)),
            SensorKind.Timing => v.ToString("0.00", culture) + " ns",
            SensorKind.Noise => v.ToString("0.0", culture) + " dBA",
            SensorKind.Conductivity => v.ToString("0.0", culture) + " µS/cm",
            SensorKind.Count => v.ToString("0", culture),
            _ => v.ToString("0.###", culture),
        };
    }

    public static string FormatThroughput(float bytesPerSecond, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double v = bytesPerSecond;
        if (v >= 1024d * 1024 * 1024) return (v / (1024d * 1024 * 1024)).ToString("0.00", culture) + " GB/s";
        if (v >= 1024d * 1024) return (v / (1024d * 1024)).ToString("0.0", culture) + " MB/s";
        if (v >= 1024d) return (v / 1024d).ToString("0.0", culture) + " KB/s";
        return v.ToString("0", culture) + " B/s";
    }

    public static string FormatBytes(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        double v = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        int i = 0;
        while (v >= 1024 && i < units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return v.ToString(i == 0 ? "0" : "0.0", culture) + " " + units[i];
    }

    public static string FormatDuration(TimeSpan t)
    {
        if (t.TotalDays >= 1)
            return $"{(int)t.TotalDays}d {t.Hours:00}h {t.Minutes:00}min";
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes:00}min {t.Seconds:00}s";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}min {t.Seconds:00}s";
        return $"{t.TotalSeconds:0}s";
    }
}

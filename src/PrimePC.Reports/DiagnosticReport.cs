using System.Reflection;
using PrimePC.Diagnostics;
using PrimePC.GpuTesting;
using PrimePC.Sensors;
using PrimePC.Sensors.Inventory;
using PrimePC.Testing;

namespace PrimePC.Reports;

/// <summary>Estatística de um sensor durante a sessão, pronta para exportação.</summary>
public sealed record SensorStatRow(
    string Hardware,
    string Category,
    string Name,
    string Kind,
    string Unit,
    float? Min,
    float? Average,
    float? Max,
    string MinText,
    string AverageText,
    string MaxText,
    long Samples,
    long UnavailableSamples,
    long WarningSamples,
    long CriticalSamples,
    string Source);

/// <summary>Driver resumido para o relatório.</summary>
public sealed record DriverReportRow(string Device, string Category, string Provider, string Version, string Date, string Status, string Detail);

/// <summary>Resumo dos drivers: contagens, drivers de vídeo e itens que precisam de atenção.</summary>
public sealed record DriverReport(
    DateTime CollectedAt,
    int Total,
    int ThirdParty,
    int Problems,
    int Warnings,
    int Unsigned,
    string UpdateStatus,
    IReadOnlyList<DriverReportRow> Video,
    IReadOnlyList<DriverReportRow> Attention);

/// <summary>Relatório de diagnóstico completo (modelo neutro, exportado para PDF, CSV ou JSON).</summary>
public sealed class DiagnosticReport
{
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public string AppVersion { get; init; } = "";
    public bool IncludesIdentifiers { get; init; }
    public string AccessLevel { get; init; } = "";
    public HardwareInventory? Inventory { get; init; }
    public DateTime SessionStart { get; init; }
    public TimeSpan SessionDuration { get; init; }
    public double UpdateIntervalSeconds { get; init; }
    public IReadOnlyList<SensorStatRow> Sensors { get; init; } = Array.Empty<SensorStatRow>();
    public IReadOnlyList<AlertEvent> Alerts { get; init; } = Array.Empty<AlertEvent>();
    public MemoryTestResult? RamTest { get; init; }
    public IReadOnlyList<CpuStressResult> CpuStress { get; init; } = Array.Empty<CpuStressResult>();
    public IReadOnlyList<StorageBenchmarkResult> StorageBenchmarks { get; init; } = Array.Empty<StorageBenchmarkResult>();
    public IReadOnlyList<BenchmarkResult> Benchmarks { get; init; } = Array.Empty<BenchmarkResult>();
    public IReadOnlyList<GpuStressResult> GpuStress { get; init; } = Array.Empty<GpuStressResult>();
    public VramTestResult? VramTest { get; init; }
    public DriverReport? Drivers { get; init; }
    public string? BottleneckSessionName { get; init; }
    public BottleneckReport? Bottleneck { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
}

public sealed record ReportOptions
{
    public bool IncludeInventory { get; init; } = true;
    public bool IncludeSensors { get; init; } = true;
    public bool IncludeAlerts { get; init; } = true;
    public bool IncludeRamTest { get; init; } = true;
    public bool IncludeStability { get; init; } = true;
    public bool IncludeStorage { get; init; } = true;
    public bool IncludeBottleneck { get; init; } = true;
    public bool IncludeDrivers { get; init; } = true;

    /// <summary>Inclui o nome do computador. Desativado por padrão (privacidade).</summary>
    public bool IncludeIdentifiers { get; init; }
}

/// <summary>Dados de entrada para montar um relatório.</summary>
public sealed record ReportInputs
{
    public HardwareInventory? Inventory { get; init; }
    public IReadOnlyList<SensorSessionStats> SensorStats { get; init; } = Array.Empty<SensorSessionStats>();
    public DateTime SessionStart { get; init; }
    public double UpdateIntervalSeconds { get; init; }
    public IReadOnlyList<AlertEvent> Alerts { get; init; } = Array.Empty<AlertEvent>();
    public MemoryTestResult? RamTest { get; init; }
    public IReadOnlyList<CpuStressResult> CpuStress { get; init; } = Array.Empty<CpuStressResult>();
    public IReadOnlyList<StorageBenchmarkResult> StorageBenchmarks { get; init; } = Array.Empty<StorageBenchmarkResult>();
    public IReadOnlyList<BenchmarkResult> Benchmarks { get; init; } = Array.Empty<BenchmarkResult>();
    public IReadOnlyList<GpuStressResult> GpuStress { get; init; } = Array.Empty<GpuStressResult>();
    public VramTestResult? VramTest { get; init; }
    public PrimePC.Drivers.DriverScan? DriverScan { get; init; }
    public PrimePC.Drivers.DriverUpdateSearch? DriverUpdates { get; init; }
    public BottleneckSession? BottleneckSession { get; init; }
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();
    public TemperatureUnit TemperatureUnit { get; init; } = TemperatureUnit.Celsius;
}

public static class ReportBuilder
{
    public static string AppVersion =>
        typeof(ReportBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static DriverReport BuildDrivers(PrimePC.Drivers.DriverScan scan, PrimePC.Drivers.DriverUpdateSearch? updates)
    {
        var byDevice = updates is { Succeeded: true }
            ? PrimePC.Drivers.DriverEvaluator.Match(scan.Drivers, updates.Updates).ByDevice
            : new Dictionary<string, PrimePC.Drivers.DriverUpdate>();
        var assessed = scan.Drivers
            .Select(d => (Driver: d, A: PrimePC.Drivers.DriverEvaluator.Assess(d, byDevice.GetValueOrDefault(d.DeviceId))))
            .ToList();

        DriverReportRow Row((PrimePC.Drivers.InstalledDriver Driver, PrimePC.Drivers.DriverAssessment A) x) => new(
            PrimePC.Drivers.DriverInventory.DisplayName(x.Driver),
            PrimePC.Drivers.DriverInventory.CategoryName(x.Driver.Category),
            x.Driver.Provider ?? x.Driver.Manufacturer ?? "—",
            x.Driver.Version is null ? "Sem driver"
                : x.Driver.FriendlyVersion != x.Driver.Version ? $"{x.Driver.FriendlyVersion} ({x.Driver.Version})" : x.Driver.Version,
            x.Driver.Date?.ToString("dd/MM/yyyy") ?? "—",
            x.A.Label,
            x.A.Detail);

        string updateStatus = updates is null
            ? "O Windows Update não foi consultado nesta sessão."
            : $"{updates.Message} (consulta às {updates.SearchedAt:dd/MM/yyyy HH:mm})";
        return new DriverReport(
            scan.CollectedAt,
            scan.Drivers.Count(d => d.HasDriver),
            scan.Drivers.Count(d => d.HasDriver && !d.IsInbox),
            assessed.Count(x => x.A.Status == PrimePC.Drivers.DriverStatus.Problem),
            assessed.Count(x => x.A.Status is PrimePC.Drivers.DriverStatus.Warning or PrimePC.Drivers.DriverStatus.UpdateAvailable),
            scan.Drivers.Count(d => d.IsSigned == false),
            updateStatus,
            assessed.Where(x => x.Driver.Category == PrimePC.Drivers.DriverCategory.Video && x.Driver.HasDriver).Select(Row).ToList(),
            assessed.Where(x => x.A.Status != PrimePC.Drivers.DriverStatus.Ok).Select(Row).ToList());
    }

    public static DiagnosticReport Build(ReportInputs input, ReportOptions options)
    {
        var inventory = input.Inventory;
        if (inventory is not null && !options.IncludeIdentifiers)
            inventory = inventory with { Os = inventory.Os with { ComputerName = null } };

        var sensors = input.SensorStats
            .Where(s => s.SampleCount > 0 || s.UnavailableCount > 0)
            .OrderBy(s => s.Descriptor.Category)
            .ThenBy(s => s.Descriptor.HardwareName)
            .ThenBy(s => s.Descriptor.Kind)
            .ThenBy(s => System.Text.RegularExpressions.Regex.Replace(s.Descriptor.Name, @"\d+", m => m.Value.PadLeft(4, '0')))
            .Select(s =>
            {
                var d = s.Descriptor;
                string unit = d.Kind == SensorKind.Temperature && input.TemperatureUnit == TemperatureUnit.Fahrenheit ? "°F" : d.Unit;
                return new SensorStatRow(d.HardwareName, SensorUnits.CategoryDisplayName(d.Category), d.Name,
                    SensorUnits.KindDisplayName(d.Kind), unit, s.Min, s.Average, s.Max,
                    SensorUnits.Format(d.Kind, s.Min, input.TemperatureUnit),
                    SensorUnits.Format(d.Kind, s.Average, input.TemperatureUnit),
                    SensorUnits.Format(d.Kind, s.Max, input.TemperatureUnit),
                    s.SampleCount, s.UnavailableCount, s.WarningCount, s.CriticalCount, d.Source);
            })
            .ToList();

        var limitations = new List<string>(input.Limitations)
        {
            "Leituras dependem do que o fabricante, o firmware e os drivers disponibilizam; sensores ausentes aparecem como \"Não disponível\".",
            "Mínimos, médias e máximos consideram apenas leituras válidas desta sessão.",
            "Testes aprovados reduzem a suspeita de defeitos, mas não garantem ausência total de falhas.",
        };

        return new DiagnosticReport
        {
            GeneratedAt = DateTime.Now,
            AppVersion = AppVersion,
            IncludesIdentifiers = options.IncludeIdentifiers,
            AccessLevel = $"Administrador: {(PrivilegeInfo.IsAdministrator ? "sim" : "não")} · Driver PawnIO: {(PrivilegeInfo.IsPawnIoInstalled ? "instalado" : "não instalado")}",
            Inventory = options.IncludeInventory ? inventory : null,
            SessionStart = input.SessionStart,
            SessionDuration = DateTime.Now - input.SessionStart,
            UpdateIntervalSeconds = input.UpdateIntervalSeconds,
            Sensors = options.IncludeSensors ? sensors : Array.Empty<SensorStatRow>(),
            Alerts = options.IncludeAlerts ? input.Alerts : Array.Empty<AlertEvent>(),
            RamTest = options.IncludeRamTest ? input.RamTest : null,
            CpuStress = options.IncludeStability ? input.CpuStress : Array.Empty<CpuStressResult>(),
            Benchmarks = options.IncludeStability ? input.Benchmarks : Array.Empty<BenchmarkResult>(),
            GpuStress = options.IncludeStability ? input.GpuStress : Array.Empty<GpuStressResult>(),
            VramTest = options.IncludeRamTest ? input.VramTest : null,
            Drivers = options.IncludeDrivers && input.DriverScan is { } scan ? BuildDrivers(scan, input.DriverUpdates) : null,
            StorageBenchmarks = options.IncludeStorage ? input.StorageBenchmarks : Array.Empty<StorageBenchmarkResult>(),
            BottleneckSessionName = options.IncludeBottleneck ? input.BottleneckSession?.DisplayName : null,
            Bottleneck = options.IncludeBottleneck ? input.BottleneckSession?.Report : null,
            Limitations = limitations.Distinct().ToList(),
        };
    }
}

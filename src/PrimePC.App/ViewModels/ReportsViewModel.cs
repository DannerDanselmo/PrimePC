using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Diagnostics;
using PrimePC.Reports;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

public sealed record SessionChoice(BottleneckSession? Session, string Name);

public sealed record AlertRow(string Time, string Message, bool IsCritical);

public sealed partial class ReportsViewModel : PageViewModel
{
    public ReportsViewModel(AppServices services) : base(services)
    {
        _includeIdentifiers = services.Settings.IncludeIdentifiersInReports;
    }

    public override string Title => "Relatórios";
    public override string Subtitle => "Exporte um relatório de diagnóstico em PDF, CSV ou JSON";
    public override string Glyph => "";

    public ObservableCollection<SessionChoice> SessionChoices { get; } = new();
    public ObservableCollection<AlertRow> Alerts { get; } = new();

    [ObservableProperty] private bool _includeInventory = true;
    [ObservableProperty] private bool _includeSensors = true;
    [ObservableProperty] private bool _includeAlerts = true;
    [ObservableProperty] private bool _includeRamTest = true;
    [ObservableProperty] private bool _includeStability = true;
    [ObservableProperty] private bool _includeStorage = true;
    [ObservableProperty] private bool _includeBottleneck = true;
    [ObservableProperty] private bool _includeDrivers = true;
    [ObservableProperty] private bool _includeIdentifiers;
    [ObservableProperty] private SessionChoice? _selectedSession;
    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private string? _lastExport;
    [ObservableProperty] private string _availableText = "";

    partial void OnIncludeIdentifiersChanged(bool value)
    {
        Services.Settings.IncludeIdentifiersInReports = value;
        Services.SaveSettings();
    }

    public override void OnActivated()
    {
        SessionChoices.Clear();
        SessionChoices.Add(new SessionChoice(null, "Nenhuma"));
        foreach (var s in Services.Sessions.LoadAll().Where(s => s.Report is not null))
            SessionChoices.Add(new SessionChoice(s, s.DisplayName));
        SelectedSession = SessionChoices.FirstOrDefault(c => c.Session?.Id == Services.LastBottleneckSession?.Id && c.Session is not null)
                          ?? (SessionChoices.Count > 1 ? SessionChoices[1] : SessionChoices[0]);

        Alerts.Clear();
        foreach (var a in Services.Alerts.Log.Reverse())
            Alerts.Add(new AlertRow(a.Time.ToString("dd/MM HH:mm:ss"), a.Message, a.IsCritical));

        var ram = Services.RamHistory.Load().LastOrDefault();
        int cpu = Services.CpuHistory.Load().Count, storage = Services.StorageHistory.Load().Count, bench = Services.BenchmarkHistory.Load().Count;
        int gpu = Services.GpuStressHistory.Load().Count, vram = Services.VramHistory.Load().Count;
        _historySummary = $"{(ram is null ? "nenhum teste de RAM" : $"teste de RAM de {ram.StartedAt:dd/MM HH:mm}")} · {vram} teste(s) de VRAM · " +
                          $"{cpu} teste(s) de CPU · {gpu} teste(s) de GPU · " +
                          $"{storage} teste(s) de armazenamento · {bench} benchmark(s) · {Alerts.Count} alerta(s)";
        UpdateSessionText();
    }

    private string _historySummary = "";

    public override void OnSnapshot(SensorSnapshot snapshot) => UpdateSessionText();

    private void UpdateSessionText()
    {
        SessionText = $"Sessão de monitoramento iniciada em {Services.Hub.SessionStart:dd/MM/yyyy HH:mm:ss} " +
                      $"({SensorUnits.FormatDuration(DateTime.Now - Services.Hub.SessionStart)}). " +
                      "Mínimos, médias e máximos do relatório referem-se a este período.";
        AvailableText = $"Disponível para o relatório: {Services.Hub.GetSessionStats().Count(s => s.SampleCount > 0)} sensores com leituras · {_historySummary}";
    }

    [RelayCommand]
    private void ResetSession()
    {
        if (!Shell.Confirm("Reiniciar a sessão? Mínimos, médias e máximos de todos os sensores serão zerados."))
            return;
        Services.Hub.ResetStatistics();
        UpdateSessionText();
    }

    [RelayCommand]
    private void ClearAlerts()
    {
        Services.Alerts.ClearLog();
        Alerts.Clear();
    }

    [RelayCommand]
    private void Export(string format)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        string? path = format switch
        {
            "pdf" => Shell.SaveFile("Exportar relatório (PDF)", "Documento PDF (*.pdf)|*.pdf", $"relatorio-primepc-{stamp}.pdf"),
            "csv" => Shell.SaveFile("Exportar estatísticas dos sensores (CSV)", "Planilha CSV (*.csv)|*.csv", $"sensores-primepc-{stamp}.csv"),
            _ => Shell.SaveFile("Exportar relatório (JSON)", "JSON (*.json)|*.json", $"relatorio-primepc-{stamp}.json"),
        };
        if (path is null)
            return;

        try
        {
            var report = BuildReport();
            switch (format)
            {
                case "pdf":
                    PdfReportExporter.Export(report, path);
                    break;
                case "csv":
                    CsvExporter.ExportSensorStatistics(report, path);
                    break;
                default:
                    JsonReportExporter.Export(report, path);
                    break;
            }

            LastExport = path;
            Log.Info($"Relatório exportado: {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao exportar relatório", ex);
            Shell.Error($"Não foi possível exportar o relatório: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenLastExport()
    {
        if (LastExport is not null)
            Shell.Open(LastExport);
    }

    [RelayCommand]
    private void ShowLastExportInFolder()
    {
        if (LastExport is not null)
            Shell.ShowInExplorer(LastExport);
    }

    private DiagnosticReport BuildReport()
    {
        var inputs = new ReportInputs
        {
            Inventory = Services.Inventory,
            SensorStats = Services.Hub.GetSessionStats(),
            SessionStart = Services.Hub.SessionStart,
            UpdateIntervalSeconds = Services.Hub.Interval.TotalSeconds,
            Alerts = Services.Alerts.Log,
            RamTest = Services.RamHistory.Load().LastOrDefault(),
            CpuStress = Services.CpuHistory.Load().TakeLast(5).ToList(),
            StorageBenchmarks = Services.StorageHistory.Load().TakeLast(5).ToList(),
            Benchmarks = Services.BenchmarkHistory.Load().TakeLast(12).ToList(),
            GpuStress = Services.GpuStressHistory.Load().TakeLast(5).ToList(),
            VramTest = Services.VramHistory.Load().LastOrDefault(),
            DriverScan = IncludeDrivers ? Services.LastDriverScan ??= Drivers.DriverInventory.Collect() : null,
            DriverUpdates = Services.LastDriverUpdateSearch,
            BottleneckSession = SelectedSession?.Session,
            Limitations = Services.Hub.Limitations,
            TemperatureUnit = TempUnit,
        };
        var options = new ReportOptions
        {
            IncludeInventory = IncludeInventory,
            IncludeSensors = IncludeSensors,
            IncludeAlerts = IncludeAlerts,
            IncludeRamTest = IncludeRamTest,
            IncludeStability = IncludeStability,
            IncludeStorage = IncludeStorage,
            IncludeBottleneck = IncludeBottleneck,
            IncludeDrivers = IncludeDrivers,
            IncludeIdentifiers = IncludeIdentifiers,
        };
        return ReportBuilder.Build(inputs, options);
    }
}

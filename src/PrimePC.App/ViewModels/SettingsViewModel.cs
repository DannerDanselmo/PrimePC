using System.Collections.ObjectModel;
using System.Globalization;
using System.IO.Compression;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

public sealed record Choice<T>(T Value, string Label);

/// <summary>Regra de alerta editável na tela de configurações.</summary>
public sealed partial class AlertRuleItem : ObservableObject
{
    private readonly Action _changed;

    public AlertRuleItem(AlertRule rule, Action changed)
    {
        Rule = rule;
        _changed = changed;
        _enabled = rule.Enabled;
        _comparisonIndex = rule.Comparison == AlertComparison.Above ? 0 : 1;
        _threshold = rule.Threshold.ToString("0.##", CultureInfo.CurrentCulture);
        _duration = rule.MinDurationSeconds;
    }

    public AlertRule Rule { get; }
    public string Sensor => $"{Rule.SensorName} · {Rule.HardwareName}";
    public string Unit => SensorUnits.UnitFor(Rule.Kind);

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _comparisonIndex;
    [ObservableProperty] private string _threshold;
    [ObservableProperty] private int _duration;

    partial void OnEnabledChanged(bool value)
    {
        Rule.Enabled = value;
        _changed();
    }

    partial void OnComparisonIndexChanged(int value)
    {
        Rule.Comparison = value == 0 ? AlertComparison.Above : AlertComparison.Below;
        _changed();
    }

    partial void OnThresholdChanged(string value)
    {
        if (float.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var v))
        {
            Rule.Threshold = v;
            _changed();
        }
    }

    partial void OnDurationChanged(int value)
    {
        Rule.MinDurationSeconds = Math.Clamp(value, 0, 3600);
        _changed();
    }
}

public sealed partial class SettingsViewModel : PageViewModel
{
    private bool _loading = true;

    public SettingsViewModel(AppServices services) : base(services)
    {
        var s = services.Settings;
        _selectedTheme = ThemeOptions.First(o => o.Value == s.Theme);
        _selectedInterval = IntervalOptions.FirstOrDefault(o => o.Value == s.UpdateIntervalMs) ?? IntervalOptions[2];
        _selectedHistory = HistoryOptions.FirstOrDefault(o => o.Value == s.HistoryMinutes) ?? HistoryOptions[1];
        _selectedTemperatureUnit = TemperatureOptions.First(o => o.Value == s.TemperatureUnit);
        _alertOnCritical = s.AlertOnCriticalState;
        _alertSound = s.AlertSound;
        LoadRules();
        _loading = false;
    }

    public override string Title => "Configurações";
    public override string Subtitle => "Aparência, atualização, alertas, dados locais e informações de compatibilidade";
    public override string Glyph => "";

    public IReadOnlyList<Choice<ThemeChoice>> ThemeOptions { get; } = new[]
    {
        new Choice<ThemeChoice>(ThemeChoice.System, "Seguir o Windows"),
        new Choice<ThemeChoice>(ThemeChoice.Light, "Claro"),
        new Choice<ThemeChoice>(ThemeChoice.Dark, "Escuro"),
    };

    public IReadOnlyList<Choice<int>> IntervalOptions { get; } = new[]
    {
        new Choice<int>(250, "250 ms"), new Choice<int>(500, "500 ms"), new Choice<int>(1000, "1 segundo"),
        new Choice<int>(2000, "2 segundos"), new Choice<int>(5000, "5 segundos"), new Choice<int>(10000, "10 segundos"),
    };

    public IReadOnlyList<Choice<int>> HistoryOptions { get; } = new[]
    {
        new Choice<int>(5, "5 minutos"), new Choice<int>(10, "10 minutos"), new Choice<int>(30, "30 minutos"), new Choice<int>(60, "1 hora"),
    };

    public IReadOnlyList<Choice<TemperatureUnit>> TemperatureOptions { get; } = new[]
    {
        new Choice<TemperatureUnit>(TemperatureUnit.Celsius, "Celsius (°C)"),
        new Choice<TemperatureUnit>(TemperatureUnit.Fahrenheit, "Fahrenheit (°F)"),
    };

    public ObservableCollection<AlertRuleItem> Rules { get; } = new();
    public ObservableCollection<string> Limitations { get; } = new();
    public IReadOnlyList<string> ComparisonOptions { get; } = new[] { "acima de", "abaixo de" };
    public string DataFolder => AppPaths.DataRoot;
    public string Version => Reports.ReportBuilder.AppVersion;
    public string AccessText => $"Administrador: {(PrivilegeInfo.IsAdministrator ? "sim" : "não")} · Driver PawnIO: " +
                                $"{(PrivilegeInfo.IsPawnIoInstalled ? "instalado" : "não instalado")} · Fontes de sensores: " +
                                string.Join(", ", Services.Hub.SourceNames);

    [ObservableProperty] private Choice<ThemeChoice> _selectedTheme;
    [ObservableProperty] private Choice<int> _selectedInterval;
    [ObservableProperty] private Choice<int> _selectedHistory;
    [ObservableProperty] private Choice<TemperatureUnit> _selectedTemperatureUnit;
    [ObservableProperty] private bool _alertOnCritical;
    [ObservableProperty] private bool _alertSound;
    [ObservableProperty] private string _sensorSummary = "";

    partial void OnSelectedThemeChanged(Choice<ThemeChoice> value)
    {
        if (_loading)
            return;
        Services.Settings.Theme = value.Value;
        ThemeService.Apply(value.Value);
        Services.SaveSettings();
    }

    // ---------------- Inicialização e ícone perto do relógio ----------------

    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private string _startupStatus = "";
    [ObservableProperty] private SensorState _startupState = SensorState.Normal;
    private bool _loadingStartup;

    private void LoadStartup()
    {
        _loadingStartup = true;
        CloseToTray = Services.Settings.CloseToTray;
        StartWithWindows = StartupTask.Exists();
        Services.Settings.StartWithWindows = StartWithWindows;
        StartupStatus = StartWithWindows
            ? "O PrimePC abre sozinho perto do relógio quando você entra no Windows."
            : "O PrimePC só abre quando você o abrir.";
        StartupState = SensorState.Normal;
        _loadingStartup = false;
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loadingStartup)
            return;
        Services.Settings.CloseToTray = value;
        Services.SaveSettings();
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loadingStartup)
            return;
        var (ok, message) = value ? StartupTask.Create() : StartupTask.Delete();
        Log.Info($"Iniciar com o Windows = {value}: {message}");
        StartupStatus = message;
        StartupState = ok ? SensorState.Normal : SensorState.Warning;
        if (!ok)
        {
            _loadingStartup = true;
            StartWithWindows = !value;
            _loadingStartup = false;
        }

        Services.Settings.StartWithWindows = StartWithWindows;
        // Quem inicia com o Windows quase sempre quer que fechar a janela o mantenha rodando.
        if (ok && value && !CloseToTray)
            CloseToTray = true;
        Services.SaveSettings();
    }

    partial void OnSelectedIntervalChanged(Choice<int> value) => ApplySensorSetting(() => Services.Settings.UpdateIntervalMs = value.Value);
    partial void OnSelectedHistoryChanged(Choice<int> value) => ApplySensorSetting(() => Services.Settings.HistoryMinutes = value.Value);
    partial void OnSelectedTemperatureUnitChanged(Choice<TemperatureUnit> value) => ApplySensorSetting(() => Services.Settings.TemperatureUnit = value.Value);
    partial void OnAlertOnCriticalChanged(bool value) => ApplySensorSetting(() => Services.Settings.AlertOnCriticalState = value);
    partial void OnAlertSoundChanged(bool value) => ApplySensorSetting(() => Services.Settings.AlertSound = value);

    private void ApplySensorSetting(Action change)
    {
        if (_loading)
            return;
        change();
        Services.ApplySensorSettings();
        Services.SaveSettings();
    }

    public override void OnActivated()
    {
        LoadStartup();
        LoadRules();
        Limitations.Clear();
        foreach (var l in Services.Hub.Limitations)
            Limitations.Add(l);
        UpdateSensorSummary();
        OnPropertyChanged(nameof(AccessText));
    }

    public override void OnSnapshot(SensorSnapshot snapshot)
    {
        UpdateSensorSummary();
        if (Limitations.Count == 0 && Services.Hub.Limitations.Count > 0)
        {
            foreach (var l in Services.Hub.Limitations)
                Limitations.Add(l);
        }
    }

    private void UpdateSensorSummary()
    {
        var stats = Services.Hub.GetSessionStats();
        if (stats.Count == 0)
        {
            SensorSummary = "Aguardando a primeira leitura dos sensores…";
            return;
        }

        int withData = stats.Count(s => s.SampleCount > 0);
        var byCategory = stats.Where(s => s.SampleCount > 0).GroupBy(s => s.Descriptor.Category)
            .Select(g => $"{SensorUnits.CategoryDisplayName(g.Key)}: {g.Count()}");
        SensorSummary = $"{stats.Count} sensores detectados, {withData} com leituras válidas nesta sessão ({string.Join(" · ", byCategory)}).";
    }

    private void LoadRules()
    {
        Rules.Clear();
        foreach (var r in Services.Settings.AlertRules)
            Rules.Add(new AlertRuleItem(r, SaveRules));
    }

    private void SaveRules()
    {
        Services.Alerts.SetRules(Services.Settings.AlertRules);
        Services.SaveSettings();
    }

    [RelayCommand]
    private void RemoveRule(AlertRuleItem item)
    {
        Services.Settings.AlertRules.Remove(item.Rule);
        Rules.Remove(item);
        SaveRules();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        Shell.Open(AppPaths.DataRoot);
    }

    [RelayCommand]
    private void OpenLog() => Shell.Open(Log.CurrentFile);

    [RelayCommand]
    private void ExportData()
    {
        var path = Shell.SaveFile("Exportar dados locais", "Arquivo ZIP (*.zip)|*.zip", $"dados-primepc-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (path is null)
            return;
        try
        {
            if (File.Exists(path))
                File.Delete(path);
            ZipFile.CreateFromDirectory(AppPaths.DataRoot, path, CompressionLevel.Optimal, includeBaseDirectory: false);
            Shell.Info($"Dados exportados para:\n{path}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao exportar dados", ex);
            Shell.Error($"Não foi possível exportar os dados: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DeleteHistory()
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info("Aguarde o término do teste em andamento.");
            return;
        }

        if (!Shell.Confirm(
                "Apagar todo o histórico local?\n\nSerão removidos: sessões de análise de gargalo, resultados de testes e benchmarks e logs técnicos. " +
                "As configurações (tema, favoritos, alertas) são mantidas. Esta ação não pode ser desfeita."))
            return;
        try
        {
            foreach (var dir in new[] { AppPaths.Sessions, AppPaths.Results })
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }

            if (Directory.Exists(AppPaths.Logs))
            {
                foreach (var f in Directory.GetFiles(AppPaths.Logs).Where(f => !f.Equals(Log.CurrentFile, StringComparison.OrdinalIgnoreCase)))
                    File.Delete(f);
            }

            Services.Alerts.ClearLog();
            Services.LastBottleneckSession = null;
            Shell.Info("Histórico apagado. Reabra as abas para ver as listas atualizadas.");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao apagar histórico", ex);
            Shell.Error($"Não foi possível apagar tudo: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenPawnIoSite() => Shell.Open(PrivilegeInfo.PawnIoWebsite);
}

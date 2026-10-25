using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.Sensors;
using QRCoder;

namespace PrimePC.App.ViewModels;

/// <summary>Sub-aba da página Jogos (usa o mesmo estilo de abas das outras telas).</summary>
public sealed partial class GameTab : ObservableObject
{
    public GameTab(string label, string glyph)
    {
        Label = label;
        Glyph = glyph;
    }

    public string Label { get; }
    public string Glyph { get; }
    public bool IsVisible => true;
    public SensorState Worst => SensorState.Normal;

    /// <summary>Número exibido na aba (nulo = sem número).</summary>
    [ObservableProperty] private int? _count;
}

// ---------------- Benchmark ----------------

public sealed class BenchmarkItem
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public BenchmarkItem(GameBenchmarkResult r, GameBenchmarkResult? previous)
    {
        Result = r;
        Game = r.Game;
        DateText = r.StartedAt.ToString("dd/MM/yyyy HH:mm", C);
        AvgText = r.AverageFps.ToString("0", C);
        Low1Text = r.Low1Fps?.ToString("0", C) ?? "—";
        Low01Text = r.Low01Fps?.ToString("0", C) ?? "—";
        StuttersText = $"{r.Stutters}";
        WorstText = $"{r.MaxFrameTimeMs.ToString("0", C)} ms";
        Summary = $"{AvgText} FPS · 1% {Low1Text} · 0,1% {Low01Text}";
        Detail = string.Join(" · ", new[]
        {
            $"{r.Frames:N0} quadros em {r.DurationSeconds.ToString("0", C)} s",
            r.Api,
            r.GpuName is null ? null : r.GpuName + (r.GpuDriver is null ? "" : $" (driver {r.GpuDriver})"),
            r.GpuLoadAverage is { } g ? $"GPU {g:0} %" : null,
            r.CpuLoadAverage is { } cpu ? $"CPU {cpu:0} %" : null,
            r.GpuTempMax is { } gt ? $"GPU máx. {gt:0} °C" : null,
            r.CpuTempMax is { } ct ? $"CPU máx. {ct:0} °C" : null,
        }.Where(s => s is not null));
        if (previous is not null && previous.AverageFps > 0)
        {
            double delta = (r.AverageFps - previous.AverageFps) / previous.AverageFps * 100;
            DeltaText = $"{(delta >= 0 ? "+" : "")}{delta.ToString("0.#", C)} % vs {previous.StartedAt.ToString("dd/MM HH:mm", C)}";
            DeltaState = delta >= 3 ? SensorState.Normal : delta <= -3 ? SensorState.Warning : SensorState.Unavailable;
        }
    }

    public GameBenchmarkResult Result { get; }
    public string Game { get; }
    public string DateText { get; }
    public string AvgText { get; }
    public string Low1Text { get; }
    public string Low01Text { get; }
    public string StuttersText { get; }
    public string WorstText { get; }
    public string Summary { get; }
    public string Detail { get; }
    public string DeltaText { get; } = "primeiro deste jogo";
    public SensorState DeltaState { get; } = SensorState.Unavailable;
    public double[] Graph => Result.FrameTimeGraph;
}

public sealed partial class BenchmarkPanel : ObservableObject
{
    private readonly AppServices _services;

    public BenchmarkPanel(AppServices services)
    {
        _services = services;
        Service = services.Benchmark!;
        _selectedDuration = DurationOptions.FirstOrDefault(o => o.Value == services.Settings.BenchmarkSeconds) ?? DurationOptions[1];
        _selectedHotkey = HotkeyOptions.First(o => o.Value == services.Settings.BenchmarkHotkey);
        Service.Completed += (_, _) => Load();
        Load();
    }

    public GameBenchmarkService Service { get; }
    public ObservableCollection<BenchmarkItem> Results { get; } = new();
    public event EventHandler? CountChanged;

    public IReadOnlyList<Choice<int>> DurationOptions { get; } = new[]
    {
        new Choice<int>(30, "30 segundos"), new Choice<int>(60, "1 minuto"), new Choice<int>(120, "2 minutos"),
        new Choice<int>(180, "3 minutos"), new Choice<int>(300, "5 minutos"),
    };

    public IReadOnlyList<Choice<OverlayHotkey>> HotkeyOptions { get; } =
        Enum.GetValues<OverlayHotkey>().Select(h => new Choice<OverlayHotkey>(h, HotkeyService.Text(h))).ToArray();

    [ObservableProperty] private Choice<int> _selectedDuration;
    [ObservableProperty] private Choice<OverlayHotkey> _selectedHotkey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private BenchmarkItem? _selectedResult;

    public bool HasSelection => SelectedResult is not null;
    public bool HasResults => Results.Count > 0;

    partial void OnSelectedDurationChanged(Choice<int> value)
    {
        _services.Settings.BenchmarkSeconds = value.Value;
        _services.SaveSettings();
    }

    partial void OnSelectedHotkeyChanged(Choice<OverlayHotkey> value)
    {
        _services.Settings.BenchmarkHotkey = value.Value;
        _services.SaveSettings();
        Service.RegisterHotkey();
    }

    [RelayCommand]
    private void Start() => Service.StartWithCountdown();

    [RelayCommand]
    private void Stop() => Service.Stop();

    [RelayCommand]
    private void Delete(BenchmarkItem? item)
    {
        if (item is null || !Shell.Confirm($"Apagar o benchmark de {item.Game} ({item.DateText})?"))
            return;
        Service.History.Save(Service.History.Load().Where(r => r.Id != item.Result.Id));
        Load();
    }

    private void Load()
    {
        var all = Service.History.Load().OrderBy(r => r.StartedAt).ToList();
        var items = new List<BenchmarkItem>();
        for (int i = 0; i < all.Count; i++)
        {
            var previous = all.Take(i).LastOrDefault(p => p.Game.Equals(all[i].Game, StringComparison.OrdinalIgnoreCase));
            items.Add(new BenchmarkItem(all[i], previous));
        }

        Results.Clear();
        foreach (var item in items.AsEnumerable().Reverse())
            Results.Add(item);
        SelectedResult = Results.FirstOrDefault();
        OnPropertyChanged(nameof(HasResults));
        CountChanged?.Invoke(this, EventArgs.Empty);
    }
}

// ---------------- Sessões de jogo ----------------

public sealed class SessionRow
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public SessionRow(GameSessionRecord r)
    {
        Record = r;
        DateText = r.StartedAt.ToString("dd/MM/yyyy HH:mm", C);
        DurationText = SensorUnits.FormatDuration(TimeSpan.FromSeconds(r.ActiveSeconds));
        FpsText = r.AverageFps is { } f ? $"{f.ToString("0", C)} FPS" + (r.LowFps is { } l ? $" (mín. {l.ToString("0", C)})" : "") : "—";
        CpuText = r.CpuTempMax is { } c ? $"{c:0} °C máx. · {r.CpuTempAverage:0} méd." : "—";
        GpuText = r.GpuTempMax is { } g ? $"{g:0} °C máx. · {r.GpuTempAverage:0} méd." + (r.GpuHotSpotMax is { } h ? $" · hot spot {h:0}" : "") : "—";
    }

    public GameSessionRecord Record { get; }
    public string Game => Record.Game;
    public string DateText { get; }
    public string DurationText { get; }
    public string FpsText { get; }
    public string CpuText { get; }
    public string GpuText { get; }
}

public sealed partial class SessionsPanel : ObservableObject
{
    private readonly AppServices _services;
    private List<GameSessionRecord> _all = new();

    public SessionsPanel(AppServices services)
    {
        _services = services;
        Recorder = services.GameSessions!;
        _recordSessions = services.Settings.RecordGameSessions;
        Recorder.Saved += (_, _) => Load();
        Load();
    }

    public GameSessionRecorder Recorder { get; }
    public ObservableCollection<SessionRow> Rows { get; } = new();
    public ObservableCollection<string> Games { get; } = new();
    public event EventHandler? CountChanged;

    [ObservableProperty] private bool _recordSessions;
    [ObservableProperty] private string? _selectedGame;
    [ObservableProperty] private ChartSeries? _gpuSeries;
    [ObservableProperty] private ChartSeries? _cpuSeries;
    [ObservableProperty] private string _trendText = "";
    [ObservableProperty] private SensorState _trendState = SensorState.Unavailable;
    [ObservableProperty] private string _currentText = "";

    public bool HasRows => Rows.Count > 0;

    partial void OnRecordSessionsChanged(bool value)
    {
        _services.Settings.RecordGameSessions = value;
        _services.SaveSettings();
    }

    partial void OnSelectedGameChanged(string? value) => ShowGame();

    /// <summary>Atualiza o texto da sessão em andamento (chamado pela página).</summary>
    public void Refresh()
    {
        var game = _services.Games?.State;
        CurrentText = !RecordSessions ? "Registro desligado."
            : game is null || !game.Measuring ? "O registro de sessões exige abrir o PrimePC como administrador."
            : Recorder.CurrentGame is { } g ? $"Sessão em andamento: {g} · {SensorUnits.FormatDuration(Recorder.CurrentDuration)} (salva quando tiver 2 min ou mais)"
            : "Nenhuma sessão em andamento. Ela começa sozinha quando um jogo fica em primeiro plano.";
    }

    [RelayCommand]
    private void Delete(SessionRow? row)
    {
        if (row is null || !Shell.Confirm($"Apagar a sessão de {row.Game} ({row.DateText})?"))
            return;
        Recorder.History.Save(_all.Where(r => r.Id != row.Record.Id));
        Load();
    }

    private void Load()
    {
        _all = Recorder.History.Load().OrderBy(r => r.StartedAt).ToList();
        var games = _all.GroupBy(r => r.Game, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Max(r => r.StartedAt)).Select(g => g.Key).ToList();
        string? keep = SelectedGame;
        Games.Clear();
        foreach (var g in games)
            Games.Add(g);
        SelectedGame = keep is not null && games.Contains(keep, StringComparer.OrdinalIgnoreCase) ? keep : games.FirstOrDefault();
        ShowGame();
        Refresh();
        CountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ShowGame()
    {
        Rows.Clear();
        var sessions = _all.Where(r => SelectedGame is not null && r.Game.Equals(SelectedGame, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var s in sessions.AsEnumerable().Reverse())
            Rows.Add(new SessionRow(s));
        OnPropertyChanged(nameof(HasRows));
        GpuSeries = Series(sessions, s => s.GpuTempMax);
        CpuSeries = Series(sessions, s => s.CpuTempMax);
        (TrendText, TrendState) = Trend(sessions);
    }

    public int Count => _all.Count;

    private static ChartSeries Series(List<GameSessionRecord> s, Func<GameSessionRecord, float?> sel)
    {
        var pts = s.Where(x => sel(x) is not null).ToList();
        return new ChartSeries(pts.Select(x => x.StartedAt).ToArray(), pts.Select(x => (double)sel(x)!.Value).ToArray());
    }

    /// <summary>Compara as temperaturas máximas das 3 primeiras sessões com as 3 últimas do mesmo jogo.</summary>
    public static (string, SensorState) Trend(IReadOnlyList<GameSessionRecord> sessions)
    {
        if (sessions.Count < 6)
            return ($"Com 6 sessões ou mais deste jogo, o PrimePC compara as temperaturas para mostrar se o PC está esquentando mais com o tempo ({sessions.Count} até agora).",
                SensorState.Unavailable);
        static float? Avg(IEnumerable<float?> v) => v.Where(x => x is not null).Select(x => x!.Value).DefaultIfEmpty(float.NaN).Average() is var a && !float.IsNaN(a) ? a : null;
        var first = sessions.Take(3).ToList();
        var last = sessions.TakeLast(3).ToList();
        float? gpu = Avg(last.Select(s => s.GpuTempMax)) - Avg(first.Select(s => s.GpuTempMax));
        float? cpu = Avg(last.Select(s => s.CpuTempMax)) - Avg(first.Select(s => s.CpuTempMax));
        string Part(string name, float? d) => d is null ? $"{name}: sem dados" : $"{name} {(d >= 0 ? "+" : "")}{d:0.#} °C";
        string text = $"Últimas 3 sessões comparadas às 3 primeiras: {Part("GPU", gpu)}, {Part("CPU", cpu)}.";
        bool hotter = gpu >= 5 || cpu >= 5;
        return hotter
            ? (text + " O PC está esquentando mais no mesmo jogo. Antes de suspeitar do hardware, considere a temperatura do ambiente e as configurações do jogo; " +
               "se persistir, verifique poeira, fluxo de ar e, se necessário, a pasta térmica.", SensorState.Warning)
            : (text + " Sem aumento relevante de temperatura.", SensorState.Normal);
    }
}

// ---------------- Painel no celular ----------------

public sealed partial class PhonePanel : ObservableObject
{
    private readonly AppServices _services;

    public PhonePanel(AppServices services)
    {
        _services = services;
        Server = services.Phone!;
        _enabled = Server.Settings.Enabled;
        _port = Server.Settings.Port;
        Server.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PhoneDashboardServer.Address))
                UpdateQr();
        };
        UpdateQr();
    }

    public PhoneDashboardServer Server { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private int _port;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQr))]
    private BitmapImage? _qrCode;

    public bool HasQr => QrCode is not null;

    partial void OnEnabledChanged(bool value)
    {
        Server.Settings.Enabled = value;
        _services.SaveSettings();
        Server.Apply();
    }

    partial void OnPortChanged(int value)
    {
        if (value is < 1024 or > 65535 || value == Server.Settings.Port)
            return;
        Server.Settings.Port = value;
        _services.SaveSettings();
        if (Server.Settings.Enabled)
            Server.Apply();
    }

    [RelayCommand]
    private void Copy()
    {
        if (Server.Address is { } a)
            Clipboard.SetText(a);
    }

    [RelayCommand]
    private void Open()
    {
        if (Server.Address is { } a)
            Shell.Open(a);
    }

    [RelayCommand]
    private void NewCode()
    {
        if (Shell.Confirm("Gerar um novo código de acesso? O endereço antigo deixa de funcionar e você precisa ler o QR code de novo no celular."))
            Server.RegenerateCode();
    }

    private void UpdateQr()
    {
        if (Server.Address is not { } address)
        {
            QrCode = null;
            return;
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(address, QRCodeGenerator.ECCLevel.Q);
        byte[] png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        using (var ms = new MemoryStream(png))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
        }

        image.Freeze();
        QrCode = image;
    }
}

using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.Diagnostics;
using PrimePC.Gaming;
using PrimePC.Sensors;
using Texts = PrimePC.Diagnostics.LikelihoodText;

namespace PrimePC.App.ViewModels;

public sealed record GpuOption(string? Id, string Name);

public sealed class FindingItem
{
    public FindingItem(FactorFinding f)
    {
        Likelihood = f.Likelihood;
        LikelihoodLabel = Texts.Display(f.Likelihood);
        Title = f.Title;
        Factor = Texts.FactorName(f.Factor);
        Evidence = f.Evidence;
        ShareText = f.Likelihood == Likelihood.Inconclusive ? "" : $"{f.ShareOfTime:P0} do tempo sob análise";
    }

    public Likelihood Likelihood { get; }
    public string LikelihoodLabel { get; }
    public string Title { get; }
    public string Factor { get; }
    public string ShareText { get; }
    public IReadOnlyList<string> Evidence { get; }
}

public sealed record MetricRow(string Name, string Average, string P95, string Max);

public sealed partial class SessionItem : ObservableObject
{
    public SessionItem(BottleneckSession session)
    {
        Session = session;
    }

    public BottleneckSession Session { get; }
    public string Name => Session.DisplayName;
    public string Headline => Session.Report?.Headline ?? "Não analisada";

    [ObservableProperty]
    private bool _isChecked;
}

public sealed partial class BottleneckViewModel : PageViewModel
{
    private readonly object _sync = new();
    private BottleneckSession? _recording;
    private string? _recordingGpuId;
    private FrameCapture? _capture;
    private readonly Dictionary<int, int> _gameTicks = new();

    public BottleneckViewModel(AppServices services) : base(services)
    {
        LoadSessions();
    }

    public override string Title => "Gargalo";
    public override string Subtitle => "Analisa o que limita o desempenho combinando telemetria ao longo do tempo";
    public override string Glyph => "";

    public ObservableCollection<GpuOption> GpuOptions { get; } = new();
    public ObservableCollection<SessionItem> Sessions { get; } = new();
    public ObservableCollection<FindingItem> Findings { get; } = new();
    public ObservableCollection<MetricRow> Metrics { get; } = new();
    public ObservableCollection<string> Conditions { get; } = new();
    public ObservableCollection<string> Limitations { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    [ObservableProperty] private string _workload = "";
    [ObservableProperty] private string _resolution = "";
    [ObservableProperty] private string _settingsNotes = "";
    [ObservableProperty] private GpuOption? _selectedGpu;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRecording;

    [ObservableProperty] private string _elapsedText = "0 s";
    [ObservableProperty] private int _sampleCount;
    [ObservableProperty] private double _minimumProgress;
    [ObservableProperty] private string _liveGpu = "—";
    [ObservableProperty] private string _liveThread = "—";
    [ObservableProperty] private string _liveCpu = "—";
    [ObservableProperty] private string _liveVram = "—";
    [ObservableProperty] private string _liveFps = "—";
    [ObservableProperty] private string _liveFpsHint = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    private BottleneckReport? _report;

    [ObservableProperty] private string? _reportSessionName;
    [ObservableProperty] private ChartSeries? _gpuLoadSeries;
    [ObservableProperty] private ChartSeries? _threadSeries;
    [ObservableProperty] private DataView? _comparison;

    public bool IsIdle => !IsRecording;
    public bool HasReport => Report is not null;

    public override void OnActivated()
    {
        var latest = Services.Hub.Latest;
        var map = Services.KeyMap;
        var current = SelectedGpu?.Id ?? Services.Settings.PreferredGpuId;
        GpuOptions.Clear();
        GpuOptions.Add(new GpuOption(null, "Automático (GPU com mais VRAM)"));
        foreach (var g in map.Gpus.OrderByDescending(g => latest.ValueOf(g.VramTotal) ?? 0))
            GpuOptions.Add(new GpuOption(g.HardwareId, g.Name + (g.LikelyIntegrated ? " (integrada)" : "")));
        SelectedGpu = GpuOptions.FirstOrDefault(o => o.Id == current) ?? GpuOptions[0];
    }

    public override void OnSnapshot(SensorSnapshot snapshot)
    {
        if (!IsRecording)
            return;
        TelemetrySample? last;
        lock (_sync)
        {
            SampleCount = _recording?.Samples.Count ?? 0;
            last = _recording?.Samples.LastOrDefault();
            if (_recording is { Samples.Count: > 1 } r)
            {
                var span = r.Samples[^1].Time - r.Samples[0].Time;
                ElapsedText = SensorUnits.FormatDuration(span);
                MinimumProgress = Math.Min(100, span.TotalSeconds / 30 * 100);
            }
        }

        if (last is null)
            return;
        LiveGpu = last.GpuLoad is { } g ? $"{g:0} %" : "Não disponível";
        LiveThread = last.CpuBusiestThread is { } t ? $"{t:0} %" : "Não disponível";
        LiveCpu = last.CpuTotalLoad is { } c ? $"{c:0} %" : "Não disponível";
        LiveVram = last.VramUsedMB is { } vu && last.VramTotalMB is > 0 and var vt ? $"{vu / vt:P0}" : "Não disponível";
        var game = Services.Games?.State;
        LiveFps = last.Fps is { } fps ? $"{fps:0}" : "—";
        LiveFpsHint = game is null || !game.Measuring
            ? "FPS por quadro exige abrir o PrimePC como administrador."
            : game.Name is { } name ? $"Medindo {name}" + (last.FrameTimeMs is { } ft ? $" · {ft:0.0} ms" : "")
            : "Abra o jogo: o FPS é medido no jogo em primeiro plano.";
        ShowSeries(_recording!);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Start()
    {
        var map = Services.KeyMap;
        string? gpuId = SelectedGpu?.Id;
        var gpu = map.PrimaryGpu(Services.Hub.Latest, gpuId);
        lock (_sync)
        {
            _recording = new BottleneckSession
            {
                Workload = Workload.Trim(),
                Resolution = Resolution.Trim(),
                Settings = SettingsNotes.Trim(),
                CpuName = Services.Inventory?.Cpu.Name ?? map.CpuName,
                GpuName = gpu?.Name,
                StartedAt = DateTime.Now,
                IntervalSeconds = Services.Hub.Interval.TotalSeconds,
            };
        }

        _recordingGpuId = gpuId;
        Services.Settings.PreferredGpuId = gpuId;
        Services.SaveSettings();
        lock (_sync)
            _gameTicks.Clear();
        // Guarda cada quadro de todos os programas; no fim, fica só o do jogo acompanhado durante a gravação.
        _capture = Services.Games?.BeginCapture();
        Services.Hub.SnapshotUpdated += OnHubSnapshot;
        IsRecording = true;
        Report = null;
        ElapsedText = "0 s";
        MinimumProgress = 0;
        LiveFps = "—";
    }

    private void OnHubSnapshot(object? sender, SensorSnapshot snapshot)
    {
        // Thread de coleta: grava mesmo se o usuário estiver em outra aba ou dentro do jogo.
        var sample = TelemetryMapper.Capture(snapshot, Services.KeyMap, _recordingGpuId);
        var game = Services.Games?.State;
        if (game is { Pid: { } pid, Frames: { } frames })
        {
            // FPS medido a cada quadro no jogo acompanhado (substitui o do driver, quando houver).
            sample.Fps = (float)frames.Fps;
            sample.FrameTimeMs = (float)frames.FrameTimeMs;
            lock (_sync)
                _gameTicks[pid] = _gameTicks.GetValueOrDefault(pid) + 1;
        }

        lock (_sync)
            _recording?.Samples.Add(sample);
    }

    /// <summary>Resume os quadros do jogo mais acompanhado durante a gravação.</summary>
    private FrameRecording? FinishCapture()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null || Services.Games is not { } games)
            return null;
        games.EndCapture(capture);

        int? pid;
        lock (_sync)
            pid = _gameTicks.Count == 0 ? null : _gameTicks.MaxBy(kv => kv.Value).Key;
        if (pid is null || capture.Result(pid, Environment.ProcessId) is not { } result)
            return null;

        string? name = games.State.Pid == pid ? games.State.Name : ForegroundProcess.NameOf(pid.Value) ?? games.LastGameName;
        var s = result.Summary;
        return new FrameRecording(
            Game: name,
            Api: result.Source switch
            {
                PresentSource.Dxgi => "DirectX 10/11/12",
                PresentSource.D3D9 => "Direct3D 9",
                _ => "Vulkan/OpenGL",
            },
            Frames: s.Frames,
            DurationSeconds: s.DurationSeconds,
            AverageFps: s.AverageFps,
            Low1Fps: s.Low1Fps,
            Low01Fps: s.Low01Fps,
            MedianFrameTimeMs: s.MedianFrameTimeMs,
            MaxFrameTimeMs: s.MaxFrameTimeMs,
            Stutters: s.Stutters,
            FrameTimeGraph: FrameStatistics.Downsample(result.FrameTimes, 600));
    }

    [RelayCommand(CanExecute = nameof(IsRecording))]
    private void Stop()
    {
        Services.Hub.SnapshotUpdated -= OnHubSnapshot;
        IsRecording = false;
        BottleneckSession session;
        lock (_sync)
        {
            session = _recording!;
            _recording = null;
        }

        session.EndedAt = DateTime.Now;
        try
        {
            session.Frames = FinishCapture();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao resumir os quadros da gravação", ex);
        }

        if (string.IsNullOrWhiteSpace(session.Workload) && session.Frames?.Game is { } game)
            session.Workload = game;
        var limits = Services.Evaluator.CpuTempLimits;
        session.Report = BottleneckAnalyzer.Analyze(session, new BottleneckAnalyzerOptions { CpuTemperatureLimit = limits.Crit - 2 });
        try
        {
            Services.Sessions.Save(session);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar a sessão de gargalo", ex);
        }

        Services.LastBottleneckSession = session;
        Display(session);
        LoadSessions();
    }

    private void Display(BottleneckSession session)
    {
        var report = session.Report ?? BottleneckAnalyzer.Analyze(session);
        Report = report;
        ReportSessionName = session.DisplayName;
        Findings.Clear();
        foreach (var f in report.Findings)
            Findings.Add(new FindingItem(f));
        Metrics.Clear();
        var c = CultureInfo.CurrentCulture;
        foreach (var m in report.Metrics)
        {
            string F(float? v) => v is null ? "Não disponível" : $"{v.Value.ToString(m.Unit is "MHz" or "MB" ? "0" : "0.#", c)} {m.Unit}";
            Metrics.Add(new MetricRow(m.Name, F(m.Average), F(m.P95), F(m.Max)));
        }

        Replace(Conditions, report.Conditions);
        Replace(Limitations, report.Limitations);
        Replace(Suggestions, report.Suggestions);
        ShowFrames(report, session.Frames ?? report.Frames);
        ShowSeries(session);
    }

    // ---------------- Fluidez (FPS medido a cada quadro) ----------------

    [ObservableProperty] private bool _hasFrames;
    [ObservableProperty] private string _smoothnessTitle = "";
    [ObservableProperty] private SensorState _smoothnessState = SensorState.Normal;
    [ObservableProperty] private string _smoothnessDetail = "";
    [ObservableProperty] private string _avgFpsText = "—";
    [ObservableProperty] private string _low1Text = "—";
    [ObservableProperty] private string _low01Text = "—";
    [ObservableProperty] private string _stuttersText = "—";
    [ObservableProperty] private string _worstFrameText = "—";
    [ObservableProperty] private string _framesSource = "";
    [ObservableProperty] private double[] _frameGraph = Array.Empty<double>();

    private void ShowFrames(BottleneckReport report, FrameRecording? f)
    {
        HasFrames = f is not null;
        if (f is null)
            return;
        var (smoothness, detail) = report.Smoothness == Smoothness.Unknown && report.SmoothnessDetail is null
            ? BottleneckAnalyzer.AssessSmoothness(f)
            : (report.Smoothness, report.SmoothnessDetail);
        SmoothnessTitle = smoothness switch
        {
            Smoothness.Smooth => "Jogo fluido",
            Smoothness.SomeStutter => "Algumas travadas",
            Smoothness.Stuttery => "Muitas travadas",
            _ => "Fluidez não avaliada",
        };
        SmoothnessState = smoothness switch
        {
            Smoothness.Smooth => SensorState.Normal,
            Smoothness.SomeStutter => SensorState.Warning,
            Smoothness.Stuttery => SensorState.Critical,
            _ => SensorState.Unavailable,
        };
        SmoothnessDetail = detail ?? "";
        var c = CultureInfo.CurrentCulture;
        AvgFpsText = f.AverageFps.ToString("0", c);
        Low1Text = f.Low1Fps?.ToString("0", c) ?? "—";
        Low01Text = f.Low01Fps?.ToString("0", c) ?? "—";
        StuttersText = $"{f.Stutters} ({f.StuttersPerMinute.ToString("0.#", c)}/min)";
        WorstFrameText = $"{f.MaxFrameTimeMs.ToString("0", c)} ms";
        FramesSource = $"{f.Game ?? "Jogo"} · {f.Api} · {f.Frames:N0} quadros em {SensorUnits.FormatDuration(TimeSpan.FromSeconds(f.DurationSeconds))}";
        FrameGraph = f.FrameTimeGraph;
    }

    private void ShowSeries(BottleneckSession session)
    {
        List<TelemetrySample> samples;
        lock (_sync)
            samples = session.Samples.ToList();
        GpuLoadSeries = Series(samples, x => x.GpuLoad);
        ThreadSeries = Series(samples, x => x.CpuBusiestThread);
    }

    private static ChartSeries Series(List<TelemetrySample> samples, Func<TelemetrySample, float?> sel)
    {
        var pts = samples.Where(x => sel(x) is not null).ToList();
        return new ChartSeries(pts.Select(x => x.Time).ToArray(), pts.Select(x => (double)sel(x)!.Value).ToArray());
    }

    [RelayCommand]
    private void OpenSession(SessionItem item)
    {
        Comparison = null;
        Display(item.Session);
        Services.LastBottleneckSession = item.Session;
    }

    [RelayCommand]
    private void DeleteSession(SessionItem item)
    {
        if (!Shell.Confirm($"Apagar a sessão \"{item.Name}\"? Esta ação não pode ser desfeita."))
            return;
        Services.Sessions.Delete(item.Session.Id);
        if (Services.LastBottleneckSession?.Id == item.Session.Id)
            Services.LastBottleneckSession = null;
        LoadSessions();
    }

    [RelayCommand]
    private void Compare()
    {
        var selected = Sessions.Where(s => s.IsChecked).Select(s => s.Session).ToList();
        if (selected.Count < 2)
        {
            Shell.Info("Marque pelo menos duas sessões na lista para comparar.");
            return;
        }

        var table = SessionComparer.Compare(selected);
        var dt = new DataTable();
        dt.Columns.Add("Métrica");
        for (int i = 0; i < table.Headers.Count; i++)
            dt.Columns.Add($"S{i + 1}").Caption = table.Headers[i];
        foreach (var row in table.Rows)
            dt.Rows.Add(new object[] { row.Metric }.Concat(row.Values).ToArray());
        ComparisonHeaders = table.Headers;
        Comparison = dt.DefaultView;
    }

    /// <summary>Títulos das colunas da comparação (as colunas do DataTable usam nomes curtos).</summary>
    [ObservableProperty] private IReadOnlyList<string>? _comparisonHeaders;

    [RelayCommand]
    private void CloseComparison() => Comparison = null;

    private void LoadSessions()
    {
        Sessions.Clear();
        foreach (var s in Services.Sessions.LoadAll())
            Sessions.Add(new SessionItem(s));
        Services.LastBottleneckSession ??= Sessions.FirstOrDefault()?.Session;
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        target.Clear();
        foreach (var i in items)
            target.Add(i);
    }
}

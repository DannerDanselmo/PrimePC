using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PrimePC.Gaming;
using PrimePC.Testing;

namespace PrimePC.App.Services;

/// <summary>Resultado de um benchmark de jogo (salvo no histórico local).</summary>
public sealed class GameBenchmarkResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Game { get; set; } = "";
    public string Api { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public double DurationSeconds { get; set; }
    public int Frames { get; set; }
    public double AverageFps { get; set; }
    public double? Low1Fps { get; set; }
    public double? Low01Fps { get; set; }
    public double MedianFrameTimeMs { get; set; }
    public double MaxFrameTimeMs { get; set; }
    public int Stutters { get; set; }
    public double[] FrameTimeGraph { get; set; } = Array.Empty<double>();
    public float? CpuLoadAverage { get; set; }
    public float? GpuLoadAverage { get; set; }
    public float? CpuTempMax { get; set; }
    public float? GpuTempMax { get; set; }
    public string? GpuName { get; set; }
    public string? GpuDriver { get; set; }
}

/// <summary>
/// Benchmark de jogo: grava cada quadro do jogo em primeiro plano por um tempo fixo (ou até parar) e calcula FPS médio,
/// 1% low, 0,1% low e travadas. Começa e para pelo botão ou pelo atalho global (padrão Ctrl + Shift + F9).
/// </summary>
public sealed partial class GameBenchmarkService : ObservableObject
{
    private const int HotkeyId = 0x5051;

    private readonly AppServices _services;
    private readonly GameMonitor _games;
    private readonly OverlayService _overlay;
    private readonly HotkeyService _hotkeys;
    private readonly Dictionary<int, int> _gameTicks = new();
    private readonly List<float> _cpuLoad = new(), _gpuLoad = new(), _cpuTemp = new(), _gpuTemp = new();
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private FrameCapture? _capture;
    private DateTime _startedAt;
    private int _countdownLeft;

    public GameBenchmarkService(AppServices services, GameMonitor games, OverlayService overlay, HotkeyService hotkeys)
    {
        _services = services;
        _games = games;
        _overlay = overlay;
        _hotkeys = hotkeys;
        History = new JsonHistoryStore<GameBenchmarkResult>(Path.Combine(AppPaths.Results, "benchmarks-jogos.json"), 300);
        _games.Updated += (_, state) => OnTick(state);
        _countdown.Tick += (_, _) => CountdownTick();
        RegisterHotkey();
    }

    public JsonHistoryStore<GameBenchmarkResult> History { get; }
    public int DurationSeconds => Math.Clamp(_services.Settings.BenchmarkSeconds, 10, 600);

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isCountingDown;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Pronto. Abra o jogo e aperte o atalho, ou clique em Iniciar.";
    [ObservableProperty] private string _hotkeyStatus = "";

    public event EventHandler<GameBenchmarkResult>? Completed;

    public static string Text(OverlayHotkey h) => HotkeyService.Text(h);

    public void RegisterHotkey()
    {
        var key = _services.Settings.BenchmarkHotkey;
        bool ok = _hotkeys.Register(HotkeyId, key, Toggle, SetHotkeyStatus);
        SetHotkeyStatus(ok);
    }

    private void SetHotkeyStatus(bool ok)
    {
        var key = _services.Settings.BenchmarkHotkey;
        HotkeyStatus = key == OverlayHotkey.None ? "Sem atalho de teclado."
            : ok ? $"Atalho {Text(key)}: começa e para o benchmark, inclusive dentro do jogo."
            : $"O atalho {Text(key)} já está sendo usado por outro programa. Escolha outro.";
    }

    /// <summary>Pelo atalho (normalmente dentro do jogo): começa na hora; se já estiver gravando, para.</summary>
    public void Toggle()
    {
        if (IsRunning)
            Finish();
        else if (IsCountingDown)
            CancelCountdown();
        else
            Begin();
    }

    /// <summary>Pelo botão do PrimePC: dá 5 segundos para voltar ao jogo.</summary>
    public void StartWithCountdown()
    {
        if (IsRunning || IsCountingDown || !CheckCanMeasure())
            return;
        _countdownLeft = 5;
        IsCountingDown = true;
        StatusText = $"Volte para o jogo: o benchmark começa em {_countdownLeft} s…";
        _countdown.Start();
    }

    public void Stop()
    {
        if (IsRunning)
            Finish();
        else if (IsCountingDown)
            CancelCountdown();
    }

    private void CancelCountdown()
    {
        _countdown.Stop();
        IsCountingDown = false;
        StatusText = "Benchmark cancelado.";
    }

    private void CountdownTick()
    {
        if (--_countdownLeft > 0)
        {
            StatusText = $"Volte para o jogo: o benchmark começa em {_countdownLeft} s…";
            return;
        }

        _countdown.Stop();
        IsCountingDown = false;
        Begin();
    }

    private bool CheckCanMeasure()
    {
        if (_games.IsMeasuring)
            return true;
        StatusText = "O benchmark mede cada quadro e exige abrir o PrimePC como administrador.";
        _overlay.ShowToast("BENCH: abra o PrimePC como administrador", TimeSpan.FromSeconds(6));
        return false;
    }

    private void Begin()
    {
        if (!CheckCanMeasure() || _games.BeginCapture() is not { } capture)
            return;
        _capture = capture;
        _gameTicks.Clear();
        _cpuLoad.Clear();
        _gpuLoad.Clear();
        _cpuTemp.Clear();
        _gpuTemp.Clear();
        _startedAt = DateTime.Now;
        Progress = 0;
        IsRunning = true;
        _services.RunningTest = "benchmark de jogo";
        StatusText = $"Gravando por {DurationSeconds} s… Jogue normalmente numa cena representativa.";
        _overlay.SetRecording($"● BENCH 0:00 / {Format(DurationSeconds)}");
        Log.Info($"Benchmark de jogo iniciado ({DurationSeconds} s).");
    }

    private void OnTick(GameState state)
    {
        if (!IsRunning)
            return;
        double elapsed = (DateTime.Now - _startedAt).TotalSeconds;
        Progress = Math.Min(100, elapsed / DurationSeconds * 100);
        if (state is { Pid: { } pid, InForeground: true })
        {
            _gameTicks[pid] = _gameTicks.GetValueOrDefault(pid) + 1;
            var snapshot = _services.Hub.Latest;
            var map = _services.KeyMap;
            var gpu = map.PrimaryGpu(snapshot, _services.Settings.PreferredGpuId);
            Add(_cpuLoad, snapshot.ValueOf(map.CpuTotalLoad));
            Add(_gpuLoad, snapshot.ValueOf(gpu?.Load));
            Add(_cpuTemp, snapshot.ValueOf(map.CpuTemperature));
            Add(_gpuTemp, snapshot.ValueOf(gpu?.Temperature));
        }

        _overlay.SetRecording($"● BENCH {Format(elapsed)} / {Format(DurationSeconds)}");
        if (elapsed >= DurationSeconds)
            Finish();
    }

    private void Finish()
    {
        var capture = _capture;
        _capture = null;
        IsRunning = false;
        _services.RunningTest = null;
        _overlay.SetRecording(null);
        if (capture is null)
            return;
        _games.EndCapture(capture);

        int? pid = _gameTicks.Count == 0 ? null : _gameTicks.MaxBy(kv => kv.Value).Key;
        if (pid is null || capture.Result(pid, Environment.ProcessId) is not { Summary.Frames: >= 30 } result)
        {
            StatusText = "Nenhum jogo ficou em primeiro plano durante o benchmark. Deixe o jogo na frente enquanto ele grava.";
            _overlay.ShowToast("BENCH: nenhum jogo medido", TimeSpan.FromSeconds(6));
            Log.Info("Benchmark de jogo sem jogo em primeiro plano.");
            return;
        }

        var s = result.Summary;
        string game = (_games.State.Pid == pid ? _games.State.Name : null) ?? ForegroundProcess.NameOf(pid.Value) ?? _games.LastGameName ?? "Jogo";
        var gpuSet = _services.KeyMap.PrimaryGpu(_services.Hub.Latest, _services.Settings.PreferredGpuId);
        var gpuInfo = _services.Inventory?.Gpus.FirstOrDefault(g => gpuSet is not null && g.Name.Contains(gpuSet.Name, StringComparison.OrdinalIgnoreCase))
                      ?? _services.Inventory?.Gpus.FirstOrDefault();
        var item = new GameBenchmarkResult
        {
            Game = game,
            Api = result.Source switch { PresentSource.Dxgi => "DirectX 10/11/12", PresentSource.D3D9 => "Direct3D 9", _ => "Vulkan/OpenGL" },
            StartedAt = _startedAt,
            DurationSeconds = s.DurationSeconds,
            Frames = s.Frames,
            AverageFps = s.AverageFps,
            Low1Fps = s.Low1Fps,
            Low01Fps = s.Low01Fps,
            MedianFrameTimeMs = s.MedianFrameTimeMs,
            MaxFrameTimeMs = s.MaxFrameTimeMs,
            Stutters = s.Stutters,
            FrameTimeGraph = FrameStatistics.Downsample(result.FrameTimes, 600),
            CpuLoadAverage = Avg(_cpuLoad),
            GpuLoadAverage = Avg(_gpuLoad),
            CpuTempMax = _cpuTemp.Count > 0 ? _cpuTemp.Max() : null,
            GpuTempMax = _gpuTemp.Count > 0 ? _gpuTemp.Max() : null,
            GpuName = gpuSet?.Name ?? gpuInfo?.Name,
            GpuDriver = gpuInfo?.DriverVersion is { } v ? Drivers.DriverInventory.FriendlyVersion(gpuInfo.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ? "NVIDIA" : null, v) : null,
        };

        try
        {
            History.Add(item);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar o benchmark", ex);
        }

        var c = CultureInfo.CurrentCulture;
        string low = item.Low1Fps is { } l ? $" · 1% {l.ToString("0", c)}" : "";
        StatusText = $"Concluído: {game} · {item.AverageFps.ToString("0", c)} FPS de média{low}.";
        _overlay.ShowToast($"BENCH {item.AverageFps.ToString("0", c)} FPS{low}", TimeSpan.FromSeconds(10));
        Log.Info($"Benchmark de jogo: {game} · {item.AverageFps:0.0} FPS · 1% {item.Low1Fps:0.0} · {item.Frames} quadros.");
        Completed?.Invoke(this, item);
    }

    private static void Add(List<float> list, float? value)
    {
        if (value is { } v)
            list.Add(v);
    }

    private static float? Avg(List<float> list) => list.Count > 0 ? list.Average() : null;

    private static string Format(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";
}

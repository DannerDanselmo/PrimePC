using PrimePC.Testing;

namespace PrimePC.App.Services;

/// <summary>Uma partida/sessão de jogo: quanto tempo durou e como CPU e GPU se comportaram.</summary>
public sealed class GameSessionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Game { get; set; } = "";
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }

    /// <summary>Tempo com o jogo em primeiro plano (alt+tab não conta).</summary>
    public double ActiveSeconds { get; set; }

    public double? AverageFps { get; set; }

    /// <summary>FPS mais baixo dos momentos ruins (5% das medições, a cada meio segundo).</summary>
    public double? LowFps { get; set; }

    public float? CpuTempAverage { get; set; }
    public float? CpuTempMax { get; set; }
    public float? GpuTempAverage { get; set; }
    public float? GpuTempMax { get; set; }
    public float? GpuHotSpotMax { get; set; }
    public float? CpuLoadAverage { get; set; }
    public float? GpuLoadAverage { get; set; }
}

/// <summary>
/// Registra sozinho cada sessão de jogo (com o PrimePC aberto e como administrador): começa quando um jogo fica em
/// primeiro plano e termina 90 s depois de ele sair (ou ao fechar o PrimePC). Sessões com menos de 2 minutos são ignoradas.
/// Serve para acompanhar se o PC esquenta mais com o tempo (poeira, pasta térmica).
/// </summary>
public sealed class GameSessionRecorder : IDisposable
{
    public static readonly TimeSpan MinimumDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(90);

    private readonly AppServices _services;
    private readonly GameMonitor _games;
    private Accumulator? _current;

    public GameSessionRecorder(AppServices services, GameMonitor games)
    {
        _services = services;
        _games = games;
        History = new JsonHistoryStore<GameSessionRecord>(Path.Combine(AppPaths.Results, "sessoes-jogo.json"), 1000);
        _games.Updated += (_, state) => OnTick(state);
    }

    public JsonHistoryStore<GameSessionRecord> History { get; }

    /// <summary>Jogo da sessão em andamento (nulo se nenhuma).</summary>
    public string? CurrentGame => _current?.Game;

    public TimeSpan CurrentDuration => TimeSpan.FromSeconds(_current?.ActiveSeconds ?? 0);

    public event EventHandler<GameSessionRecord>? Saved;

    private void OnTick(GameState state)
    {
        if (!_services.Settings.RecordGameSessions)
        {
            _current = null;
            return;
        }

        var now = DateTime.Now;
        if (state is { InForeground: true, Name: { } name })
        {
            if (_current is not null && !_current.Game.Equals(name, StringComparison.OrdinalIgnoreCase))
                Finish();
            _current ??= new Accumulator(name, now);
            var snapshot = _services.Hub.Latest;
            var map = _services.KeyMap;
            var gpu = map.PrimaryGpu(snapshot, _services.Settings.PreferredGpuId);
            _current.Add(now, state.Frames?.Fps, snapshot.ValueOf(map.CpuTemperature), snapshot.ValueOf(gpu?.Temperature),
                snapshot.ValueOf(gpu?.HotSpot), snapshot.ValueOf(map.CpuTotalLoad), snapshot.ValueOf(gpu?.Load));
        }
        else if (_current is not null && now - _current.LastSeen > EndAfter)
        {
            Finish();
        }
    }

    private void Finish()
    {
        var acc = _current;
        _current = null;
        if (acc is null || acc.ActiveSeconds < MinimumDuration.TotalSeconds)
            return;
        var record = acc.ToRecord();
        try
        {
            History.Add(record);
            Log.Info($"Sessão de jogo registrada: {record.Game} · {TimeSpan.FromSeconds(record.ActiveSeconds):h\\:mm\\:ss} · " +
                     $"GPU máx. {record.GpuTempMax:0} °C · CPU máx. {record.CpuTempMax:0} °C.");
            Saved?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao salvar a sessão de jogo", ex);
        }
    }

    /// <summary>Ao fechar o PrimePC, salva a sessão em andamento.</summary>
    public void Dispose() => Finish();

    private sealed class Accumulator(string game, DateTime start)
    {
        private readonly List<double> _fps = new();
        private readonly List<float> _cpuTemp = new(), _gpuTemp = new(), _hotSpot = new(), _cpuLoad = new(), _gpuLoad = new();
        private DateTime? _previous;

        public string Game { get; } = game;
        public DateTime StartedAt { get; } = start;
        public DateTime LastSeen { get; private set; } = start;
        public double ActiveSeconds { get; private set; }

        public void Add(DateTime now, double? fps, float? cpuTemp, float? gpuTemp, float? hotSpot, float? cpuLoad, float? gpuLoad)
        {
            // Conta só o tempo com o jogo na frente; intervalos longos (alt+tab, tela de outro programa) não somam.
            if (_previous is { } p && now - p < TimeSpan.FromSeconds(3))
                ActiveSeconds += (now - p).TotalSeconds;
            _previous = now;
            LastSeen = now;
            if (fps is { } f)
                _fps.Add(f);
            Push(_cpuTemp, cpuTemp);
            Push(_gpuTemp, gpuTemp);
            Push(_hotSpot, hotSpot);
            Push(_cpuLoad, cpuLoad);
            Push(_gpuLoad, gpuLoad);
        }

        public GameSessionRecord ToRecord()
        {
            double? low = null;
            if (_fps.Count >= 20)
            {
                var sorted = _fps.OrderBy(x => x).ToList();
                low = sorted[(int)(sorted.Count * 0.05)];
            }

            return new GameSessionRecord
            {
                Game = Game,
                StartedAt = StartedAt,
                EndedAt = LastSeen,
                ActiveSeconds = ActiveSeconds,
                AverageFps = _fps.Count > 0 ? _fps.Average() : null,
                LowFps = low,
                CpuTempAverage = Avg(_cpuTemp),
                CpuTempMax = Max(_cpuTemp),
                GpuTempAverage = Avg(_gpuTemp),
                GpuTempMax = Max(_gpuTemp),
                GpuHotSpotMax = Max(_hotSpot),
                CpuLoadAverage = Avg(_cpuLoad),
                GpuLoadAverage = Avg(_gpuLoad),
            };
        }

        private static void Push(List<float> list, float? v)
        {
            if (v is { } x)
                list.Add(x);
        }

        private static float? Avg(List<float> l) => l.Count > 0 ? l.Average() : null;

        private static float? Max(List<float> l) => l.Count > 0 ? l.Max() : null;
    }
}

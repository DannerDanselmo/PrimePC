using System.Diagnostics;
using PrimePC.Sensors.Sources;

namespace PrimePC.Sensors;

/// <summary>
/// Agrega as fontes de sensores, executa a coleta periódica numa thread dedicada, mantém histórico recente
/// e estatísticas da sessão, avalia estados e alertas. Eventos são disparados na thread de coleta.
/// </summary>
public sealed class SensorHub : IDisposable
{
    private readonly IReadOnlyList<ISensorSource> _sources;
    private readonly Dictionary<ISensorSource, int> _generations = new();
    private readonly Dictionary<string, Tracker> _trackers = new(StringComparer.Ordinal);
    private readonly List<string> _limitations = new();
    private readonly object _sync = new();
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stopping;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private int _historyCapacity = 600;

    public SensorHub(IEnumerable<ISensorSource> sources, SensorStateEvaluator evaluator, AlertEngine alerts)
    {
        _sources = sources.ToList();
        Evaluator = evaluator;
        Alerts = alerts;
    }

    public SensorStateEvaluator Evaluator { get; }
    public AlertEngine Alerts { get; }

    public SensorSnapshot Latest { get; private set; } = SensorSnapshot.Empty;
    public DateTime SessionStart { get; private set; } = DateTime.Now;
    public bool IsReady { get; private set; }

    /// <summary>Disparado após cada ciclo de coleta (na thread de coleta).</summary>
    public event EventHandler<SensorSnapshot>? SnapshotUpdated;

    /// <summary>Disparado quando sensores são adicionados ou removidos.</summary>
    public event EventHandler? SensorSetChanged;

    public event EventHandler<AlertEvent>? AlertRaised;

    /// <summary>Disparado quando a inicialização das fontes termina.</summary>
    public event EventHandler? Ready;

    /// <summary>Intervalo entre leituras (mínimo 250 ms).</summary>
    public TimeSpan Interval
    {
        get => _interval;
        set
        {
            _interval = value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
            _wake.Set();
        }
    }

    /// <summary>Quantidade de pontos mantidos no histórico de cada sensor.</summary>
    public int HistoryCapacity
    {
        get => _historyCapacity;
        set
        {
            lock (_sync)
            {
                _historyCapacity = Math.Clamp(value, 10, 100_000);
                foreach (var t in _trackers.Values)
                    t.History.Resize(_historyCapacity);
            }
        }
    }

    public IReadOnlyList<string> Limitations
    {
        get
        {
            lock (_sync)
                return _limitations.ToList();
        }
    }

    public IReadOnlyList<string> SourceNames => _sources.Select(s => s.Name).ToList();

    public void Start()
    {
        if (_thread is not null)
            return;
        _thread = new Thread(Run) { IsBackground = true, Name = "PrimePC.SensorHub" };
        _thread.Start();
    }

    /// <summary>Solicita uma leitura imediata (por exemplo, ao mudar de aba).</summary>
    public void RequestUpdate() => _wake.Set();

    private readonly object _tickGate = new();
    private volatile bool _paused;

    public bool IsPaused => _paused;

    /// <summary>
    /// Suspende a leitura do hardware e espera o ciclo em andamento terminar. Usado durante a troca do driver de vídeo:
    /// enquanto o driver antigo é removido, as bibliotecas do fabricante (NVML/NVAPI) não podem ser chamadas.
    /// </summary>
    public void Pause()
    {
        _paused = true;
        lock (_tickGate)
        {
            // espera o ciclo atual
        }
    }

    public void Resume()
    {
        _paused = false;
        _wake.Set();
    }

    public SensorSample[] GetHistory(string sensorId)
    {
        lock (_sync)
            return _trackers.TryGetValue(sensorId, out var t) ? t.History.ToArray() : Array.Empty<SensorSample>();
    }

    public IReadOnlyList<SensorSessionStats> GetSessionStats()
    {
        lock (_sync)
        {
            return _trackers.Values
                .Select(t => new SensorSessionStats(t.Descriptor, t.Min, t.Average, t.Max, t.Count, t.UnavailableCount,
                    t.WarningCount, t.CriticalCount))
                .ToList();
        }
    }

    /// <summary>Zera mínimos, máximos, médias e o início da sessão (o histórico de gráficos é mantido).</summary>
    public void ResetStatistics()
    {
        lock (_sync)
        {
            foreach (var t in _trackers.Values)
                t.ResetStats();
            SessionStart = DateTime.Now;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        foreach (var source in _sources)
        {
            try
            {
                source.Dispose();
            }
            catch
            {
                // Encerramento best-effort.
            }
        }

        _wake.Dispose();
    }

    private void Run()
    {
        foreach (var source in _sources)
        {
            try
            {
                source.Open();
                _generations[source] = source.Generation;
                lock (_sync)
                    _limitations.AddRange(source.Limitations);
            }
            catch (Exception ex)
            {
                lock (_sync)
                    _limitations.Add($"Falha ao iniciar a fonte de sensores \"{source.Name}\": {ex.Message}");
            }
        }

        IsReady = true;
        Ready?.Invoke(this, EventArgs.Empty);

        var sw = new Stopwatch();
        while (!_stopping)
        {
            sw.Restart();
            try
            {
                lock (_tickGate)
                {
                    if (!_paused)
                        Tick();
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SensorHub] Falha no ciclo de coleta: {ex}");
            }

            var remaining = _interval - sw.Elapsed;
            if (remaining > TimeSpan.Zero)
                _wake.WaitOne(remaining);
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        bool setChanged = false;
        var readings = new List<SensorReading>(Latest.Readings.Count + 16);

        foreach (var source in _sources)
        {
            try
            {
                source.Update();
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[SensorHub] {source.Name}: {ex.Message}");
            }

            if (!_generations.TryGetValue(source, out var gen) || gen != source.Generation)
            {
                _generations[source] = source.Generation;
                setChanged = true;
            }
        }

        lock (_sync)
        {
            foreach (var source in _sources)
            {
                List<RawReading> raw;
                try
                {
                    raw = source.Read().ToList();
                    Evaluator.LearnDeviceLimits(raw);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[SensorHub] {source.Name}: {ex.Message}");
                    continue;
                }

                foreach (var r in raw)
                {
                    if (!_trackers.TryGetValue(r.Descriptor.Id, out var tracker))
                    {
                        tracker = new Tracker(r.Descriptor, _historyCapacity);
                        _trackers[r.Descriptor.Id] = tracker;
                        setChanged = true;
                    }
                    else if (!ReferenceEquals(tracker.Descriptor, r.Descriptor))
                    {
                        tracker.Descriptor = r.Descriptor;
                    }

                    var (state, reason) = Evaluator.Evaluate(r.Descriptor, r.Value);
                    float? value = state == SensorState.Unavailable ? null : r.Value;
                    if (state == SensorState.Unavailable && r.Value is { } rawValue && !float.IsNaN(rawValue))
                        reason = $"{reason} Valor bruto ignorado: {rawValue:0.###}.";
                    tracker.Record(now, value, state);
                    readings.Add(new SensorReading(r.Descriptor, value, tracker.Min, tracker.Max, tracker.Average, state, reason));
                }
            }
        }

        var snapshot = new SensorSnapshot(now, readings);
        Latest = snapshot;

        if (setChanged)
            SensorSetChanged?.Invoke(this, EventArgs.Empty);

        SnapshotUpdated?.Invoke(this, snapshot);

        foreach (var alert in Alerts.Evaluate(snapshot))
            AlertRaised?.Invoke(this, alert);
    }

    private sealed class Tracker(SensorDescriptor descriptor, int capacity)
    {
        private double _sum;

        public SensorDescriptor Descriptor { get; set; } = descriptor;
        public RingBuffer<SensorSample> History { get; } = new(capacity);
        public float? Min { get; private set; }
        public float? Max { get; private set; }
        public long Count { get; private set; }
        public long UnavailableCount { get; private set; }
        public long WarningCount { get; private set; }
        public long CriticalCount { get; private set; }
        public float? Average => Count == 0 ? null : (float)(_sum / Count);

        public void Record(DateTime time, float? value, SensorState state)
        {
            switch (state)
            {
                case SensorState.Unavailable:
                    UnavailableCount++;
                    return;
                case SensorState.Warning:
                    WarningCount++;
                    break;
                case SensorState.Critical:
                    CriticalCount++;
                    break;
            }

            if (value is not { } v)
                return;
            History.Add(new SensorSample(time, v));
            Min = Min is null ? v : Math.Min(Min.Value, v);
            Max = Max is null ? v : Math.Max(Max.Value, v);
            _sum += v;
            Count++;
        }

        public void ResetStats()
        {
            Min = Max = null;
            _sum = 0;
            Count = UnavailableCount = WarningCount = CriticalCount = 0;
        }
    }
}

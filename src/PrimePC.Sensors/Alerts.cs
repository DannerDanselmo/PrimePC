namespace PrimePC.Sensors;

public enum AlertComparison
{
    Above,
    Below,
}

/// <summary>Regra de alerta configurada pelo usuário para um sensor específico.</summary>
public sealed class AlertRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string SensorId { get; set; } = "";
    public string SensorName { get; set; } = "";
    public string HardwareName { get; set; } = "";
    public SensorKind Kind { get; set; }
    public AlertComparison Comparison { get; set; } = AlertComparison.Above;
    public float Threshold { get; set; }

    /// <summary>Tempo mínimo (s) que a condição precisa durar antes de disparar, para evitar picos isolados.</summary>
    public int MinDurationSeconds { get; set; } = 5;

    public bool Enabled { get; set; } = true;

    public bool IsMet(float value) => Comparison == AlertComparison.Above ? value > Threshold : value < Threshold;

    public string Describe() =>
        $"{SensorName} ({HardwareName}) {(Comparison == AlertComparison.Above ? "acima de" : "abaixo de")} " +
        $"{SensorUnits.Format(Kind, Threshold)} por {MinDurationSeconds}s";
}

public sealed record AlertEvent(DateTime Time, string SensorId, string SensorName, string HardwareName, SensorKind Kind,
    float Value, string Message, bool IsCritical);

/// <summary>
/// Avalia regras de alerta e transições para o estado crítico. Cada condição dispara uma única vez e é
/// rearmada quando deixa de ser verdadeira (histerese), evitando avalanches de notificações.
/// </summary>
public sealed class AlertEngine
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, DateTime> _ruleSince = new();
    private readonly HashSet<Guid> _ruleFired = new();
    private readonly Dictionary<string, DateTime> _criticalSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _criticalFired = new(StringComparer.Ordinal);
    private readonly RingBuffer<AlertEvent> _log = new(500);
    private IReadOnlyList<AlertRule> _rules = Array.Empty<AlertRule>();

    /// <summary>Gera alerta automático quando um sensor permanece em estado crítico.</summary>
    public bool AlertOnCriticalState { get; set; } = true;

    public TimeSpan CriticalDebounce { get; set; } = TimeSpan.FromSeconds(3);

    public void SetRules(IEnumerable<AlertRule> rules)
    {
        lock (_sync)
        {
            _rules = rules.Select(r => r).ToList();
            _ruleSince.Clear();
            _ruleFired.Clear();
        }
    }

    public IReadOnlyList<AlertEvent> Log
    {
        get
        {
            lock (_sync)
                return _log.ToArray();
        }
    }

    public void ClearLog()
    {
        lock (_sync)
            _log.Clear();
    }

    public IReadOnlyList<AlertEvent> Evaluate(SensorSnapshot snapshot)
    {
        var raised = new List<AlertEvent>();
        lock (_sync)
        {
            var now = snapshot.Timestamp;
            foreach (var rule in _rules)
            {
                if (!rule.Enabled)
                    continue;
                var reading = snapshot.Find(rule.SensorId);
                if (reading?.Value is not { } value || reading.State == SensorState.Unavailable || !rule.IsMet(value))
                {
                    _ruleSince.Remove(rule.Id);
                    _ruleFired.Remove(rule.Id);
                    continue;
                }

                if (!_ruleSince.TryGetValue(rule.Id, out var since))
                    _ruleSince[rule.Id] = since = now;

                if (!_ruleFired.Contains(rule.Id) && now - since >= TimeSpan.FromSeconds(rule.MinDurationSeconds))
                {
                    _ruleFired.Add(rule.Id);
                    raised.Add(new AlertEvent(now, rule.SensorId, rule.SensorName, rule.HardwareName, rule.Kind, value,
                        $"Alerta: {rule.Describe()} — valor atual {SensorUnits.Format(rule.Kind, value)}.", IsCritical: false));
                }
            }

            if (AlertOnCriticalState)
            {
                foreach (var reading in snapshot.Readings)
                {
                    string id = reading.Descriptor.Id;
                    if (reading.State != SensorState.Critical)
                    {
                        _criticalSince.Remove(id);
                        _criticalFired.Remove(id);
                        continue;
                    }

                    if (!_criticalSince.TryGetValue(id, out var since))
                        _criticalSince[id] = since = now;

                    if (!_criticalFired.Contains(id) && now - since >= CriticalDebounce)
                    {
                        _criticalFired.Add(id);
                        var d = reading.Descriptor;
                        raised.Add(new AlertEvent(now, id, d.Name, d.HardwareName, d.Kind, reading.Value ?? 0,
                            $"Estado crítico: {d.Name} ({d.HardwareName}) = {SensorUnits.Format(d.Kind, reading.Value)}. {reading.StateReason}",
                            IsCritical: true));
                    }
                }
            }

            foreach (var e in raised)
                _log.Add(e);
        }

        return raised;
    }
}

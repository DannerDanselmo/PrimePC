namespace PrimePC.Gaming;

/// <summary>Resumo de uma sequência de quadros (benchmark, gravação de gargalo).</summary>
/// <param name="Low1Fps">FPS médio do 1% de quadros mais lentos (nulo com menos de 100 quadros).</param>
/// <param name="Low01Fps">FPS médio do 0,1% de quadros mais lentos (nulo com menos de 1000 quadros).</param>
/// <param name="Stutters">Quadros muito mais lentos que o normal (travadas perceptíveis).</param>
public sealed record FrameSummary(
    int Frames,
    double DurationSeconds,
    double AverageFps,
    double? Low1Fps,
    double? Low01Fps,
    double MedianFrameTimeMs,
    double MaxFrameTimeMs,
    int Stutters);

public static class FrameStatistics
{
    /// <summary>Resume uma lista de tempos de quadro (ms).</summary>
    public static FrameSummary? Summarize(IReadOnlyList<double> frameTimes)
    {
        if (frameTimes.Count < 2)
            return null;
        var sorted = frameTimes.ToArray();
        Array.Sort(sorted);
        double total = frameTimes.Sum();
        if (total <= 0)
            return null;
        double median = sorted[sorted.Length / 2];
        double stutterAbove = Math.Max(median * 2.5, median + 10);
        return new FrameSummary(
            Frames: frameTimes.Count,
            DurationSeconds: total / 1000,
            AverageFps: frameTimes.Count / total * 1000,
            Low1Fps: sorted.Length >= 100 ? WorstAverageFps(sorted, 0.01) : null,
            Low01Fps: sorted.Length >= 1000 ? WorstAverageFps(sorted, 0.001) : null,
            MedianFrameTimeMs: median,
            MaxFrameTimeMs: sorted[^1],
            Stutters: frameTimes.Count(ft => ft > stutterAbove));
    }

    /// <summary>
    /// FPS médio da fração mais lenta dos quadros ("1% low", "0,1% low"), como nas análises de hardware.
    /// <paramref name="sortedFrameTimes"/> em ordem crescente.
    /// </summary>
    public static double WorstAverageFps(double[] sortedFrameTimes, double fraction)
    {
        int n = sortedFrameTimes.Length;
        int worst = Math.Max(1, (int)Math.Ceiling(n * fraction));
        double sum = 0;
        for (int i = n - worst; i < n; i++)
            sum += sortedFrameTimes[i];
        double average = sum / worst;
        return average > 0 ? 1000 / average : 0;
    }

    /// <summary>Reduz para no máximo <paramref name="points"/> valores, mantendo o pico de cada trecho (as travadas aparecem).</summary>
    public static double[] Downsample(IReadOnlyList<double> values, int points)
    {
        if (values.Count <= points)
            return values.ToArray();
        var result = new double[points];
        double step = values.Count / (double)points;
        for (int i = 0; i < points; i++)
        {
            int from = (int)(i * step), to = Math.Min(values.Count, (int)((i + 1) * step));
            double max = 0;
            for (int j = from; j < to; j++)
                max = Math.Max(max, values[j]);
            result[i] = max;
        }

        return result;
    }
}

/// <summary>Resultado de uma captura: os quadros do jogo escolhido.</summary>
public sealed record CaptureResult(int ProcessId, PresentSource Source, double[] FrameTimes, FrameSummary Summary);

/// <summary>
/// Guarda todos os quadros apresentados (de todos os programas) enquanto estiver ativa. No fim, escolhe o jogo e calcula
/// as estatísticas sobre cada quadro — não sobre médias por segundo.
/// </summary>
public sealed class FrameCapture
{
    private const int MaxFrames = 3_000_000; // ~2,5 h a 300 FPS
    private readonly List<(int Pid, PresentSource Source, ulong SwapChain, double Time)> _frames = new(16_384);
    private readonly object _lock = new();

    public DateTime StartedAt { get; } = DateTime.Now;

    internal void Add(int pid, PresentSource source, ulong swapChain, double time)
    {
        lock (_lock)
        {
            if (_frames.Count < MaxFrames)
                _frames.Add((pid, source, swapChain, time));
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
                return _frames.Count;
        }
    }

    /// <summary>
    /// Quadros do processo <paramref name="pid"/> (ou, se nulo, do processo que mais apresentou, exceto
    /// <paramref name="excludePid"/>). Usa a cadeia de troca principal, preferindo os avisos do DirectX aos do kernel.
    /// </summary>
    public CaptureResult? Result(int? pid, int excludePid = -1)
    {
        List<(int Pid, PresentSource Source, ulong SwapChain, double Time)> frames;
        lock (_lock)
            frames = _frames.ToList();

        // Sem jogo indicado: o processo com mais quadros.
        pid ??= frames.Where(f => f.Pid != excludePid).GroupBy(f => f.Pid).OrderByDescending(g => g.Count()).Select(g => (int?)g.Key).FirstOrDefault();
        var best = frames
            .Where(f => f.Pid == pid)
            .GroupBy(f => (f.Pid, f.Source, f.SwapChain))
            .OrderBy(g => g.Key.Source == PresentSource.Kernel ? 1 : 0)
            .ThenByDescending(g => g.Count())
            .FirstOrDefault();
        if (best is null)
            return null;

        var times = best.Select(f => f.Time).OrderBy(t => t).ToArray();
        var frameTimes = new double[Math.Max(0, times.Length - 1)];
        for (int i = 1; i < times.Length; i++)
            frameTimes[i - 1] = times[i] - times[i - 1];
        return FrameStatistics.Summarize(frameTimes) is { } summary
            ? new CaptureResult(best.Key.Pid, best.Key.Source, frameTimes, summary)
            : null;
    }
}

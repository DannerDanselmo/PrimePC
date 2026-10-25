namespace PrimePC.Gaming;

/// <summary>De onde veio o aviso de quadro apresentado.</summary>
public enum PresentSource
{
    /// <summary>DirectX 10/11/12 (DXGI). Também cobre Vulkan e OpenGL quando o driver apresenta via DXGI.</summary>
    Dxgi,

    /// <summary>Direct3D 9.</summary>
    D3D9,

    /// <summary>Kernel gráfico do Windows (DxgKrnl): usado para jogos que não passam pelo DXGI nem pelo D3D9.</summary>
    Kernel,
}

/// <summary>Estatísticas de quadros de um processo.</summary>
/// <param name="Fps">Quadros por segundo no último segundo.</param>
/// <param name="FrameTimeMs">Tempo médio de quadro no último meio segundo.</param>
/// <param name="Low1Fps">"1% low": FPS médio do 1% de quadros mais lentos nos últimos 10 s (nulo com poucos quadros).</param>
/// <param name="RecentFrameTimes">Tempos de quadro mais recentes (para o gráfico), do mais antigo ao mais novo.</param>
public sealed record FrameStats(int ProcessId, PresentSource Source, double Fps, double FrameTimeMs, double? Low1Fps, double[] RecentFrameTimes);

/// <summary>
/// Guarda o instante de cada quadro apresentado, por processo e cadeia de troca (swap chain), e calcula FPS, tempo de
/// quadro e 1% low. Os tempos estão em milissegundos numa base comum (a mesma de <c>nowMs</c>).
/// </summary>
public sealed class FrameTracker
{
    /// <summary>Os eventos chegam do Windows com até ~1 s de atraso; depois disso o processo deixa de contar como ativo.</summary>
    public const double StaleAfterMs = 2500;

    private const int Capacity = 4096;
    private const int GraphFrames = 120;
    private readonly Dictionary<(int Pid, PresentSource Source, ulong SwapChain), Track> _tracks = new();
    private readonly object _lock = new();

    public void OnPresent(int pid, PresentSource source, ulong swapChain, double timeMs)
    {
        if (pid <= 4)
            return;
        lock (_lock)
        {
            if (!_tracks.TryGetValue((pid, source, swapChain), out var track))
                _tracks[(pid, source, swapChain)] = track = new Track(pid, source);
            track.Add(timeMs);
            foreach (var capture in _captures)
                capture.Add(pid, source, swapChain, timeMs);
        }
    }

    private readonly List<FrameCapture> _captures = new();

    /// <summary>Começa a guardar cada quadro (benchmark, gravação de gargalo). Várias capturas podem coexistir.</summary>
    public FrameCapture BeginCapture()
    {
        var capture = new FrameCapture();
        lock (_lock)
            _captures.Add(capture);
        return capture;
    }

    public void EndCapture(FrameCapture capture)
    {
        lock (_lock)
            _captures.Remove(capture);
    }

    /// <summary>O processo apresentou pelo menos <paramref name="minFrames"/> quadros no último segundo registrado.</summary>
    public bool IsPresenting(int pid, double nowMs, int minFrames = 10)
    {
        lock (_lock)
            return Best(pid, nowMs) is { } t && t.CountSince(t.Last - 1000) >= minFrames;
    }

    public FrameStats? GetStats(int pid, double nowMs)
    {
        double[] times;
        PresentSource source;
        lock (_lock)
        {
            if (Best(pid, nowMs) is not { } track)
                return null;
            times = track.Snapshot();
            source = track.Source;
        }

        return Compute(pid, source, times);
    }

    /// <summary>Calcula as estatísticas a partir dos instantes dos quadros (em ordem crescente).</summary>
    public static FrameStats? Compute(int pid, PresentSource source, double[] times)
    {
        if (times.Length < 2)
            return null;
        double last = times[^1];
        int i1 = FirstAtOrAfter(times, last - 1000);
        if (times.Length - i1 < 2)
            i1 = times.Length - 2; // menos de 2 quadros no último segundo: usa o intervalo dos dois últimos
        double fps = (times.Length - 1 - i1) / Math.Max(0.001, last - times[i1]) * 1000;

        int i05 = Math.Min(FirstAtOrAfter(times, last - 500), times.Length - 2);
        double frameTime = (last - times[i05]) / (times.Length - 1 - i05);

        int i10 = FirstAtOrAfter(times, last - 10_000);
        int count = times.Length - 1 - i10;
        double? low1 = null;
        if (count >= 30)
        {
            // "1% low" como nas análises de hardware: FPS médio do 1% de quadros mais lentos. Travadas raras aparecem
            // (um percentil puro as esconderia quando são menos de 1% dos quadros).
            var deltas = new double[count];
            for (int i = 0; i < count; i++)
                deltas[i] = times[i10 + i + 1] - times[i10 + i];
            Array.Sort(deltas);
            double worst = FrameStatistics.WorstAverageFps(deltas, 0.01);
            low1 = worst > 0 ? Math.Min(worst, fps) : null;
        }

        int g = Math.Max(1, times.Length - GraphFrames);
        var recent = new double[times.Length - g];
        for (int i = g; i < times.Length; i++)
            recent[i - g] = times[i] - times[i - 1];
        return new FrameStats(pid, source, fps, frameTime, low1, recent);
    }

    /// <summary>Esquece processos que pararam de apresentar quadros.</summary>
    public void Prune(double nowMs)
    {
        lock (_lock)
        {
            foreach (var key in _tracks.Where(kv => kv.Value.Last < nowMs - 10_000).Select(kv => kv.Key).ToList())
                _tracks.Remove(key);
        }
    }

    /// <summary>
    /// A cadeia de troca principal do processo: prefere os avisos do próprio DirectX (DXGI/D3D9) e, entre elas, a que mais
    /// apresentou quadros (um jogo pode ter mais de uma, por exemplo a do lançador). O kernel só vale se não houver outra.
    /// </summary>
    private Track? Best(int pid, double nowMs)
    {
        Track? best = null;
        int bestCount = -1;
        foreach (var track in _tracks.Values)
        {
            if (track.Pid != pid || track.Last < nowMs - StaleAfterMs)
                continue;
            int count = track.CountSince(track.Last - 1000);
            bool better = best is null
                          || (best.Source == PresentSource.Kernel && track.Source != PresentSource.Kernel)
                          || ((best.Source == PresentSource.Kernel) == (track.Source == PresentSource.Kernel) && count > bestCount);
            if (better)
            {
                best = track;
                bestCount = count;
            }
        }

        return best;
    }

    private static int FirstAtOrAfter(double[] times, double t)
    {
        int i = Array.BinarySearch(times, t);
        return i >= 0 ? i : ~i;
    }

    private sealed class Track(int pid, PresentSource source)
    {
        private readonly double[] _ring = new double[Capacity];
        private int _count;
        private int _next;

        public int Pid { get; } = pid;
        public PresentSource Source { get; } = source;
        public double Last { get; private set; } = double.MinValue;

        public void Add(double t)
        {
            if (t < Last)
                t = Last; // eventos de CPUs diferentes podem chegar levemente fora de ordem
            _ring[_next] = t;
            _next = (_next + 1) % Capacity;
            _count = Math.Min(_count + 1, Capacity);
            Last = t;
        }

        public int CountSince(double t)
        {
            int n = 0;
            for (int i = 1; i <= _count; i++)
            {
                if (_ring[(_next - i + Capacity) % Capacity] < t)
                    break;
                n++;
            }

            return n;
        }

        public double[] Snapshot()
        {
            var result = new double[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _ring[(_next - _count + i + Capacity) % Capacity];
            return result;
        }
    }
}

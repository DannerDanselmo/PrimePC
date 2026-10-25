using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrimePC.Testing;

/// <summary>Resultado de benchmark: mediana de várias execuções e dispersão entre elas.</summary>
public sealed record BenchmarkResult(
    DateTime At,
    string Name,
    string Unit,
    double Value,
    double SpreadPercent,
    int Runs,
    bool HigherIsBetter,
    string Conditions);

/// <summary>
/// Benchmarks reprodutíveis de CPU e memória. Cada um roda 3 vezes e informa a mediana e a dispersão.
/// As pontuações são próprias do PrimePC e servem para comparar execuções neste mesmo computador
/// (antes/depois de mudanças), não para comparar com outros programas.
/// </summary>
public static class SystemBenchmarks
{
    private const int Runs = 3;

    public static Task<BenchmarkResult> CpuSingleThreadAsync(CancellationToken token) =>
        Task.Run(() => CpuScore(1, "CPU — uma thread", token), token);

    public static Task<BenchmarkResult> CpuMultiThreadAsync(CancellationToken token) =>
        Task.Run(() => CpuScore(Environment.ProcessorCount, $"CPU — {Environment.ProcessorCount} threads", token), token);

    public static Task<BenchmarkResult> MemoryBandwidthAsync(CancellationToken token) => Task.Run(() => Bandwidth(token), token);

    public static Task<BenchmarkResult> MemoryLatencyAsync(CancellationToken token) => Task.Run(() => Latency(token), token);

    private static BenchmarkResult CpuScore(int threads, string name, CancellationToken token)
    {
        var results = new List<double>();
        for (int run = 0; run < Runs; run++)
        {
            token.ThrowIfCancellationRequested();
            long total = 0;
            var duration = TimeSpan.FromSeconds(3);
            var workers = Enumerable.Range(0, threads).Select(_ => new Thread(() =>
            {
                var ws = new CpuStressTester.Workspace(64 * 1024);
                var input = new byte[64 * 1024];
                long n = 0;
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < duration && !token.IsCancellationRequested)
                {
                    CpuStressTester.HashWorkload(input, ws);
                    CpuStressTester.MatrixWorkload(ws);
                    n++;
                }

                Interlocked.Add(ref total, n);
            }) { IsBackground = true }).ToList();
            workers.ForEach(w => w.Start());
            workers.ForEach(w => w.Join());
            results.Add(total / duration.TotalSeconds);
        }

        token.ThrowIfCancellationRequested();
        return Summarize(name, "pontos", results.Select(r => r / 10.0).ToList(), higherIsBetter: true,
            $"{threads} thread(s), 3 execuções de 3 s");
    }

    private static unsafe BenchmarkResult Bandwidth(CancellationToken token)
    {
        const long size = 512L * 1024 * 1024;
        void* src = NativeMemory.AlignedAlloc((nuint)size, 4096);
        void* dst = NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            new Span<byte>(src, (int)size).Fill(0x5A);
            new Span<byte>(dst, (int)size).Clear();
            int threads = Math.Max(1, Environment.ProcessorCount / 2);
            long slice = size / threads / 4096 * 4096;
            var results = new List<double>();
            for (int run = 0; run < Runs; run++)
            {
                token.ThrowIfCancellationRequested();
                var sw = Stopwatch.StartNew();
                const int repeats = 4;
                Parallel.For(0, threads, new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = token }, t =>
                {
                    byte* s = (byte*)src + t * slice;
                    byte* d = (byte*)dst + t * slice;
                    for (int r = 0; r < repeats; r++)
                        Buffer.MemoryCopy(s, d, slice, slice);
                });
                double bytes = (double)slice * threads * repeats * 2; // leitura + gravação
                results.Add(bytes / sw.Elapsed.TotalSeconds / 1e9);
            }

            return Summarize("Memória — largura de banda (cópia)", "GB/s", results, higherIsBetter: true,
                $"cópia de 512 MB com {threads} threads (leitura + gravação), 3 execuções");
        }
        finally
        {
            NativeMemory.AlignedFree(src);
            NativeMemory.AlignedFree(dst);
        }
    }

    /// <summary>Latência por perseguição de ponteiros num ciclo aleatório de linhas de cache (derrota o prefetcher).</summary>
    private static unsafe BenchmarkResult Latency(CancellationToken token)
    {
        const int lineWords = 8; // 64 bytes
        const int lines = 4 * 1024 * 1024; // 256 MB
        long* buf = (long*)NativeMemory.AlignedAlloc((nuint)lines * lineWords * sizeof(long), 4096);
        try
        {
            // Algoritmo de Sattolo: permutação cíclica única.
            var order = new int[lines];
            for (int i = 0; i < lines; i++)
                order[i] = i;
            var rng = new Random(42);
            for (int i = lines - 1; i > 0; i--)
            {
                int j = rng.Next(i);
                (order[i], order[j]) = (order[j], order[i]);
            }

            for (int i = 0; i < lines; i++)
                buf[(long)i * lineWords] = order[i];

            var results = new List<double>();
            for (int run = 0; run < Runs; run++)
            {
                token.ThrowIfCancellationRequested();
                const int steps = 20_000_000;
                long idx = 0;
                var sw = Stopwatch.StartNew();
                for (int s = 0; s < steps; s++)
                    idx = buf[idx * lineWords];
                sw.Stop();
                GC.KeepAlive(idx);
                results.Add(sw.Elapsed.TotalNanoseconds / steps);
            }

            return Summarize("Memória — latência", "ns", results, higherIsBetter: false,
                "acesso aleatório em 256 MB (inclui faltas de TLB), 3 execuções");
        }
        finally
        {
            NativeMemory.AlignedFree(buf);
        }
    }

    private static BenchmarkResult Summarize(string name, string unit, List<double> values, bool higherIsBetter, string conditions)
    {
        values.Sort();
        double median = values[values.Count / 2];
        double spread = median > 0 ? (values[^1] - values[0]) / median * 100 : 0;
        return new BenchmarkResult(DateTime.Now, name, unit, median, spread, values.Count, higherIsBetter, conditions);
    }
}

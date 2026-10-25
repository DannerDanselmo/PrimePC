using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;

namespace PrimePC.Testing;

public sealed record CpuStressOptions
{
    public int Threads { get; init; } = Environment.ProcessorCount;
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Temperatura (°C) que encerra a carga imediatamente. Nulo = sem corte térmico.</summary>
    public float? TemperatureLimit { get; init; } = 90;

    /// <summary>Permite rodar sem leitura de temperatura (o usuário precisa confirmar explicitamente).</summary>
    public bool AllowWithoutTemperature { get; init; }
}

public sealed record CpuStressProgress(
    TimeSpan Elapsed,
    double Fraction,
    long Iterations,
    double IterationsPerSecond,
    long Errors,
    float? Temperature);

public enum StressOutcome
{
    Completed,
    ErrorsDetected,
    Cancelled,
    StoppedByTemperature,
    Failed,
}

public sealed record CpuStressError(DateTime Time, int Thread, string Workload);

public sealed record CpuStressResult(
    StressOutcome Outcome,
    DateTime StartedAt,
    TimeSpan Elapsed,
    TimeSpan PlannedDuration,
    int Threads,
    long Iterations,
    long Errors,
    IReadOnlyList<CpuStressError> ErrorSamples,
    float? MaxTemperature,
    string Summary,
    IReadOnlyList<string> Notes);

/// <summary>
/// Teste de estabilidade da CPU: carga controlada em todas as threads com cálculos determinísticos
/// (hash SHA-256 inteiro e álgebra de ponto flutuante vetorial). Cada resultado é comparado com uma
/// referência calculada antes da carga; qualquer divergência indica erro de cálculo (instabilidade).
/// A carga é encerrada se a temperatura ultrapassar o limite configurado.
/// </summary>
public sealed class CpuStressTester
{
    private const int MatrixSize = 64;
    private readonly Func<float?> _readTemperature;
    private readonly object _sync = new();
    private readonly List<CpuStressError> _errors = new();
    private long _iterations;
    private long _errorCount;

    /// <param name="readTemperature">Leitura atual da temperatura da CPU (°C) ou nulo se indisponível.</param>
    public CpuStressTester(Func<float?> readTemperature)
    {
        _readTemperature = readTemperature;
    }

    public async Task<CpuStressResult> RunAsync(CpuStressOptions options, IProgress<CpuStressProgress>? progress,
        CancellationToken cancellationToken)
    {
        var started = DateTime.Now;
        int threads = Math.Clamp(options.Threads, 1, Environment.ProcessorCount);
        float? initialTemp = _readTemperature();

        if (options.TemperatureLimit is not null && initialTemp is null && !options.AllowWithoutTemperature)
        {
            return new CpuStressResult(StressOutcome.Failed, started, TimeSpan.Zero, options.Duration, threads, 0, 0,
                Array.Empty<CpuStressError>(), null,
                "Teste não iniciado: a temperatura da CPU não está disponível, então o corte térmico automático não funcionaria. " +
                "Confirme explicitamente para executar sem essa proteção.",
                Array.Empty<string>());
        }

        // Referências calculadas antes da carga (CPU fria), usadas para validar cada iteração.
        var buffer = new byte[256 * 1024];
        new Random(1234).NextBytes(buffer);
        var reference = new Workspace(buffer.Length);
        byte[] goldenHash = HashWorkload(buffer, reference);
        double goldenMatrix = MatrixWorkload(reference);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workers = new List<Thread>();
        for (int t = 0; t < threads; t++)
        {
            int id = t;
            var thread = new Thread(() => Worker(id, buffer, goldenHash, goldenMatrix, stop.Token))
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"PrimePC.CpuStress.{id}",
            };
            workers.Add(thread);
        }

        var sw = Stopwatch.StartNew();
        workers.ForEach(w => w.Start());

        bool thermalStop = false;
        float? maxTemp = initialTemp;
        long lastIterations = 0;
        var lastTick = sw.Elapsed;
        while (sw.Elapsed < options.Duration && !stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            float? temp = _readTemperature();
            if (temp is not null)
                maxTemp = maxTemp is null ? temp : Math.Max(maxTemp.Value, temp.Value);
            if (options.TemperatureLimit is { } limit && temp >= limit)
            {
                thermalStop = true;
                stop.Cancel();
            }

            long it = Interlocked.Read(ref _iterations);
            var now = sw.Elapsed;
            double rate = (it - lastIterations) / Math.Max(0.001, (now - lastTick).TotalSeconds);
            lastIterations = it;
            lastTick = now;
            progress?.Report(new CpuStressProgress(now, Math.Min(1, now / options.Duration), it, rate,
                Interlocked.Read(ref _errorCount), temp));
        }

        stop.Cancel();
        foreach (var w in workers)
            w.Join(TimeSpan.FromSeconds(10));
        sw.Stop();

        long errors = Interlocked.Read(ref _errorCount);
        bool userCancelled = cancellationToken.IsCancellationRequested;
        StressOutcome outcome = thermalStop ? StressOutcome.StoppedByTemperature
            : errors > 0 ? StressOutcome.ErrorsDetected
            : userCancelled ? StressOutcome.Cancelled
            : StressOutcome.Completed;

        string summary = outcome switch
        {
            StressOutcome.StoppedByTemperature =>
                $"Carga encerrada por segurança: a temperatura da CPU atingiu o limite de {options.TemperatureLimit:0} °C " +
                $"após {Sensors.SensorUnits.FormatDuration(sw.Elapsed)}." + (errors > 0 ? $" Erros de cálculo detectados: {errors}." : ""),
            StressOutcome.ErrorsDetected =>
                $"Erros de cálculo detectados: {errors}. A CPU produziu resultados diferentes da referência sob carga — " +
                "sinal de instabilidade (overclock/undervolt, temperatura, fonte ou configuração de BIOS).",
            StressOutcome.Cancelled =>
                $"Teste cancelado após {Sensors.SensorUnits.FormatDuration(sw.Elapsed)}. Nenhum erro de cálculo até o cancelamento.",
            _ => $"Nenhum erro detectado durante {Sensors.SensorUnits.FormatDuration(sw.Elapsed)} de carga em {threads} threads.",
        };

        var notes = new List<string>
        {
            "Um teste aprovado reduz a suspeita de instabilidade, mas não garante ausência de falhas em outras cargas.",
            "A carga combina hash SHA-256 (inteiros) e álgebra vetorial de ponto flutuante; cargas como AVX-512 intenso ou jogos podem se comportar diferente.",
        };
        if (maxTemp is null)
            notes.Add("A temperatura da CPU não estava disponível; o teste rodou sem corte térmico automático.");

        List<CpuStressError> samples;
        lock (_sync)
            samples = _errors.ToList();

        return new CpuStressResult(outcome, started, sw.Elapsed, options.Duration, threads, Interlocked.Read(ref _iterations), errors,
            samples, maxTemp, summary, notes);
    }

    private void Worker(int id, byte[] sharedInput, byte[] goldenHash, double goldenMatrix, CancellationToken token)
    {
        var input = sharedInput.ToArray(); // cópia local: evita disputa de cache entre threads
        var ws = new Workspace(input.Length);
        while (!token.IsCancellationRequested)
        {
            if (!HashWorkload(input, ws).AsSpan().SequenceEqual(goldenHash))
                Report(id, "Hash SHA-256 (inteiros)");
            if (token.IsCancellationRequested)
                break;
            if (BitConverter.DoubleToInt64Bits(MatrixWorkload(ws)) != BitConverter.DoubleToInt64Bits(goldenMatrix))
                Report(id, "Matrizes de ponto flutuante");
            Interlocked.Increment(ref _iterations);
        }
    }

    /// <summary>Áreas de trabalho reutilizadas por thread (sem alocações dentro do laço de carga).</summary>
    internal sealed class Workspace(int bufferLength)
    {
        public byte[] Scratch { get; } = new byte[bufferLength];
        public byte[] Hash { get; } = new byte[32];
        public double[] A { get; set; } = new double[MatrixSize * MatrixSize];
        public double[] B { get; } = new double[MatrixSize * MatrixSize];
        public double[] C { get; set; } = new double[MatrixSize * MatrixSize];
    }

    private void Report(int thread, string workload)
    {
        if (Interlocked.Increment(ref _errorCount) > 200)
            return;
        lock (_sync)
            _errors.Add(new CpuStressError(DateTime.Now, thread, workload));
    }

    /// <summary>Encadeia hashes SHA-256 sobre uma cópia da entrada; a entrada original nunca é alterada.</summary>
    internal static byte[] HashWorkload(byte[] input, Workspace ws)
    {
        input.CopyTo(ws.Scratch, 0);
        for (int i = 0; i < 8; i++)
        {
            SHA256.HashData(ws.Scratch, ws.Hash);
            ws.Hash.CopyTo(ws.Scratch, i * 32);
        }

        return ws.Hash;
    }

    /// <summary>Multiplicação de matrizes determinística; a ordem das operações é fixa, então o resultado é bit a bit idêntico.</summary>
    internal static double MatrixWorkload(Workspace ws)
    {
        const int n = MatrixSize;
        var a = ws.A;
        var b = ws.B;
        var c = ws.C;
        Array.Clear(c);
        for (int i = 0; i < n * n; i++)
        {
            a[i] = (i % 17) * 0.031 + 1.0;
            b[i] = (i % 13) * 0.017 + 0.5;
        }

        int width = Vector<double>.Count;
        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < n; i++)
            {
                for (int k = 0; k < n; k++)
                {
                    var aik = new Vector<double>(a[i * n + k]);
                    int j = 0;
                    for (; j <= n - width; j += width)
                    {
                        var cv = new Vector<double>(c, i * n + j);
                        var bv = new Vector<double>(b, k * n + j);
                        (cv + aik * bv).CopyTo(c, i * n + j);
                    }

                    for (; j < n; j++)
                        c[i * n + j] += a[i * n + k] * b[k * n + j];
                }
            }

            (a, c) = (c, a);
            Array.Clear(c);
            for (int i = 0; i < a.Length; i++)
                a[i] = Math.Sqrt(Math.Abs(a[i])) * 0.5 + 0.25;
        }

        ws.A = a;
        ws.C = c;
        double sum = 0;
        foreach (var v in a)
            sum += v;
        return sum;
    }
}

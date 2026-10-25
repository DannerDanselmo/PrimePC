using System.Diagnostics;
using ComputeSharp;
using PrimePC.Sensors;

namespace PrimePC.GpuTesting;

/// <summary>
/// Teste de estabilidade da GPU: executa repetidamente uma carga de computação determinística e compara, na própria
/// GPU, cada resultado com o da primeira execução. Qualquer diferença é um erro de cálculo. Cada despacho dura
/// ~40 ms (calibrado), bem abaixo do limite de 2 s do Windows para a GPU responder.
/// </summary>
public sealed class GpuStressTester
{
    /// <summary>Threads por despacho (61.440 grupos × 64), dentro do limite de grupos do DirectX 12.</summary>
    private const int Threads = 61440 * 64;

    private const uint Seed = 0x2545F491u;
    private const double FlopsPerIteration = 8; // 4 multiplicações + 4 somas em FP32 por iteração

    private readonly Func<float?> _readTemperature;

    /// <param name="readTemperature">Temperatura atual da GPU testada (°C) ou nulo se indisponível.</param>
    public GpuStressTester(Func<float?> readTemperature)
    {
        _readTemperature = readTemperature;
    }

    public Task<GpuStressResult> RunAsync(GpuStressOptions options, IProgress<GpuStressProgress>? progress, CancellationToken token) =>
        Task.Run(() => Run(options, progress, token), CancellationToken.None);

    private GpuStressResult Run(GpuStressOptions o, IProgress<GpuStressProgress>? progress, CancellationToken token)
    {
        var started = DateTime.Now;
        var total = Stopwatch.StartNew();
        string deviceName = "GPU";

        GpuStressResult Finish(GpuTestOutcome outcome, string summary, long dispatches = 0, double gflops = 0, long errors = 0,
            float? maxTemp = null, IReadOnlyList<string>? notes = null) =>
            new(outcome, deviceName, started, total.Elapsed, o.Duration, dispatches, gflops, errors, maxTemp, summary,
                notes ?? Array.Empty<string>());

        GraphicsDevice device;
        try
        {
            device = GpuDevices.Get(o.DeviceIndex);
            deviceName = device.Name;
        }
        catch (Exception ex)
        {
            return Finish(GpuTestOutcome.Failed, $"Não foi possível iniciar o teste: {ex.Message}");
        }

        float? maxTemp = _readTemperature();
        if (o.TemperatureLimit is not null && maxTemp is null && !o.AllowWithoutTemperature)
        {
            return Finish(GpuTestOutcome.Failed,
                "Teste não iniciado: a temperatura da GPU não está disponível, então o corte térmico automático não funcionaria.");
        }

        long dispatches = 0, errors = 0;
        bool thermalStop = false;
        var run = new Stopwatch();
        int iterations = 256;
        try
        {
            using var output = device.AllocateReadWriteBuffer<uint>(Threads);
            using var reference = device.AllocateReadWriteBuffer<uint>(Threads);
            using var errorBuffer = device.AllocateReadWriteBuffer<uint>(new uint[1]);

            // Aquecimento: o primeiro despacho inclui a criação do pipeline e não serve para medir tempo.
            device.For(Threads, new StressShader(output, iterations, Seed));

            // Calibração: ajusta o número de iterações para cada despacho levar ~40 ms.
            for (int attempt = 0; attempt < 10; attempt++)
            {
                var t = Stopwatch.StartNew();
                device.For(Threads, new StressShader(output, iterations, Seed));
                double ms = t.Elapsed.TotalMilliseconds;
                if (ms >= 25 || iterations >= 400_000)
                    break;
                iterations = (int)Math.Min(400_000, iterations * Math.Clamp(40 / Math.Max(0.2, ms), 1.5, 16));
            }

            // Referência: resultado da primeira execução com a carga calibrada.
            device.For(Threads, new StressShader(output, iterations, Seed));
            output.CopyTo(reference);

            double flopsPerDispatch = (double)Threads * iterations * FlopsPerIteration;
            var window = Stopwatch.StartNew();
            long windowStart = 0;
            var readback = new uint[1];
            run.Start();
            while (run.Elapsed < o.Duration && !token.IsCancellationRequested)
            {
                device.For(Threads, new StressShader(output, iterations, Seed));
                device.For(Threads, new CompareShader(output, reference, errorBuffer));
                dispatches++;

                if (window.Elapsed >= TimeSpan.FromSeconds(1))
                {
                    errorBuffer.CopyTo(readback);
                    errors = readback[0];
                    float? temp = _readTemperature();
                    if (temp is not null)
                        maxTemp = maxTemp is null ? temp : Math.Max(maxTemp.Value, temp.Value);
                    double gflops = (dispatches - windowStart) * flopsPerDispatch / window.Elapsed.TotalSeconds / 1e9;
                    progress?.Report(new GpuStressProgress(run.Elapsed, Math.Min(1, run.Elapsed / o.Duration), dispatches, gflops, errors, temp));
                    if (o.TemperatureLimit is { } limit && temp >= limit)
                    {
                        thermalStop = true;
                        break;
                    }

                    window.Restart();
                    windowStart = dispatches;
                }
            }

            errorBuffer.CopyTo(readback);
            errors = readback[0];
            run.Stop();
        }
        catch (Exception ex) when (GpuDevices.IsDeviceLost(ex))
        {
            return Finish(GpuTestOutcome.DeviceLost, GpuDevices.DeviceLostMessage, dispatches, 0, errors, maxTemp);
        }
        catch (Exception ex)
        {
            return Finish(GpuTestOutcome.Failed, $"O teste foi interrompido por um erro: {ex.Message}", dispatches, 0, errors, maxTemp);
        }

        double average = run.Elapsed.TotalSeconds > 0 ? dispatches * (double)Threads * iterations * FlopsPerIteration / run.Elapsed.TotalSeconds / 1e9 : 0;
        var outcome = thermalStop ? GpuTestOutcome.StoppedByTemperature
            : errors > 0 ? GpuTestOutcome.ErrorsDetected
            : token.IsCancellationRequested ? GpuTestOutcome.Cancelled
            : GpuTestOutcome.Completed;
        string summary = outcome switch
        {
            GpuTestOutcome.StoppedByTemperature =>
                $"Carga encerrada por segurança: a GPU atingiu {o.TemperatureLimit:0} °C após {SensorUnits.FormatDuration(run.Elapsed)}." +
                (errors > 0 ? $" Erros de cálculo detectados: {errors:N0}." : ""),
            GpuTestOutcome.ErrorsDetected =>
                $"Erros de cálculo detectados: {errors:N0} resultados diferentes da referência. A GPU está instável sob carga " +
                "(overclock/undervolt, temperatura, fonte ou driver).",
            GpuTestOutcome.Cancelled => $"Teste cancelado após {SensorUnits.FormatDuration(run.Elapsed)}. Nenhum erro de cálculo até o cancelamento.",
            _ => $"Nenhum erro detectado durante {SensorUnits.FormatDuration(run.Elapsed)} de carga ({dispatches:N0} execuções verificadas).",
        };

        var notes = new List<string>
        {
            $"Carga calibrada para ~40 ms por execução ({iterations:N0} iterações × {Threads:N0} threads). Desempenho médio estimado: {average:N0} GFLOPS (FP32).",
            "A carga é de computação (DirectX 12). Jogos usam também rasterização, texturas e ray tracing, que podem se comportar diferente.",
            "Um teste aprovado reduz a suspeita de instabilidade, mas não garante ausência de falhas em outras cargas.",
        };
        if (maxTemp is null)
            notes.Add("A temperatura da GPU não estava disponível; o teste rodou sem corte térmico automático.");

        return Finish(outcome, summary, dispatches, average, errors, maxTemp, notes);
    }
}

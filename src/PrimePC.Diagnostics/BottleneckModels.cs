namespace PrimePC.Diagnostics;

/// <summary>Grau de confiança de uma conclusão. Nunca é apresentado como percentual de gargalo.</summary>
public enum Likelihood
{
    NotIndicated,
    Possible,
    Probable,
    Inconclusive,
}

public enum LimitingFactor
{
    Gpu,
    Cpu,
    Vram,
    Ram,
    Storage,
    CpuThermal,
    GpuThermal,
    None,
}

/// <summary>Conclusão sobre um fator específico, com as evidências observadas.</summary>
public sealed record FactorFinding(
    LimitingFactor Factor,
    Likelihood Likelihood,
    double ShareOfTime,
    string Title,
    IReadOnlyList<string> Evidence);

/// <summary>Resumo estatístico de uma métrica durante a sessão.</summary>
public sealed record MetricSummary(string Name, string Unit, float? Average, float? P95, float? Max, int Samples);

public sealed record BottleneckReport(
    DateTime AnalyzedAt,
    TimeSpan Duration,
    int SampleCount,
    int LoadedSampleCount,
    string Headline,
    Likelihood HeadlineLikelihood,
    string Explanation,
    IReadOnlyList<FactorFinding> Findings,
    IReadOnlyList<MetricSummary> Metrics,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<string> Suggestions,
    FrameRecording? Frames = null,
    Smoothness Smoothness = Smoothness.Unknown,
    string? SmoothnessDetail = null);

/// <summary>Fluidez do jogo durante a gravação, a partir do tempo de cada quadro.</summary>
public enum Smoothness
{
    Unknown,
    Smooth,
    SomeStutter,
    Stuttery,
}

/// <summary>
/// FPS medido a cada quadro durante a gravação (eventos que o Windows publica a cada quadro, como o PresentMon).
/// As estatísticas consideram todos os quadros, não médias por segundo.
/// </summary>
/// <param name="FrameTimeGraph">Tempos de quadro reduzidos para o gráfico (mantém o pico de cada trecho).</param>
public sealed record FrameRecording(
    string? Game,
    string Api,
    int Frames,
    double DurationSeconds,
    double AverageFps,
    double? Low1Fps,
    double? Low01Fps,
    double MedianFrameTimeMs,
    double MaxFrameTimeMs,
    int Stutters,
    double[] FrameTimeGraph)
{
    public double StuttersPerMinute => DurationSeconds > 0 ? Stutters / (DurationSeconds / 60) : 0;
}

/// <summary>Sessão de gravação para análise de gargalo (salva localmente em JSON).</summary>
public sealed class BottleneckSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Workload { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Settings { get; set; } = "";
    public string? CpuName { get; set; }
    public string? GpuName { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime EndedAt { get; set; }
    public double IntervalSeconds { get; set; } = 1;
    public List<TelemetrySample> Samples { get; set; } = new();

    /// <summary>Quadros do jogo medidos durante a gravação (nulo sem administrador ou sem jogo em execução).</summary>
    public FrameRecording? Frames { get; set; }

    public BottleneckReport? Report { get; set; }

    public string DisplayName =>
        $"{(string.IsNullOrWhiteSpace(Workload) ? "Sessão" : Workload)} · {StartedAt:dd/MM/yyyy HH:mm}" +
        (string.IsNullOrWhiteSpace(Resolution) ? "" : $" · {Resolution}");
}

public static class LikelihoodText
{
    public static string Display(Likelihood l) => l switch
    {
        Likelihood.Probable => "Provável",
        Likelihood.Possible => "Possível",
        Likelihood.Inconclusive => "Não foi possível concluir",
        _ => "Não indicado",
    };

    public static string FactorName(LimitingFactor f) => f switch
    {
        LimitingFactor.Gpu => "Placa de vídeo (GPU)",
        LimitingFactor.Cpu => "Processador (CPU)",
        LimitingFactor.Vram => "Memória de vídeo (VRAM)",
        LimitingFactor.Ram => "Memória RAM",
        LimitingFactor.Storage => "Armazenamento",
        LimitingFactor.CpuThermal => "Temperatura da CPU",
        LimitingFactor.GpuThermal => "Temperatura da GPU",
        _ => "Nenhum",
    };
}

namespace PrimePC.Diagnostics;

public sealed class BottleneckAnalyzerOptions
{
    public TimeSpan MinimumDuration { get; init; } = TimeSpan.FromSeconds(30);
    public int MinimumSamples { get; init; } = 20;

    /// <summary>Temperatura da CPU considerada limite térmico (°C). Depende do modelo.</summary>
    public float CpuTemperatureLimit { get; init; } = 95;

    public float GpuTemperatureLimit { get; init; } = 87;
    public float GpuHotSpotLimit { get; init; } = 100;
}

/// <summary>
/// Analisa uma sessão de telemetria e indica o que provavelmente limita o desempenho (GPU, CPU, VRAM, RAM,
/// armazenamento, temperatura). A conclusão combina várias amostras ao longo do tempo, informa a fração do
/// tempo em que cada padrão apareceu, o nível de confiança e as limitações — nunca um "percentual de gargalo".
/// </summary>
public static class BottleneckAnalyzer
{
    public const float GpuSaturated = 95f;
    public const float GpuHasHeadroom = 85f;
    public const float ThreadSaturated = 85f;
    public const float CpuTotalSaturated = 90f;

    public static BottleneckReport Analyze(BottleneckSession session, BottleneckAnalyzerOptions? options = null)
    {
        options ??= new BottleneckAnalyzerOptions();
        var s = session.Samples;
        var duration = s.Count >= 2 ? s[^1].Time - s[0].Time : TimeSpan.Zero;
        var metrics = BuildMetrics(s, session.Frames is not null);
        var conditions = BuildConditions(session, duration);
        var limitations = BuildLimitations(session, metrics);

        if (s.Count < options.MinimumSamples || duration < options.MinimumDuration)
        {
            return new BottleneckReport(DateTime.Now, duration, s.Count, 0,
                "Não foi possível concluir: gravação curta demais",
                Likelihood.Inconclusive,
                $"A gravação teve {FormatDuration(duration)} e {s.Count} amostras. São necessários pelo menos " +
                $"{options.MinimumDuration.TotalSeconds:0} segundos com o jogo ou a carga em execução para uma análise confiável. " +
                "Recomendado: 2 a 5 minutos numa cena representativa.",
                Array.Empty<FactorFinding>(), metrics, conditions, limitations,
                new[] { "Grave novamente por mais tempo, com o jogo ou programa em execução durante toda a gravação." },
                session.Frames, Smoothness.Unknown, null);
        }

        var loaded = s.Where(IsUnderLoad).ToList();
        double loadedShare = loaded.Count / (double)s.Count;

        var findings = new List<FactorFinding>
        {
            AnalyzeGpu(loaded),
            AnalyzeCpu(loaded),
            AnalyzeVram(loaded),
            AnalyzeRam(s),
            AnalyzeStorage(s),
            AnalyzeThermal(LimitingFactor.CpuThermal, loaded, x => x.CpuTemperature, null, options.CpuTemperatureLimit, 0,
                x => x.CpuClockMHz, "da CPU", " (exige o driver PawnIO e execução como administrador)"),
            AnalyzeThermal(LimitingFactor.GpuThermal, loaded, x => x.GpuTemperature, x => x.GpuHotSpot, options.GpuTemperatureLimit,
                options.GpuHotSpotLimit, x => x.GpuClockMHz, "da GPU", ""),
        };

        var ordered = findings
            .OrderBy(f => f.Likelihood switch
            {
                Likelihood.Probable => 0,
                Likelihood.Possible => 1,
                Likelihood.NotIndicated => 2,
                _ => 3,
            })
            .ThenByDescending(f => f.ShareOfTime)
            .ToList();

        var relevant = ordered.Where(f => f.Likelihood is Likelihood.Probable or Likelihood.Possible).ToList();
        string headline;
        Likelihood headlineLikelihood;
        string explanation;

        if (relevant.Count > 0)
        {
            var top = relevant[0];
            headlineLikelihood = top.Likelihood;
            headline = $"{LikelihoodText.Display(top.Likelihood)} limitação: {LikelihoodText.FactorName(top.Factor)}";
            explanation = Explain(top.Factor);
            bool gpu = relevant.Any(f => f.Factor == LimitingFactor.Gpu);
            bool cpu = relevant.Any(f => f.Factor == LimitingFactor.Cpu);
            if (gpu && cpu)
                explanation += " A limitação alternou entre CPU e GPU ao longo da gravação (comportamento misto, comum quando a cena muda).";
        }
        else if (loadedShare < 0.3)
        {
            headlineLikelihood = Likelihood.Inconclusive;
            headline = "Não foi possível concluir: carga baixa";
            explanation = "CPU e GPU ficaram com folga na maior parte da gravação. O jogo pode estar com limite de FPS ou V-Sync ativo, " +
                          "em menus/carregamento, ou a carga era leve demais para revelar um gargalo.";
        }
        else
        {
            headlineLikelihood = Likelihood.NotIndicated;
            headline = "Nenhum gargalo claro identificado";
            explanation = "Nenhum componente apresentou saturação consistente. O desempenho pode estar limitado por limite de FPS/V-Sync, " +
                          "pelo próprio motor do jogo ou por fatores que não são medidos aqui (latência de memória, rede, servidor).";
        }

        var suggestions = BuildSuggestions(relevant);
        var (smoothness, smoothDetail) = AssessSmoothness(session.Frames);
        if (session.Frames is { } frames)
        {
            // FPS praticamente constante com CPU e GPU folgadas = limite de FPS/V-Sync (o desempenho não está limitado pelo hardware).
            bool steady = frames.Low1Fps is { } low && low >= frames.AverageFps * 0.85;
            if (relevant.Count == 0 && steady)
                explanation += $" O FPS ficou estável em torno de {frames.AverageFps:0} durante toda a gravação, o que é típico de limite de FPS ou V-Sync.";
            if (smoothness == Smoothness.Stuttery)
            {
                suggestions.Insert(Math.Max(0, suggestions.Count - 2),
                    "Travadas: causas comuns são compilação de shaders (primeiras partidas depois de atualizar o jogo ou o driver), VRAM ou RAM " +
                    "no limite, programas em segundo plano e carregamento de mapa. Limitar o FPS um pouco abaixo da média costuma deixar o jogo mais estável.");
            }
        }

        return new BottleneckReport(DateTime.Now, duration, s.Count, loaded.Count, headline, headlineLikelihood, explanation,
            ordered, metrics, conditions, limitations, suggestions, session.Frames, smoothness, smoothDetail);
    }

    /// <summary>
    /// Fluidez pelo tempo de cada quadro: compara o 1% low com a média e conta as travadas (quadros muito mais lentos
    /// que o normal). Não depende de um "FPS bom" fixo: 60 FPS estáveis são fluidos; 200 FPS com quedas, não.
    /// </summary>
    public static (Smoothness, string?) AssessSmoothness(FrameRecording? f)
    {
        if (f is null)
            return (Smoothness.Unknown, null);
        if (f.Low1Fps is not { } low)
            return (Smoothness.Unknown, $"Quadros insuficientes para avaliar a fluidez ({f.Frames} quadros).");

        double ratio = low / f.AverageFps;
        double perMinute = f.StuttersPerMinute;
        string numbers = $"FPS médio {f.AverageFps:0}, 1% low {low:0}" + (f.Low01Fps is { } l01 ? $", 0,1% low {l01:0}" : "") +
                         $"; {f.Stutters} travada(s) ({perMinute:0.#} por minuto), pior quadro {f.MaxFrameTimeMs:0} ms.";
        if (ratio >= 0.6 && perMinute < 3)
            return (Smoothness.Smooth, $"Jogo fluido: o 1% low ficou perto da média. {numbers}");
        if (ratio < 0.4 || perMinute >= 15)
            return (Smoothness.Stuttery, $"Muitas travadas: o 1% low ficou bem abaixo da média ou as travadas foram frequentes. {numbers}");
        return (Smoothness.SomeStutter, $"Algumas travadas: quedas ocasionais em relação à média. {numbers}");
    }

    /// <summary>Uma amostra está "sob carga" quando a GPU ou a CPU mostra demanda relevante.</summary>
    public static bool IsUnderLoad(TelemetrySample x) =>
        x.GpuLoad >= 50 || x.CpuTotalLoad >= 50 || x.CpuBusiestThread >= 80;

    private static FactorFinding AnalyzeGpu(List<TelemetrySample> loaded)
    {
        var withGpu = loaded.Where(x => x.GpuLoad is not null).ToList();
        if (withGpu.Count == 0)
            return Inconclusive(LimitingFactor.Gpu, "Uso da GPU indisponível durante a gravação (ou nenhuma amostra sob carga).");

        double share = Share(withGpu, x => x.GpuLoad >= GpuSaturated);
        var likelihood = share >= 0.6 ? Likelihood.Probable : share >= 0.3 ? Likelihood.Possible : Likelihood.NotIndicated;
        var evidence = new List<string>
        {
            $"GPU com uso ≥ {GpuSaturated:0}% em {share:P0} do tempo sob carga (média {Avg(withGpu, x => x.GpuLoad):0}%).",
        };

        var busy = withGpu.Where(x => x.GpuLoad >= 90 && x.GpuPowerLimitPercent is not null).ToList();
        if (busy.Count > 0)
        {
            double atLimit = Share(busy, x => x.GpuPowerLimitPercent >= 97);
            if (atLimit >= 0.2)
                evidence.Add($"Com a GPU ocupada, ela operou no limite de potência em {atLimit:P0} do tempo — comportamento esperado quando ela é o fator limitante.");
        }

        return new FactorFinding(LimitingFactor.Gpu, likelihood, share,
            likelihood == Likelihood.NotIndicated ? "A GPU não ficou consistentemente saturada" : "Limitação pela placa de vídeo (GPU)",
            evidence);
    }

    private static FactorFinding AnalyzeCpu(List<TelemetrySample> loaded)
    {
        var usable = loaded.Where(x => x.GpuLoad is not null && (x.CpuBusiestThread is not null || x.CpuTotalLoad is not null)).ToList();
        if (usable.Count == 0)
            return Inconclusive(LimitingFactor.Cpu, "Uso da CPU ou da GPU indisponível durante a gravação.");

        bool hasThreads = usable.Any(x => x.CpuBusiestThread is not null);
        var hits = usable.Where(x => x.GpuLoad < GpuHasHeadroom && (x.CpuBusiestThread >= ThreadSaturated || x.CpuTotalLoad >= CpuTotalSaturated)).ToList();
        double share = hits.Count / (double)usable.Count;
        var likelihood = share >= 0.5 ? Likelihood.Probable : share >= 0.25 ? Likelihood.Possible : Likelihood.NotIndicated;
        if (!hasThreads && likelihood == Likelihood.Probable)
            likelihood = Likelihood.Possible;

        var evidence = new List<string>
        {
            $"Em {share:P0} do tempo sob carga, ao menos uma thread estava ≥ {ThreadSaturated:0}% (ou a CPU total ≥ {CpuTotalSaturated:0}%) " +
            $"enquanto a GPU estava abaixo de {GpuHasHeadroom:0}%.",
        };
        if (hits.Count > 0)
            evidence.Add($"Nesses momentos: GPU em média {Avg(hits, x => x.GpuLoad):0}%, thread mais ocupada em média {Avg(hits, x => x.CpuBusiestThread):0}%.");
        evidence.Add($"Uso total médio da CPU sob carga: {Avg(usable, x => x.CpuTotalLoad):0}%. Jogos costumam saturar poucas threads, " +
                     "por isso o uso total isolado não indica que a CPU tem folga.");
        if (!hasThreads)
            evidence.Add("Uso por thread indisponível: a conclusão usa apenas o uso total e tem confiança reduzida.");

        return new FactorFinding(LimitingFactor.Cpu, likelihood, share,
            likelihood == Likelihood.NotIndicated ? "A CPU não mostrou sinais consistentes de limitação" : "Limitação pelo processador (CPU)",
            evidence);
    }

    private static FactorFinding AnalyzeVram(List<TelemetrySample> loaded)
    {
        var v = loaded.Where(x => x.VramUsedMB is not null && x.VramTotalMB is > 0).ToList();
        if (v.Count == 0)
            return Inconclusive(LimitingFactor.Vram, "Uso de memória de vídeo (VRAM) indisponível durante a gravação.");

        var full = v.Where(x => x.VramUsedMB / x.VramTotalMB >= 0.95f).ToList();
        double share = full.Count / (double)v.Count;
        float peak = v.Max(x => x.VramUsedMB!.Value);
        float total = v.Max(x => x.VramTotalMB!.Value);
        var likelihood = share >= 0.5 && Avg(full, x => x.GpuLoad) < GpuHasHeadroom
            ? Likelihood.Probable
            : share >= 0.2 ? Likelihood.Possible : Likelihood.NotIndicated;

        var evidence = new List<string>
        {
            $"VRAM acima de 95% da capacidade em {share:P0} do tempo sob carga (pico de {peak:0} de {total:0} MB, {peak / total:P0}).",
        };
        if (likelihood != Likelihood.NotIndicated)
            evidence.Add("Jogos costumam reservar VRAM de forma agressiva; ocupação alta sozinha não prova falta de memória de vídeo.");

        return new FactorFinding(LimitingFactor.Vram, likelihood, share,
            likelihood == Likelihood.NotIndicated ? "A memória de vídeo teve folga" : "Memória de vídeo (VRAM) quase esgotada",
            evidence);
    }

    private static FactorFinding AnalyzeRam(List<TelemetrySample> all)
    {
        var r = all.Where(x => x.RamLoad is not null).ToList();
        if (r.Count == 0)
            return Inconclusive(LimitingFactor.Ram, "Uso da memória RAM indisponível durante a gravação.");

        double share = Share(r, x => x.RamLoad >= 90);
        var likelihood = share >= 0.5 ? Likelihood.Probable : share >= 0.2 ? Likelihood.Possible : Likelihood.NotIndicated;
        return new FactorFinding(LimitingFactor.Ram, likelihood, share,
            likelihood == Likelihood.NotIndicated ? "A memória RAM teve folga" : "Memória RAM quase esgotada",
            new[] { $"RAM acima de 90% em uso em {share:P0} do tempo (média {Avg(r, x => x.RamLoad):0}%, pico {r.Max(x => x.RamLoad!.Value):0}%)." });
    }

    private static FactorFinding AnalyzeStorage(List<TelemetrySample> all)
    {
        var d = all.Where(x => x.DiskActivity is not null).ToList();
        if (d.Count == 0)
            return Inconclusive(LimitingFactor.Storage,
                "Atividade de disco indisponível (os sensores de disco exigem executar o PrimePC como administrador).");

        double share = Share(d, x => x.DiskActivity >= 90 && (x.GpuLoad is null || x.GpuLoad < GpuHasHeadroom));
        var likelihood = share >= 0.3 ? Likelihood.Probable : share >= 0.1 ? Likelihood.Possible : Likelihood.NotIndicated;
        return new FactorFinding(LimitingFactor.Storage, likelihood, share,
            likelihood == Likelihood.NotIndicated ? "O armazenamento não limitou o desempenho" : "Armazenamento muito ocupado",
            new[]
            {
                $"Disco com atividade ≥ 90% em {share:P0} do tempo enquanto a GPU tinha folga " +
                $"(atividade média {Avg(d, x => x.DiskActivity):0}%). Isso pode causar travadas em carregamentos e streaming de texturas.",
            });
    }

    private static FactorFinding AnalyzeThermal(LimitingFactor factor, List<TelemetrySample> loaded, Func<TelemetrySample, float?> temp,
        Func<TelemetrySample, float?>? hotSpot, float limit, float hotSpotLimit, Func<TelemetrySample, float?> clock, string name,
        string unavailableHint)
    {
        var t = loaded.Where(x => temp(x) is not null).ToList();
        if (t.Count == 0)
            return Inconclusive(factor, $"Temperatura {name} indisponível durante a gravação{unavailableHint}.");

        bool IsHot(TelemetrySample x) => temp(x) >= limit || (hotSpot is not null && hotSpot(x) >= hotSpotLimit);
        var hot = t.Where(IsHot).ToList();
        var cool = t.Where(x => !IsHot(x)).ToList();
        double share = hot.Count / (double)t.Count;
        float max = t.Max(x => temp(x)!.Value);

        float? hotClock = Avg(hot, clock), coolClock = Avg(cool, clock);
        double? drop = hotClock is not null && coolClock is > 0 ? 1 - hotClock.Value / coolClock.Value : null;

        var likelihood = share >= 0.2 && drop >= 0.05 ? Likelihood.Probable
            : share >= 0.1 ? Likelihood.Possible
            : Likelihood.NotIndicated;

        var evidence = new List<string>
        {
            $"Temperatura {name} ≥ {limit:0} °C em {share:P0} do tempo sob carga (máxima {max:0.0} °C).",
        };
        if (drop is not null)
            evidence.Add($"Clock médio de {hotClock:0} MHz quando quente contra {coolClock:0} MHz no restante ({drop:P0} de diferença).");
        else if (hot.Count > 0)
            evidence.Add("Não foi possível correlacionar com queda de clock (clock indisponível ou sem amostras para comparar).");

        string title = factor == LimitingFactor.CpuThermal ? "CPU" : "GPU";
        return new FactorFinding(factor, likelihood, share,
            likelihood == Likelihood.NotIndicated ? $"Temperatura da {title} dentro do esperado" : $"Possível limitação térmica da {title}",
            evidence);
    }

    private static string Explain(LimitingFactor f) => f switch
    {
        LimitingFactor.Gpu =>
            "A GPU passou a maior parte do tempo sob carga máxima. Em jogos, isso costuma ser o comportamento esperado: o processador " +
            "consegue alimentar a placa de vídeo e o limite é o poder gráfico nas configurações escolhidas.",
        LimitingFactor.Cpu =>
            "A GPU ficou com folga enquanto ao menos uma thread do processador estava saturada — sinal de que o processador " +
            "(ou a thread principal do jogo) limita o desempenho nestas condições.",
        LimitingFactor.Vram =>
            "A memória de vídeo esteve quase cheia com a GPU abaixo da carga máxima, o que pode causar quedas de desempenho e travadas.",
        LimitingFactor.Ram =>
            "A memória RAM esteve quase toda ocupada; o Windows pode estar usando o arquivo de paginação, o que causa travadas.",
        LimitingFactor.Storage =>
            "O disco esteve muito ocupado enquanto CPU/GPU tinham folga, o que indica espera por leitura de dados.",
        LimitingFactor.CpuThermal =>
            "A CPU atingiu temperaturas próximas do limite térmico, o que pode reduzir os clocks para protegê-la.",
        LimitingFactor.GpuThermal =>
            "A GPU atingiu temperaturas elevadas, o que pode reduzir os clocks para protegê-la.",
        _ => "",
    };

    private static List<string> BuildSuggestions(List<FactorFinding> relevant)
    {
        var list = new List<string>();
        foreach (var f in relevant)
        {
            switch (f.Factor)
            {
                case LimitingFactor.Gpu:
                    list.Add("GPU: para mais FPS, reduza resolução ou qualidade gráfica, ative upscaling (DLSS/FSR/XeSS) e mantenha o driver de vídeo atualizado.");
                    break;
                case LimitingFactor.Cpu:
                    list.Add("CPU: reduza opções que pesam no processador (distância de visão, densidade de NPCs/multidões, física), feche programas em segundo plano e verifique temperatura e clocks da CPU. Aumentar a resolução/qualidade tende a não reduzir o FPS nesta situação.");
                    break;
                case LimitingFactor.Vram:
                    list.Add("VRAM: reduza a qualidade das texturas, a resolução ou o ray tracing.");
                    break;
                case LimitingFactor.Ram:
                    list.Add("RAM: feche programas em segundo plano (navegadores, launchers) e verifique o uso de memória; se for recorrente, considere mais memória.");
                    break;
                case LimitingFactor.Storage:
                    list.Add("Armazenamento: instale o jogo em um SSD (de preferência NVMe) e verifique atividade de disco em segundo plano (atualizações, antivírus, downloads).");
                    break;
                case LimitingFactor.CpuThermal:
                case LimitingFactor.GpuThermal:
                    list.Add("Temperatura: verifique poeira, fluxo de ar do gabinete, curva das ventoinhas e, se necessário, a pasta térmica.");
                    break;
            }
        }

        list.Add("Verifique se há limite de FPS ou V-Sync ativo e se o driver de vídeo está atualizado.");
        list.Add("O PrimePC não altera configurações, overclock nem BIOS automaticamente; as sugestões acima são apenas orientações.");
        return list.Distinct().ToList();
    }

    private static List<MetricSummary> BuildMetrics(List<TelemetrySample> s, bool measuredPerFrame) => new()
    {
        Metric(s, "Uso total da CPU", "%", x => x.CpuTotalLoad),
        Metric(s, "Thread mais ocupada", "%", x => x.CpuBusiestThread),
        Metric(s, "Clock médio da CPU", "MHz", x => x.CpuClockMHz),
        Metric(s, "Temperatura da CPU", "°C", x => x.CpuTemperature),
        Metric(s, "Potência da CPU", "W", x => x.CpuPower),
        Metric(s, "Uso da GPU", "%", x => x.GpuLoad),
        Metric(s, "Clock da GPU", "MHz", x => x.GpuClockMHz),
        Metric(s, "Temperatura da GPU", "°C", x => x.GpuTemperature),
        Metric(s, "Potência da GPU", "W", x => x.GpuPower),
        Metric(s, "Potência da GPU (% do limite)", "%", x => x.GpuPowerLimitPercent),
        Metric(s, "VRAM usada", "MB", x => x.VramUsedMB),
        Metric(s, "Uso da RAM", "%", x => x.RamLoad),
        Metric(s, "Atividade de disco (maior)", "%", x => x.DiskActivity),
        Metric(s, measuredPerFrame ? "FPS (medido a cada quadro)" : "FPS (informado pelo driver)", "FPS", x => x.Fps),
        Metric(s, "Tempo de quadro", "ms", x => x.FrameTimeMs),
    };

    public static MetricSummary Metric(IReadOnlyList<TelemetrySample> s, string name, string unit, Func<TelemetrySample, float?> sel)
    {
        var values = s.Select(sel).Where(v => v is not null).Select(v => v!.Value).ToList();
        if (values.Count == 0)
            return new MetricSummary(name, unit, null, null, null, 0);
        values.Sort();
        float p95 = values[Math.Min(values.Count - 1, (int)Math.Ceiling(values.Count * 0.95) - 1)];
        return new MetricSummary(name, unit, values.Average(), p95, values[^1], values.Count);
    }

    private static List<string> BuildConditions(BottleneckSession session, TimeSpan duration)
    {
        var c = new List<string>();
        if (!string.IsNullOrWhiteSpace(session.Workload)) c.Add($"Carga/jogo: {session.Workload}");
        if (!string.IsNullOrWhiteSpace(session.Resolution)) c.Add($"Resolução: {session.Resolution}");
        if (!string.IsNullOrWhiteSpace(session.Settings)) c.Add($"Configurações: {session.Settings}");
        c.Add($"Início: {session.StartedAt:dd/MM/yyyy HH:mm:ss}");
        c.Add($"Duração: {FormatDuration(duration)} ({session.Samples.Count} amostras, intervalo de {session.IntervalSeconds:0.##} s)");
        if (session.Frames is { } f)
            c.Add($"Jogo medido: {f.Game ?? "jogo em primeiro plano"} · {f.Api} · {f.Frames:N0} quadros");
        if (session.CpuName is not null) c.Add($"CPU: {session.CpuName}");
        if (session.GpuName is not null) c.Add($"GPU analisada: {session.GpuName}");
        return c;
    }

    private static List<string> BuildLimitations(BottleneckSession session, List<MetricSummary> metrics)
    {
        var l = new List<string>
        {
            "O resultado vale apenas para as condições desta gravação (jogo, cena, resolução, qualidade gráfica, limite de FPS, driver e " +
            "processos em segundo plano). Não existe um percentual universal de gargalo.",
            $"As amostras foram coletadas a cada {session.IntervalSeconds:0.##} s; variações mais rápidas que isso não aparecem.",
            "O Windows move threads entre núcleos lógicos, o que pode fazer a saturação de uma única thread parecer menor do que é.",
        };

        bool hasFps = metrics.Any(m => m.Unit == "FPS" && m.Samples > 0);
        if (session.Frames is { } f)
        {
            l.Add($"FPS e tempo de quadro medidos a cada quadro pelos eventos que o Windows publica (o mesmo método do PresentMon), " +
                  $"no jogo {f.Game ?? "em primeiro plano"} ({f.Api}). Média, 1% low e 0,1% low consideram todos os {f.Frames:N0} quadros " +
                  "da gravação. Medem o ritmo em que o jogo entrega quadros, não o instante exato em que cada um aparece no monitor.");
        }
        else
        {
            l.Add(hasFps
                ? "FPS informado pelo driver da GPU e amostrado a cada intervalo; 1% low e tempo de quadro não foram medidos " +
                  "(para medir cada quadro, abra o PrimePC como administrador e mantenha o jogo em primeiro plano)."
                : "FPS não foi medido: abra o PrimePC como administrador e mantenha o jogo em primeiro plano durante a gravação.");
        }

        var missing = metrics.Where(m => m.Samples == 0 && m.Unit is not ("FPS" or "ms")).Select(m => m.Name).ToList();
        if (missing.Count > 0)
            l.Add($"Não disponível durante a gravação: {string.Join(", ", missing)}.");
        return l;
    }

    private static FactorFinding Inconclusive(LimitingFactor f, string reason) =>
        new(f, Likelihood.Inconclusive, 0, $"{LikelihoodText.FactorName(f)}: não avaliado", new[] { reason });

    private static double Share(List<TelemetrySample> items, Func<TelemetrySample, bool> predicate) =>
        items.Count == 0 ? 0 : items.Count(predicate) / (double)items.Count;

    private static float? Avg(IEnumerable<TelemetrySample> items, Func<TelemetrySample, float?> sel)
    {
        double sum = 0;
        int n = 0;
        foreach (var item in items)
        {
            if (sel(item) is { } v)
            {
                sum += v;
                n++;
            }
        }

        return n == 0 ? null : (float)(sum / n);
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.TotalSeconds:0} s";
}

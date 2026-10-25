using System.Globalization;
using System.Text.Json;

namespace PrimePC.Diagnostics;

/// <summary>Armazena sessões de análise de gargalo localmente (um arquivo JSON por sessão).</summary>
public sealed class BottleneckSessionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public BottleneckSessionStore(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public void Save(BottleneckSession session)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string path = PathFor(session.Id);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(session, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public IReadOnlyList<BottleneckSession> LoadAll()
    {
        if (!System.IO.Directory.Exists(Directory))
            return Array.Empty<BottleneckSession>();

        var list = new List<BottleneckSession>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<BottleneckSession>(File.ReadAllText(file), Json) is { } s)
                    list.Add(s);
            }
            catch
            {
                // Arquivo corrompido ou de versão incompatível: ignorado.
            }
        }

        return list.OrderByDescending(s => s.StartedAt).ToList();
    }

    public void Delete(Guid id)
    {
        string path = PathFor(id);
        if (File.Exists(path))
            File.Delete(path);
    }

    private string PathFor(Guid id) => Path.Combine(Directory, id.ToString("N") + ".json");
}

public sealed record ComparisonRow(string Metric, IReadOnlyList<string> Values);

public sealed record ComparisonTable(IReadOnlyList<string> Headers, IReadOnlyList<ComparisonRow> Rows);

/// <summary>Monta uma tabela comparando sessões (configurações, resoluções, jogos diferentes).</summary>
public static class SessionComparer
{
    public static ComparisonTable Compare(IReadOnlyList<BottleneckSession> sessions, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var headers = sessions.Select(s => s.DisplayName).ToList();
        var rows = new List<ComparisonRow>
        {
            Row("Carga/jogo", sessions, s => Text(s.Workload)),
            Row("Resolução", sessions, s => Text(s.Resolution)),
            Row("Configurações", sessions, s => Text(s.Settings)),
            Row("Duração", sessions, s => s.Samples.Count >= 2 ? Duration(s.Samples[^1].Time - s.Samples[0].Time) : "—"),
            Row("Conclusão", sessions, s => s.Report?.Headline ?? "Não analisada"),
        };

        void AddMetric(string label, string unit, Func<TelemetrySample, float?> sel, bool useMax = false)
        {
            rows.Add(Row(label, sessions, s =>
            {
                var m = BottleneckAnalyzer.Metric(s.Samples, label, unit, sel);
                float? v = useMax ? m.Max : m.Average;
                return v is null ? "Não disponível" : $"{v.Value.ToString("0.#", culture)} {unit}";
            }));
        }

        AddMetric("Uso médio da GPU", "%", x => x.GpuLoad);
        AddMetric("Uso médio da CPU (total)", "%", x => x.CpuTotalLoad);
        AddMetric("Thread mais ocupada (média)", "%", x => x.CpuBusiestThread);
        AddMetric("Temperatura máx. da CPU", "°C", x => x.CpuTemperature, useMax: true);
        AddMetric("Temperatura máx. da GPU", "°C", x => x.GpuTemperature, useMax: true);
        AddMetric("Clock médio da GPU", "MHz", x => x.GpuClockMHz);
        AddMetric("Pico de VRAM", "MB", x => x.VramUsedMB, useMax: true);
        AddMetric("Uso médio da RAM", "%", x => x.RamLoad);
        AddMetric("FPS médio (amostras)", "FPS", x => x.Fps);
        rows.Add(Row("FPS médio (cada quadro)", sessions, s => s.Frames is { } f ? $"{f.AverageFps.ToString("0", culture)} FPS" : "Não medido"));
        rows.Add(Row("1% low", sessions, s => s.Frames?.Low1Fps is { } v ? $"{v.ToString("0", culture)} FPS" : "Não medido"));
        rows.Add(Row("0,1% low", sessions, s => s.Frames?.Low01Fps is { } v ? $"{v.ToString("0", culture)} FPS" : "Não medido"));
        rows.Add(Row("Travadas por minuto", sessions, s => s.Frames is { } f ? f.StuttersPerMinute.ToString("0.#", culture) : "Não medido"));

        return new ComparisonTable(headers, rows);
    }

    private static ComparisonRow Row(string metric, IReadOnlyList<BottleneckSession> sessions, Func<BottleneckSession, string> value) =>
        new(metric, sessions.Select(value).ToList());

    private static string Text(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.TotalSeconds:0} s";
}

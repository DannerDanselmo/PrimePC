using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrimePC.Sensors;

namespace PrimePC.Reports;

public static class JsonReportExporter
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Export(DiagnosticReport report, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(report, Options), new UTF8Encoding(false));
}

/// <summary>
/// Exportação CSV compatível com o Excel na configuração regional atual (no Brasil: separador ";" e
/// vírgula decimal), com BOM UTF-8 para preservar acentos.
/// </summary>
public static class CsvExporter
{
    public static void ExportSensorStatistics(DiagnosticReport report, string path, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string sep = Separator(culture);
        var sb = new StringBuilder();
        sb.AppendLine(Join(sep, "Hardware", "Categoria", "Sensor", "Tipo", "Unidade", "Mínimo", "Média", "Máximo",
            "Amostras válidas", "Leituras indisponíveis", "Amostras em atenção", "Amostras críticas", "Origem"));
        foreach (var r in report.Sensors)
        {
            sb.AppendLine(Join(sep, r.Hardware, r.Category, r.Name, r.Kind, r.Unit,
                Number(r.Min, culture), Number(r.Average, culture), Number(r.Max, culture),
                r.Samples.ToString(culture), r.UnavailableSamples.ToString(culture),
                r.WarningSamples.ToString(culture), r.CriticalSamples.ToString(culture), r.Source));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    /// <summary>Histórico em formato largo: uma linha por instante, uma coluna por sensor.</summary>
    public static void ExportHistory(IReadOnlyList<(SensorDescriptor Descriptor, SensorSample[] Samples)> series, string path,
        CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string sep = Separator(culture);
        var times = series.SelectMany(s => s.Samples.Select(x => x.Timestamp)).Distinct().Order().ToList();
        var lookups = series.Select(s => s.Samples.GroupBy(x => x.Timestamp).ToDictionary(g => g.Key, g => g.Last().Value)).ToList();

        var sb = new StringBuilder();
        var header = new List<string> { "Data/hora" };
        header.AddRange(series.Select(s => $"{s.Descriptor.HardwareName} - {s.Descriptor.Name} ({s.Descriptor.Unit})"));
        sb.AppendLine(Join(sep, header.ToArray()));
        foreach (var t in times)
        {
            var row = new List<string> { t.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) };
            row.AddRange(lookups.Select(l => l.TryGetValue(t, out var v) ? v.ToString("0.###", culture) : ""));
            sb.AppendLine(Join(sep, row.ToArray()));
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string Separator(CultureInfo culture)
    {
        string s = culture.TextInfo.ListSeparator;
        return string.IsNullOrEmpty(s) || s == culture.NumberFormat.NumberDecimalSeparator ? ";" : s;
    }

    private static string Number(float? v, CultureInfo culture) => v?.ToString("0.###", culture) ?? "";

    private static string Join(string sep, params string[] fields) => string.Join(sep, fields.Select(f => Escape(f, sep)));

    public static string Escape(string field, string sep)
    {
        if (field.Contains('"') || field.Contains(sep) || field.Contains('\n') || field.Contains('\r'))
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }
}

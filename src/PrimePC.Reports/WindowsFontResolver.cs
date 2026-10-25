using PdfSharp.Fonts;

namespace PrimePC.Reports;

/// <summary>
/// Fornece ao PDFsharp as fontes instaladas no Windows (C:\Windows\Fonts). Usa Segoe UI e, se ausente, Arial.
/// </summary>
internal sealed class WindowsFontResolver : IFontResolver
{
    private static readonly string FontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

    private static readonly Dictionary<string, string[]> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        // regular, negrito, itálico, negrito itálico
        ["Segoe UI"] = ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"],
        ["Arial"] = ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"],
        ["Consolas"] = ["consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"],
    };

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        foreach (var family in new[] { familyName, "Segoe UI", "Arial" })
        {
            if (!Families.TryGetValue(family, out var files))
                continue;
            int index = (bold ? 1 : 0) + (italic ? 2 : 0);
            if (File.Exists(Path.Combine(FontsDir, files[index])))
                return new FontResolverInfo(files[index]);
            if (File.Exists(Path.Combine(FontsDir, files[0])))
                return new FontResolverInfo(files[0], bold, italic); // simula estilo a partir da fonte regular
        }

        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        string path = Path.Combine(FontsDir, faceName);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }
}

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PrimePC.Drivers;

/// <summary>
/// Consulta, no serviço oficial da NVIDIA (o mesmo usado pelo aplicativo da NVIDIA para avisar de drivers novos), qual é o
/// driver Game Ready mais recente para a placa instalada. Envia só o identificador da placa (ex.: 2C02), a versão do
/// Windows e o idioma. O download vem sempre dos servidores da NVIDIA e a assinatura digital é conferida antes de abrir.
/// </summary>
public static partial class NvidiaDriverService
{
    private const string LookupBase =
        "https://gfwsl.geforce.com/nvidia_web_services/controller.gfeclientcontent.NG.php/com.nvidia.services.GFEClientContent_NG.getDispDrvrByDevid/";

    /// <summary>Organização que deve assinar o instalador.</summary>
    public const string Signer = "NVIDIA Corporation";

    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var version = typeof(NvidiaDriverService).Assembly.GetName().Version ?? new Version(1, 0);
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PrimePC", version.ToString(3)));
        return client;
    }

    public static bool IsNvidia(InstalledDriver d) => d.Category == DriverCategory.Video && DeviceIdOf(d) is not null;

    /// <summary>Identificador PCI da placa NVIDIA (VEN_10DE&amp;DEV_2C02 → "2C02").</summary>
    public static string? DeviceIdOf(InstalledDriver d)
    {
        foreach (var id in d.HardwareIds.Prepend(d.DeviceId))
        {
            var m = DeviceIdRegex().Match(id);
            if (m.Success)
                return m.Groups[1].Value.ToUpperInvariant();
        }

        return null;
    }

    public static Uri BuildLookupUri(string deviceId, int osBuild, int languageId)
    {
        string json = "{\"dIDa\":[\"" + deviceId + "_10DE\"],\"osC\":\"10.0\",\"osB\":\"" + osBuild.ToString(CultureInfo.InvariantCulture) +
                      "\",\"is6\":\"1\",\"lg\":\"" + languageId.ToString(CultureInfo.InvariantCulture) +
                      "\",\"iLp\":\"0\",\"prvMd\":\"0\",\"gcV\":\"3.28.0.417\",\"gIsB\":\"0\",\"dch\":\"1\",\"upCRD\":\"0\",\"isCRD\":\"0\"}";
        return new Uri(LookupBase + Uri.EscapeDataString(json));
    }

    /// <summary>Lê a resposta da consulta. Retorna nulo se a NVIDIA não tiver driver para esta placa.</summary>
    public static NvidiaDriverRelease? ParseLookup(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("DriverAttributes", out var a) || a.ValueKind != JsonValueKind.Object)
            return null;
        string? version = Text(a, "Version");
        string? url = Text(a, "DownloadURLAdmin") ?? Text(a, "DownloadURL");
        if (version is null || url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsAllowedDownload(uri))
            return null;
        DateTime? date = Text(a, "ReleaseDateTime") is { Length: >= 10 } d &&
                         DateTime.TryParseExact(d[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
            ? dt
            : null;
        return new NvidiaDriverRelease(
            Version: version,
            Name: Text(a, "NameLocalized") ?? Text(a, "Name") ?? "Driver NVIDIA",
            ReleaseDate: date,
            IsWhql: Text(a, "IsWHQL") == "1",
            DownloadUrl: uri,
            DetailsUrl: Text(a, "DetailsURL"));
    }

    /// <summary>Só aceita downloads por HTTPS dos servidores da NVIDIA.</summary>
    public static bool IsAllowedDownload(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.Host.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>Compara versões no formato da NVIDIA ("617.42" &lt; "621.01").</summary>
    public static int CompareVersions(string a, string b)
    {
        static (int, int) Parse(string v)
        {
            var parts = v.Trim().Split('.');
            int major = int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
            int minor = parts.Length > 1 && int.TryParse(parts[1].PadRight(2, '0')[..2], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return (major, minor);
        }

        return Parse(a).CompareTo(Parse(b));
    }

    public static async Task<NvidiaCheck> CheckAsync(InstalledDriver gpu, CancellationToken token)
    {
        string? installed = gpu.FriendlyVersion;
        if (DeviceIdOf(gpu) is not { } deviceId)
            return new NvidiaCheck(false, null, installed, false, "Não foi possível identificar o modelo desta placa NVIDIA.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var uri = BuildLookupUri(deviceId, Environment.OSVersion.Version.Build, LanguageId());
            string json = await Http.GetStringAsync(uri, timeout.Token).ConfigureAwait(false);
            var latest = ParseLookup(json);
            if (latest is null)
                return new NvidiaCheck(false, null, installed, false, "A NVIDIA não informou um driver para esta placa. Use o site da NVIDIA.");

            latest = latest with { SizeBytes = await SizeOf(latest.DownloadUrl, timeout.Token).ConfigureAwait(false) };
            bool newer = installed is not null && CompareVersions(latest.Version, installed) > 0;
            string message = installed is null
                ? $"Versão mais recente na NVIDIA: {latest.Version}."
                : newer
                    ? $"Há um driver mais novo: {latest.Version} (você tem o {installed})."
                    : $"Você já tem o driver mais recente da NVIDIA ({installed}).";
            return new NvidiaCheck(true, latest, installed, newer, message);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new NvidiaCheck(false, null, installed, false, "A NVIDIA não respondeu a tempo. Verifique a conexão com a internet.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return new NvidiaCheck(false, null, installed, false, $"Não foi possível consultar a NVIDIA: {ex.Message}");
        }
    }

    private static async Task<long?> SizeOf(Uri uri, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? response.Content.Headers.ContentLength : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Idioma das notas e da página (português do Brasil = 1046); o instalador é o internacional, com todos os idiomas.</summary>
    private static int LanguageId()
    {
        int lcid = CultureInfo.CurrentUICulture.LCID;
        return lcid is > 0 and < 0x10000 && lcid != 4096 ? lcid : 1033;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String when !string.IsNullOrWhiteSpace(v.GetString()) => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    [GeneratedRegex(@"VEN_10DE&DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdRegex();
}

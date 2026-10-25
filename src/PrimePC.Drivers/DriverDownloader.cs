using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace PrimePC.Drivers;

/// <summary>
/// Baixa um instalador para um arquivo ".part" e só o renomeia quando termina. Se o download for interrompido, a próxima
/// tentativa continua de onde parou (quando o servidor aceita, como os da NVIDIA).
/// </summary>
public static class DriverDownloader
{
    public static async Task DownloadAsync(HttpClient http, Uri url, string destination, IProgress<DownloadProgress>? progress, CancellationToken token)
    {
        string part = destination + ".part";
        long existing = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
            request.Headers.Range = new RangeHeaderValue(existing, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // O arquivo parcial não corresponde mais ao do servidor: recomeça do zero.
            File.Delete(part);
            await DownloadAsync(http, url, destination, progress, token).ConfigureAwait(false);
            return;
        }

        response.EnsureSuccessStatusCode();
        bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed)
            existing = 0;
        long? total = response.Content.Headers.ContentLength is { } length ? length + existing : null;

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        long received = existing;
        await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
        await using (var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 20];
            var clock = Stopwatch.StartNew();
            long windowStart = received;
            var lastReport = TimeSpan.Zero;
            double speed = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                var now = clock.Elapsed;
                if (now - lastReport >= TimeSpan.FromMilliseconds(250))
                {
                    speed = (received - windowStart) / Math.Max(0.001, (now - lastReport).TotalSeconds);
                    windowStart = received;
                    lastReport = now;
                    progress?.Report(new DownloadProgress(received, total, speed));
                }
            }

            progress?.Report(new DownloadProgress(received, total, speed));
        }

        if (total is { } expected && received != expected)
            throw new IOException($"O download terminou incompleto ({received} de {expected} bytes). Tente de novo para continuar.");
        File.Move(part, destination, overwrite: true);
    }
}

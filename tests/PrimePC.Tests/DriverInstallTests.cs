using System.Net;
using PrimePC.Drivers;

namespace PrimePC.Tests;

public class DriverInstallTests
{
    // Trecho real da resposta do serviço da NVIDIA para a RTX 5080 (DEV_2C02).
    private const string NvidiaResponse = """
        {
            "criteria": { "overallState": "true", "IsDispDriverNewer": { "state": "true", "latestDispDriverVersion": "621.01" } },
            "DriverAttributes": {
                "ID": 282470, "Name": "Driver GeForce Game Ready", "NameLocalized": "Driver GeForce Game Ready",
                "ReleaseDateTime": "2026-10-21T00:00:00+0000", "Version": "621.01", "IsBeta": 0, "IsWHQL": 1,
                "DetailsURL": "https:\/\/www.nvidia.com\/pt-br\/drivers\/details\/282470\/",
                "DownloadURL": "https:\/\/international-gfe.download.nvidia.com\/Windows\/621.01\/621.01-desktop-win10-win11-64bit-international-dch-whql-g.exe",
                "DownloadURLAdmin": "https:\/\/us.download.nvidia.com\/Windows\/621.01\/621.01-desktop-win10-win11-64bit-international-dch-whql.exe"
            }
        }
        """;

    private static InstalledDriver Gpu(string id, string? provider = "NVIDIA", string version = "32.0.16.1742") =>
        new("Placa", id, "DISPLAY", DriverCategory.Video, provider, provider, version, null, true, null, "oem1.inf", new[] { id }, 0);

    [Fact]
    public void NvidiaResponseIsParsed()
    {
        var r = NvidiaDriverService.ParseLookup(NvidiaResponse);
        Assert.NotNull(r);
        Assert.Equal("621.01", r.Version);
        Assert.Equal("Driver GeForce Game Ready", r.Name);
        Assert.Equal(new DateTime(2026, 10, 21), r.ReleaseDate);
        Assert.True(r.IsWhql);
        Assert.Equal("us.download.nvidia.com", r.DownloadUrl.Host);
        Assert.Equal("621.01-desktop-win10-win11-64bit-international-dch-whql.exe", r.FileName);
        Assert.Equal("https://www.nvidia.com/pt-br/drivers/details/282470/", r.DetailsUrl);
    }

    [Fact]
    public void UnknownCardAndForeignDownloadHostAreRejected()
    {
        Assert.Null(NvidiaDriverService.ParseLookup("""{ "criteria": { "overallState": "false" }, "DriverAttributes": null }"""));
        Assert.Null(NvidiaDriverService.ParseLookup("""{ "DriverAttributes": { "Version": "999.99", "DownloadURLAdmin": "https://exemplo.com/driver.exe" } }"""));
        Assert.Null(NvidiaDriverService.ParseLookup("""{ "DriverAttributes": { "Version": "999.99", "DownloadURLAdmin": "http://us.download.nvidia.com/driver.exe" } }"""));
    }

    [Theory]
    [InlineData("https://us.download.nvidia.com/Windows/621.01/x.exe", true)]
    [InlineData("https://international-gfe.download.nvidia.com/Windows/x.exe", true)]
    [InlineData("https://download.nvidia.com/x.exe", true)]
    [InlineData("http://us.download.nvidia.com/x.exe", false)]
    [InlineData("https://download.nvidia.com.exemplo.com/x.exe", false)]
    [InlineData("https://notdownload.nvidia.com/x.exe", false)]
    public void OnlyNvidiaServersOverHttpsAreAllowed(string url, bool allowed) =>
        Assert.Equal(allowed, NvidiaDriverService.IsAllowedDownload(new Uri(url)));

    [Theory]
    [InlineData("617.42", "621.01", -1)]
    [InlineData("621.01", "617.42", 1)]
    [InlineData("621.01", "621.01", 0)]
    [InlineData("621.1", "621.01", 1)]
    [InlineData("1000.05", "999.99", 1)]
    public void NvidiaVersionsAreCompared(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(NvidiaDriverService.CompareVersions(a, b)));

    [Fact]
    public void NvidiaCardIsIdentifiedByPciDeviceId()
    {
        var nvidia = Gpu(@"PCI\VEN_10DE&DEV_2C02&SUBSYS_177419DA&REV_A1\F08ADEEBC62DB04800");
        var amd = Gpu(@"PCI\VEN_1002&DEV_164E&SUBSYS_7E241462&REV_CB\4&31D3501B&0&0041", "Advanced Micro Devices, Inc.", "32.0.21030.2001");
        Assert.Equal("2C02", NvidiaDriverService.DeviceIdOf(nvidia));
        Assert.True(NvidiaDriverService.IsNvidia(nvidia));
        Assert.Null(NvidiaDriverService.DeviceIdOf(amd));
        Assert.False(NvidiaDriverService.IsNvidia(amd));

        var uri = NvidiaDriverService.BuildLookupUri("2C02", 26200, 1046);
        string query = Uri.UnescapeDataString(uri.AbsoluteUri);
        Assert.StartsWith("https://gfwsl.geforce.com/", uri.AbsoluteUri);
        Assert.Contains("\"dIDa\":[\"2C02_10DE\"]", query);
        Assert.Contains("\"osB\":\"26200\"", query);
        Assert.Contains("\"lg\":\"1046\"", query);
    }

    [Fact]
    public async Task InterruptedDownloadResumesWhereItStopped()
    {
        var content = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 31)).ToArray();
        string dir = Directory.CreateTempSubdirectory("primepc-dl-").FullName;
        try
        {
            string dest = Path.Combine(dir, "driver.exe");
            File.WriteAllBytes(dest + ".part", content[..100_000]);
            var server = new FakeServer(content, supportsRange: true);
            using var http = new HttpClient(server);
            DownloadProgress? last = null;
            await DriverDownloader.DownloadAsync(http, new Uri("https://us.download.nvidia.com/x.exe"), dest, new SyncProgress(p => last = p), CancellationToken.None);

            Assert.Equal(100_000, server.LastRangeStart);
            Assert.Equal(content, File.ReadAllBytes(dest));
            Assert.False(File.Exists(dest + ".part"));
            Assert.Equal(content.Length, last!.Received);
            Assert.Equal(1.0, last.Fraction);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ServerWithoutResumeRestartsAndIncompleteDownloadIsKeptAsPart()
    {
        var content = Enumerable.Range(0, 50_000).Select(i => (byte)i).ToArray();
        string dir = Directory.CreateTempSubdirectory("primepc-dl-").FullName;
        try
        {
            string dest = Path.Combine(dir, "driver.exe");
            File.WriteAllBytes(dest + ".part", new byte[10_000]); // lixo de uma tentativa anterior
            using (var http = new HttpClient(new FakeServer(content, supportsRange: false)))
                await DriverDownloader.DownloadAsync(http, new Uri("https://us.download.nvidia.com/x.exe"), dest, null, CancellationToken.None);
            Assert.Equal(content, File.ReadAllBytes(dest));

            string cut = Path.Combine(dir, "cortado.exe");
            using (var http = new HttpClient(new FakeServer(content, supportsRange: false, truncateAt: 20_000)))
                await Assert.ThrowsAsync<IOException>(() =>
                    DriverDownloader.DownloadAsync(http, new Uri("https://us.download.nvidia.com/x.exe"), cut, null, CancellationToken.None));
            Assert.False(File.Exists(cut));
            Assert.Equal(20_000, new FileInfo(cut + ".part").Length);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SignatureMustBeValidAndFromTheExpectedCompany()
    {
        string dir = Directory.CreateTempSubdirectory("primepc-sig-").FullName;
        try
        {
            string unsigned = Path.Combine(dir, "sem-assinatura.exe");
            File.WriteAllBytes(unsigned, new byte[4096]);
            Assert.False(AuthenticodeVerifier.Verify(unsigned, NvidiaDriverService.Signer).IsTrusted);

            // O dotnet.exe é assinado pela Microsoft: vale como Microsoft, nunca como NVIDIA.
            string dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
            if (!File.Exists(dotnet))
                return;
            Assert.True(AuthenticodeVerifier.Verify(dotnet, "Microsoft Corporation").IsTrusted);
            var asNvidia = AuthenticodeVerifier.Verify(dotnet, NvidiaDriverService.Signer);
            Assert.False(asNvidia.IsTrusted);
            Assert.Equal("Microsoft Corporation", asNvidia.Organization);

            string tampered = Path.Combine(dir, "alterado.exe");
            var bytes = File.ReadAllBytes(dotnet);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(tampered, bytes);
            Assert.False(AuthenticodeVerifier.Verify(tampered, "Microsoft Corporation").IsTrusted);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WindowsUpdateCodesAreExplained()
    {
        Assert.Equal("concluído", WindowsUpdateDriverInstaller.ResultText(2));
        Assert.Equal("falhou", WindowsUpdateDriverInstaller.ResultText(4));
        Assert.Contains("administrador", WindowsUpdateDriverInstaller.HResultText(unchecked((int)0x80070005)));
        Assert.Contains("em andamento", WindowsUpdateDriverInstaller.HResultText(unchecked((int)0x80240016)));
        Assert.Equal("0x80001234", WindowsUpdateDriverInstaller.HResultText(unchecked((int)0x80001234)));
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }

    /// <summary>Servidor falso: entrega o conteúdo inteiro, ou a partir do byte pedido (Range), e pode cortar a conexão.</summary>
    private sealed class FakeServer(byte[] content, bool supportsRange, int? truncateAt = null) : HttpMessageHandler
    {
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            long start = 0;
            var status = HttpStatusCode.OK;
            if (supportsRange && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
            {
                LastRangeStart = from;
                start = from;
                status = HttpStatusCode.PartialContent;
            }

            var body = content[(int)start..];
            var sent = truncateAt is { } cut ? body[..Math.Min(cut, body.Length)] : body;
            var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(sent) };
            response.Content.Headers.ContentLength = body.Length; // anuncia o tamanho total, mesmo se cortar
            return Task.FromResult(response);
        }
    }
}

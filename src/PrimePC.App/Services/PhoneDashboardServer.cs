using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using PrimePC.Sensors;

namespace PrimePC.App.Services;

/// <summary>
/// Painel no celular: uma página simples, só leitura, servida na rede local. Mostra os sensores principais, o FPS do jogo
/// e o ping, atualizando a cada segundo. Só responde a quem tiver o código de acesso (incluído no endereço/QR code).
/// Não aceita nenhum comando: ninguém controla o PC pelo painel.
/// </summary>
public sealed partial class PhoneDashboardServer : ObservableObject, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly AppServices _services;
    private readonly GameMonitor _games;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private string? _page;

    public PhoneDashboardServer(AppServices services, GameMonitor games)
    {
        _services = services;
        _games = games;
        if (string.IsNullOrEmpty(Settings.AccessCode))
            Settings.AccessCode = NewCode();
    }

    public PhoneDashboardSettings Settings => _services.Settings.Phone;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Painel desligado.";
    [ObservableProperty] private SensorState _statusState = SensorState.Unavailable;
    [ObservableProperty] private string? _address;
    [ObservableProperty] private string _lastAccess = "Nenhum acesso ainda.";

    public void Apply()
    {
        Stop();
        if (!Settings.Enabled)
        {
            StatusText = "Painel desligado.";
            StatusState = SensorState.Unavailable;
            _games.SetPingWanted("celular", false);
            return;
        }

        string? ip = LocalAddress();
        try
        {
            _listener = new TcpListener(IPAddress.Any, Settings.Port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _listener = null;
            StatusText = $"Não foi possível usar a porta {Settings.Port}: {ex.Message}. Escolha outra porta.";
            StatusState = SensorState.Critical;
            return;
        }

        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_listener, _cts.Token);
        _games.SetPingWanted("celular", true, _services.Settings.Overlay.PingReferenceHost);
        IsRunning = true;
        Address = ip is null ? null : $"http://{ip}:{Settings.Port}/?k={Settings.AccessCode}";
        StatusText = ip is null
            ? "Painel ligado, mas nenhuma rede local foi encontrada (o PC está conectado por Wi-Fi ou cabo?)."
            : "Painel ligado. Abra o endereço (ou o QR code) no celular conectado à mesma rede Wi-Fi.";
        StatusState = ip is null ? SensorState.Warning : SensorState.Normal;
        Log.Info($"Painel no celular ligado na porta {Settings.Port}.");
    }

    public void RegenerateCode()
    {
        Settings.AccessCode = NewCode();
        _services.SaveSettings();
        Apply();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // já parado
        }

        _listener = null;
        IsRunning = false;
        Address = null;
    }

    public void Dispose() => Stop();

    private static string NewCode()
    {
        const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
        return string.Create(8, 0, (span, _) =>
        {
            for (int i = 0; i < span.Length; i++)
                span[i] = chars[RandomNumberGenerator.GetInt32(chars.Length)];
        });
    }

    /// <summary>
    /// Endereço IPv4 da rede local (Wi-Fi ou cabo com gateway). Ignora adaptadores virtuais e de VPN, que o celular não alcança.
    /// </summary>
    public static string? LocalAddress()
    {
        string[] skip = { "virtual", "vethernet", "vpn", "radmin", "hamachi", "wintun", "tap", "zerotier", "tailscale", "loopback", "bluetooth", "wsl", "hyper-v" };
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet
                        && !skip.Any(s => (n.Name + " " + n.Description).Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Select(n => n.GetIPProperties())
            .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .SelectMany(p => p.UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !Gaming.NetworkTracker.IsPublic(a) && !IPAddress.IsLoopback(a))
            .ToList();
        return candidates.FirstOrDefault()?.ToString();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, token), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                var stream = client.GetStream();
                string? requestLine = await ReadRequestLineAsync(stream, token);
                if (requestLine is null)
                    return;
                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || parts[0] != "GET")
                {
                    await WriteAsync(stream, 405, "text/plain", "Somente leitura.");
                    return;
                }

                var uri = new Uri("http://localhost" + parts[1]);
                string? code = System.Web.HttpUtility.ParseQueryString(uri.Query)["k"];
                bool authorized = code is not null && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(code), Encoding.UTF8.GetBytes(Settings.AccessCode));
                if (!authorized)
                {
                    await WriteAsync(stream, 403, "text/html; charset=utf-8",
                        "<meta name=viewport content='width=device-width'><body style='font-family:sans-serif;background:#08080a;color:#eee;padding:24px'>" +
                        "<h2>PrimePC</h2><p>Código de acesso inválido. Leia o QR code na aba Jogos › Celular do PrimePC.</p>");
                    return;
                }

                string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => LastAccess = $"Último acesso: {DateTime.Now:HH:mm:ss} de {remote}");
                if (uri.AbsolutePath == "/api")
                    await WriteAsync(stream, 200, "application/json; charset=utf-8", JsonSerializer.Serialize(BuildData(), Json));
                else if (uri.AbsolutePath is "/" or "/index.html")
                    await WriteAsync(stream, 200, "text/html; charset=utf-8", Page());
                else
                    await WriteAsync(stream, 404, "text/plain", "Não encontrado.");
            }
            catch
            {
                // conexão interrompida: ignora
            }
        }
    }

    private static async Task<string?> ReadRequestLineAsync(NetworkStream stream, CancellationToken token)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total), token);
            if (read == 0)
                break;
            total += read;
            string text = Encoding.ASCII.GetString(buffer, 0, total);
            if (text.Contains("\r\n\r\n"))
                return text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        }

        return null;
    }

    private static async Task WriteAsync(NetworkStream stream, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", _ => "Method Not Allowed" };
        string header = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                        "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bytes);
    }

    private string Page()
    {
        if (_page is null)
        {
            using var s = typeof(PhoneDashboardServer).Assembly.GetManifestResourceStream("PrimePC.painel.html")!;
            using var reader = new StreamReader(s, Encoding.UTF8);
            _page = reader.ReadToEnd();
        }

        return _page;
    }

    /// <summary>Dados do painel (sem nome do computador, usuário ou números de série).</summary>
    private object BuildData()
    {
        var snap = _services.Hub.Latest;
        var map = _services.KeyMap;
        var gpu = map.PrimaryGpu(snap, _services.Settings.PreferredGpuId);
        var game = _games.State;
        float? R(string? id) => snap.ValueOf(id) is { } v ? MathF.Round(v, 1) : null;
        string? S(string? id) => id is null ? null : snap.Find(id)?.State.ToString();
        return new
        {
            time = DateTime.Now.ToString("HH:mm:ss"),
            cpu = new { name = map.CpuName, load = R(map.CpuTotalLoad), temp = R(map.CpuTemperature), tempState = S(map.CpuTemperature), power = R(map.CpuPower), clock = map.AverageCpuClock(snap) is { } c ? MathF.Round(c) : (float?)null },
            gpu = new { name = gpu?.Name, load = R(gpu?.Load), temp = R(gpu?.Temperature), tempState = S(gpu?.Temperature), hotSpot = R(gpu?.HotSpot), clock = R(gpu?.CoreClock), power = R(gpu?.Power), vramUsed = R(gpu?.VramUsed), vramTotal = R(gpu?.VramTotal), fan = R(gpu?.Fan) },
            ram = new { load = R(map.RamLoad), used = R(map.RamUsed) },
            game = new { name = game.Name, fps = game.Frames is { } f ? Math.Round(f.Fps) : (double?)null, frameTime = game.Frames is { } f2 ? Math.Round(f2.FrameTimeMs, 1) : (double?)null, low1 = game.Frames?.Low1Fps is { } l ? Math.Round(l) : (double?)null, measuring = game.Measuring },
            ping = game.Ping is { } p ? new { ms = p.Milliseconds, target = p.Target == Gaming.PingTarget.GameServer ? "servidor" : "internet" } : null,
            unit = _services.Settings.TemperatureUnit == TemperatureUnit.Fahrenheit ? "F" : "C",
        };
    }
}

using System.Net;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace PrimePC.Gaming;

/// <summary>
/// Mede os quadros de todos os programas a partir dos eventos que o próprio Windows publica (ETW), o mesmo método do
/// PresentMon da Intel. Nada é injetado nos jogos (seguro com anti-cheat). Exige administrador.
/// Opcionalmente acompanha para quais endereços cada processo envia pacotes (para medir o ping do servidor do jogo).
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    public const string SessionName = "PrimePC-Sobreposicao";

    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private static readonly Guid DxgKrnlProvider = new("802EC45A-1E99-4B83-9920-87C98277BA9D");
    private static readonly Guid NetworkProvider = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    private const int DxgiPresentStart = 42;
    private const int D3D9PresentStart = 1;
    private const long DxgiPresentTest = 0x1;
    private const int DxgKrnlPresent = 184;
    private const int TcpSendV4 = 10, TcpSendV6 = 26, UdpSendV4 = 42, UdpSendV6 = 58;

    private TraceEventSession? _session;
    private Thread? _thread;
    private DateTime _epoch = DateTime.Now;
    private bool _networkEnabled;

    public FrameTracker Frames { get; } = new();
    public NetworkTracker Network { get; } = new();
    public bool IsRunning => _session is not null && Error is null;
    public string? Error { get; private set; }

    /// <summary>Milissegundos desde o início da medição (mesma base dos tempos dos quadros).</summary>
    public double NowMs => (DateTime.Now - _epoch).TotalMilliseconds;

    public bool Start(bool trackNetwork)
    {
        if (_session is not null)
        {
            SetNetworkTracking(trackNetwork);
            return IsRunning;
        }

        Error = null;
        if (TraceEventSession.IsElevated() != true)
        {
            Error = "Medir FPS exige abrir o PrimePC como administrador.";
            return false;
        }

        try
        {
            _epoch = DateTime.Now;
            var session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 16 };
            session.EnableProvider(DxgiProvider, TraceEventLevel.Informational, ulong.MaxValue, Only(DxgiPresentStart));
            session.EnableProvider(D3D9Provider, TraceEventLevel.Informational, ulong.MaxValue, Only(D3D9PresentStart));
            session.EnableProvider(DxgKrnlProvider, TraceEventLevel.Informational, 0x1UL | 0x8000000000000000UL, Only(DxgKrnlPresent));
            _session = session;
            SetNetworkTracking(trackNetwork);

            var parser = new RegisteredTraceEventParser(session.Source);
            parser.All += OnEvent;
            _thread = new Thread(() =>
            {
                try
                {
                    session.Source.Process();
                }
                catch (Exception ex)
                {
                    Error = $"A medição de quadros parou: {ex.Message}";
                }
            })
            { IsBackground = true, Name = "PrimePC quadros (ETW)" };
            _thread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Error = $"Não foi possível iniciar a medição de quadros: {ex.Message}";
            Stop();
            return false;
        }
    }

    public void SetNetworkTracking(bool enabled)
    {
        if (_session is null || enabled == _networkEnabled)
            return;
        if (enabled)
            _session.EnableProvider(NetworkProvider, TraceEventLevel.Informational, 0x10 | 0x20, Only(TcpSendV4, TcpSendV6, UdpSendV4, UdpSendV6));
        else
            _session.DisableProvider(NetworkProvider);
        _networkEnabled = enabled;
    }

    public FrameStats? GetStats(int pid) => Frames.GetStats(pid, NowMs);

    public bool IsNetworkTracking => _networkEnabled;

    public bool IsPresenting(int pid) => Frames.IsPresenting(pid, NowMs);

    public IPAddress? ServerOf(int pid) => Network.TopEndpoint(pid, NowMs);

    /// <summary>Manutenção periódica (chamar a cada segundo).</summary>
    public void Prune()
    {
        double now = NowMs;
        Frames.Prune(now);
        Network.Prune(now);
    }

    public void Stop()
    {
        var session = _session;
        _session = null;
        _networkEnabled = false;
        try
        {
            session?.Dispose();
        }
        catch
        {
            // a sessão pode já ter sido encerrada pelo Windows
        }

        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
    }

    public void Dispose() => Stop();

    private void OnEvent(TraceEvent e)
    {
        try
        {
            int id = (int)e.ID;
            Guid provider = e.ProviderGuid;
            // Cada chamada Present é um quadro (conferido contra a contagem da própria aplicação de teste, inclusive com a
            // janela encoberta). Chamadas com DXGI_PRESENT_TEST só perguntam se a janela está visível: não são quadros.
            if (provider == DxgiProvider && id == DxgiPresentStart)
            {
                if ((Convert.ToInt64(e.PayloadByName("Flags") ?? 0) & DxgiPresentTest) == 0)
                    Frames.OnPresent(e.ProcessID, PresentSource.Dxgi, U64(e.PayloadByName("pIDXGISwapChain")), Ms(e));
            }
            else if (provider == D3D9Provider && id == D3D9PresentStart)
            {
                Frames.OnPresent(e.ProcessID, PresentSource.D3D9, U64(e.PayloadByName("pSwapchain")), Ms(e));
            }
            else if (provider == DxgKrnlProvider && id == DxgKrnlPresent)
                Frames.OnPresent(e.ProcessID, PresentSource.Kernel, 0, Ms(e));
            else if (provider == NetworkProvider && id is TcpSendV4 or TcpSendV6 or UdpSendV4 or UdpSendV6)
            {
                int pid = e.PayloadByName("PID") is int p ? p : e.ProcessID;
                IPAddress? address = e.PayloadByName("daddr") switch
                {
                    int v4 => NetworkTracker.FromInt32(v4),
                    byte[] { Length: 16 } v6 => new IPAddress(v6),
                    byte[] { Length: 4 } v4b => new IPAddress(v4b),
                    _ => null,
                };
                Network.OnSend(pid, address, id is UdpSendV4 or UdpSendV6, Ms(e));
            }
        }
        catch
        {
            // um evento com formato inesperado não pode derrubar a medição
        }
    }

    private double Ms(TraceEvent e) => (e.TimeStamp - _epoch).TotalMilliseconds;

    private static ulong U64(object? v) => v switch
    {
        ulong u => u,
        long l => unchecked((ulong)l),
        int i => unchecked((uint)i),
        _ => 0,
    };

    private static TraceEventProviderOptions Only(params int[] ids) => new() { EventIDsToEnable = ids.ToList() };
}

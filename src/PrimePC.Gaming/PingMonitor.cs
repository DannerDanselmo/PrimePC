using System.Net;
using System.Net.NetworkInformation;

namespace PrimePC.Gaming;

public enum PingTarget
{
    /// <summary>Servidor do jogo (endereço para o qual o jogo mais envia pacotes).</summary>
    GameServer,

    /// <summary>Referência na internet, usada quando o servidor do jogo não responde ao ping ou não foi identificado.</summary>
    Internet,
}

/// <param name="Milliseconds">Tempo de ida e volta; nulo se não houve resposta.</param>
public sealed record PingResult(long? Milliseconds, PingTarget Target, string Address, DateTime At);

/// <summary>
/// Mede o ping uma vez por segundo. Tenta o servidor do jogo; muitos servidores bloqueiam o ping (ICMP), e nesse caso
/// mede a latência da internet até um endereço de referência — e informa qual dos dois foi medido.
/// </summary>
public sealed class PingMonitor : IDisposable
{
    private const int FailuresBeforeFallback = 3;
    private readonly Dictionary<IPAddress, (int Failures, DateTime RetryAt)> _servers = new();
    private CancellationTokenSource? _cts;

    public string ReferenceHost { get; set; } = "1.1.1.1";

    /// <summary>Fornece o servidor do jogo atual (ou nulo).</summary>
    public Func<IPAddress?>? ServerProvider { get; set; }

    public PingResult? Latest { get; private set; }
    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (_cts is not null)
            return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Latest = null;
    }

    public void Dispose() => Stop();

    private async Task LoopAsync(CancellationToken token)
    {
        using var ping = new Ping();
        while (!token.IsCancellationRequested)
        {
            var started = DateTime.Now;
            try
            {
                Latest = await MeasureAsync(ping) ?? Latest;
            }
            catch
            {
                // sem rede, endereço inválido etc.: tenta de novo no próximo segundo
            }

            var wait = TimeSpan.FromSeconds(1) - (DateTime.Now - started);
            try
            {
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<PingResult?> MeasureAsync(Ping ping)
    {
        if (ServerProvider?.Invoke() is { } server)
        {
            var state = _servers.GetValueOrDefault(server);
            if (state.Failures < FailuresBeforeFallback || DateTime.Now >= state.RetryAt)
            {
                var reply = await ping.SendPingAsync(server, 900);
                if (reply.Status == IPStatus.Success)
                {
                    _servers[server] = (0, default);
                    return new PingResult(reply.RoundtripTime, PingTarget.GameServer, server.ToString(), DateTime.Now);
                }

                int failures = state.Failures + 1;
                // Servidor que não responde ao ping: volta a tentar só daqui a um minuto.
                _servers[server] = (failures, failures >= FailuresBeforeFallback ? DateTime.Now.AddMinutes(1) : default);
            }
        }

        var internet = await ping.SendPingAsync(ReferenceHost, 900);
        return new PingResult(internet.Status == IPStatus.Success ? internet.RoundtripTime : null, PingTarget.Internet, ReferenceHost, DateTime.Now);
    }
}

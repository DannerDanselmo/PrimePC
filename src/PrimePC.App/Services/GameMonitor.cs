using System.Windows.Threading;
using PrimePC.Gaming;

namespace PrimePC.App.Services;

/// <summary>Estado do jogo a cada atualização (duas vezes por segundo).</summary>
/// <param name="Pid">Jogo acompanhado (apresentando quadros e em primeiro plano há pouco), ou nulo.</param>
/// <param name="InForeground">O jogo é a janela em primeiro plano agora.</param>
public sealed record GameState(int? Pid, string? Name, bool InForeground, FrameStats? Frames, PingResult? Ping, bool Measuring, string? Error)
{
    public static GameState None { get; } = new(null, null, false, null, null, false, null);
}

/// <summary>
/// Monitor central de jogos: mede os quadros de todos os programas (ETW, exige administrador), identifica o jogo em
/// primeiro plano e mede o ping quando algum recurso pede. Usado pela sobreposição, pelo benchmark, pela análise de
/// gargalo, pelas sessões de jogo e pelo painel no celular.
/// </summary>
public sealed class GameMonitor : IDisposable
{
    private readonly FrameMonitor _frames = new();
    private readonly PingMonitor _ping = new();
    private readonly DispatcherTimer _timer;
    private readonly HashSet<string> _pingUsers = new();
    private readonly int _ownPid = Environment.ProcessId;
    private int? _targetPid;
    private string? _targetName;
    private int _ticks;

    public GameMonitor()
    {
        _ping.ServerProvider = () => _targetPid is { } pid ? _frames.ServerOf(pid) : null;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => Tick();
    }

    public GameState State { get; private set; } = GameState.None;

    /// <summary>Último jogo detectado (continua conhecido depois que você volta ao PrimePC).</summary>
    public string? LastGameName { get; private set; }

    public int? LastGamePid { get; private set; }

    public bool IsMeasuring => _frames.IsRunning;

    /// <summary>Disparado na thread da interface a cada atualização.</summary>
    public event EventHandler<GameState>? Updated;

    public event EventHandler<string>? GameDetected;

    public void Start()
    {
        _frames.Start(_pingUsers.Count > 0);
        if (_frames.Error is { } error)
            Log.Info($"Medição de quadros indisponível: {error}");
        _timer.Start();
        Tick();
    }

    /// <summary>Liga/desliga o ping para um recurso (a sobreposição, o painel no celular...).</summary>
    public void SetPingWanted(string who, bool wanted, string referenceHost = "1.1.1.1")
    {
        if (wanted)
            _pingUsers.Add(who);
        else
            _pingUsers.Remove(who);
        _ping.ReferenceHost = string.IsNullOrWhiteSpace(referenceHost) ? "1.1.1.1" : referenceHost.Trim();
        _frames.SetNetworkTracking(_pingUsers.Count > 0);
        if (_pingUsers.Count > 0)
            _ping.Start();
        else
            _ping.Stop();
    }

    public FrameCapture? BeginCapture() => _frames.IsRunning ? _frames.Frames.BeginCapture() : null;

    public void EndCapture(FrameCapture capture) => _frames.Frames.EndCapture(capture);

    public string? MeasureError => _frames.Error;

    private void Tick()
    {
        try
        {
            int? foreground = ForegroundProcess.GetId();
            if (_frames.IsRunning)
            {
                if (foreground is { } fg && fg != _ownPid && _frames.IsPresenting(fg))
                {
                    if (_targetPid != fg)
                    {
                        _targetPid = fg;
                        _targetName = ForegroundProcess.NameOf(fg);
                        LastGamePid = fg;
                        LastGameName = _targetName;
                        Log.Info($"Jogo detectado: {_targetName} (PID {fg}).");
                        if (_targetName is not null)
                            GameDetected?.Invoke(this, _targetName);
                    }
                }
                else if (_targetPid is { } t && !_frames.IsPresenting(t))
                {
                    _targetPid = null;
                    _targetName = null;
                }

                if (++_ticks % 2 == 0)
                    _frames.Prune();
            }

            var stats = _targetPid is { } pid ? _frames.GetStats(pid) : null;
            State = new GameState(_targetPid, _targetName, _targetPid is not null && foreground == _targetPid, stats,
                _pingUsers.Count > 0 ? _ping.Latest : null, _frames.IsRunning, _frames.Error);
            Updated?.Invoke(this, State);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao atualizar o monitor de jogos", ex);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _frames.Dispose();
        _ping.Dispose();
    }
}

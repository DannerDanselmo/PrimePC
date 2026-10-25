using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PrimePC.App.ViewModels;
using PrimePC.App.Views;
using PrimePC.Gaming;
using PrimePC.Sensors;

namespace PrimePC.App.Services;

/// <summary>
/// Controla a sobreposição: usa o monitor de jogos (quadros, jogo em primeiro plano, ping) e os sensores de CPU/GPU,
/// aplica o perfil do jogo atual (se houver) e atende o atalho global que liga/desliga.
/// </summary>
public sealed partial class OverlayService : ObservableObject, IDisposable
{
    private const int HotkeyId = 0x5050;

    private readonly AppServices _services;
    private readonly GameMonitor _games;
    private readonly HotkeyService _hotkeys;
    private OverlayWindow? _window;
    private OverlayLayout? _appliedLayout;
    private DateTime _toastUntil;

    public OverlayService(AppServices services, GameMonitor games, HotkeyService hotkeys)
    {
        _services = services;
        _games = games;
        _hotkeys = hotkeys;
        _games.Updated += (_, state) => Tick(state);
        RegisterHotkey();
        Apply();
    }

    public OverlaySettings Settings => _services.Settings.Overlay;
    public OverlayDisplay Display { get; } = new();

    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _frameStatus = "";
    [ObservableProperty] private SensorState _frameStatusState = SensorState.Unavailable;
    [ObservableProperty] private string _pingStatus = "";
    [ObservableProperty] private string _hotkeyStatus = "";
    [ObservableProperty] private SensorState _hotkeyState = SensorState.Normal;

    /// <summary>Perfil do jogo detectado agora (ou do último jogo), se existir.</summary>
    [ObservableProperty] private OverlayProfile? _activeProfile;

    public event EventHandler? ProfilesChanged;

    public void SetEnabled(bool enabled)
    {
        if (Settings.Enabled == enabled)
            return;
        Settings.Enabled = enabled;
        _services.SaveSettings();
        Log.Info($"Sobreposição {(enabled ? "ligada" : "desligada")}.");
        Apply();
    }

    public void Toggle() => SetEnabled(!Settings.Enabled);

    /// <summary>Indicador de gravação do benchmark (aparece mesmo com a sobreposição desligada, se o jogo estiver na frente).</summary>
    public void SetRecording(string? text)
    {
        Display.RecordingText = text;
        Tick(_games.State);
    }

    /// <summary>Mostra uma mensagem curta na sobreposição (ex.: resultado do benchmark).</summary>
    public void ShowToast(string text, TimeSpan duration)
    {
        Display.ToastText = text;
        _toastUntil = DateTime.Now + duration;
        Tick(_games.State);
    }

    /// <summary>Chamado quando qualquer opção da sobreposição muda.</summary>
    public void SettingsChanged()
    {
        _services.SaveSettings();
        _appliedLayout = null;
        RegisterHotkey();
        Apply();
    }

    public OverlayProfile? ProfileFor(string? game) =>
        game is null ? null : Settings.Profiles.FirstOrDefault(p => p.Game.Equals(game, StringComparison.OrdinalIgnoreCase));

    /// <summary>Salva a aparência atual (opções gerais) como perfil do jogo, criando ou substituindo.</summary>
    public OverlayProfile SaveProfile(string game)
    {
        var profile = ProfileFor(game);
        if (profile is null)
        {
            profile = new OverlayProfile { Game = game };
            Settings.Profiles.Add(profile);
        }

        Settings.CopyLayoutTo(profile);
        Log.Info($"Perfil da sobreposição salvo para {game}.");
        SettingsChanged();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
        return profile;
    }

    public void RemoveProfile(OverlayProfile profile)
    {
        Settings.Profiles.Remove(profile);
        SettingsChanged();
        ProfilesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Apply()
    {
        IsEnabled = Settings.Enabled;
        bool anyAuto = Settings.Profiles.Any(p => p.AutoShow);
        _games.SetPingWanted("sobreposicao", (Settings.Enabled || anyAuto) && (Settings.ShowPing || Settings.Profiles.Any(p => p.ShowPing)),
            Settings.PingReferenceHost);
        if (_window is null)
        {
            _window = new OverlayWindow { DataContext = Display };
            _window.SourceInitialized += (_, _) => _window.SetHideFromCapture(Settings.HideFromCapture);
        }
        else
        {
            _window.SetHideFromCapture(Settings.HideFromCapture);
        }

        Tick(_games.State);
    }

    private void Tick(GameState state)
    {
        try
        {
            // Perfil do jogo em primeiro plano; sem jogo, o do último jogo vale só para a prévia.
            var profile = ProfileFor(state.Name ?? _games.LastGameName);
            ActiveProfile = profile;
            OverlayLayout layout = state.Name is not null && profile is not null ? profile : Settings;
            if (!ReferenceEquals(layout, _appliedLayout))
            {
                Display.ApplySettings(layout);
                _appliedLayout = layout;
            }

            Display.SetFrames(state.Frames);
            var snapshot = _services.Hub.Latest;
            var map = _services.KeyMap;
            Display.SetSensors(snapshot, map, map.PrimaryGpu(snapshot, _services.Settings.PreferredGpuId), _services.Settings.TemperatureUnit);
            Display.SetPing(layout.ShowPing ? state.Ping : null);
            Display.ClockText = DateTime.Now.ToString("HH:mm", CultureInfo.CurrentCulture);
            if (Display.ToastText is not null && DateTime.Now > _toastUntil)
                Display.ToastText = null;

            // Visível: ligada (só durante jogos, se marcado), perfil do jogo com "ligar sozinha", ou benchmark em andamento.
            // Sem a medição de quadros (sem administrador) não dá para saber o que é jogo, então segue só a opção geral.
            bool byGlobal = Settings.Enabled && (!Settings.OnlyInGames || !state.Measuring || state.InForeground);
            bool byProfile = state.InForeground && profile?.AutoShow == true;
            bool byBenchmark = state.InForeground && (Display.HasRecording || Display.HasToast);
            if (byGlobal || byProfile || byBenchmark)
                _window?.Place(layout.Corner);
            else
                _window?.Hide();

            UpdateStatus(state, profile);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao atualizar a sobreposição", ex);
        }
    }

    private void UpdateStatus(GameState state, OverlayProfile? profile)
    {
        if (!state.Measuring)
        {
            FrameStatus = (state.Error ?? "A medição de quadros não está ativa.") + " CPU, GPU e ping continuam funcionando.";
            FrameStatusState = SensorState.Warning;
        }
        else if (state.Frames is { } stats)
        {
            string api = stats.Source switch
            {
                PresentSource.Dxgi => "DirectX 10/11/12",
                PresentSource.D3D9 => "Direct3D 9",
                _ => "Vulkan/OpenGL (pelo kernel do Windows)",
            };
            FrameStatus = $"Medindo {state.Name ?? "jogo"} · {api} · {stats.Fps.ToString("0", CultureInfo.CurrentCulture)} FPS" +
                          (profile is not null ? " · usando o perfil deste jogo" : "");
            FrameStatusState = SensorState.Normal;
        }
        else
        {
            FrameStatus = "Medição ativa. Nenhum jogo em primeiro plano" +
                          (_games.LastGameName is { } last ? $" (último detectado: {last})." : ".");
            FrameStatusState = SensorState.Normal;
        }

        bool pingActive = Settings.Enabled || Settings.Profiles.Any(p => p.AutoShow);
        if (!Settings.ShowPing)
            PingStatus = "Ping desligado.";
        else if (!pingActive)
            PingStatus = "O ping é medido quando a sobreposição está ligada.";
        else if (state.Ping is not { } p)
            PingStatus = "Medindo o ping…";
        else if (p.Target == PingTarget.GameServer)
            PingStatus = $"PING = servidor do jogo ({p.Address}): {p.Milliseconds} ms.";
        else
            PingStatus = $"REDE = latência da internet ({p.Address}): {(p.Milliseconds is { } ms ? $"{ms} ms" : "sem resposta")}." +
                         (state.Pid is null ? " O ping do servidor aparece quando um jogo online estiver aberto e ativo."
                             : " O servidor do jogo não foi identificado ou não responde ao ping.");
    }

    public static string HotkeyText(OverlayHotkey h) => HotkeyService.Text(h);

    private void RegisterHotkey()
    {
        bool ok = _hotkeys.Register(HotkeyId, Settings.Hotkey, Toggle, SetHotkeyStatus);
        SetHotkeyStatus(ok);
    }

    private void SetHotkeyStatus(bool ok)
    {
        if (Settings.Hotkey == OverlayHotkey.None)
        {
            HotkeyStatus = "Sem atalho de teclado.";
            HotkeyState = SensorState.Normal;
            return;
        }

        HotkeyStatus = ok
            ? $"Atalho {HotkeyText(Settings.Hotkey)}: liga/desliga a sobreposição, inclusive dentro do jogo."
            : $"O atalho {HotkeyText(Settings.Hotkey)} já está sendo usado por outro programa. Escolha outro.";
        HotkeyState = ok ? SensorState.Normal : SensorState.Warning;
    }

    public void Dispose() => _window?.Close();
}

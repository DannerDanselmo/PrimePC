using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Página da sobreposição: liga/desliga, escolhe o que mostrar, posição, tamanho e atalho, com prévia ao vivo.</summary>
public sealed partial class OverlayViewModel : PageViewModel
{
    public OverlayViewModel(AppServices services) : base(services)
    {
        Service = services.Overlay ?? throw new InvalidOperationException("A sobreposição não foi iniciada.");
        var s = Service.Settings;
        _showFps = s.ShowFps;
        _showFrameTime = s.ShowFrameTime;
        _showLow1 = s.ShowLow1;
        _showGraph = s.ShowGraph;
        _showCpuLoad = s.ShowCpuLoad;
        _showCpuTemp = s.ShowCpuTemp;
        _showGpuLoad = s.ShowGpuLoad;
        _showGpuTemp = s.ShowGpuTemp;
        _showPing = s.ShowPing;
        _showCpuPower = s.ShowCpuPower;
        _showGpuClock = s.ShowGpuClock;
        _showGpuPower = s.ShowGpuPower;
        _showVram = s.ShowVram;
        _showRam = s.ShowRam;
        _showClock = s.ShowClock;
        _onlyInGames = s.OnlyInGames;
        _hideFromCapture = s.HideFromCapture;
        _backgroundOpacity = s.BackgroundOpacity;
        _referenceHost = s.PingReferenceHost;
        _selectedCorner = CornerOptions.First(c => c.Value == s.Corner);
        _selectedSize = SizeOptions.First(c => c.Value == s.Size);
        _selectedHotkey = HotkeyOptions.First(c => c.Value == s.Hotkey);

        Service.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverlayService.IsEnabled))
                OnPropertyChanged(nameof(ToggleText));
        };
        PropertyChanged += OnOwnPropertyChanged;

        Benchmark = new BenchmarkPanel(services);
        GameSessions = new SessionsPanel(services);
        Phone = new PhonePanel(services);
        Tabs = new[] { new GameTab("Sobreposição", "\uE7F4"), new GameTab("Benchmark", "\uEC4A"), new GameTab("Sessões de jogo", "\uE81C"), new GameTab("Celular", "\uE8EA") };
        _selectedTab = Tabs[0];
        Benchmark.CountChanged += (_, _) => Tabs[1].Count = Benchmark.Results.Count;
        GameSessions.CountChanged += (_, _) => Tabs[2].Count = GameSessions.Count;
        Tabs[1].Count = Benchmark.Results.Count;
        Tabs[2].Count = GameSessions.Count;
        Service.ProfilesChanged += (_, _) => LoadProfiles();
        services.Games!.Updated += (_, state) => OnGameUpdate(state);
        LoadProfiles();
    }

    public BenchmarkPanel Benchmark { get; }
    public SessionsPanel GameSessions { get; }
    public PhonePanel Phone { get; }
    public IReadOnlyList<GameTab> Tabs { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayTab), nameof(IsBenchmarkTab), nameof(IsSessionsTab), nameof(IsPhoneTab))]
    private GameTab _selectedTab;

    public bool IsOverlayTab => SelectedTab == Tabs[0];
    public bool IsBenchmarkTab => SelectedTab == Tabs[1];
    public bool IsSessionsTab => SelectedTab == Tabs[2];
    public bool IsPhoneTab => SelectedTab == Tabs[3];

    partial void OnSelectedTabChanged(GameTab value)
    {
        if (value is null)
            SelectedTab = Tabs[0];
    }

    // ---------------- Perfis por jogo ----------------

    public ObservableCollection<ProfileItem> Profiles { get; } = new();

    [ObservableProperty] private string _profileGameText = "Nenhum jogo detectado ainda. Abra um jogo para salvar um perfil para ele.";
    [ObservableProperty] private bool _canSaveProfile;

    private void OnGameUpdate(Services.GameState state)
    {
        string? game = state.Name ?? Services.Games?.LastGameName;
        CanSaveProfile = game is not null;
        if (game is not null)
            ProfileGameText = Service.ProfileFor(game) is not null
                ? $"Jogo detectado: {game}. Ele já tem um perfil; salvar de novo substitui pelo visual atual."
                : $"Jogo detectado: {game}. Salve o visual atual como perfil deste jogo.";
        if (IsSessionsTab)
            GameSessions.Refresh();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if ((Services.Games?.State.Name ?? Services.Games?.LastGameName) is { } game)
            Service.SaveProfile(game);
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in Service.Settings.Profiles.OrderBy(p => p.Game, StringComparer.OrdinalIgnoreCase))
            Profiles.Add(new ProfileItem(p, Service));
    }

    public override string Title => "Jogos";
    public override string Subtitle => "Sobreposição, benchmark, sessões de jogo e painel no celular";
    public override string Glyph => "";

    public OverlayService Service { get; }
    public OverlayDisplay Display => Service.Display;
    public bool IsAdministrator { get; } = PrivilegeInfo.IsAdministrator;
    public string ToggleText => Service.IsEnabled ? "Desligar sobreposição" : "Ligar sobreposição";

    public IReadOnlyList<Choice<OverlayCorner>> CornerOptions { get; } = new[]
    {
        new Choice<OverlayCorner>(OverlayCorner.TopLeft, "Canto superior esquerdo"),
        new Choice<OverlayCorner>(OverlayCorner.TopRight, "Canto superior direito"),
        new Choice<OverlayCorner>(OverlayCorner.BottomLeft, "Canto inferior esquerdo"),
        new Choice<OverlayCorner>(OverlayCorner.BottomRight, "Canto inferior direito"),
    };

    public IReadOnlyList<Choice<OverlaySize>> SizeOptions { get; } = new[]
    {
        new Choice<OverlaySize>(OverlaySize.Small, "Pequeno"),
        new Choice<OverlaySize>(OverlaySize.Medium, "Médio"),
        new Choice<OverlaySize>(OverlaySize.Large, "Grande"),
    };

    public IReadOnlyList<Choice<OverlayHotkey>> HotkeyOptions { get; } =
        Enum.GetValues<OverlayHotkey>().Select(h => new Choice<OverlayHotkey>(h, OverlayService.HotkeyText(h))).ToArray();

    [ObservableProperty] private bool _showFps;
    [ObservableProperty] private bool _showFrameTime;
    [ObservableProperty] private bool _showLow1;
    [ObservableProperty] private bool _showGraph;
    [ObservableProperty] private bool _showCpuLoad;
    [ObservableProperty] private bool _showCpuTemp;
    [ObservableProperty] private bool _showGpuLoad;
    [ObservableProperty] private bool _showGpuTemp;
    [ObservableProperty] private bool _showPing;
    [ObservableProperty] private bool _showCpuPower;
    [ObservableProperty] private bool _showGpuClock;
    [ObservableProperty] private bool _showGpuPower;
    [ObservableProperty] private bool _showVram;
    [ObservableProperty] private bool _showRam;
    [ObservableProperty] private bool _showClock;
    [ObservableProperty] private bool _onlyInGames;
    [ObservableProperty] private bool _hideFromCapture;
    [ObservableProperty] private int _backgroundOpacity;
    [ObservableProperty] private string _referenceHost;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviewHorizontal), nameof(PreviewVertical))]
    private Choice<OverlayCorner> _selectedCorner;

    [ObservableProperty] private Choice<OverlaySize> _selectedSize;
    [ObservableProperty] private Choice<OverlayHotkey> _selectedHotkey;

    public HorizontalAlignment PreviewHorizontal =>
        SelectedCorner.Value is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? HorizontalAlignment.Left : HorizontalAlignment.Right;

    public VerticalAlignment PreviewVertical =>
        SelectedCorner.Value is OverlayCorner.TopLeft or OverlayCorner.TopRight ? VerticalAlignment.Top : VerticalAlignment.Bottom;

    [RelayCommand]
    private void Toggle() => Service.Toggle();

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Aguarde o término de: {Services.RunningTest}.");
            return;
        }

        if (Shell.RestartElevated())
            ((App)Application.Current).ExitApplication();
    }

    /// <summary>Copia a opção alterada para as configurações e aplica na hora.</summary>
    private void OnOwnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        var s = Service.Settings;
        switch (e.PropertyName)
        {
            case nameof(ShowFps): s.ShowFps = ShowFps; break;
            case nameof(ShowFrameTime): s.ShowFrameTime = ShowFrameTime; break;
            case nameof(ShowLow1): s.ShowLow1 = ShowLow1; break;
            case nameof(ShowGraph): s.ShowGraph = ShowGraph; break;
            case nameof(ShowCpuLoad): s.ShowCpuLoad = ShowCpuLoad; break;
            case nameof(ShowCpuTemp): s.ShowCpuTemp = ShowCpuTemp; break;
            case nameof(ShowGpuLoad): s.ShowGpuLoad = ShowGpuLoad; break;
            case nameof(ShowGpuTemp): s.ShowGpuTemp = ShowGpuTemp; break;
            case nameof(ShowPing): s.ShowPing = ShowPing; break;
            case nameof(ShowCpuPower): s.ShowCpuPower = ShowCpuPower; break;
            case nameof(ShowGpuClock): s.ShowGpuClock = ShowGpuClock; break;
            case nameof(ShowGpuPower): s.ShowGpuPower = ShowGpuPower; break;
            case nameof(ShowVram): s.ShowVram = ShowVram; break;
            case nameof(ShowRam): s.ShowRam = ShowRam; break;
            case nameof(ShowClock): s.ShowClock = ShowClock; break;
            case nameof(OnlyInGames): s.OnlyInGames = OnlyInGames; break;
            case nameof(HideFromCapture): s.HideFromCapture = HideFromCapture; break;
            case nameof(BackgroundOpacity): s.BackgroundOpacity = Math.Clamp(BackgroundOpacity, 0, 100); break;
            case nameof(ReferenceHost): s.PingReferenceHost = ReferenceHost.Trim(); break;
            case nameof(SelectedCorner): s.Corner = SelectedCorner.Value; break;
            case nameof(SelectedSize): s.Size = SelectedSize.Value; break;
            case nameof(SelectedHotkey): s.Hotkey = SelectedHotkey.Value; break;
            default:
                return;
        }

        Service.SettingsChanged();
    }
}

/// <summary>Perfil de um jogo na lista (liga sozinha / remover).</summary>
public sealed partial class ProfileItem : ObservableObject
{
    private readonly OverlayService _service;

    public ProfileItem(OverlayProfile profile, OverlayService service)
    {
        Profile = profile;
        _service = service;
        _autoShow = profile.AutoShow;
    }

    public OverlayProfile Profile { get; }
    public string Game => Profile.Game;

    public string Summary => string.Join(", ", new[]
    {
        Profile.ShowFps ? "FPS" : null, Profile.ShowFrameTime ? "ms" : null, Profile.ShowLow1 ? "1% low" : null,
        Profile.ShowCpuLoad || Profile.ShowCpuTemp ? "CPU" : null, Profile.ShowGpuLoad || Profile.ShowGpuTemp ? "GPU" : null,
        Profile.ShowVram ? "VRAM" : null, Profile.ShowRam ? "RAM" : null, Profile.ShowPing ? "ping" : null,
    }.Where(x => x is not null)) + Profile.Corner switch
    {
        OverlayCorner.TopRight => " · canto superior direito",
        OverlayCorner.BottomLeft => " · canto inferior esquerdo",
        OverlayCorner.BottomRight => " · canto inferior direito",
        _ => " · canto superior esquerdo",
    };

    [ObservableProperty] private bool _autoShow;

    partial void OnAutoShowChanged(bool value)
    {
        Profile.AutoShow = value;
        _service.SettingsChanged();
    }

    [RelayCommand]
    private void Remove()
    {
        if (Shell.Confirm($"Remover o perfil de {Game}? A sobreposição volta a usar as opções gerais neste jogo."))
            _service.RemoveProfile(Profile);
    }
}

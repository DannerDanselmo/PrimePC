using System.Collections.ObjectModel;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Janela principal: navegação, avisos, alertas e distribuição das leituras para as telas.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dispatcher _dispatcher;
    private SensorSnapshot _pendingSnapshot = SensorSnapshot.Empty;
    private int _dispatchPending;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _dispatcher = Application.Current.Dispatcher;

        Overview = new OverviewViewModel(services, this);
        Sensors = new SensorsViewModel(services);
        Pages = new ObservableCollection<PageViewModel>
        {
            Overview,
            Sensors,
            new BottleneckViewModel(services),
            new RamTestViewModel(services),
            new StabilityViewModel(services),
            new StorageViewModel(services),
            new DriversViewModel(services),
            new OverlayViewModel(services),
            new ReportsViewModel(services),
            new SettingsViewModel(services),
        };
        _currentPage = Overview;
        Overview.IsActive = true;

        IsAdministrator = PrivilegeInfo.IsAdministrator;
        IsPawnIoInstalled = PrivilegeInfo.IsPawnIoInstalled;

        services.Hub.SnapshotUpdated += OnSnapshotFromHub;
        services.Hub.Ready += (_, _) => _dispatcher.BeginInvoke(BuildBanners);
        services.Hub.AlertRaised += (_, e) => _dispatcher.BeginInvoke(() => OnAlert(e));
        BuildBanners();
    }

    public ObservableCollection<PageViewModel> Pages { get; }
    public OverviewViewModel Overview { get; }
    public SensorsViewModel Sensors { get; }
    public ObservableCollection<BannerItem> Banners { get; } = new();

    [ObservableProperty]
    private PageViewModel _currentPage;

    [ObservableProperty]
    private bool _isAdministrator;

    [ObservableProperty]
    private bool _isPawnIoInstalled;

    [ObservableProperty]
    private string _statusText = "Iniciando a leitura dos sensores…";

    [ObservableProperty]
    private string? _alertMessage;

    [ObservableProperty]
    private bool _alertIsCritical;

    public string AccessText => IsAdministrator ? "Administrador" : "Sem administrador";
    public string DriverText => IsPawnIoInstalled ? "PawnIO instalado" : "PawnIO ausente";

    partial void OnCurrentPageChanged(PageViewModel? oldValue, PageViewModel newValue)
    {
        if (oldValue is not null)
            oldValue.IsActive = false;
        newValue.IsActive = true;
        newValue.OnActivated();
        if (_services.Hub.Latest.Readings.Count > 0)
            newValue.OnSnapshot(_services.Hub.Latest);
    }

    public void Navigate<T>() where T : PageViewModel
    {
        var page = Pages.OfType<T>().FirstOrDefault();
        if (page is not null)
            CurrentPage = page;
    }

    private void OnSnapshotFromHub(object? sender, SensorSnapshot snapshot)
    {
        // Agrupa atualizações: se a interface ainda não processou a anterior, entrega só a mais recente.
        _pendingSnapshot = snapshot;
        if (Interlocked.Exchange(ref _dispatchPending, 1) == 0)
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _dispatchPending, 0);
                Deliver(_pendingSnapshot);
            });
        }
    }

    private void Deliver(SensorSnapshot snapshot)
    {
        try
        {
            StatusText = $"{snapshot.Readings.Count} sensores · atualizado às {snapshot.Timestamp:HH:mm:ss}";
            Overview.OnSnapshot(snapshot);
            if (!ReferenceEquals(CurrentPage, Overview))
                CurrentPage.OnSnapshot(snapshot);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao atualizar a interface.", ex);
        }
    }

    private void OnAlert(AlertEvent alert)
    {
        AlertMessage = $"{alert.Time:HH:mm:ss} · {alert.Message}";
        AlertIsCritical = alert.IsCritical;
        Log.Info(alert.Message);
        if (_services.Settings.AlertSound)
            SystemSounds.Exclamation.Play();
    }

    [RelayCommand]
    private void DismissAlert() => AlertMessage = null;

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (_services.RunningTest is not null)
        {
            Shell.Info($"Aguarde o término do teste em andamento ({_services.RunningTest}) antes de reiniciar o programa.");
            return;
        }

        if (Shell.RestartElevated())
            ((App)Application.Current).ExitApplication();
    }

    [RelayCommand]
    private void ShowPawnIoInfo()
    {
        bool open = Shell.Confirm(
            "O PawnIO é um driver de código aberto e assinado, usado pelo LibreHardwareMonitor para ler sensores de baixo nível: " +
            "temperatura, clocks e potência da CPU, ventoinhas, tensões e temperaturas da placa-mãe.\n\n" +
            "Por segurança, o PrimePC nunca instala drivers sozinho. Se quiser esses sensores, instale o PawnIO pelo site oficial " +
            "e depois abra o PrimePC como administrador. Para remover, desinstale-o em Configurações > Aplicativos do Windows.\n\n" +
            "Deseja abrir o site oficial do PawnIO agora?",
            "Driver PawnIO");
        if (open)
            Shell.Open(PrivilegeInfo.PawnIoWebsite);
    }

    private void BuildBanners()
    {
        Banners.Clear();
        if (_services.UnfinishedRun is { } run)
        {
            Banners.Add(new BannerItem("Critical", "Teste interrompido inesperadamente",
                $"O teste \"{run.TestName}\" iniciado em {run.StartedAt:dd/MM/yyyy HH:mm} não terminou normalmente. " +
                "Se o computador travou ou reiniciou durante o teste, isso pode indicar instabilidade sob carga (temperatura, " +
                "fonte, memória ou overclock).") { Key = "unfinished" });
        }

        if (!IsPawnIoInstalled)
        {
            Banners.Add(new BannerItem("Warning", "Sensores de baixo nível indisponíveis",
                "Sem o driver PawnIO, temperatura/clock/potência da CPU e os sensores da placa-mãe aparecem como \"Não disponível\". " +
                "O PrimePC não instala drivers automaticamente.")
            {
                ActionText = "Saiba mais",
                Action = ShowPawnIoInfo,
                Key = "pawnio",
            });
        }

        if (!IsAdministrator)
        {
            Banners.Add(new BannerItem(IsPawnIoInstalled ? "Warning" : "Info", "Executando sem privilégios de administrador",
                IsPawnIoInstalled
                    ? "O driver PawnIO está instalado: abra como administrador para ver temperatura e potência da CPU, ventoinhas, " +
                      "tensões e temperaturas da placa-mãe, temperatura da RAM e dados SMART dos discos."
                    : "Dados SMART/saúde dos discos e sensores de baixo nível exigem administrador. O restante funciona normalmente.")
            {
                ActionText = "Reiniciar como administrador",
                Action = RestartAsAdministrator,
                Key = "admin",
            });
        }
    }

    [RelayCommand]
    private void DismissBanner(BannerItem banner) => Banners.Remove(banner);

    [RelayCommand]
    private void RunBannerAction(BannerItem banner) => banner.Action?.Invoke();
}

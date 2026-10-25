using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PrimePC.App.Services;
using PrimePC.App.ViewModels;

namespace PrimePC.App;

public partial class App : Application
{
    private AppServices? _services;
    private HotkeyService? _hotkeys;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Antes de tudo (inclusive do log): traz os dados do nome anterior (PulsoPC), se for a primeira execução.
        string? migrated = AppPaths.MigrateLegacyData();
        Log.Init();
        if (migrated is not null)
            Log.Info(migrated);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Exceção não tratada", ex.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            Log.Error("Exceção em tarefa não observada", ex.Exception);
            ex.SetObserved();
        };

        // Formatação de números e datas nas ligações (bindings) segue o idioma do Windows.
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

        _services = AppServices.Create();
        ThemeService.Apply(_services.Settings.Theme);
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/PrimePC;component/Themes/Styles.xaml"),
        });

        _services.Start();
        _hotkeys = new HotkeyService();
        _services.Games = new GameMonitor();
        _services.Games.Start();
        _services.Overlay = new OverlayService(_services, _services.Games, _hotkeys);
        _services.Benchmark = new GameBenchmarkService(_services, _services.Games, _services.Overlay, _hotkeys);
        _services.GameSessions = new GameSessionRecorder(_services, _services.Games);
        _services.Phone = new PhoneDashboardServer(_services, _services.Games);
        _services.Phone.Apply();
        bool startWithWindows = _services.Settings.StartWithWindows;
        Task.Run(() =>
        {
            if (StartupTask.MigrateLegacy(startWithWindows) is { } note)
                Log.Info(note);
        });

        var main = new MainViewModel(_services);
        var window = new MainWindow { DataContext = main };
        ApplyCommandLine(e.Args, main, window);
        MainWindow = window;
        window.Closing += OnMainWindowClosing;
        _tray = new TrayService(
            open: ShowMainWindow,
            toggleOverlay: _services.Overlay.Toggle,
            overlayOn: () => _services.Settings.Overlay.Enabled,
            toggleBenchmark: _services.Benchmark.Toggle,
            benchmarkRunning: () => _services.Benchmark.IsRunning,
            exit: ExitApplication);

        // "--bandeja": aberto pela inicialização do Windows; fica só perto do relógio até o usuário abrir.
        if (e.Args.Any(a => a.Equals("--bandeja", StringComparison.OrdinalIgnoreCase)))
            Log.Info("Iniciado perto do relógio (inicialização com o Windows).");
        else
            window.Show();
    }

    private TrayService? _tray;
    private bool _exiting;
    private bool _trayHintShown;

    public void ShowMainWindow()
    {
        var window = MainWindow;
        if (window is null)
            return;
        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Sai de verdade (menu do ícone perto do relógio).</summary>
    public void ExitApplication()
    {
        _exiting = true;
        Shutdown();
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting || _services?.Settings.CloseToTray != true)
            return;
        // Continua rodando perto do relógio (sobreposição, sessões de jogo, painel no celular).
        e.Cancel = true;
        MainWindow?.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray?.ShowBalloon("O PrimePC continua aberto", "Ele está perto do relógio. Clique no ícone para abrir, ou use o menu dele para sair.");
        }
    }

    /// <summary>
    /// Opções de linha de comando: --aba=N abre direto na aba N (0 = Visão geral … 6 = Drivers, 7 = Jogos … 9 = Configurações);
    /// --largura=N e --altura=N definem o tamanho inicial da janela; --bandeja inicia só com o ícone perto do relógio.
    /// </summary>
    private static void ApplyCommandLine(string[] args, MainViewModel main, Window window)
    {
        foreach (var arg in args)
        {
            var parts = arg.Split('=', 2);
            if (parts.Length != 2 || !int.TryParse(parts[1], out int value))
                continue;
            switch (parts[0].ToLowerInvariant())
            {
                case "--aba" when value >= 0 && value < main.Pages.Count:
                    main.CurrentPage = main.Pages[value];
                    break;
                case "--largura" when value >= 800:
                    window.Width = value;
                    break;
                case "--altura" when value >= 600:
                    window.Height = value;
                    break;
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _tray?.Dispose();
            _services?.GameSessions?.Dispose();
            _services?.Phone?.Dispose();
            _services?.Overlay?.Dispose();
            _services?.Games?.Dispose();
            _hotkeys?.Dispose();
            _services?.SaveSettings();
            _services?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao encerrar", ex);
        }

        Log.Info("PrimePC encerrado.");
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Erro na interface", e.Exception);
        e.Handled = true;

        // Um erro de layout pode se repetir a cada quadro: mostra no máximo um aviso por vez e não repete a mesma mensagem.
        string message = e.Exception.Message;
        if (_showingError || message == _lastErrorShown)
            return;
        _showingError = true;
        _lastErrorShown = message;
        try
        {
            MessageBox.Show(
                $"Ocorreu um erro inesperado:\n\n{message}\n\nDetalhes técnicos foram gravados no log:\n{Log.CurrentFile}",
                "PrimePC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    private bool _showingError;
    private string? _lastErrorShown;
}

using PrimePC.Diagnostics;
using PrimePC.GpuTesting;
using PrimePC.Sensors;
using PrimePC.Sensors.Inventory;
using PrimePC.Sensors.Sources;
using PrimePC.Testing;

namespace PrimePC.App.Services;

/// <summary>
/// Raiz de composição: cria e conecta os módulos independentes (sensores, diagnóstico, testes, relatórios)
/// e guarda o estado compartilhado pelas telas.
/// </summary>
public sealed class AppServices : IDisposable
{
    private AppServices()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
        Evaluator = new SensorStateEvaluator { LowLevelAccess = LowLevelAccess };
        Alerts = new AlertEngine { AlertOnCriticalState = Settings.AlertOnCriticalState };
        Alerts.SetRules(Settings.AlertRules);
        Hub = new SensorHub(new ISensorSource[] { new LibreHardwareMonitorSource(), new WindowsSystemSource() }, Evaluator, Alerts);
        ApplySensorSettings();

        Sessions = new BottleneckSessionStore(AppPaths.Sessions);
        Journal = new TestRunJournal(AppPaths.DataRoot);
        UnfinishedRun = Journal.TakeUnfinished();
        RamHistory = new JsonHistoryStore<MemoryTestResult>(Path.Combine(AppPaths.Results, "teste-ram.json"), 50);
        CpuHistory = new JsonHistoryStore<CpuStressResult>(Path.Combine(AppPaths.Results, "teste-cpu.json"), 50);
        StorageHistory = new JsonHistoryStore<StorageBenchmarkResult>(Path.Combine(AppPaths.Results, "teste-armazenamento.json"), 100);
        BenchmarkHistory = new JsonHistoryStore<BenchmarkResult>(Path.Combine(AppPaths.Results, "benchmarks.json"), 300);
        GpuStressHistory = new JsonHistoryStore<GpuStressResult>(Path.Combine(AppPaths.Results, "teste-gpu.json"), 50);
        VramHistory = new JsonHistoryStore<VramTestResult>(Path.Combine(AppPaths.Results, "teste-vram.json"), 50);

        Hub.SensorSetChanged += (_, _) => RebuildKeyMap();
    }

    public static AppServices Current { get; private set; } = null!;

    public AppSettings Settings { get; }
    public SettingsStore SettingsStore { get; }
    public SensorStateEvaluator Evaluator { get; }
    public AlertEngine Alerts { get; }
    public SensorHub Hub { get; }
    public BottleneckSessionStore Sessions { get; }
    public TestRunJournal Journal { get; }
    public UnfinishedTestRun? UnfinishedRun { get; }
    public JsonHistoryStore<MemoryTestResult> RamHistory { get; }
    public JsonHistoryStore<CpuStressResult> CpuHistory { get; }
    public JsonHistoryStore<StorageBenchmarkResult> StorageHistory { get; }
    public JsonHistoryStore<BenchmarkResult> BenchmarkHistory { get; }
    public JsonHistoryStore<GpuStressResult> GpuStressHistory { get; }
    public JsonHistoryStore<VramTestResult> VramHistory { get; }

    private IReadOnlyList<GpuDeviceInfo>? _computeDevices;

    /// <summary>Placas de vídeo disponíveis para os testes de GPU/VRAM (DirectX 12), carregadas sob demanda.</summary>
    public IReadOnlyList<GpuDeviceInfo> ComputeDevices => _computeDevices ??= GpuDevices.List();

    /// <summary>Sensores da GPU com o mesmo nome do dispositivo de computação (para temperatura, clock e potência).</summary>
    public GpuSensorSet? SensorsForGpu(string? deviceName)
    {
        if (deviceName is null)
            return null;
        var gpus = KeyMap.Gpus;
        return gpus.FirstOrDefault(g => g.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase))
               ?? gpus.FirstOrDefault(g => g.Name.Contains(deviceName, StringComparison.OrdinalIgnoreCase)
                                           || deviceName.Contains(g.Name, StringComparison.OrdinalIgnoreCase));
    }

    public HardwareInventory? Inventory { get; private set; }
    public KeySensorMap KeyMap { get; private set; } = KeySensorMap.Empty;

    /// <summary>Última lista de drivers coletada nesta execução (usada também no relatório).</summary>
    public Drivers.DriverScan? LastDriverScan { get; set; }

    /// <summary>Última consulta ao Windows Update por drivers nesta execução.</summary>
    public Drivers.DriverUpdateSearch? LastDriverUpdateSearch { get; set; }

    /// <summary>Sessão de gargalo mais recente analisada (usada como padrão nos relatórios).</summary>
    public BottleneckSession? LastBottleneckSession { get; set; }

    /// <summary>Indica se um teste intensivo está em execução (evita testes concorrentes acidentais).</summary>
    public string? RunningTest { get; set; }

    /// <summary>Monitor de jogos: quadros (FPS) e ping, compartilhado pela sobreposição e pela análise de gargalo.</summary>
    public GameMonitor? Games { get; set; }

    /// <summary>Sobreposição dos jogos (criada pela aplicação, pois usa janelas e o atalho de teclado).</summary>
    public OverlayService? Overlay { get; set; }

    public GameBenchmarkService? Benchmark { get; set; }

    /// <summary>Registro automático das sessões de jogo (temperaturas e FPS de cada partida).</summary>
    public GameSessionRecorder? GameSessions { get; set; }

    public PhoneDashboardServer? Phone { get; set; }

    public static bool LowLevelAccess => PrivilegeInfo.IsPawnIoInstalled && PrivilegeInfo.IsAdministrator;

    public event EventHandler? InventoryReady;
    public event EventHandler? KeyMapChanged;

    /// <summary>Favoritos ou sensores ocultos foram alterados (para sincronizar o painel inicial).</summary>
    public event EventHandler? FavoritesChanged;

    public void NotifyFavoritesChanged()
    {
        SaveSettings();
        FavoritesChanged?.Invoke(this, EventArgs.Empty);
    }

    public static AppServices Create() => Current = new AppServices();

    public void Start()
    {
        Hub.Start();
        RefreshInventory();
    }

    /// <summary>Coleta (ou recoleta) o inventário de hardware em segundo plano e avisa as telas.</summary>
    public void RefreshInventory() => Task.Run(() =>
    {
        try
        {
            Inventory = HardwareInventoryCollector.Collect();
            Evaluator.CpuModel = Inventory.Cpu.Name;
            Log.Info($"Inventário coletado: {Inventory.Cpu.Name}; {Inventory.Gpus.Count} GPU(s); {Inventory.Disks.Count} disco(s).");
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao coletar o inventário de hardware.", ex);
        }

        InventoryReady?.Invoke(this, EventArgs.Empty);
    });

    public void ApplySensorSettings()
    {
        Hub.Interval = TimeSpan.FromMilliseconds(Settings.UpdateIntervalMs);
        Hub.HistoryCapacity = Math.Max(30, Settings.HistoryMinutes * 60_000 / Math.Max(250, Settings.UpdateIntervalMs));
        Alerts.AlertOnCriticalState = Settings.AlertOnCriticalState;
    }

    public void SaveSettings() => SettingsStore.Save(Settings);

    /// <summary>Temperatura atual da CPU (para o corte térmico dos testes), ou nulo se indisponível.</summary>
    public float? CurrentCpuTemperature() => Hub.Latest.ValueOf(KeyMap.CpuTemperature);

    private void RebuildKeyMap()
    {
        var map = KeySensorMap.Build(Hub.Latest.Readings);
        KeyMap = map;
        if (Evaluator.CpuModel is null && map.CpuName is not null)
            Evaluator.CpuModel = map.CpuName;
        KeyMapChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Hub.Dispose();
}

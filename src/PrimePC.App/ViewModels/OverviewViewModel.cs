using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.Sensors;
using PrimePC.Sensors.Inventory;
using PrimePC.Sensors.Sources;

namespace PrimePC.App.ViewModels;

public sealed record InfoRow(string Label, string Value);

public sealed record MemoryModuleRow(string Slot, string Bank, string Capacity, string Type, string Configured, string Rated, string Manufacturer, string Model);

public sealed record GpuRow(string Name, string Vendor, string Memory, string Driver, string DriverDate, string Mode);

/// <summary>Resumo de um disco no painel inicial.</summary>
public sealed partial class DiskSummary : ObservableObject
{
    public DiskSummary(string name, string detail, string? health, string volumes)
    {
        Name = name;
        Detail = detail;
        HealthText = health ?? "Não disponível";
        Volumes = volumes;
    }

    public string Name { get; }
    public string Detail { get; }
    public string HealthText { get; }
    public string Volumes { get; }
    public DiskSensorSet? Sensors { get; set; }
    public MetricItem Temperature { get; } = new("Temperatura");
    public MetricItem Activity { get; } = new("Atividade");
    public MetricItem UsedSpace { get; } = new("Espaço usado");

    [ObservableProperty]
    private double _usedPercent;
}

/// <summary>Sensor favorito exibido como bloco no painel inicial.</summary>
public sealed partial class FavoriteTile : ObservableObject
{
    public FavoriteTile(string id)
    {
        Id = id;
    }

    public string Id { get; }

    [ObservableProperty]
    private string _name = "Sensor não encontrado";

    [ObservableProperty]
    private string _hardware = "";

    [ObservableProperty]
    private string _value = "Não disponível";

    [ObservableProperty]
    private SensorState _state = SensorState.Unavailable;

    [ObservableProperty]
    private string? _tooltip;

    [ObservableProperty]
    private ChartSeries? _series;
}

public sealed partial class OverviewViewModel : PageViewModel
{
    private readonly MainViewModel _main;

    public OverviewViewModel(AppServices services, MainViewModel main) : base(services)
    {
        _main = main;
        CpuMetrics = new ObservableCollection<MetricItem> { _cpuLoad, _cpuTemp, _cpuClock, _cpuPower, _cpuThread };
        GpuMetrics = new ObservableCollection<MetricItem> { _gpuLoad, _gpuTemp, _gpuClock, _gpuPower, _gpuVram };
        RamMetrics = new ObservableCollection<MetricItem> { _ramLoad, _ramUsed, _ramAvailable, _ramCommit };
        SystemMetrics = new ObservableCollection<MetricItem> { _uptime, _processes };

        services.InventoryReady += (_, _) => Application.Current.Dispatcher.BeginInvoke(ApplyInventory);
        services.KeyMapChanged += (_, _) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            LinkDisks();
            RebuildBoard();
        });
        RebuildBoard();
        services.FavoritesChanged += (_, _) => SyncFavorites();
        SyncFavorites();
        if (services.Inventory is not null)
            ApplyInventory();
    }

    public override string Title => "Visão geral";
    public override string Subtitle => "Estado resumido do computador em tempo real";
    public override string Glyph => "";

    private readonly MetricItem _cpuLoad = new("Uso total");
    private readonly MetricItem _cpuTemp = new("Temperatura");
    private readonly MetricItem _cpuClock = new("Clock médio");
    private readonly MetricItem _cpuPower = new("Potência");
    private readonly MetricItem _cpuThread = new("Thread mais ocupada");
    private readonly MetricItem _gpuLoad = new("Uso");
    private readonly MetricItem _gpuTemp = new("Temperatura");
    private readonly MetricItem _gpuClock = new("Clock");
    private readonly MetricItem _gpuPower = new("Potência");
    private readonly MetricItem _gpuVram = new("VRAM em uso");
    private readonly MetricItem _ramLoad = new("Em uso");
    private readonly MetricItem _ramUsed = new("Usada");
    private readonly MetricItem _ramAvailable = new("Disponível");
    private readonly MetricItem _ramCommit = new("Comprometida");
    private readonly MetricItem _uptime = new("Tempo ligado");
    private readonly MetricItem _processes = new("Processos");

    public ObservableCollection<MetricItem> CpuMetrics { get; }
    public ObservableCollection<MetricItem> GpuMetrics { get; }
    public ObservableCollection<MetricItem> RamMetrics { get; }
    public ObservableCollection<MetricItem> SystemMetrics { get; }
    public ObservableCollection<DiskSummary> Disks { get; } = new();
    public ObservableCollection<FavoriteTile> Favorites { get; } = new();
    public ObservableCollection<InfoRow> CpuInfo { get; } = new();
    public ObservableCollection<InfoRow> BoardInfo { get; } = new();
    public ObservableCollection<InfoRow> SystemInfo { get; } = new();
    public ObservableCollection<MemoryModuleRow> MemoryModules { get; } = new();
    public ObservableCollection<GpuRow> GpuList { get; } = new();

    [ObservableProperty] private string _cpuName = "Processador";
    [ObservableProperty] private string _cpuDetail = "Identificando…";
    [ObservableProperty] private string _gpuName = "Placa de vídeo";
    [ObservableProperty] private string _gpuDetail = "";
    [ObservableProperty] private string _ramTitle = "Memória RAM";
    [ObservableProperty] private string _ramDetail = "";
    [ObservableProperty] private string _osText = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private ChartSeries? _cpuSeries;
    [ObservableProperty] private ChartSeries? _gpuSeries;
    [ObservableProperty] private ChartSeries? _ramSeries;
    [ObservableProperty] private bool _hasFavorites;
    [ObservableProperty] private string? _memoryNote;
    [ObservableProperty] private string? _inventoryNotes;
    [ObservableProperty] private bool _inventoryLoaded;

    private readonly List<(MetricItem Item, string Id)> _boardItems = new();

    public ObservableCollection<MetricItem> BoardTemperatures { get; } = new();
    public ObservableCollection<MetricItem> BoardFans { get; } = new();
    public ObservableCollection<MetricItem> BoardRails { get; } = new();

    [ObservableProperty] private bool _hasBoardSensors;
    [ObservableProperty] private string _boardTitle = "Placa-mãe e refrigeração";
    [ObservableProperty] private string _boardHint = "";
    [ObservableProperty] private bool _canElevate;

    /// <summary>Monta o cartão da placa-mãe com os sensores encontrados (Super I/O, controlador embarcado, refrigeração).</summary>
    private void RebuildBoard()
    {
        var map = Services.KeyMap;
        _boardItems.Clear();
        BoardTemperatures.Clear();
        BoardFans.Clear();
        BoardRails.Clear();

        void Add(ObservableCollection<MetricItem> target, IEnumerable<BoardSensor> sensors)
        {
            foreach (var s in sensors)
            {
                var item = new MetricItem(FriendlyBoardName(s.Name));
                target.Add(item);
                _boardItems.Add((item, s.Id));
            }
        }

        Add(BoardTemperatures, map.BoardTemperatures);
        Add(BoardFans, map.Fans);
        Add(BoardRails, map.Rails);
        HasBoardSensors = _boardItems.Count > 0;

        string? board = Services.Inventory is { } inv ? $"{inv.Board.Manufacturer} {inv.Board.Product}".Trim() : null;
        BoardTitle = board is null ? "Placa-mãe e refrigeração" : $"Placa-mãe · {board}";
        BoardHint = HasBoardSensors
            ? $"Sensores lidos de: {map.BoardSensorSource}"
            : PrivilegeInfo.IsPawnIoInstalled && !PrivilegeInfo.IsAdministrator
                ? "O driver PawnIO está instalado. Abra o PrimePC como administrador para ler ventoinhas, tensões e temperaturas da placa-mãe."
                : PrivilegeInfo.IsPawnIoInstalled
                    ? "Nenhum sensor da placa-mãe foi reconhecido neste computador (o chip pode não ser suportado)."
                    : "Ventoinhas, tensões e temperaturas da placa-mãe exigem o driver PawnIO e o PrimePC aberto como administrador.";
        CanElevate = !HasBoardSensors && !PrivilegeInfo.IsAdministrator;
    }

    /// <summary>Traduz os nomes mais comuns dos canais do Super I/O.</summary>
    private static string FriendlyBoardName(string name) => name switch
    {
        "CPU" => "CPU (sensor da placa)",
        "System" => "Sistema",
        "VRM MOS" or "VRM" => "VRM",
        "CPU Socket" => "Soquete da CPU",
        "PCH" => "Chipset (PCH)",
        "CPU Fan" => "Ventoinha da CPU",
        "CPU Pump Fan" or "Pump Fan" or "Pump" => "Bomba (water cooler)",
        "CPU Opt Fan" or "CPU OPT" => "Ventoinha da CPU (opcional)",
        "CMOS Battery" => "Bateria do CMOS",
        _ when name.StartsWith("System Fan", StringComparison.OrdinalIgnoreCase) => "Ventoinha do gabinete" + name["System Fan".Length..],
        _ when name.StartsWith("Chassis Fan", StringComparison.OrdinalIgnoreCase) => "Ventoinha do gabinete" + name["Chassis Fan".Length..],
        _ => name,
    };

    [RelayCommand]
    private void RestartAsAdministrator() => _main.RestartAsAdministratorCommand.Execute(null);

    public override void OnSnapshot(SensorSnapshot s)
    {
        foreach (var (item, id) in _boardItems)
            item.Set(s, id, TempUnit);

        var map = Services.KeyMap;
        var hub = Services.Hub;
        var u = TempUnit;
        string driverHint = AppServices.LowLevelAccess
            ? "O hardware ou o driver não informou esta leitura."
            : "Esta leitura exige o driver PawnIO instalado e o PrimePC executado como administrador.";

        _cpuLoad.Set(s, map.CpuTotalLoad, u);
        _cpuTemp.Set(s, map.CpuTemperature, u, driverHint);
        _cpuPower.Set(s, map.CpuPower, u, driverHint);
        var clock = map.AverageCpuClock(s);
        if (clock is null)
            _cpuClock.SetText("Não disponível", SensorState.Unavailable, driverHint);
        else
            _cpuClock.SetText(Format(SensorKind.Clock, clock), SensorState.Normal, $"Média dos clocks de {map.CpuCoreClocks.Count} núcleos.");
        var busiest = map.BusiestThreadLoad(s);
        _cpuThread.SetText(busiest is null ? "Não disponível" : Format(SensorKind.Load, busiest),
            busiest is null ? SensorState.Unavailable : SensorState.Normal,
            "Uso da thread lógica mais ocupada. Jogos costumam saturar poucas threads mesmo com o uso total baixo.");
        if (map.CpuTotalLoad is not null)
            CpuSeries = ChartSeries.From(hub.GetHistory(map.CpuTotalLoad));

        var gpu = map.PrimaryGpu(s, Services.Settings.PreferredGpuId);
        if (gpu is not null)
        {
            if (GpuName != gpu.Name)
                GpuName = gpu.Name;
            _gpuLoad.Set(s, gpu.Load, u);
            _gpuTemp.Set(s, gpu.Temperature, u);
            _gpuClock.Set(s, gpu.CoreClock, u);
            _gpuPower.Set(s, gpu.Power, u);
            float? used = s.ValueOf(gpu.VramUsed), total = s.ValueOf(gpu.VramTotal);
            if (used is not null && total is > 0)
            {
                _gpuVram.SetText($"{used / 1024:0.0} de {total / 1024:0.0} GB", SensorState.Normal, "Memória de vídeo dedicada em uso.");
                VramPercent = used.Value / total.Value * 100;
            }
            else
            {
                _gpuVram.Set(s, gpu.VramUsed, u);
            }

            if (gpu.Load is not null)
                GpuSeries = ChartSeries.From(hub.GetHistory(gpu.Load));
        }
        else
        {
            GpuName = "Nenhuma placa de vídeo com sensores";
            foreach (var m in GpuMetrics)
                m.SetText("Não disponível", SensorState.Unavailable, "Nenhuma GPU com sensores foi detectada.");
        }

        _ramLoad.Set(s, map.RamLoad, u);
        _ramUsed.Set(s, map.RamUsed, u);
        _ramAvailable.Set(s, map.RamAvailable, u);
        _ramCommit.Set(s, WindowsSystemSource.CommitChargeId, u);
        RamPercent = s.ValueOf(map.RamLoad) ?? 0;
        if (map.RamLoad is not null)
            RamSeries = ChartSeries.From(hub.GetHistory(map.RamLoad));

        _uptime.Set(s, WindowsSystemSource.UptimeId, u);
        _processes.Set(s, WindowsSystemSource.ProcessesId, u);

        string diskHint = PrivilegeInfo.IsAdministrator
            ? "Este disco não informou esta leitura."
            : "Os sensores de disco exigem executar o PrimePC como administrador.";
        foreach (var d in Disks)
        {
            d.Temperature.Set(s, d.Sensors?.Temperature, u, diskHint);
            d.Activity.Set(s, d.Sensors?.TotalActivity, u, diskHint);
            if (d.Sensors?.UsedSpace is { } usedId && s.ValueOf(usedId) is { } usedPct)
            {
                d.UsedSpace.Set(s, usedId, u);
                d.UsedPercent = usedPct;
            }
        }

        foreach (var tile in Favorites)
        {
            var r = s.Find(tile.Id);
            if (r is null)
            {
                tile.Value = "Não disponível";
                tile.State = SensorState.Unavailable;
                tile.Tooltip = "Este sensor não foi encontrado nesta leitura (dispositivo removido ou sem permissão).";
                continue;
            }

            tile.Name = r.Descriptor.Name;
            tile.Hardware = r.Descriptor.HardwareName;
            tile.Value = Format(r.Descriptor.Kind, r.Value);
            tile.State = r.State;
            tile.Tooltip = r.StateReason ?? r.Descriptor.Source;
            var kind = r.Descriptor.Kind;
            tile.Series = ChartSeries.From(hub.GetHistory(tile.Id),
                kind == SensorKind.Temperature && u == TemperatureUnit.Fahrenheit ? c => c * 9 / 5 + 32 : null);
        }
    }

    private void ApplyInventory()
    {
        var inv = Services.Inventory;
        if (inv is null)
            return;
        InventoryLoaded = true;

        CpuName = inv.Cpu.Name;
        CpuDetail = $"{inv.Cpu.Cores} núcleos · {inv.Cpu.Threads} threads" + (inv.Cpu.Socket is null ? "" : $" · {inv.Cpu.Socket}");
        CpuInfo.Clear();
        CpuInfo.Add(new("Fabricante", inv.Cpu.Manufacturer));
        CpuInfo.Add(new("Arquitetura", inv.Cpu.Architecture));
        CpuInfo.Add(new("Núcleos / threads", $"{inv.Cpu.Cores} / {inv.Cpu.Threads}" + (inv.Cpu.Sockets > 1 ? $" ({inv.Cpu.Sockets} soquetes)" : "")));
        CpuInfo.Add(new("Clock máximo informado", inv.Cpu.MaxClockMHz is { } mhz ? $"{mhz} MHz" : "Não disponível"));
        CpuInfo.Add(new("Cache L2 / L3", $"{Kb(inv.Cpu.L2CacheKB)} / {Kb(inv.Cpu.L3CacheKB)}"));
        CpuInfo.Add(new("Virtualização", inv.Cpu.VirtualizationEnabled switch { true => "Ativada", false => "Desativada", _ => "Não informado" }));
        CpuInfo.Add(new("Instruções", string.Join(", ", inv.Cpu.InstructionSets)));

        BoardInfo.Clear();
        BoardInfo.Add(new("Placa-mãe", $"{inv.Board.Manufacturer} {inv.Board.Product}".Trim()));
        if (inv.Board.Version is not null)
            BoardInfo.Add(new("Revisão", inv.Board.Version));
        BoardInfo.Add(new("Chipset", inv.Board.Chipset));
        BoardInfo.Add(new("BIOS/UEFI", $"{inv.Board.BiosVendor} · versão {inv.Board.BiosVersion ?? "?"}" +
                                       (inv.Board.BiosDate is { } bd ? $" · {bd:dd/MM/yyyy}" : "")));
        BoardInfo.Add(new("Modo do firmware", inv.Board.FirmwareMode));
        BoardInfo.Add(new("Secure Boot", inv.Board.SecureBoot switch { true => "Ativado", false => "Desativado", _ => "Não informado" }));

        RamTitle = $"Memória RAM · {SensorUnits.FormatBytes(inv.Memory.InstalledBytes > 0 ? inv.Memory.InstalledBytes : inv.Memory.UsableBytes)}";
        var first = inv.Memory.Modules.FirstOrDefault();
        RamDetail = first is null
            ? "Módulos não informados pelo firmware"
            : $"{inv.Memory.Modules.Count} × {SensorUnits.FormatBytes(first.CapacityBytes)} {first.MemoryType}" +
              (first.ConfiguredSpeedMTs is { } sp ? $" · {sp} MT/s" : "");
        MemoryNote = $"{inv.Memory.ChannelEstimate}. {inv.Memory.XmpExpoStatus}";
        MemoryModules.Clear();
        foreach (var m in inv.Memory.Modules)
        {
            MemoryModules.Add(new MemoryModuleRow(m.Slot, m.Bank ?? "—", SensorUnits.FormatBytes(m.CapacityBytes), $"{m.MemoryType} {m.FormFactor}",
                m.ConfiguredSpeedMTs is { } cs ? $"{cs} MT/s" : "Não informado", m.RatedSpeedMTs is { } rs ? $"{rs} MT/s" : "Não informado",
                m.Manufacturer ?? "Não informado", m.PartNumber ?? "Não informado"));
        }

        GpuList.Clear();
        foreach (var g in inv.Gpus)
        {
            GpuList.Add(new GpuRow(g.Name, g.Vendor ?? "—", g.DedicatedMemoryBytes is { } vb ? SensorUnits.FormatBytes(vb) : "Não disponível",
                g.DriverVersion ?? "—", g.DriverDate?.ToString("dd/MM/yyyy") ?? "—", g.CurrentMode ?? "—"));
        }
        var mainGpu = inv.Gpus.OrderByDescending(g => g.DedicatedMemoryBytes ?? 0).FirstOrDefault();
        if (mainGpu is not null)
        {
            GpuDetail = $"{(mainGpu.DedicatedMemoryBytes is { } vb ? SensorUnits.FormatBytes(vb) + " · " : "")}driver {mainGpu.DriverVersion}" +
                        (mainGpu.CurrentMode is null ? "" : $" · {mainGpu.CurrentMode}");
        }

        OsText = $"{inv.Os.Caption} {inv.Os.DisplayVersion}".Trim();
        SystemInfo.Clear();
        SystemInfo.Add(new("Sistema", OsText));
        SystemInfo.Add(new("Versão / build", $"{inv.Os.Version} (build {inv.Os.Build})"));
        SystemInfo.Add(new("Arquitetura", inv.Os.Architecture));
        if (inv.Os.LastBoot is { } boot)
            SystemInfo.Add(new("Ligado desde", boot.ToString("dd/MM/yyyy HH:mm")));

        InventoryNotes = inv.Notes.Count == 0 ? null : string.Join("\n", inv.Notes);

        Disks.Clear();
        foreach (var d in inv.Disks)
        {
            string volumes = d.Volumes.Count == 0
                ? "Sem volumes com letra"
                : string.Join("   ", d.Volumes.Select(v => $"{v.Letter} {SensorUnits.FormatBytes(v.FreeBytes)} livres de {SensorUnits.FormatBytes(v.SizeBytes)}"));
            string health = d.HealthStatus ?? "Não disponível";
            var summary = new DiskSummary(d.Model, $"{d.MediaType} {d.BusType} · {SensorUnits.FormatBytes(d.SizeBytes)}", $"Saúde (Windows): {health}", volumes);
            long size = d.Volumes.Sum(v => v.SizeBytes), free = d.Volumes.Sum(v => v.FreeBytes);
            if (size > 0)
            {
                summary.UsedPercent = (size - free) * 100.0 / size;
                summary.UsedSpace.SetText($"{summary.UsedPercent:0} %", summary.UsedPercent >= 97 ? SensorState.Critical
                    : summary.UsedPercent >= 90 ? SensorState.Warning : SensorState.Normal, "Calculado pelos volumes do disco.");
            }

            Disks.Add(summary);
        }

        LinkDisks();
        RebuildBoard();
    }

    private void LinkDisks()
    {
        var map = Services.KeyMap;
        foreach (var d in Disks)
        {
            d.Sensors = map.Disks.FirstOrDefault(x => x.Name.Trim().Equals(d.Name.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? map.Disks.FirstOrDefault(x => x.Name.Contains(d.Name, StringComparison.OrdinalIgnoreCase)
                                                        || d.Name.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void SyncFavorites()
    {
        var ids = Services.Settings.Favorites;
        var existing = Favorites.ToDictionary(f => f.Id);
        Favorites.Clear();
        foreach (var id in ids)
            Favorites.Add(existing.TryGetValue(id, out var tile) ? tile : new FavoriteTile(id));
        HasFavorites = Favorites.Count > 0;
        if (Services.Hub.Latest.Readings.Count > 0)
            OnSnapshot(Services.Hub.Latest);
    }

    [RelayCommand]
    private void MoveFavoriteUp(FavoriteTile tile) => MoveFavorite(tile, -1);

    [RelayCommand]
    private void MoveFavoriteDown(FavoriteTile tile) => MoveFavorite(tile, +1);

    [RelayCommand]
    private void RemoveFavorite(FavoriteTile tile)
    {
        Services.Settings.Favorites.Remove(tile.Id);
        Services.NotifyFavoritesChanged();
    }

    private void MoveFavorite(FavoriteTile tile, int delta)
    {
        var list = Services.Settings.Favorites;
        int i = list.IndexOf(tile.Id);
        int j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count)
            return;
        (list[i], list[j]) = (list[j], list[i]);
        Services.NotifyFavoritesChanged();
    }

    [RelayCommand]
    private void OpenSensors() => _main.Navigate<SensorsViewModel>();

    private static string Kb(int? kb) => kb is null ? "?" : SensorUnits.FormatBytes(kb.Value * 1024L);
}

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Drivers;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Aba da tela de drivers (mesmas propriedades usadas pelo estilo Prime.TabPill).</summary>
public sealed partial class DriverTab : ObservableObject
{
    public DriverTab(string label, string glyph, bool attention, params DriverCategory[] categories)
    {
        Label = label;
        Glyph = glyph;
        IsAttention = attention;
        Categories = categories;
    }

    public string Label { get; }
    public string Glyph { get; }
    public bool IsAttention { get; }
    public IReadOnlyList<DriverCategory> Categories { get; }
    public bool IsAll => !IsAttention && Categories.Count == 0;

    public bool Matches(DriverRow r) =>
        IsAttention ? r.Status != DriverStatus.Ok : Categories.Count == 0 || Categories.Contains(r.Driver.Category);

    [ObservableProperty] private int _count;
    [ObservableProperty] private SensorState _worst = SensorState.Normal;
    [ObservableProperty] private bool _isVisible = true;
}

/// <summary>Linha da lista de drivers.</summary>
public sealed partial class DriverRow : ObservableObject
{
    public DriverRow(InstalledDriver driver)
    {
        Driver = driver;
        Apply(null);
    }

    public InstalledDriver Driver { get; }
    public string Name => DriverInventory.DisplayName(Driver);
    public string CategoryText => DriverInventory.CategoryName(Driver.Category);
    public string Provider => Driver.Provider ?? Driver.Manufacturer ?? "—";

    public string VersionText => Driver.Version is null
        ? "Sem driver"
        : Driver.FriendlyVersion != Driver.Version ? $"{Driver.FriendlyVersion} ({Driver.Version})" : Driver.Version;

    public string DateText => Driver.Date?.ToString("dd/MM/yyyy") ?? "—";
    public string SignedText => Driver.IsSigned switch { true => "Assinado", false => "Sem assinatura", _ => "—" };
    public string SignerText => Driver.Signer ?? "—";
    public string? VendorUrl => DriverInventory.VendorDriverPage(Driver.Provider, Driver.Manufacturer);
    public bool HasVendorUrl => VendorUrl is not null;
    public string OriginText => !Driver.HasDriver ? "—" : Driver.IsInbox ? $"Incluído no Windows ({Driver.InfName})" : $"Instalado à parte ({Driver.InfName})";
    public string HardwareIdsText => Driver.HardwareIds.Count == 0 ? "—" : string.Join("\n", Driver.HardwareIds.Take(5));

    [ObservableProperty] private DriverStatus _status;
    [ObservableProperty] private SensorState _state;
    [ObservableProperty] private string _statusText = "OK";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private DriverUpdate? _update;

    public bool HasUpdate => Update is not null;

    public int SortKey => Status switch
    {
        DriverStatus.Problem => 0,
        DriverStatus.UpdateAvailable => 1,
        DriverStatus.Warning => 2,
        _ => 3,
    };

    public void Apply(DriverUpdate? update)
    {
        var a = DriverEvaluator.Assess(Driver, update);
        Update = update;
        Status = a.Status;
        StatusText = a.Label;
        Detail = a.Detail;
        State = a.Status switch
        {
            DriverStatus.Problem => SensorState.Critical,
            DriverStatus.Ok => SensorState.Normal,
            _ => SensorState.Warning,
        };
    }
}

public sealed partial class DriversViewModel : PageViewModel
{
    public DriversViewModel(AppServices services) : base(services)
    {
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is DriverRow r && Visible(r) && MatchesSearch(r);
        Tabs = new ObservableCollection<DriverTab>
        {
            new("Atenção", "", true),
            new("Vídeo", "", false, DriverCategory.Video),
            new("Áudio", "", false, DriverCategory.Audio),
            new("Rede", "", false, DriverCategory.Network),
            new("Bluetooth", "", false, DriverCategory.Bluetooth),
            new("Armazenamento", "", false, DriverCategory.Storage),
            new("USB", "", false, DriverCategory.Usb),
            new("Periféricos", "", false, DriverCategory.Input),
            new("Sistema e chipset", "", false, DriverCategory.System),
            new("Processador", "", false, DriverCategory.Processor),
            new("Monitores", "", false, DriverCategory.Monitor),
            new("Impressoras", "", false, DriverCategory.Printer),
            new("Software", "", false, DriverCategory.Software),
            new("Outros", "", false, DriverCategory.Other),
            new("Todos", "", false),
        };
        _selectedTab = Tabs[1];
        Metrics = new ObservableCollection<MetricItem> { _total, _thirdParty, _problems, _unsigned, _updates };
        _updates.SetText("Não verificado", SensorState.Unavailable, "Clique em \"Procurar atualizações\" para consultar o Windows Update.");
    }

    private bool _populatedOnce;

    public override string Title => "Drivers";
    public override string Subtitle => "Todos os drivers instalados, dispositivos com problema e atualizações disponíveis";
    public override string Glyph => "";

    private readonly MetricItem _total = new("Drivers instalados");
    private readonly MetricItem _thirdParty = new("De fabricantes");
    private readonly MetricItem _problems = new("Com problema");
    private readonly MetricItem _unsigned = new("Sem assinatura");
    private readonly MetricItem _updates = new("Atualizações (Windows Update)");

    public ObservableCollection<MetricItem> Metrics { get; }
    public ObservableCollection<DriverTab> Tabs { get; }
    public ObservableCollection<DriverRow> Rows { get; } = new();
    public ObservableCollection<GpuDriverCard> GpuDrivers { get; } = new();
    public ObservableCollection<UpdateItem> AvailableUpdates { get; } = new();
    public ICollectionView View { get; }

    /// <summary>Serviços compartilhados, usados pelos cartões das placas de vídeo.</summary>
    internal AppServices Shared => Services;

    [ObservableProperty] private DriverTab _selectedTab;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _showInbox;
    [ObservableProperty] private int _visibleCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchUpdatesCommand), nameof(InstallSelectedCommand), nameof(InstallRowUpdateCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchUpdatesCommand), nameof(InstallSelectedCommand), nameof(InstallRowUpdateCommand))]
    private bool _isSearching;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(SearchUpdatesCommand), nameof(InstallSelectedCommand), nameof(InstallRowUpdateCommand))]
    private bool _isInstalling;

    [ObservableProperty] private string _scanText = "Lendo os drivers instalados…";
    [ObservableProperty] private string _updateText = "O Windows Update ainda não foi consultado nesta sessão.";
    [ObservableProperty] private SensorState _updateState = SensorState.Unavailable;
    [ObservableProperty] private string? _installStatus;
    [ObservableProperty] private SensorState _installState = SensorState.Normal;

    /// <summary>Cria um ponto de restauração do Windows antes de cada instalação (recomendado).</summary>
    [ObservableProperty] private bool _createRestorePoint = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DriverRow? _selectedRow;

    public bool HasSelection => SelectedRow is not null;
    public bool IsIdle => !IsLoading && !IsSearching && !IsInstalling;
    public bool IsAdministrator { get; } = PrivilegeInfo.IsAdministrator;
    public bool HasAvailableUpdates => AvailableUpdates.Count > 0;

    partial void OnSelectedTabChanged(DriverTab value)
    {
        if (value is null)
        {
            SelectedTab = Tabs[1];
            return;
        }

        SelectedRow = null;
        RefreshView();
    }

    partial void OnSearchTextChanged(string value) => RefreshView();

    partial void OnShowInboxChanged(bool value)
    {
        UpdateTabs();
        RefreshView();
    }

    public override void OnActivated()
    {
        if (Services.LastDriverScan is null && !IsLoading)
            _ = RefreshAsync();
    }

    /// <summary>Drivers do próprio Windows só aparecem se o usuário pedir, ou se tiverem problema/atualização.</summary>
    private bool Visible(DriverRow r) =>
        SelectedTab.Matches(r) && (ShowInbox || SelectedTab.IsAttention || !r.Driver.IsInbox || r.Status != DriverStatus.Ok);

    private bool MatchesSearch(DriverRow r)
    {
        if (string.IsNullOrWhiteSpace(SearchText))
            return true;
        var q = SearchText.Trim();
        return r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || r.Provider.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || r.VersionText.Contains(q, StringComparison.CurrentCultureIgnoreCase)
               || r.Driver.DeviceClass.Contains(q, StringComparison.CurrentCultureIgnoreCase);
    }

    private void RefreshView()
    {
        View.Refresh();
        VisibleCount = View.Cast<object>().Count();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        ScanText = "Lendo os drivers instalados…";
        try
        {
            var scan = await Task.Run(DriverInventory.Collect);
            Services.LastDriverScan = scan;
            Populate(scan);
            if (Services.LastDriverUpdateSearch is { Succeeded: true } search)
                ApplyUpdates(search);
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao listar drivers", ex);
            ScanText = $"Não foi possível listar os drivers: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Consulta o Windows Update e, para placas NVIDIA, o serviço oficial da NVIDIA, ao mesmo tempo.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SearchUpdatesAsync()
    {
        IsSearching = true;
        try
        {
            await Task.WhenAll(SearchWindowsUpdateAsync(), Task.WhenAll(GpuDrivers.Select(c => c.CheckAsync(CancellationToken.None))));
        }
        finally
        {
            IsSearching = false;
        }
    }

    private async Task SearchWindowsUpdateAsync()
    {
        UpdateText = "Consultando o Windows Update… (pode levar até um minuto)";
        UpdateState = SensorState.Unavailable;
        var search = await WindowsUpdateDriverSearch.SearchAsync(CancellationToken.None);
        Services.LastDriverUpdateSearch = search;
        Log.Info($"Busca de drivers no Windows Update: {search.Message}");
        ApplyUpdates(search);
    }

    private void Populate(DriverScan scan)
    {
        var selectedId = SelectedRow?.Driver.DeviceId;
        Rows.Clear();
        foreach (var d in scan.Drivers)
            Rows.Add(new DriverRow(d));
        Resort();

        // Os cartões das placas de vídeo são mantidos entre leituras (um download pode estar em andamento).
        var videos = scan.Drivers.Where(d => d.Category == DriverCategory.Video && d.HasDriver).ToList();
        foreach (var stale in GpuDrivers.Where(c => !c.IsBusy && videos.All(v => v.DeviceId != c.Driver.DeviceId)).ToList())
            GpuDrivers.Remove(stale);
        foreach (var d in videos)
        {
            if (GpuDrivers.FirstOrDefault(c => c.Driver.DeviceId == d.DeviceId) is { } card)
                card.Driver = d;
            else
                GpuDrivers.Add(new GpuDriverCard(d, this));
        }

        foreach (var card in GpuDrivers)
            card.AfterDriverListRead();

        ScanText = $"{scan.Drivers.Count} drivers e dispositivos · lista lida às {scan.CollectedAt:HH:mm:ss}" +
                   (scan.Notes.Count > 0 ? " · " + string.Join(" ", scan.Notes) : "");
        UpdateSummary();
        UpdateTabs();
        var attention = Tabs[0];
        // Na primeira leitura, abre direto em "Atenção" se houver algo para ver; depois respeita a aba escolhida.
        if (!_populatedOnce || SelectedTab.IsAttention || SelectedTab.Count == 0)
            SelectedTab = attention.Count > 0 ? attention : Tabs.First(t => !t.IsAttention && t.Count > 0);
        _populatedOnce = true;
        RefreshView();
        if (selectedId is not null)
            SelectedRow = Rows.FirstOrDefault(r => r.Driver.DeviceId == selectedId);
    }

    private void ApplyUpdates(DriverUpdateSearch search)
    {
        UpdateText = search.Message;
        if (!search.Succeeded)
        {
            UpdateState = SensorState.Critical;
            _updates.SetText("Falhou", SensorState.Unavailable, search.Message);
            return;
        }

        var (byDevice, unmatched) = DriverEvaluator.Match(Rows.Select(r => r.Driver).ToList(), search.Updates);
        foreach (var row in Rows)
            row.Apply(byDevice.GetValueOrDefault(row.Driver.DeviceId));
        AvailableUpdates.Clear();
        foreach (var u in search.Updates)
        {
            var device = Rows.FirstOrDefault(r => byDevice.TryGetValue(r.Driver.DeviceId, out var m) && ReferenceEquals(m, u));
            AvailableUpdates.Add(new UpdateItem(u, device?.Name));
        }

        OnPropertyChanged(nameof(HasAvailableUpdates));
        InstallSelectedCommand.NotifyCanExecuteChanged();
        UpdateState = search.Updates.Count == 0 ? SensorState.Normal : SensorState.Warning;
        _updates.SetText(search.Updates.Count.ToString(), search.Updates.Count == 0 ? SensorState.Normal : SensorState.Warning,
            $"Consulta feita às {search.SearchedAt:HH:mm:ss}.");
        if (unmatched.Count > 0)
            UpdateText += $" {unmatched.Count} delas são para dispositivos que não estão conectados agora: " +
                          string.Join("; ", unmatched.Select(u => u.Title)) + ".";
        UpdateText += $" (consultado às {search.SearchedAt:HH:mm})";
        Resort();
        UpdateSummary();
        UpdateTabs();
        RefreshView();
    }

    private void Resort()
    {
        var sorted = Rows.OrderBy(r => r.SortKey).ThenBy(r => r.Driver.Category).ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        for (int i = 0; i < sorted.Count; i++)
        {
            int at = Rows.IndexOf(sorted[i]);
            if (at != i)
                Rows.Move(at, i);
        }
    }

    private void UpdateSummary()
    {
        int total = Rows.Count(r => r.Driver.HasDriver);
        int third = Rows.Count(r => r.Driver.HasDriver && !r.Driver.IsInbox);
        int problems = Rows.Count(r => r.Status == DriverStatus.Problem);
        int warnings = Rows.Count(r => r.Status == DriverStatus.Warning);
        int unsigned = Rows.Count(r => r.Driver.IsSigned == false);
        _total.SetText(total.ToString(), SensorState.Normal, "Drivers informados pelo Windows (inclui os que já vêm com o sistema).");
        _thirdParty.SetText(third.ToString(), SensorState.Normal, "Drivers instalados à parte (pelo fabricante ou pelo Windows Update).");
        _problems.SetText(problems == 0 && warnings == 0 ? "0" : $"{problems} + {warnings} atenção",
            problems > 0 ? SensorState.Critical : warnings > 0 ? SensorState.Warning : SensorState.Normal,
            "Dispositivos com erro, sem driver ou desativados, segundo o Gerenciador de Dispositivos.");
        _unsigned.SetText(unsigned.ToString(), unsigned > 0 ? SensorState.Warning : SensorState.Normal, "Drivers sem assinatura digital válida.");
    }

    private void UpdateTabs()
    {
        foreach (var tab in Tabs)
        {
            var rows = Rows.Where(r => tab.Matches(r) && (ShowInbox || tab.IsAttention || !r.Driver.IsInbox || r.Status != DriverStatus.Ok)).ToList();
            tab.Count = rows.Count;
            tab.Worst = rows.Any(r => r.Status == DriverStatus.Problem) ? SensorState.Critical
                : rows.Any(r => r.Status != DriverStatus.Ok) ? SensorState.Warning
                : SensorState.Normal;
            tab.IsVisible = tab.IsAll || tab.Count > 0;
        }
    }

    // ---------------- Instalação pelo Windows Update ----------------

    private bool CanInstallSelected() => IsIdle && AvailableUpdates.Count > 0;

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private Task InstallSelectedAsync() =>
        InstallUpdatesAsync(AvailableUpdates.Where(u => u.IsSelected).Select(u => u.Update).ToList());

    private bool CanInstallRow(DriverRow? row) => IsIdle && row?.Update is not null;

    [RelayCommand(CanExecute = nameof(CanInstallRow))]
    private Task InstallRowUpdateAsync(DriverRow? row) =>
        row?.Update is { } update ? InstallUpdatesAsync(new[] { update }) : Task.CompletedTask;

    partial void OnSelectedRowChanged(DriverRow? value) => InstallRowUpdateCommand.NotifyCanExecuteChanged();

    private async Task InstallUpdatesAsync(IReadOnlyList<DriverUpdate> updates)
    {
        if (updates.Count == 0)
        {
            Shell.Info("Marque pelo menos uma atualização na lista.");
            return;
        }

        if (!IsAdministrator)
        {
            Shell.Info("Para instalar drivers, o PrimePC precisa estar aberto como administrador. Use o botão \"Reiniciar como administrador\".");
            return;
        }

        long total = updates.Sum(u => u.SizeBytes);
        string list = string.Join("\n", updates.Select(u => $"• {u.Title}"));
        if (!Shell.Confirm(
                $"Baixar e instalar {(updates.Count == 1 ? "esta atualização" : $"estas {updates.Count} atualizações")} de driver pelo Windows Update?\n\n" +
                list + "\n\n" +
                (total > 0 ? $"Download: até {DriverFormat.Size(total)}, direto dos servidores da Microsoft.\n" : "") +
                (CreateRestorePoint ? "Antes de instalar, o PrimePC cria um ponto de restauração do Windows.\n" : "Não será criado ponto de restauração (opção desmarcada).\n") +
                "\nDurante a instalação o dispositivo pode desconectar por alguns segundos (a tela pode piscar, o som ou a rede podem cair). " +
                "Alguns drivers pedem para reiniciar o computador.",
                "Instalar drivers"))
            return;

        IsInstalling = true;
        InstallState = SensorState.Normal;
        Services.RunningTest = "instalação de drivers";
        try
        {
            if (!await PrepareRestorePointAsync("PrimePC: drivers do Windows Update", t => InstallStatus = t))
            {
                InstallStatus = "Instalação cancelada.";
                return;
            }

            var progress = new Progress<DriverInstallProgress>(p => InstallStatus = p.Count == 0 || p.Index == 0
                ? p.Stage
                : $"{p.Stage} {p.Index} de {p.Count}: {p.Title}…");
            var results = await WindowsUpdateDriverInstaller.InstallAsync(updates, progress, CancellationToken.None);
            foreach (var r in results)
                Log.Info($"Instalação de driver (Windows Update): {r.Title} → {r.Message}");

            int ok = results.Count(r => r.Succeeded);
            bool reboot = results.Any(r => r.RebootRequired);
            InstallState = ok == results.Count ? SensorState.Normal : ok == 0 ? SensorState.Critical : SensorState.Warning;
            InstallStatus = $"{ok} de {results.Count} instalada(s)." + (reboot ? " Reinicie o computador para concluir." : "");
            Shell.Info(string.Join("\n\n", results.Select(r => $"{(r.Succeeded ? "✔" : "✖")} {r.Title}\n{r.Message}")) +
                       (reboot ? "\n\nReinicie o computador quando puder para concluir a instalação." : ""),
                "Resultado da instalação");

            Services.RunningTest = null;
            await RefreshAsync();
            await SearchWindowsUpdateAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao instalar drivers do Windows Update", ex);
            InstallState = SensorState.Critical;
            InstallStatus = $"Não foi possível instalar: {ex.Message}";
        }
        finally
        {
            Services.RunningTest = null;
            IsInstalling = false;
        }
    }

    /// <summary>
    /// Cria o ponto de restauração, se a opção estiver marcada. Retorna falso se não foi possível criar e o usuário
    /// preferiu não continuar sem ele.
    /// </summary>
    internal async Task<bool> PrepareRestorePointAsync(string description, Action<string> status)
    {
        if (!CreateRestorePoint)
            return true;
        status("Criando ponto de restauração do Windows… (pode levar até um minuto)");
        var result = await Task.Run(() => RestorePoint.Create(description));
        Log.Info($"Ponto de restauração: {result.Message}");
        status(result.Message);
        return result.CanUndo || Shell.Confirm($"{result.Message}\n\nContinuar a instalação sem ponto de restauração?", "Ponto de restauração");
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Aguarde o término de: {Services.RunningTest}.");
            return;
        }

        if (Shell.RestartElevated())
            ((App)System.Windows.Application.Current).ExitApplication();
    }

    [RelayCommand]
    private void OpenDeviceManager() => Shell.Open("devmgmt.msc");

    [RelayCommand]
    private void OpenWindowsUpdate() => Shell.Open("ms-settings:windowsupdate-optionalupdates");

    [RelayCommand]
    private void OpenVendor(string? url)
    {
        if (!string.IsNullOrEmpty(url))
            Shell.Open(url);
    }
}

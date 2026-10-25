using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Drivers;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Atualização do Windows Update na lista de instalação (com caixa de seleção).</summary>
public sealed partial class UpdateItem : ObservableObject
{
    public UpdateItem(DriverUpdate update, string? deviceName)
    {
        Update = update;
        DeviceName = deviceName;
    }

    public DriverUpdate Update { get; }
    public string? DeviceName { get; }
    public string Title => Update.Title;

    public string DetailText => string.Join(" · ", new[]
    {
        DeviceName is null ? "Dispositivo não conectado agora" : $"Para: {DeviceName}",
        Update.Date is { } d ? $"versão de {d:dd/MM/yyyy}" : null,
        Update.SizeBytes > 0 ? $"até {DriverFormat.Size(Update.SizeBytes)}" : null,
    }.Where(s => s is not null));

    [ObservableProperty] private bool _isSelected = true;
}

public static class DriverFormat
{
    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.0} GB"
        : $"{Math.Max(1, bytes / (1L << 20)):N0} MB";

    public static string Speed(double bytesPerSecond) => $"{bytesPerSecond / (1 << 20):0.0} MB/s";

    public static string Remaining(long left, double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
            return "";
        var t = TimeSpan.FromSeconds(left / bytesPerSecond);
        return t.TotalMinutes >= 1 ? $" · faltam {Math.Ceiling(t.TotalMinutes):0} min" : $" · faltam {Math.Ceiling(t.TotalSeconds):0} s";
    }
}

/// <summary>
/// Cartão de uma placa de vídeo: versão instalada e, para NVIDIA, a versão mais recente publicada pela NVIDIA, com
/// download do instalador oficial, conferência da assinatura digital e abertura do instalador (com a janela dele).
/// </summary>
public sealed partial class GpuDriverCard : ObservableObject
{
    private readonly DriversViewModel _owner;
    private CancellationTokenSource? _cts;

    public GpuDriverCard(InstalledDriver driver, DriversViewModel owner)
    {
        _owner = owner;
        _driver = driver;
        IsNvidia = NvidiaDriverService.IsNvidia(driver);
        LatestText = IsNvidia
            ? "Clique em \"Procurar atualizações\" para consultar a versão mais recente na NVIDIA."
            : "A AMD e a Intel não oferecem consulta automática de versão: use o site do fabricante ou o Windows Update.";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Version), nameof(DateText), nameof(VendorUrl), nameof(HasVendorUrl))]
    private InstalledDriver _driver;

    public bool IsNvidia { get; }
    public string Name => Driver.DeviceName;
    public string Version => Driver.FriendlyVersion != Driver.Version ? $"{Driver.FriendlyVersion} ({Driver.Version})" : Driver.Version ?? "—";

    public string DateText => Driver.Date is { } dt
        ? $"{dt:dd/MM/yyyy} (há {Math.Max(0, (int)(DateTime.Now - dt).TotalDays)} dias)"
        : "";

    public string? VendorUrl => DriverInventory.VendorDriverPage(Driver.Provider, Driver.Manufacturer);
    public bool HasVendorUrl => VendorUrl is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotes), nameof(InstallText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private NvidiaDriverRelease? _latest;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isNewer;

    [ObservableProperty] private string _latestText;
    [ObservableProperty] private SensorState _latestState = SensorState.Unavailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CancelCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isIndeterminate;
    [ObservableProperty] private string? _statusText;
    [ObservableProperty] private SensorState _statusState = SensorState.Normal;

    public bool IsIdle => !IsBusy;
    public bool HasNotes => Latest?.DetailsUrl is not null;

    public string InstallText => Latest is null
        ? "Baixar e instalar"
        : Latest.SizeBytes is { } size ? $"Baixar e instalar {Latest.Version} ({DriverFormat.Size(size)})" : $"Baixar e instalar {Latest.Version}";

    private string DownloadFolder => Path.Combine(AppPaths.Downloads, "nvidia");

    public async Task CheckAsync(CancellationToken token)
    {
        if (!IsNvidia || IsBusy)
            return;
        LatestText = "Consultando a NVIDIA…";
        LatestState = SensorState.Unavailable;
        var check = await NvidiaDriverService.CheckAsync(Driver, token);
        Log.Info($"Consulta NVIDIA ({Driver.DeviceName}): {check.Message}");
        Latest = check.Latest;
        IsNewer = check.IsNewer;
        LatestText = check.Latest is { } r
            ? $"Mais recente na NVIDIA: {r.Version} · {r.Name}{(r.IsWhql ? " (WHQL)" : "")}{(r.ReleaseDate is { } d ? $" · {d:dd/MM/yyyy}" : "")}. " +
              (check.IsNewer ? $"Você tem o {check.InstalledVersion}." : "Você já tem esta versão ou uma mais nova.")
            : check.Message;
        LatestState = !check.Succeeded ? SensorState.Unavailable : check.IsNewer ? SensorState.Warning : SensorState.Normal;
    }

    private bool CanInstall() => IsNvidia && !IsBusy && Latest is not null && IsNewer;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        var release = Latest!;
        string size = release.SizeBytes is { } s ? $" ({DriverFormat.Size(s)})" : "";
        string restore = _owner.CreateRestorePoint
            ? "• Antes de abrir o instalador, o PrimePC cria um ponto de restauração do Windows.\n"
            : "• Não será criado ponto de restauração (opção desmarcada).\n";
        if (!Shell.Confirm(
                $"Baixar e instalar o driver NVIDIA {release.Version}{size}?\n\n" +
                $"Instalado agora: {Driver.FriendlyVersion}\n\n" +
                $"• O arquivo vem do servidor oficial da NVIDIA ({release.DownloadUrl.Host}).\n" +
                "• Depois do download, o PrimePC confere a assinatura digital da NVIDIA; se não for válida, o arquivo é apagado.\n" +
                restore +
                "• Em seguida abre o instalador oficial da NVIDIA: siga as etapas na janela dele (a instalação \"Expressa\" mantém suas configurações).\n\n" +
                "Feche os jogos antes de instalar: durante a instalação a tela pisca e pode ficar preta por alguns segundos.",
                "Instalar driver NVIDIA"))
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        StatusState = SensorState.Normal;
        string path = Path.Combine(DownloadFolder, release.FileName);
        try
        {
            // 1) Download (continua de onde parou se uma tentativa anterior foi interrompida).
            if (!File.Exists(path))
            {
                RemoveOldInstallers(keep: release.FileName);
                IsDownloading = true;
                IsIndeterminate = true;
                StatusText = "Conectando ao servidor da NVIDIA…";
                var progress = new Progress<DownloadProgress>(p =>
                {
                    IsIndeterminate = p.Fraction is null;
                    Progress = (p.Fraction ?? 0) * 100;
                    StatusText = p.Total is { } total
                        ? $"Baixando {DriverFormat.Size(p.Received)} de {DriverFormat.Size(total)} · {DriverFormat.Speed(p.BytesPerSecond)}{DriverFormat.Remaining(total - p.Received, p.BytesPerSecond)}"
                        : $"Baixando {DriverFormat.Size(p.Received)} · {DriverFormat.Speed(p.BytesPerSecond)}";
                });
                await DriverDownloader.DownloadAsync(NvidiaDriverService.Http, release.DownloadUrl, path, progress, _cts.Token);
                Log.Info($"Driver NVIDIA {release.Version} baixado: {path}");
            }

            // 2) Assinatura digital da NVIDIA.
            IsDownloading = false;
            IsIndeterminate = true;
            StatusText = "Conferindo a assinatura digital da NVIDIA…";
            var signature = await Task.Run(() => AuthenticodeVerifier.Verify(path, NvidiaDriverService.Signer));
            Log.Info($"Assinatura do instalador NVIDIA: {signature.Message}");
            if (!signature.IsTrusted)
            {
                TryDelete(path);
                Fail($"Instalação cancelada: {signature.Message} O arquivo foi apagado.");
                return;
            }

            // 3) Ponto de restauração (opcional).
            if (!await _owner.PrepareRestorePointAsync($"PrimePC: driver NVIDIA {release.Version}", t => StatusText = t))
            {
                StatusText = "Instalação cancelada. O instalador baixado foi mantido para a próxima tentativa.";
                return;
            }

            // 4) Instalador oficial, com a janela dele. Durante a troca do driver as bibliotecas da NVIDIA usadas pelos
            // sensores deixam de ser seguras de chamar (o PrimePC 1.2.0 fechava aqui), então a leitura fica pausada.
            StatusText = "Instalador da NVIDIA aberto: siga as etapas na janela dele. A leitura dos sensores fica pausada até ele terminar.";
            _owner.Shared.RunningTest = "instalação do driver NVIDIA";
            await Task.Run(_owner.Shared.Hub.Pause);
            bool started = await InstallerLauncher.RunAndWaitAsync(path, CancellationToken.None);
            _owner.Shared.RunningTest = null;
            if (!started)
            {
                _owner.Shared.Hub.Resume();
                StatusText = "A permissão de administrador foi recusada. O instalador baixado foi mantido para a próxima tentativa.";
                StatusState = SensorState.Warning;
                return;
            }

            // 5) O processo ainda tem carregadas as bibliotecas do driver antigo: reabre o PrimePC, que confere a versão
            // instalada assim que abrir (na aba Drivers).
            PendingDriverInstall.Save(new PendingDriverInstall(Driver.DeviceId, release.Version, Driver.FriendlyVersion, path, DateTime.Now));
            StatusText = "Instalador concluído. Reabrindo o PrimePC para carregar o driver novo…";
            Log.Info($"Instalador do driver NVIDIA {release.Version} terminou; reabrindo o PrimePC.");
            if (Shell.StartNewInstance("--aba=6"))
            {
                ((App)System.Windows.Application.Current).ExitApplication();
                return;
            }

            _owner.Shared.Hub.Resume();
            StatusText = "O instalador terminou. Feche e abra o PrimePC para conferir a versão nova do driver.";
            StatusState = SensorState.Warning;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download pausado. Clique em instalar de novo para continuar de onde parou.";
            StatusState = SensorState.Warning;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao instalar driver NVIDIA", ex);
            Fail($"Não foi possível concluir: {ex.Message}");
        }
        finally
        {
            _owner.Shared.RunningTest = null;
            IsDownloading = false;
            IsIndeterminate = false;
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool CanCancel() => IsBusy && IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    partial void OnIsDownloadingChanged(bool value) => CancelCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void OpenNotes()
    {
        if (Latest?.DetailsUrl is { } url)
            Shell.Open(url);
    }

    private void Fail(string message)
    {
        StatusText = message;
        StatusState = SensorState.Critical;
    }

    /// <summary>
    /// Depois que o PrimePC reabre (após o instalador), confere se a versão nova foi instalada e informa o resultado.
    /// Também apaga instaladores baixados que já não são necessários (versão igual ou anterior à instalada).
    /// </summary>
    public void AfterDriverListRead()
    {
        if (!IsNvidia || IsBusy || Driver.FriendlyVersion is not { } installed)
            return;

        if (PendingDriverInstall.Load() is { } pending && pending.DeviceId == Driver.DeviceId)
        {
            PendingDriverInstall.Clear();
            if (NvidiaDriverService.CompareVersions(installed, pending.ExpectedVersion) >= 0)
            {
                StatusText = $"Driver atualizado: {pending.PreviousVersion ?? "versão anterior"} → {installed}. O instalador baixado foi apagado para liberar espaço.";
                StatusState = SensorState.Normal;
                Log.Info($"Driver NVIDIA conferido após a instalação: {installed}.");
            }
            else
            {
                StatusText = $"O driver continua na versão {installed}. Se o instalador pediu para reiniciar, reinicie o computador; " +
                             "se ele foi cancelado, clique em instalar de novo (o arquivo já baixado será reaproveitado).";
                StatusState = SensorState.Warning;
            }
        }

        if (!Directory.Exists(DownloadFolder))
            return;
        foreach (var file in Directory.EnumerateFiles(DownloadFolder))
        {
            string version = Path.GetFileName(file).Split('-')[0];
            if (version.Contains('.') && NvidiaDriverService.CompareVersions(version, installed) <= 0)
            {
                TryDelete(file);
                Log.Info($"Instalador já instalado apagado: {file}");
            }
        }
    }

    /// <summary>Apaga instaladores antigos baixados pelo próprio PrimePC (cada um tem cerca de 1 GB).</summary>
    private void RemoveOldInstallers(string keep)
    {
        if (!Directory.Exists(DownloadFolder))
            return;
        foreach (var file in Directory.EnumerateFiles(DownloadFolder))
        {
            string name = Path.GetFileName(file);
            if (!name.Equals(keep, StringComparison.OrdinalIgnoreCase) && !name.Equals(keep + ".part", StringComparison.OrdinalIgnoreCase))
                TryDelete(file);
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex)
        {
            Log.Error($"Não foi possível apagar {file}", ex);
        }
    }
}

/// <summary>Instalação de driver de vídeo à espera de conferência depois que o PrimePC reabre.</summary>
public sealed record PendingDriverInstall(string DeviceId, string ExpectedVersion, string? PreviousVersion, string InstallerPath, DateTime At)
{
    public static void Save(PendingDriverInstall pending)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataRoot);
            File.WriteAllText(AppPaths.PendingDriverCheck, System.Text.Json.JsonSerializer.Serialize(pending));
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao registrar a instalação de driver", ex);
        }
    }

    /// <summary>Lê a instalação pendente (ignora registros de mais de um dia).</summary>
    public static PendingDriverInstall? Load()
    {
        try
        {
            if (!File.Exists(AppPaths.PendingDriverCheck))
                return null;
            var pending = System.Text.Json.JsonSerializer.Deserialize<PendingDriverInstall>(File.ReadAllText(AppPaths.PendingDriverCheck));
            return pending is not null && DateTime.Now - pending.At < TimeSpan.FromDays(1) ? pending : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(AppPaths.PendingDriverCheck);
        }
        catch
        {
            // sem importância
        }
    }
}

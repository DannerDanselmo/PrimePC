using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Sensors;
using PrimePC.Sensors.Inventory;
using PrimePC.Testing;

namespace PrimePC.App.ViewModels;

/// <summary>Disco com dados de inventário, confiabilidade e sensores ao vivo.</summary>
public sealed partial class DiskDetail : ObservableObject
{
    public DiskDetail(DiskInfo info)
    {
        Info = info;
        Title = info.Model;
        Subtitle = $"{info.MediaType} {info.BusType} · {SensorUnits.FormatBytes(info.SizeBytes)} · disco {info.Number}" +
                   (info.FirmwareVersion is null ? "" : $" · firmware {info.FirmwareVersion}");
        Volumes = info.Volumes.Count == 0
            ? "Sem volumes com letra"
            : string.Join("   ", info.Volumes.Select(v =>
                $"{v.Letter} {(v.Label is null ? "" : $"\"{v.Label}\" ")}{SensorUnits.FormatBytes(v.FreeBytes)} livres de {SensorUnits.FormatBytes(v.SizeBytes)} ({v.FileSystem})"));
        Health.SetText(info.HealthStatus ?? "Não disponível",
            info.HealthStatus switch { "Saudável" => SensorState.Normal, "Atenção" => SensorState.Warning, "Não saudável" => SensorState.Critical, _ => SensorState.Unavailable },
            "Estado de saúde informado pelo Windows (Storage Management API).");

        var r = info.Reliability;
        string adminHint = PrivilegeInfo.IsAdministrator ? "O disco não informou este contador." : "Exige executar o PrimePC como administrador.";
        PowerOnHours.SetText(r?.PowerOnHours is { } h ? $"{h.ToString("N0", CultureInfo.CurrentCulture)} h" : "Não disponível",
            r?.PowerOnHours is null ? SensorState.Unavailable : SensorState.Normal, r?.PowerOnHours is null ? adminHint : "Contador de confiabilidade do Windows.");
        Wear.SetText(r?.WearPercent is { } w ? $"{w} %" : "Não disponível",
            r?.WearPercent switch { null => SensorState.Unavailable, >= 100 => SensorState.Critical, >= 80 => SensorState.Warning, _ => SensorState.Normal },
            r?.WearPercent is null ? adminHint : "Desgaste estimado informado pelo disco (0% = novo).");
        long? errors = r is null ? null : (r.ReadErrorsUncorrected ?? 0) + (r.WriteErrorsUncorrected ?? 0);
        UncorrectedErrors.SetText(errors is { } e ? e.ToString(CultureInfo.CurrentCulture) : "Não disponível",
            errors switch { null => SensorState.Unavailable, > 0 => SensorState.Critical, _ => SensorState.Normal },
            errors is null ? adminHint : "Erros de leitura/gravação não corrigidos informados pelo disco.");
        Metrics = new ObservableCollection<MetricItem> { Health, Temperature, Activity, ReadRate, WriteRate, PowerOnHours, Wear, UncorrectedErrors, Spare, PercentUsed, DataWritten };
    }

    public DiskInfo Info { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string Volumes { get; }
    public DiskSensorSet? Sensors { get; set; }
    public ObservableCollection<MetricItem> Metrics { get; }
    public MetricItem Health { get; } = new("Saúde (Windows)");
    public MetricItem Temperature { get; } = new("Temperatura");
    public MetricItem Activity { get; } = new("Atividade");
    public MetricItem ReadRate { get; } = new("Leitura");
    public MetricItem WriteRate { get; } = new("Gravação");
    public MetricItem PowerOnHours { get; } = new("Horas ligado");
    public MetricItem Wear { get; } = new("Desgaste");
    public MetricItem UncorrectedErrors { get; } = new("Erros não corrigidos");
    public MetricItem Spare { get; } = new("Área reserva (SSD)");
    public MetricItem PercentUsed { get; } = new("Vida útil usada");
    public MetricItem DataWritten { get; } = new("Dados gravados");
}

public sealed record VolumeOption(string Root, string Label, string? DiskModel);

public sealed record StorageResultRow(string When, string Volume, string SeqRead, string SeqWrite, string RndRead, string RndWrite, string RndReadQ4);

public sealed partial class StorageViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    public StorageViewModel(AppServices services) : base(services)
    {
        services.InventoryReady += (_, _) => Application.Current.Dispatcher.BeginInvoke(BuildDisks);
        BuildDisks();
        LoadResults();
    }

    public override string Title => "Armazenamento";
    public override string Subtitle => "Saúde dos discos e teste de velocidade não destrutivo";
    public override string Glyph => "";

    public ObservableCollection<DiskDetail> Disks { get; } = new();
    public ObservableCollection<VolumeOption> VolumeOptions { get; } = new();
    public ObservableCollection<StorageResultRow> Results { get; } = new();
    public ObservableCollection<string> ResultNotes { get; } = new();
    public IReadOnlyList<int> SizeOptionsMb { get; } = new[] { 256, 512, 1024, 2048, 4096 };

    [ObservableProperty] private VolumeOption? _selectedVolume;
    [ObservableProperty] private int _sizeMb = 1024;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _phase = "";
    [ObservableProperty] private string? _lastSummary;

    public bool IsIdle => !IsRunning;
    public bool IsAdministrator => PrivilegeInfo.IsAdministrator;

    public override void OnSnapshot(SensorSnapshot s)
    {
        var map = Services.KeyMap;
        string hint = PrivilegeInfo.IsAdministrator ? "Este disco não informou esta leitura." : "Os sensores de disco exigem executar o PrimePC como administrador.";
        foreach (var d in Disks)
        {
            d.Sensors ??= map.Disks.FirstOrDefault(x => x.Name.Trim().Equals(d.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                          ?? map.Disks.FirstOrDefault(x => x.Name.Contains(d.Title, StringComparison.OrdinalIgnoreCase) || d.Title.Contains(x.Name, StringComparison.OrdinalIgnoreCase));
            var x = d.Sensors;
            d.Temperature.Set(s, x?.Temperature, TempUnit, hint);
            d.Activity.Set(s, x?.TotalActivity, TempUnit, hint);
            d.ReadRate.Set(s, x?.ReadRate, TempUnit, hint);
            d.WriteRate.Set(s, x?.WriteRate, TempUnit, hint);
            d.Spare.Set(s, x?.AvailableSpare, TempUnit, x is null ? hint : "Este disco não informa a área reserva.");
            d.PercentUsed.Set(s, x?.PercentageUsed ?? x?.RemainingLife, TempUnit, x is null ? hint : "Este disco não informa a vida útil.");
            d.DataWritten.Set(s, x?.DataWritten, TempUnit, x is null ? hint : "Este disco não informa o total gravado.");
            if (d.Info.Reliability?.PowerOnHours is null && s.ValueOf(x?.PowerOnHours) is { } hours)
                d.PowerOnHours.SetText($"{hours.ToString("N0", CultureInfo.CurrentCulture)} h", SensorState.Normal, "Informado pelo disco (SMART/NVMe).");
        }
    }

    private void BuildDisks()
    {
        var inv = Services.Inventory;
        if (inv is null)
            return;
        Disks.Clear();
        foreach (var d in inv.Disks)
            Disks.Add(new DiskDetail(d));

        VolumeOptions.Clear();
        foreach (var d in inv.Disks)
        {
            foreach (var v in d.Volumes)
            {
                VolumeOptions.Add(new VolumeOption(v.Letter + "\\",
                    $"{v.Letter} {(v.Label is null ? "" : $"\"{v.Label}\" ")}· {d.Model} · {SensorUnits.FormatBytes(v.FreeBytes)} livres", d.Model));
            }
        }

        SelectedVolume ??= VolumeOptions.FirstOrDefault();
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task StartAsync()
    {
        if (SelectedVolume is not { } volume)
            return;
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {Services.RunningTest}.");
            return;
        }

        long size = SizeMb * 1024L * 1024;
        string dir = StorageBenchmark.ResolveTargetDirectory(volume.Root);
        if (!Shell.Confirm(
                $"O teste vai gravar um arquivo temporário de {SensorUnits.FormatBytes(size)} em:\n{dir}\n\n" +
                "Ele mede leitura e gravação sequenciais e aleatórias (4 KiB) e apaga o arquivo ao final.\n" +
                "Nenhum arquivo seu é lido, alterado ou apagado. Testes destrutivos não são oferecidos.\n\n" +
                "Feche programas que estejam usando o disco para resultados mais consistentes. Continuar?",
                "Teste de armazenamento"))
            return;

        IsRunning = true;
        Services.RunningTest = "Teste de armazenamento";
        _cts = new CancellationTokenSource();
        var progress = new Progress<StorageBenchmarkProgress>(p =>
        {
            Phase = p.Phase;
            Progress = p.Fraction * 100;
        });
        try
        {
            var result = await StorageBenchmark.RunAsync(new StorageBenchmarkOptions { VolumeRoot = volume.Root, FileSizeBytes = size },
                volume.DiskModel, progress, _cts.Token);
            if (!result.Cancelled)
                Services.StorageHistory.Add(result);
            LastSummary = result.Cancelled
                ? "Teste cancelado. O arquivo temporário foi removido."
                : $"{result.Volume} · leitura {result.SequentialReadMBs:0} MB/s · gravação {result.SequentialWriteMBs:0} MB/s · " +
                  $"4K leitura {result.Random4kReadIops:N0} IOPS · 4K gravação {result.Random4kWriteIops:N0} IOPS";
            ResultNotes.Clear();
            foreach (var n in result.Notes)
                ResultNotes.Add(n);
            LoadResults();
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de armazenamento", ex);
            Shell.Error($"Não foi possível executar o teste: {ex.Message}");
        }
        finally
        {
            Services.RunningTest = null;
            IsRunning = false;
            Phase = "";
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void RefreshDisks() => Services.RefreshInventory();

    private void LoadResults()
    {
        Results.Clear();
        var c = CultureInfo.CurrentCulture;
        foreach (var r in Services.StorageHistory.Load().Reverse().Take(20))
        {
            Results.Add(new StorageResultRow(r.At.ToString("dd/MM HH:mm"), $"{r.Volume} {r.DiskModel}",
                $"{r.SequentialReadMBs.ToString("N0", c)} MB/s", $"{r.SequentialWriteMBs.ToString("N0", c)} MB/s",
                $"{r.Random4kReadIops.ToString("N0", c)} IOPS ({r.Random4kReadLatencyUs.ToString("0", c)} µs)",
                $"{r.Random4kWriteIops.ToString("N0", c)} IOPS ({r.Random4kWriteLatencyUs.ToString("0", c)} µs)",
                $"{r.Random4kReadQ4Iops.ToString("N0", c)} IOPS"));
        }
    }
}

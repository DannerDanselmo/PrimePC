using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.GpuTesting;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Teste de estabilidade da GPU (exibido na aba Estabilidade).</summary>
public sealed partial class GpuStressPanel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public GpuStressPanel(AppServices services)
    {
        _services = services;
        var last = services.GpuStressHistory.Load().LastOrDefault();
        if (last is not null)
            Show(last);
    }

    public ObservableCollection<GpuDeviceInfo> Devices { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public MetricItem LiveTemp { get; } = new("Temperatura da GPU");
    public MetricItem LiveClock { get; } = new("Clock");
    public MetricItem LivePower { get; } = new("Potência");
    public MetricItem LivePowerLimit { get; } = new("% do limite de potência");
    public MetricItem LiveLoad { get; } = new("Uso");

    [ObservableProperty] private GpuDeviceInfo? _selectedDevice;
    [ObservableProperty] private int _durationMinutes = 10;
    [ObservableProperty] private int _temperatureLimit = 88;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private long _errors;
    [ObservableProperty] private ChartSeries? _tempSeries;
    [ObservableProperty] private ChartSeries? _loadSeries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty] private SensorState _resultState;
    [ObservableProperty] private string? _resultDetail;
    [ObservableProperty] private string? _deviceMessage;

    public bool IsIdle => !IsRunning;
    public bool HasResult => ResultText is not null;

    public void Activate()
    {
        if (Devices.Count > 0)
            return;
        foreach (var d in _services.ComputeDevices)
            Devices.Add(d);
        int index = GpuDevices.DefaultIndex(Devices);
        SelectedDevice = Devices.FirstOrDefault(d => d.Index == index);
        DeviceMessage = Devices.Count == 0 ? "Nenhuma placa de vídeo compatível com DirectX 12 foi encontrada." : null;
    }

    public void OnSnapshot(SensorSnapshot s)
    {
        var gpu = _services.SensorsForGpu(SelectedDevice?.Name);
        var u = _services.Settings.TemperatureUnit;
        const string missing = "Os sensores desta placa de vídeo não foram encontrados.";
        LiveTemp.Set(s, gpu?.Temperature, u, missing);
        LiveClock.Set(s, gpu?.CoreClock, u, missing);
        LivePower.Set(s, gpu?.Power, u, missing);
        LivePowerLimit.Set(s, gpu?.PowerLimitPercent, u, "Esta placa não informa a potência em relação ao limite.");
        LiveLoad.Set(s, gpu?.Load, u, missing);
        if (gpu?.Temperature is { } t)
            TempSeries = ChartSeries.From(_services.Hub.GetHistory(t));
        if (gpu?.Load is { } l)
            LoadSeries = ChartSeries.From(_services.Hub.GetHistory(l));
    }

    /// <summary>Leitura da temperatura da GPU selecionada, usada para o corte térmico.</summary>
    public Func<float?> TemperatureReader(string? deviceName) => () =>
        _services.Hub.Latest.ValueOf(_services.SensorsForGpu(deviceName)?.Temperature);

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task StartAsync()
    {
        if (SelectedDevice is not { } device)
            return;
        if (_services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {_services.RunningTest}.");
            return;
        }

        if (!Shell.Confirm(
                $"Iniciar o teste de estabilidade da GPU ({device.Name}) por {DurationMinutes} min?\n\n" +
                "• A placa de vídeo vai trabalhar em carga máxima: temperatura, consumo e ruído das ventoinhas vão subir.\n" +
                "• A tela pode ficar menos responsiva durante o teste. Salve seu trabalho.\n" +
                $"• A carga é encerrada automaticamente se a GPU atingir {TemperatureLimit} °C.\n" +
                "• Você pode interromper a qualquer momento.",
                "Teste de GPU"))
            return;

        var reader = TemperatureReader(device.Name);
        bool allowWithoutTemp = false;
        if (reader() is null)
        {
            if (!Shell.Confirm("A temperatura desta GPU não está disponível, então o corte térmico automático não vai funcionar. " +
                               "Acompanhe o computador e interrompa se notar algo errado.\n\nContinuar mesmo assim?", "Sem proteção térmica automática"))
                return;
            allowWithoutTemp = true;
        }

        ResultText = null;
        Notes.Clear();
        Errors = 0;
        Progress = 0;
        RateText = "Calibrando a carga…";
        IsRunning = true;
        _services.RunningTest = "Teste de GPU";
        _services.Journal.Begin("Teste de GPU");
        _cts = new CancellationTokenSource();
        var duration = TimeSpan.FromMinutes(DurationMinutes);
        try
        {
            var result = await new GpuStressTester(reader).RunAsync(new GpuStressOptions
            {
                DeviceIndex = device.Index,
                Duration = duration,
                TemperatureLimit = TemperatureLimit,
                AllowWithoutTemperature = allowWithoutTemp,
            }, new Progress<GpuStressProgress>(p =>
            {
                Progress = p.Fraction * 100;
                ElapsedText = $"{SensorUnits.FormatDuration(p.Elapsed)} de {SensorUnits.FormatDuration(duration)}";
                RateText = $"{p.Gflops.ToString("N0", CultureInfo.CurrentCulture)} GFLOPS · {p.Dispatches.ToString("N0", CultureInfo.CurrentCulture)} execuções verificadas";
                Errors = p.Errors;
            }), _cts.Token);
            Show(result);
            _services.GpuStressHistory.Add(result);
            Log.Info($"Teste de GPU: {result.Summary}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de GPU", ex);
            ResultText = $"Não foi possível concluir o teste: {ex.Message}";
            ResultState = SensorState.Critical;
        }
        finally
        {
            _services.Journal.End();
            _services.RunningTest = null;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private void Show(GpuStressResult r)
    {
        ResultText = r.Summary;
        ResultState = r.Outcome switch
        {
            GpuTestOutcome.Completed => SensorState.Normal,
            GpuTestOutcome.Cancelled or GpuTestOutcome.StoppedByTemperature => SensorState.Warning,
            _ => SensorState.Critical,
        };
        ResultDetail = $"{r.DeviceName} · {r.StartedAt:dd/MM/yyyy HH:mm} · {SensorUnits.FormatDuration(r.Elapsed)} · " +
                       $"{r.AverageGflops.ToString("N0", CultureInfo.CurrentCulture)} GFLOPS médios · " +
                       (r.MaxTemperature is { } t ? $"temperatura máxima {t:0.0} °C" : "temperatura não disponível");
        Notes.Clear();
        foreach (var n in r.Notes)
            Notes.Add(n);
    }
}

public sealed record VramErrorRow(int Pass, string Pattern, string Offset, string Expected, string Actual, int Bits);

/// <summary>Teste da memória de vídeo (exibido na aba Teste de RAM, como módulo separado).</summary>
public sealed partial class VramTestPanel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public VramTestPanel(AppServices services)
    {
        _services = services;
        var last = services.VramHistory.Load().LastOrDefault();
        if (last is not null)
            Show(last);
    }

    public ObservableCollection<GpuDeviceInfo> Devices { get; } = new();
    public ObservableCollection<VramErrorRow> Errors { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public IReadOnlyList<int> PassOptions { get; } = new[] { 1, 5, 10, 25, 50, 100 };
    public double MinMb => VramTester.ChunkBytes / 1024d / 1024d;

    [ObservableProperty] private GpuDeviceInfo? _selectedDevice;
    [ObservableProperty] private double _maxMb = 1024;
    [ObservableProperty] private double _amountMb = 1024;
    [ObservableProperty] private int _passes = 10;
    [ObservableProperty] private string _memoryText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private long _errorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty] private SensorState _resultState;
    [ObservableProperty] private string? _resultDetail;
    [ObservableProperty] private string? _deviceMessage;

    public bool IsIdle => !IsRunning;
    public bool HasResult => ResultText is not null;
    public string AmountText => SensorUnits.FormatBytes((long)AmountMb * 1024 * 1024);

    private bool _budgetFromSensors;
    private bool _amountEditedByUser;
    private bool _settingAmount;

    partial void OnAmountMbChanged(double value)
    {
        OnPropertyChanged(nameof(AmountText));
        if (!_settingAmount)
            _amountEditedByUser = true;
    }

    partial void OnSelectedDeviceChanged(GpuDeviceInfo? value)
    {
        RefreshBudget();
        SetAmount(MaxMb);
        _amountEditedByUser = false;
    }

    private void SetAmount(double mb)
    {
        _settingAmount = true;
        AmountMb = Math.Floor(mb / 256) * 256;
        _settingAmount = false;
    }

    /// <summary>Recalcula o limite quando a leitura da VRAM em uso fica disponível (ela chega após a primeira coleta).</summary>
    public void OnSnapshot(SensorSnapshot snapshot)
    {
        if (IsRunning || _budgetFromSensors || SelectedDevice is null)
            return;
        RefreshBudget();
        if (_budgetFromSensors && !_amountEditedByUser)
            SetAmount(MaxMb);
    }

    public void Activate()
    {
        if (Devices.Count == 0)
        {
            foreach (var d in _services.ComputeDevices)
                Devices.Add(d);
            int index = GpuDevices.DefaultIndex(Devices);
            SelectedDevice = Devices.FirstOrDefault(d => d.Index == index);
            DeviceMessage = Devices.Count == 0 ? "Nenhuma placa de vídeo compatível com DirectX 12 foi encontrada." : null;
        }
        else if (!IsRunning)
        {
            RefreshBudget();
        }
    }

    /// <summary>
    /// Limite seguro: VRAM livre informada pelos sensores menos 1 GB de reserva para o Windows e a tela;
    /// sem sensores, 70% da memória dedicada.
    /// </summary>
    private void RefreshBudget()
    {
        if (SelectedDevice is not { } d)
            return;
        var s = _services.Hub.Latest;
        var gpu = _services.SensorsForGpu(d.Name);
        float? used = s.ValueOf(gpu?.VramUsed), total = s.ValueOf(gpu?.VramTotal);
        double totalMb = d.DedicatedMemoryBytes / 1024d / 1024d;
        _budgetFromSensors = used is not null && total is > 0;
        double safeMb = _budgetFromSensors
            ? Math.Min(totalMb, total!.Value) - used!.Value - 1024
            : totalMb * 0.7;
        MaxMb = Math.Max(MinMb, Math.Floor(safeMb / 256) * 256);
        if (AmountMb > MaxMb)
            SetAmount(MaxMb);
        MemoryText = used is not null && total is > 0
            ? $"VRAM: {total / 1024:0.0} GB · em uso agora: {used / 1024:0.0} GB · limite seguro para teste: {SensorUnits.FormatBytes((long)MaxMb * 1024 * 1024)} (reserva de 1 GB para o Windows e a tela)"
            : $"VRAM dedicada: {SensorUnits.FormatBytes(d.DedicatedMemoryBytes)} · uso atual não disponível; limite seguro estimado em 70%: {SensorUnits.FormatBytes((long)MaxMb * 1024 * 1024)}";
    }

    [RelayCommand]
    private void UseMaximum()
    {
        RefreshBudget();
        SetAmount(MaxMb);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task StartAsync()
    {
        if (SelectedDevice is not { } device)
            return;
        if (_services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {_services.RunningTest}.");
            return;
        }

        RefreshBudget();
        long bytes = (long)Math.Min(AmountMb, MaxMb) * 1024 * 1024;
        if (!Shell.Confirm(
                $"Testar {SensorUnits.FormatBytes(bytes)} da VRAM da {device.Name} ({Passes} passagem(ns), 8 padrões)?\n\n" +
                "• Feche jogos e programas que usam a placa de vídeo para liberar VRAM.\n" +
                "• A tela pode ficar menos responsiva durante o teste.\n" +
                "• Você pode cancelar a qualquer momento.",
                "Teste de VRAM"))
            return;

        Errors.Clear();
        Notes.Clear();
        ResultText = null;
        ErrorCount = 0;
        Progress = 0;
        IsRunning = true;
        _services.RunningTest = "Teste de VRAM";
        _services.Journal.Begin("Teste de VRAM");
        _cts = new CancellationTokenSource();
        try
        {
            var result = await new VramTester().RunAsync(new VramTestOptions { DeviceIndex = device.Index, BytesToTest = bytes, Passes = Passes },
                new Progress<VramTestProgress>(p =>
                {
                    Progress = p.Fraction * 100;
                    StatusText = $"Passagem {p.Pass} de {p.TotalPasses} · {p.Pattern} · {p.Phase} · {SensorUnits.FormatDuration(p.Elapsed)}";
                    ErrorCount = p.ErrorCount;
                }), _cts.Token);
            Show(result);
            _services.VramHistory.Add(result);
            Log.Info($"Teste de VRAM: {result.Summary}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de VRAM", ex);
            ResultText = $"Não foi possível concluir o teste: {ex.Message}";
            ResultState = SensorState.Critical;
        }
        finally
        {
            _services.Journal.End();
            _services.RunningTest = null;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private void Show(VramTestResult r)
    {
        ResultText = r.Summary;
        ResultState = r.Outcome switch
        {
            GpuTestOutcome.Completed => SensorState.Normal,
            GpuTestOutcome.Cancelled => SensorState.Warning,
            _ => SensorState.Critical,
        };
        ResultDetail = $"{r.DeviceName} · {r.StartedAt:dd/MM/yyyy HH:mm} · {SensorUnits.FormatBytes(r.BytesTested)} testados · " +
                       $"{r.PassesCompleted} passagem(ns) · {r.PatternsCompleted} padrões · {SensorUnits.FormatDuration(r.Elapsed)}";
        Errors.Clear();
        foreach (var e in r.Errors)
        {
            Errors.Add(new VramErrorRow(e.Pass, e.Pattern, $"0x{e.Offset:X}", $"0x{e.Expected:X8}", $"0x{e.Actual:X8}",
                System.Numerics.BitOperations.PopCount(e.Expected ^ e.Actual)));
        }

        ErrorCount = r.ErrorCount;
        Notes.Clear();
        foreach (var n in r.Notes)
            Notes.Add(n);
        if (r.ErrorCount > r.Errors.Count)
            Notes.Insert(0, $"Apenas os primeiros {r.Errors.Count} erros estão listados (total: {r.ErrorCount:N0}).");
    }
}

using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Controls;
using PrimePC.App.Services;
using PrimePC.GpuTesting;
using PrimePC.Sensors;
using PrimePC.Testing;

namespace PrimePC.App.ViewModels;

public sealed record BenchmarkRow(string Name, string Result, string Spread, string Comparison, string When, string Conditions);

public sealed partial class StabilityViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    private bool _limitEditedByUser;
    private bool _settingLimit;

    public StabilityViewModel(AppServices services) : base(services)
    {
        Gpu = new GpuStressPanel(services);
        ApplyDefaultTemperatureLimit();
        // O modelo da CPU (e portanto o limite térmico seguro, ex.: 89 °C nos X3D) só é conhecido após o inventário.
        services.InventoryReady += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyDefaultTemperatureLimit);
        services.KeyMapChanged += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyDefaultTemperatureLimit);
        LoadBenchmarks();
        LoadLastStress();
    }

    private void ApplyDefaultTemperatureLimit()
    {
        if (_limitEditedByUser || IsRunning)
            return;
        _settingLimit = true;
        TemperatureLimit = (int)Services.Evaluator.CpuTempLimits.Crit;
        _settingLimit = false;
    }

    partial void OnTemperatureLimitChanged(int value)
    {
        if (!_settingLimit)
            _limitEditedByUser = true;
    }

    public override void OnActivated()
    {
        ApplyDefaultTemperatureLimit();
        Gpu.Activate();
    }

    /// <summary>Teste de estabilidade da placa de vídeo (módulo de GPU).</summary>
    public GpuStressPanel Gpu { get; }

    [ObservableProperty] private bool _includeGpu;

    public override string Title => "Estabilidade";
    public override string Subtitle => "Testes de carga controlada e benchmarks reprodutíveis";
    public override string Glyph => "";

    public int ProcessorCount { get; } = Environment.ProcessorCount;
    public ObservableCollection<BenchmarkRow> Benchmarks { get; } = new();
    public ObservableCollection<string> StressNotes { get; } = new();
    public MetricItem LiveTemp { get; } = new("Temperatura da CPU");
    public MetricItem LiveClock { get; } = new("Clock médio");
    public MetricItem LivePower { get; } = new("Potência da CPU");
    public MetricItem LiveLoad { get; } = new("Uso total");

    [ObservableProperty] private int _threads = Environment.ProcessorCount;
    [ObservableProperty] private int _durationMinutes = 10;
    [ObservableProperty] private int _temperatureLimit;
    [ObservableProperty] private bool _includeRam;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartStressCommand), nameof(CancelCommand), nameof(RunBenchmarkCommand))]
    private bool _isRunning;

    [ObservableProperty] private string _runningLabel = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _rateText = "";
    [ObservableProperty] private long _computeErrors;
    [ObservableProperty] private string? _ramProgressText;
    [ObservableProperty] private ChartSeries? _tempSeries;
    [ObservableProperty] private ChartSeries? _loadSeries;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStressResult))]
    private string? _stressResult;

    [ObservableProperty] private SensorState _stressState;
    [ObservableProperty] private string? _stressDetail;

    public bool IsIdle => !IsRunning;
    public bool HasStressResult => StressResult is not null;
    public bool TemperatureAvailable => Services.KeyMap.CpuTemperature is { } id && Services.Hub.Latest.ValueOf(id) is not null;

    public override void OnSnapshot(SensorSnapshot s)
    {
        var map = Services.KeyMap;
        string hint = AppServices.LowLevelAccess ? "O hardware não informou esta leitura." : "Exige o driver PawnIO e execução como administrador.";
        LiveTemp.Set(s, map.CpuTemperature, TempUnit, hint);
        LivePower.Set(s, map.CpuPower, TempUnit, hint);
        LiveLoad.Set(s, map.CpuTotalLoad, TempUnit);
        var clock = map.AverageCpuClock(s);
        LiveClock.SetText(clock is null ? "Não disponível" : Format(SensorKind.Clock, clock),
            clock is null ? SensorState.Unavailable : SensorState.Normal, clock is null ? hint : null);
        if (map.CpuTemperature is { } t)
            TempSeries = ChartSeries.From(Services.Hub.GetHistory(t));
        if (map.CpuTotalLoad is { } l)
            LoadSeries = ChartSeries.From(Services.Hub.GetHistory(l));
        OnPropertyChanged(nameof(TemperatureAvailable));
        Gpu.OnSnapshot(s);
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task StartStressAsync()
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {Services.RunningTest}.");
            return;
        }

        string what = (IncludeRam, IncludeGpu) switch
        {
            (true, true) => "teste combinado (CPU + RAM + GPU)",
            (true, false) => "teste combinado (CPU + RAM)",
            (false, true) => "teste combinado (CPU + GPU)",
            _ => "teste de estabilidade da CPU",
        };
        if (!Shell.Confirm(
                $"Iniciar {what} por {DurationMinutes} min em {Threads} threads?\n\n" +
                "• O teste eleva a temperatura, o consumo de energia e o ruído das ventoinhas.\n" +
                "• Salve seu trabalho antes de começar.\n" +
                $"• A carga é encerrada automaticamente se a CPU atingir {TemperatureLimit} °C.\n" +
                "• Você pode interromper a qualquer momento.",
                "Teste de estabilidade"))
            return;

        bool allowWithoutTemp = false;
        if (!TemperatureAvailable)
        {
            if (!Shell.Confirm(
                    "A temperatura da CPU NÃO está disponível neste momento (driver PawnIO ausente ou sem administrador).\n\n" +
                    "Sem essa leitura, o corte térmico automático não funciona: você precisará acompanhar o computador e " +
                    "interromper o teste manualmente se notar algo errado.\n\nContinuar mesmo assim?",
                    "Sem proteção térmica automática"))
                return;
            allowWithoutTemp = true;
        }

        StressResult = null;
        StressNotes.Clear();
        ComputeErrors = 0;
        Progress = 0;
        RamProgressText = null;
        RunningLabel = IncludeRam || IncludeGpu ? "Teste combinado em andamento" : "Teste de CPU em andamento";
        IsRunning = true;
        Services.RunningTest = what;
        Services.Journal.Begin(what);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var duration = TimeSpan.FromMinutes(DurationMinutes);
            var cpuProgress = new Progress<CpuStressProgress>(p =>
            {
                Progress = p.Fraction * 100;
                ElapsedText = $"{SensorUnits.FormatDuration(p.Elapsed)} de {SensorUnits.FormatDuration(duration)}";
                RateText = $"{p.IterationsPerSecond.ToString("N0", CultureInfo.CurrentCulture)} iterações/s";
                ComputeErrors = p.Errors;
            });

            Task<MemoryTestResult>? ramTask = null;
            using var ramCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (IncludeRam)
            {
                var (_, _, safe) = MemoryBudget.Current();
                long bytes = Math.Max(MemoryBudget.MinimumTestBytes, safe / 2 / MemoryBudget.Granularity * MemoryBudget.Granularity);
                var ramProgress = new Progress<MemoryTestProgress>(p =>
                    RamProgressText = $"RAM: {SensorUnits.FormatBytes(bytes)} · {p.Pattern} · passagem {p.Pass} · erros: {p.ErrorCount}");
                ramTask = new MemoryTester().RunAsync(new MemoryTestOptions
                {
                    BytesToTest = bytes,
                    Mode = MemoryTestMode.Timed,
                    Duration = duration,
                    Threads = Math.Max(1, Threads / 4),
                }, ramProgress, ramCts.Token);
            }

            Task<GpuStressResult>? gpuTask = null;
            using var gpuCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (IncludeGpu)
            {
                Gpu.Activate();
                if (Gpu.SelectedDevice is { } device)
                {
                    var reader = Gpu.TemperatureReader(device.Name);
                    gpuTask = new GpuStressTester(reader).RunAsync(new GpuStressOptions
                    {
                        DeviceIndex = device.Index,
                        Duration = duration,
                        TemperatureLimit = Gpu.TemperatureLimit,
                        AllowWithoutTemperature = true,
                    }, null, gpuCts.Token);
                }
            }

            var tester = new CpuStressTester(Services.CurrentCpuTemperature);
            var result = await tester.RunAsync(new CpuStressOptions
            {
                Threads = Threads,
                Duration = duration,
                TemperatureLimit = TemperatureLimit,
                AllowWithoutTemperature = allowWithoutTemp,
            }, cpuProgress, token);

            MemoryTestResult? ramResult = null;
            if (ramTask is not null)
            {
                ramCts.Cancel();
                ramResult = await ramTask;
            }

            GpuStressResult? gpuResult = null;
            if (gpuTask is not null)
            {
                gpuCts.Cancel();
                gpuResult = await gpuTask;
            }

            ShowStress(result, ramResult);
            if (gpuResult is not null)
            {
                StressNotes.Insert(0, $"GPU (simultâneo, {gpuResult.DeviceName}): {gpuResult.Summary}");
                if (gpuResult.Outcome is GpuTestOutcome.ErrorsDetected or GpuTestOutcome.DeviceLost)
                    StressState = SensorState.Critical;
                Services.GpuStressHistory.Add(gpuResult);
            }

            Services.CpuHistory.Add(result);
            if (ramResult is not null)
                Services.RamHistory.Add(ramResult);
            Log.Info($"Teste de estabilidade: {result.Summary}");
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de estabilidade", ex);
            StressResult = $"Não foi possível concluir o teste: {ex.Message}";
            StressState = SensorState.Critical;
        }
        finally
        {
            Services.Journal.End();
            Services.RunningTest = null;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    private void ShowStress(CpuStressResult r, MemoryTestResult? ram)
    {
        StressResult = r.Summary;
        StressState = r.Outcome switch
        {
            StressOutcome.Completed => SensorState.Normal,
            StressOutcome.Cancelled => SensorState.Warning,
            StressOutcome.StoppedByTemperature => SensorState.Warning,
            _ => SensorState.Critical,
        };
        StressDetail = $"{r.StartedAt:dd/MM/yyyy HH:mm} · {r.Threads} threads · {SensorUnits.FormatDuration(r.Elapsed)} · " +
                       $"{r.Iterations.ToString("N0", CultureInfo.CurrentCulture)} iterações verificadas · " +
                       (r.MaxTemperature is { } t ? $"temperatura máxima {t:0.0} °C" : "temperatura não disponível");
        StressNotes.Clear();
        if (ram is not null)
        {
            StressNotes.Add($"RAM (simultâneo): {ram.Summary}");
            if (ram.ErrorCount > 0)
                StressState = SensorState.Critical;
        }

        foreach (var n in r.Notes)
            StressNotes.Add(n);
        foreach (var e in r.ErrorSamples.Take(10))
            StressNotes.Add($"Erro de cálculo às {e.Time:HH:mm:ss} na thread {e.Thread} ({e.Workload}).");
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RunBenchmarkAsync(string which)
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {Services.RunningTest}.");
            return;
        }

        IsRunning = true;
        Services.RunningTest = "Benchmark";
        _cts = new CancellationTokenSource();
        try
        {
            var list = which switch
            {
                "cpu1" => new[] { "cpu1" },
                "cpuN" => new[] { "cpuN" },
                "mem" => new[] { "bw", "lat" },
                _ => new[] { "cpu1", "cpuN", "bw", "lat" },
            };
            foreach (var b in list)
            {
                RunningLabel = b switch
                {
                    "cpu1" => "Benchmark de CPU (uma thread)…",
                    "cpuN" => "Benchmark de CPU (todas as threads)…",
                    "bw" => "Benchmark de largura de banda da memória…",
                    _ => "Benchmark de latência da memória…",
                };
                var result = b switch
                {
                    "cpu1" => await SystemBenchmarks.CpuSingleThreadAsync(_cts.Token),
                    "cpuN" => await SystemBenchmarks.CpuMultiThreadAsync(_cts.Token),
                    "bw" => await SystemBenchmarks.MemoryBandwidthAsync(_cts.Token),
                    _ => await SystemBenchmarks.MemoryLatencyAsync(_cts.Token),
                };
                Services.BenchmarkHistory.Add(result);
                LoadBenchmarks();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelado pelo usuário.
        }
        catch (Exception ex)
        {
            Log.Error("Falha no benchmark", ex);
            Shell.Error($"Falha no benchmark: {ex.Message}");
        }
        finally
        {
            RunningLabel = "";
            Services.RunningTest = null;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    private void LoadBenchmarks()
    {
        var c = CultureInfo.CurrentCulture;
        var history = Services.BenchmarkHistory.Load();
        Benchmarks.Clear();
        foreach (var r in history.Reverse().Take(30))
        {
            var previous = history.Where(h => h.Name == r.Name && h.At < r.At).OrderByDescending(h => h.At).FirstOrDefault();
            string comparison = "Primeira execução";
            if (previous is not null && previous.Value > 0)
            {
                double delta = (r.Value - previous.Value) / previous.Value * 100;
                bool better = r.HigherIsBetter ? delta > 0 : delta < 0;
                comparison = Math.Abs(delta) < Math.Max(2, r.SpreadPercent)
                    ? $"Igual à anterior (dentro da variação, {delta:+0.#;-0.#;0}%)"
                    : $"{delta:+0.#;-0.#}% vs. anterior ({(better ? "melhor" : "pior")})";
            }

            Benchmarks.Add(new BenchmarkRow(r.Name, $"{r.Value.ToString(r.Value >= 100 ? "N0" : "0.##", c)} {r.Unit}",
                $"±{r.SpreadPercent.ToString("0.#", c)}%", comparison, r.At.ToString("dd/MM HH:mm"), r.Conditions));
        }
    }

    [RelayCommand]
    private void ClearBenchmarks()
    {
        if (!Shell.Confirm("Apagar o histórico de benchmarks?"))
            return;
        Services.BenchmarkHistory.Clear();
        LoadBenchmarks();
    }

    private void LoadLastStress()
    {
        var last = Services.CpuHistory.Load().LastOrDefault();
        if (last is not null)
            ShowStress(last, null);
    }
}

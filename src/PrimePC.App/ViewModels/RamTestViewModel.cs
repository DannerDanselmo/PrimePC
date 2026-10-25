using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrimePC.App.Services;
using PrimePC.Sensors;
using PrimePC.Testing;

namespace PrimePC.App.ViewModels;

public sealed record MemoryErrorRow(string Time, int Pass, string Pattern, string Offset, string Expected, string Actual, int Bits);

public sealed partial class RamTestViewModel : PageViewModel
{
    private CancellationTokenSource? _cts;

    public RamTestViewModel(AppServices services) : base(services)
    {
        Vram = new VramTestPanel(services);
        RefreshBudget();
        AmountMb = Math.Min(4096, Math.Max(MinMb, MaxMb / 2));
        LoadLast();
    }

    public override string Title => "Teste de RAM";
    public override string Subtitle => "Procura erros de memória gravando e verificando padrões variados";
    public override string Glyph => "";

    public ObservableCollection<MemoryErrorRow> Errors { get; } = new();
    public ObservableCollection<string> Notes { get; } = new();
    public int ProcessorCount { get; } = Environment.ProcessorCount;
    public double MinMb => MemoryBudget.MinimumTestBytes / 1024d / 1024d;

    [ObservableProperty] private double _maxMb = 1024;
    [ObservableProperty] private string _memoryText = "";
    [ObservableProperty] private double _amountMb;
    [ObservableProperty] private bool _isQuick = true;
    [ObservableProperty] private bool _isFull;
    [ObservableProperty] private bool _isTimed;
    [ObservableProperty] private int _passes = 2;
    [ObservableProperty] private int _durationMinutes = 30;
    [ObservableProperty] private int _threads = Environment.ProcessorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CancelCommand))]
    private bool _isRunning;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _phaseText = "";
    [ObservableProperty] private string _patternText = "";
    [ObservableProperty] private string _passText = "";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _processedText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private long _errorCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultText;

    [ObservableProperty] private SensorState _resultState;
    [ObservableProperty] private string? _resultDetail;

    public bool IsIdle => !IsRunning;
    public bool HasResult => ResultText is not null;
    public string AmountText => SensorUnits.FormatBytes((long)AmountMb * 1024 * 1024);

    partial void OnAmountMbChanged(double value) => OnPropertyChanged(nameof(AmountText));

    /// <summary>Teste da memória de vídeo (módulo independente do teste de RAM).</summary>
    public VramTestPanel Vram { get; }

    public override void OnActivated()
    {
        if (!IsRunning)
            RefreshBudget();
        Vram.Activate();
    }

    public override void OnSnapshot(SensorSnapshot snapshot) => Vram.OnSnapshot(snapshot);

    private void RefreshBudget()
    {
        var (total, available, safe) = MemoryBudget.Current();
        MaxMb = Math.Max(MinMb, safe / 1024d / 1024d);
        if (AmountMb > MaxMb)
            AmountMb = MaxMb;
        MemoryText = $"Memória física: {SensorUnits.FormatBytes(total)} · disponível agora: {SensorUnits.FormatBytes(available)} · " +
                     $"limite seguro para teste: {SensorUnits.FormatBytes(safe)} (reserva de {SensorUnits.FormatBytes(MemoryBudget.SystemReserve)} para o Windows)";
    }

    [RelayCommand]
    private void UseMaximum()
    {
        RefreshBudget();
        AmountMb = Math.Floor(MaxMb / 256) * 256;
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task StartAsync()
    {
        if (Services.RunningTest is not null)
        {
            Shell.Info($"Já existe um teste em execução: {Services.RunningTest}.");
            return;
        }

        RefreshBudget();
        long bytes = (long)AmountMb * 1024 * 1024;
        var mode = IsFull ? MemoryTestMode.Full : IsTimed ? MemoryTestMode.Timed : MemoryTestMode.Quick;
        string modeText = mode switch
        {
            MemoryTestMode.Quick => "rápido (1 passagem, 6 padrões)",
            MemoryTestMode.Full => $"completo ({Passes} passagens, 25 padrões)",
            _ => $"por duração ({DurationMinutes} min)",
        };
        if (!Shell.Confirm(
                $"Iniciar teste {modeText} em {SensorUnits.FormatBytes(bytes)} de RAM?\n\n" +
                "• Salve seu trabalho antes: o teste ocupa bastante memória e deixa o computador mais lento.\n" +
                "• Nenhum arquivo é lido ou alterado; apenas uma área de memória reservada para o teste.\n" +
                "• Você pode cancelar a qualquer momento com segurança.\n" +
                "• O teste é interrompido automaticamente se o Windows ficar com pouca memória livre.",
                "Teste de memória RAM"))
            return;

        Errors.Clear();
        Notes.Clear();
        ResultText = null;
        ResultDetail = null;
        ErrorCount = 0;
        Progress = 0;
        IsRunning = true;
        Services.RunningTest = "Teste de RAM";
        Services.Journal.Begin("Teste de RAM");
        _cts = new CancellationTokenSource();
        var progress = new Progress<MemoryTestProgress>(p =>
        {
            Progress = p.Fraction * 100;
            PhaseText = p.Phase;
            PatternText = p.Pattern;
            PassText = p.TotalPasses is { } tp ? $"Passagem {p.Pass} de {tp}" : $"Passagem {p.Pass}";
            ElapsedText = SensorUnits.FormatDuration(p.Elapsed);
            ProcessedText = SensorUnits.FormatBytes(p.BytesProcessed);
            SpeedText = $"{p.ThroughputGBs:0.0} GB/s";
            ErrorCount = p.ErrorCount;
        });

        try
        {
            var options = new MemoryTestOptions
            {
                BytesToTest = bytes,
                Mode = mode,
                Passes = Passes,
                Duration = TimeSpan.FromMinutes(DurationMinutes),
                Threads = Threads,
            };
            var result = await new MemoryTester().RunAsync(options, progress, _cts.Token);
            Show(result);
            Services.RamHistory.Add(result);
            Log.Info($"Teste de RAM: {result.Summary} ({SensorUnits.FormatBytes(result.BytesTested)}, {result.Elapsed})");
        }
        catch (Exception ex)
        {
            Log.Error("Falha no teste de RAM", ex);
            ResultText = $"Não foi possível concluir o teste: {ex.Message}";
            ResultState = SensorState.Critical;
        }
        finally
        {
            Services.Journal.End();
            Services.RunningTest = null;
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
            RefreshBudget();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void OpenWindowsMemoryDiagnostic()
    {
        if (!Shell.Confirm(
                "O Diagnóstico de Memória do Windows (mdsched.exe) testa a RAM fora do Windows, alcançando mais memória física.\n\n" +
                "Ele vai perguntar se você quer reiniciar agora ou na próxima inicialização. O resultado aparece após o reinício " +
                "(Visualizador de Eventos > Logs do Windows > Sistema, origem \"MemoryDiagnostics-Results\").\n\nAbrir agora?",
                "Teste inicializável"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo("mdsched.exe") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Shell.Error($"Não foi possível abrir o Diagnóstico de Memória do Windows: {ex.Message}");
        }
    }

    private void Show(MemoryTestResult r)
    {
        ResultText = r.Summary;
        ResultState = r.Outcome switch
        {
            MemoryTestOutcome.NoErrors => SensorState.Normal,
            MemoryTestOutcome.ErrorsDetected => SensorState.Critical,
            MemoryTestOutcome.Failed => SensorState.Critical,
            _ => SensorState.Warning,
        };
        ResultDetail = $"{r.StartedAt:dd/MM/yyyy HH:mm} · {SensorUnits.FormatBytes(r.BytesTested)} testados · {r.PassesCompleted} passagem(ns) completa(s) · " +
                       $"{r.PatternsCompleted} padrões · {SensorUnits.FormatDuration(r.Elapsed)} · {r.Threads} threads · " +
                       (r.MemoryLocked ? "memória fixada na RAM" : "memória não fixada");
        Errors.Clear();
        foreach (var e in r.Errors)
        {
            Errors.Add(new MemoryErrorRow(e.Time.ToString("HH:mm:ss"), e.Pass, e.Pattern, $"0x{e.Offset:X}", $"0x{e.Expected:X16}",
                $"0x{e.Actual:X16}", e.DifferingBitCount));
        }

        ErrorCount = r.ErrorCount;
        Notes.Clear();
        foreach (var n in r.Notes)
            Notes.Add(n);
        if (r.ErrorCount > r.Errors.Count)
            Notes.Insert(0, $"Apenas os primeiros {r.Errors.Count} erros estão listados (total: {r.ErrorCount}).");
    }

    private void LoadLast()
    {
        var last = Services.RamHistory.Load().LastOrDefault();
        if (last is not null)
            Show(last);
    }
}

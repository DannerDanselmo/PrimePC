using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PrimePC.App.Services;
using PrimePC.Gaming;
using PrimePC.Sensors;

namespace PrimePC.App.ViewModels;

/// <summary>Valores exibidos na sobreposição (e na prévia da página Jogos).</summary>
public sealed partial class OverlayDisplay : ObservableObject
{
    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    [ObservableProperty] private string _fpsText = "—";
    [ObservableProperty] private string _frameTimeText = "— ms";
    [ObservableProperty] private string _low1Text = "—";
    [ObservableProperty] private double[] _frameTimes = Array.Empty<double>();
    [ObservableProperty] private string _cpuLoadText = "—";
    [ObservableProperty] private string _cpuTempText = "—";
    [ObservableProperty] private SensorState _cpuTempState = SensorState.Normal;
    [ObservableProperty] private string _cpuPowerText = "—";
    [ObservableProperty] private string _gpuLoadText = "—";
    [ObservableProperty] private string _gpuTempText = "—";
    [ObservableProperty] private SensorState _gpuTempState = SensorState.Normal;
    [ObservableProperty] private string _gpuClockText = "—";
    [ObservableProperty] private string _gpuPowerText = "—";
    [ObservableProperty] private string _vramText = "—";
    [ObservableProperty] private string _ramText = "—";
    [ObservableProperty] private string _clockText = "";
    [ObservableProperty] private string _pingLabel = "PING";
    [ObservableProperty] private string _pingText = "—";

    /// <summary>"● BENCH 0:42" enquanto um benchmark grava (nulo quando não há).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecording))]
    private string? _recordingText;

    /// <summary>Mensagem curta temporária (ex.: resultado do benchmark).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    private string? _toastText;

    [ObservableProperty] private bool _showFps;
    [ObservableProperty] private bool _showFrameTime;
    [ObservableProperty] private bool _showLow1;
    [ObservableProperty] private bool _showGraph;
    [ObservableProperty] private bool _showCpuLoad;
    [ObservableProperty] private bool _showCpuTemp;
    [ObservableProperty] private bool _showCpuPower;
    [ObservableProperty] private bool _showGpuLoad;
    [ObservableProperty] private bool _showGpuTemp;
    [ObservableProperty] private bool _showGpuClock;
    [ObservableProperty] private bool _showGpuPower;
    [ObservableProperty] private bool _showVram;
    [ObservableProperty] private bool _showRam;
    [ObservableProperty] private bool _showClock;
    [ObservableProperty] private bool _showPing;
    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private double _backgroundOpacity = 0.6;

    public bool ShowFpsRow => ShowFps || ShowFrameTime || ShowLow1;
    public bool ShowCpuRow => ShowCpuLoad || ShowCpuTemp || ShowCpuPower;
    public bool ShowGpuRow => ShowGpuLoad || ShowGpuTemp || ShowGpuClock || ShowGpuPower;
    public bool HasRecording => RecordingText is not null;
    public bool HasToast => ToastText is not null;

    public void ApplySettings(OverlayLayout s)
    {
        ShowFps = s.ShowFps;
        ShowFrameTime = s.ShowFrameTime;
        ShowLow1 = s.ShowLow1;
        ShowGraph = s.ShowGraph;
        ShowCpuLoad = s.ShowCpuLoad;
        ShowCpuTemp = s.ShowCpuTemp;
        ShowCpuPower = s.ShowCpuPower;
        ShowGpuLoad = s.ShowGpuLoad;
        ShowGpuTemp = s.ShowGpuTemp;
        ShowGpuClock = s.ShowGpuClock;
        ShowGpuPower = s.ShowGpuPower;
        ShowVram = s.ShowVram;
        ShowRam = s.ShowRam;
        ShowClock = s.ShowClock;
        ShowPing = s.ShowPing;
        Scale = s.Size switch { OverlaySize.Small => 0.85, OverlaySize.Large => 1.3, _ => 1.0 };
        BackgroundOpacity = Math.Clamp(s.BackgroundOpacity, 0, 100) / 100.0;
        OnPropertyChanged(nameof(ShowFpsRow));
        OnPropertyChanged(nameof(ShowCpuRow));
        OnPropertyChanged(nameof(ShowGpuRow));
    }

    public void SetFrames(FrameStats? stats)
    {
        FpsText = stats is null ? "—" : Math.Round(stats.Fps).ToString("0", Culture);
        FrameTimeText = stats is null ? "— ms" : stats.FrameTimeMs.ToString("0.0", Culture) + " ms";
        Low1Text = stats?.Low1Fps is { } low ? Math.Round(low).ToString("0", Culture) : "—";
        FrameTimes = stats?.RecentFrameTimes ?? Array.Empty<double>();
    }

    public void SetSensors(SensorSnapshot snapshot, KeySensorMap map, GpuSensorSet? gpu, TemperatureUnit unit)
    {
        CpuLoadText = Percent(snapshot.ValueOf(map.CpuTotalLoad));
        (CpuTempText, CpuTempState) = Temperature(snapshot, map.CpuTemperature, unit);
        CpuPowerText = Watts(snapshot.ValueOf(map.CpuPower));
        GpuLoadText = Percent(snapshot.ValueOf(gpu?.Load));
        (GpuTempText, GpuTempState) = Temperature(snapshot, gpu?.Temperature, unit);
        GpuClockText = snapshot.ValueOf(gpu?.CoreClock) is { } mhz ? $"{Math.Round(mhz).ToString("N0", Culture)} MHz" : "—";
        GpuPowerText = Watts(snapshot.ValueOf(gpu?.Power));
        VramText = snapshot.ValueOf(gpu?.VramUsed) is { } mb ? $"{(mb / 1024).ToString("0.0", Culture)} GB" : "—";
        float? ramLoad = snapshot.ValueOf(map.RamLoad), ramGb = snapshot.ValueOf(map.RamUsed);
        RamText = ramLoad is null && ramGb is null ? "—"
            : string.Join("  ", new[] { ramLoad is { } l ? $"{Math.Round(l):0} %" : null, ramGb is { } g ? $"{g.ToString("0.0", Culture)} GB" : null }.Where(x => x is not null));
    }

    public void SetPing(PingResult? ping)
    {
        PingLabel = ping?.Target == PingTarget.Internet ? "REDE" : "PING";
        PingText = ping?.Milliseconds is { } ms ? $"{ms} ms" : "—";
    }

    private static string Percent(float? v) => v is { } x ? $"{Math.Round(x):0} %" : "—";

    private static string Watts(float? v) => v is { } x ? $"{Math.Round(x):0} W" : "—";

    private static (string, SensorState) Temperature(SensorSnapshot snapshot, string? id, TemperatureUnit unit)
    {
        var reading = id is null ? null : snapshot.Find(id);
        if (reading?.Value is not { } c)
            return ("—", SensorState.Normal);
        string text = unit == TemperatureUnit.Fahrenheit
            ? $"{Math.Round(SensorUnits.CelsiusToFahrenheit(c)):0} °F"
            : $"{Math.Round(c):0} °C";
        return (text, reading.State);
    }
}

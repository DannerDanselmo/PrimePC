using PrimePC.Sensors;
using PrimePC.Sensors.Sources;

namespace PrimePC.Tests;

public class SensorHubPauseTests
{
    private sealed class CountingSource : ISensorSource
    {
        public int Updates;
        public string Name => "Teste";
        public IReadOnlyList<string> Limitations => Array.Empty<string>();
        public int Generation => 1;
        public void Open() { }
        public void Update() => Interlocked.Increment(ref Updates);
        public IEnumerable<RawReading> Read() => Array.Empty<RawReading>();
        public void Dispose() { }
    }

    [Fact]
    public void PausedHubDoesNotTouchTheHardwareUntilResumed()
    {
        var source = new CountingSource();
        using var hub = new SensorHub(new[] { source }, new SensorStateEvaluator(), new AlertEngine()) { Interval = TimeSpan.FromMilliseconds(250) };
        hub.Start();
        SpinWait.SpinUntil(() => source.Updates >= 2, TimeSpan.FromSeconds(5));
        Assert.True(source.Updates >= 2);

        hub.Pause(); // o driver de vídeo está sendo trocado
        int atPause = source.Updates;
        Thread.Sleep(900);
        Assert.Equal(atPause, source.Updates);
        Assert.True(hub.IsPaused);

        hub.Resume();
        SpinWait.SpinUntil(() => source.Updates > atPause, TimeSpan.FromSeconds(5));
        Assert.True(source.Updates > atPause);
    }
}

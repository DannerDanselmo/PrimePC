using PrimePC.Diagnostics;
using PrimePC.Gaming;

namespace PrimePC.Tests;

public class FrameAnalysisTests
{
    private static double[] FrameTimes(int count, double ms, int stallEvery = 0, double stallMs = 0) =>
        Enumerable.Range(1, count).Select(i => stallEvery > 0 && i % stallEvery == 0 ? stallMs : ms).ToArray();

    [Fact]
    public void SummaryUsesEveryFrame()
    {
        // 6000 quadros a 10 ms com uma travada de 50 ms a cada 200: média ~98 FPS, 1% low ~ (metade travadas) e 0,1% low = só travadas.
        var s = FrameStatistics.Summarize(FrameTimes(6000, 10, stallEvery: 200, stallMs: 50))!;
        Assert.Equal(6000, s.Frames);
        Assert.InRange(s.AverageFps, 97, 99);
        Assert.InRange(s.Low1Fps!.Value, 27, 34);
        Assert.InRange(s.Low01Fps!.Value, 19.9, 20.1);
        Assert.Equal(30, s.Stutters);
        Assert.Equal(50, s.MaxFrameTimeMs);
        Assert.Equal(10, s.MedianFrameTimeMs);

        var short_ = FrameStatistics.Summarize(FrameTimes(50, 16.7))!;
        Assert.Null(short_.Low1Fps);
        Assert.Null(short_.Low01Fps);
        Assert.Null(FrameStatistics.Summarize(new[] { 10.0 }));
    }

    [Fact]
    public void DownsampleKeepsTheSpikes()
    {
        var values = FrameTimes(6000, 10, stallEvery: 1000, stallMs: 80);
        var small = FrameStatistics.Downsample(values, 600);
        Assert.Equal(600, small.Length);
        Assert.Equal(6, small.Count(v => v == 80));
        Assert.Equal(values, FrameStatistics.Downsample(values.Take(100).ToArray(), 600).Concat(values.Skip(100)).ToArray());
    }

    [Fact]
    public void CaptureKeepsOnlyTheGamesMainSwapChain()
    {
        var tracker = new FrameTracker();
        var capture = tracker.BeginCapture();
        for (int i = 0; i < 1200; i++)
        {
            tracker.OnPresent(100, PresentSource.Dxgi, 0xA, i * 10.0);        // jogo, 100 FPS
            tracker.OnPresent(100, PresentSource.Kernel, 0, i * 10.0);        // o mesmo jogo visto pelo kernel
            if (i % 10 == 0)
                tracker.OnPresent(100, PresentSource.Dxgi, 0xB, i * 10.0);    // janela secundária do jogo
            tracker.OnPresent(200, PresentSource.Dxgi, 0xC, i * 6.06);        // outro programa a 165 FPS
        }

        tracker.EndCapture(capture);
        tracker.OnPresent(100, PresentSource.Dxgi, 0xA, 999_999); // depois do fim: não entra

        var game = capture.Result(100)!;
        Assert.Equal(100, game.ProcessId);
        Assert.Equal(PresentSource.Dxgi, game.Source);
        Assert.Equal(1199, game.FrameTimes.Length);
        Assert.InRange(game.Summary.AverageFps, 99.9, 100.1);

        // Sem jogo indicado, escolhe o processo com mais quadros, exceto o excluído.
        Assert.Equal(100, capture.Result(null, excludePid: 200)!.ProcessId);
        Assert.Null(capture.Result(300));
    }

    private static BottleneckSession Session(FrameRecording? frames, float gpu = 99)
    {
        var start = new DateTime(2026, 10, 21, 20, 0, 0);
        var s = new BottleneckSession { StartedAt = start, Frames = frames, Workload = "Teste" };
        for (int i = 0; i < 90; i++)
        {
            s.Samples.Add(new TelemetrySample
            {
                Time = start.AddSeconds(i), GpuLoad = gpu, CpuTotalLoad = 40, CpuBusiestThread = 60,
                Fps = frames is null ? null : (float)frames.AverageFps, FrameTimeMs = frames is null ? null : 7,
            });
        }

        return s;
    }

    private static FrameRecording Recording(double avg, double? low1, int stutters, double seconds = 90) =>
        new("Discovery", "DirectX 10/11/12", (int)(avg * seconds), seconds, avg, low1, low1 * 0.8, 1000 / avg, 60, stutters, new[] { 7.0, 7.1, 30 });

    [Fact]
    public void SmoothnessComparesLowsWithTheAverage()
    {
        Assert.Equal(Smoothness.Smooth, BottleneckAnalyzer.AssessSmoothness(Recording(144, 120, 2)).Item1);
        Assert.Equal(Smoothness.SomeStutter, BottleneckAnalyzer.AssessSmoothness(Recording(144, 70, 6)).Item1);
        Assert.Equal(Smoothness.Stuttery, BottleneckAnalyzer.AssessSmoothness(Recording(144, 40, 10)).Item1);
        Assert.Equal(Smoothness.Stuttery, BottleneckAnalyzer.AssessSmoothness(Recording(144, 120, 40)).Item1); // 26,7 travadas/min
        Assert.Equal(Smoothness.Smooth, BottleneckAnalyzer.AssessSmoothness(Recording(60, 58, 0)).Item1);       // 60 estáveis = fluido
        Assert.Equal(Smoothness.Unknown, BottleneckAnalyzer.AssessSmoothness(Recording(60, null, 0)).Item1);
        Assert.Equal(Smoothness.Unknown, BottleneckAnalyzer.AssessSmoothness(null).Item1);
    }

    [Fact]
    public void ReportShowsMeasuredFpsAndExplainsTheSource()
    {
        var report = BottleneckAnalyzer.Analyze(Session(Recording(144, 40, 10)));
        Assert.NotNull(report.Frames);
        Assert.Equal(Smoothness.Stuttery, report.Smoothness);
        Assert.Contains(report.Metrics, m => m.Name == "FPS (medido a cada quadro)" && m.Samples == 90);
        Assert.Contains(report.Metrics, m => m.Name == "Tempo de quadro" && m.Unit == "ms");
        Assert.Contains(report.Limitations, l => l.Contains("medidos a cada quadro") && l.Contains("Discovery"));
        Assert.Contains(report.Conditions, c => c.StartsWith("Jogo medido: Discovery"));
        Assert.Contains(report.Suggestions, s => s.StartsWith("Travadas:"));

        var noFrames = BottleneckAnalyzer.Analyze(Session(null));
        Assert.Null(noFrames.Frames);
        Assert.Contains(noFrames.Limitations, l => l.Contains("administrador"));
        Assert.DoesNotContain(noFrames.Limitations, l => l.StartsWith("Não disponível") && l.Contains("Tempo de quadro"));
    }

    [Fact]
    public void SteadyFpsWithIdleHardwarePointsToAFrameLimit()
    {
        var report = BottleneckAnalyzer.Analyze(Session(Recording(60, 58, 0), gpu: 55));
        Assert.Contains("estável em torno de 60", report.Explanation);
    }

    [Fact]
    public void SessionWithFramesSurvivesSaveAndLoad()
    {
        string dir = Directory.CreateTempSubdirectory("primepc-sessoes-").FullName;
        try
        {
            var store = new BottleneckSessionStore(dir);
            var session = Session(Recording(144, 120, 2));
            session.Report = BottleneckAnalyzer.Analyze(session);
            store.Save(session);
            var loaded = Assert.Single(store.LoadAll());
            Assert.Equal(144, loaded.Frames!.AverageFps);
            Assert.Equal(Smoothness.Smooth, loaded.Report!.Smoothness);
            Assert.Equal(3, loaded.Report.Frames!.FrameTimeGraph.Length);

            var table = SessionComparer.Compare(new[] { loaded, Session(null) });
            Assert.Contains(table.Rows, r => r.Metric == "1% low" && r.Values[0].StartsWith("120") && r.Values[1] == "Não medido");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

using PrimePC.Diagnostics;

namespace PrimePC.Tests;

public class BottleneckAnalyzerTests
{
    private static BottleneckSession Session(int seconds, Func<int, TelemetrySample> make)
    {
        var start = new DateTime(2026, 1, 1, 20, 0, 0);
        var s = new BottleneckSession { Workload = "Jogo de teste", StartedAt = start, IntervalSeconds = 1 };
        for (int i = 0; i < seconds; i++)
        {
            var sample = make(i);
            sample.Time = start.AddSeconds(i);
            s.Samples.Add(sample);
        }

        return s;
    }

    private static FactorFinding Find(BottleneckReport r, LimitingFactor f) => r.Findings.Single(x => x.Factor == f);

    [Fact]
    public void ShortRecordingIsInconclusive()
    {
        var r = BottleneckAnalyzer.Analyze(Session(10, _ => new TelemetrySample { GpuLoad = 99, CpuTotalLoad = 40 }));
        Assert.Equal(Likelihood.Inconclusive, r.HeadlineLikelihood);
        Assert.Empty(r.Findings);
    }

    [Fact]
    public void SaturatedGpuIsProbableGpuLimit()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, i => new TelemetrySample
        {
            GpuLoad = 97 + i % 3, CpuTotalLoad = 35, CpuBusiestThread = 60, RamLoad = 50, VramUsedMB = 8000, VramTotalMB = 16000,
            GpuPowerLimitPercent = 99,
        }));
        Assert.Equal(Likelihood.Probable, Find(r, LimitingFactor.Gpu).Likelihood);
        Assert.Equal(Likelihood.NotIndicated, Find(r, LimitingFactor.Cpu).Likelihood);
        Assert.Contains("GPU", r.Headline);
        Assert.Contains(Find(r, LimitingFactor.Gpu).Evidence, e => e.Contains("limite de potência"));
    }

    [Fact]
    public void SingleSaturatedThreadWithIdleGpuIsCpuLimitEvenWithLowTotalUsage()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, _ => new TelemetrySample
        {
            GpuLoad = 55, CpuTotalLoad = 22, CpuBusiestThread = 98, RamLoad = 50,
        }));
        var cpu = Find(r, LimitingFactor.Cpu);
        Assert.Equal(Likelihood.Probable, cpu.Likelihood);
        Assert.Equal(LimitingFactor.Cpu, r.Findings[0].Factor);
        Assert.Contains(cpu.Evidence, e => e.Contains("uso total isolado"));
    }

    [Fact]
    public void WithoutPerThreadDataCpuConclusionIsCappedAtPossible()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, _ => new TelemetrySample { GpuLoad = 50, CpuTotalLoad = 95 }));
        Assert.Equal(Likelihood.Possible, Find(r, LimitingFactor.Cpu).Likelihood);
    }

    [Fact]
    public void LowLoadIsInconclusive()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, _ => new TelemetrySample { GpuLoad = 20, CpuTotalLoad = 10, CpuBusiestThread = 30 }));
        Assert.Equal(Likelihood.Inconclusive, r.HeadlineLikelihood);
        Assert.Contains("carga baixa", r.Headline);
    }

    [Fact]
    public void HotCpuWithClockDropIsProbableThermalLimit()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, i => new TelemetrySample
        {
            GpuLoad = 70, CpuTotalLoad = 60, CpuBusiestThread = 70,
            CpuTemperature = i < 60 ? 70 : 95, CpuClockMHz = i < 60 ? 5000 : 4300,
        }), new BottleneckAnalyzerOptions { CpuTemperatureLimit = 87 });
        Assert.Equal(Likelihood.Probable, Find(r, LimitingFactor.CpuThermal).Likelihood);
    }

    [Fact]
    public void MissingSensorsAreReportedNotGuessed()
    {
        var r = BottleneckAnalyzer.Analyze(Session(60, _ => new TelemetrySample { GpuLoad = 99, CpuTotalLoad = 30, CpuBusiestThread = 50 }));
        Assert.Equal(Likelihood.Inconclusive, Find(r, LimitingFactor.CpuThermal).Likelihood);
        Assert.Equal(Likelihood.Inconclusive, Find(r, LimitingFactor.Storage).Likelihood);
        Assert.Contains(r.Limitations, l => l.StartsWith("FPS não foi medido"));
        Assert.Contains(r.Limitations, l => l.Contains("Temperatura da CPU"));
    }

    [Fact]
    public void HeadlineNeverPresentsAUniversalBottleneckPercentage()
    {
        var r = BottleneckAnalyzer.Analyze(Session(120, _ => new TelemetrySample { GpuLoad = 99, CpuTotalLoad = 40, CpuBusiestThread = 60 }));
        Assert.DoesNotContain("%", r.Headline);
        Assert.Contains(r.Limitations, l => l.Contains("Não existe um percentual universal de gargalo"));
    }

    [Fact]
    public void SessionsRoundTripAndCompare()
    {
        var dir = Path.Combine(Path.GetTempPath(), "primepc-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new BottleneckSessionStore(dir);
            var a = Session(60, _ => new TelemetrySample { GpuLoad = 99, CpuTotalLoad = 40, CpuBusiestThread = 60 });
            a.Report = BottleneckAnalyzer.Analyze(a);
            var b = Session(60, _ => new TelemetrySample { GpuLoad = 60, CpuTotalLoad = 30, CpuBusiestThread = 97 });
            b.StartedAt = b.StartedAt.AddHours(1);
            b.Report = BottleneckAnalyzer.Analyze(b);
            store.Save(a);
            store.Save(b);

            var loaded = store.LoadAll();
            Assert.Equal(2, loaded.Count);
            Assert.Equal(b.Id, loaded[0].Id);
            Assert.Equal(60, loaded[1].Samples.Count);
            Assert.Equal(a.Report.Headline, loaded[1].Report!.Headline);

            var table = SessionComparer.Compare(loaded);
            Assert.Equal(2, table.Headers.Count);
            Assert.All(table.Rows, row => Assert.Equal(2, row.Values.Count));

            store.Delete(a.Id);
            Assert.Single(store.LoadAll());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

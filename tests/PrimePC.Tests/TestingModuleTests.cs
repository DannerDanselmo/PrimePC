using System.Runtime.InteropServices;
using PrimePC.Testing;

namespace PrimePC.Tests;

public class MemoryPatternTests
{
    [Fact]
    public void EveryPatternVerifiesCleanlyAndDetectsAFlippedBit()
    {
        const int bytes = 1 << 20;
        nint ptr = Marshal.AllocHGlobal(bytes);
        try
        {
            var block = new MemoryBlock(ptr, bytes, Offset: 4096, Index: 0);
            foreach (var pattern in MemoryTester.PatternsFor(MemoryTestMode.Full))
            {
                var errors = new List<(long Offset, ulong Expected, ulong Actual)>();
                pattern.Write(block);
                pattern.Verify(block, (o, _, e, a) => errors.Add((o, e, a)));
                Assert.Empty(errors);

                // Injeta uma falha: inverte um bit de uma palavra no meio do bloco.
                const int word = 1000;
                long original = Marshal.ReadInt64(ptr, word * 8);
                Marshal.WriteInt64(ptr, word * 8, original ^ (1L << 17));
                pattern.Verify(block, (o, _, e, a) => errors.Add((o, e, a)));

                var error = Assert.Single(errors);
                Assert.Equal(4096 + word * 8L, error.Offset);
                Assert.Equal((ulong)original, error.Expected);
                Assert.Equal(1UL << 17, error.Expected ^ error.Actual);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    [Fact]
    public void FullModeHasMorePatternsThanQuick()
    {
        Assert.Equal(6, MemoryTester.PatternsFor(MemoryTestMode.Quick).Count);
        Assert.Equal(25, MemoryTester.PatternsFor(MemoryTestMode.Full).Count);
    }
}

public class MemoryTesterTests
{
    [Fact]
    public async Task QuickTestOnRealMemoryFindsNoErrors()
    {
        var result = await new MemoryTester().RunAsync(new MemoryTestOptions { BytesToTest = 256L * 1024 * 1024, Threads = 4 },
            null, CancellationToken.None);
        Assert.Equal(MemoryTestOutcome.NoErrors, result.Outcome);
        Assert.Equal("Nenhum erro detectado durante este teste.", result.Summary);
        Assert.Equal(1, result.PassesCompleted);
        Assert.Equal(6, result.PatternsCompleted);
        Assert.Contains(result.Notes, n => n.Contains("não garante"));
    }

    [Fact]
    public async Task RefusesMoreThanTheSafeLimit()
    {
        var (total, _, _) = MemoryBudget.Current();
        var result = await new MemoryTester().RunAsync(new MemoryTestOptions { BytesToTest = total }, null, CancellationToken.None);
        Assert.Equal(MemoryTestOutcome.Failed, result.Outcome);
        Assert.Contains("limite seguro", result.Summary);
    }

    [Fact]
    public async Task CancellationIsSafeAndReported()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var result = await new MemoryTester().RunAsync(new MemoryTestOptions
        {
            BytesToTest = 512L * 1024 * 1024, Mode = MemoryTestMode.Timed, Duration = TimeSpan.FromMinutes(5), Threads = 2,
        }, null, cts.Token);
        Assert.Equal(MemoryTestOutcome.Cancelled, result.Outcome);
    }
}

public class CpuStressTesterTests
{
    [Fact]
    public async Task ShortRunProducesNoComputationErrors()
    {
        var result = await new CpuStressTester(() => 60f).RunAsync(new CpuStressOptions
        {
            Threads = 4, Duration = TimeSpan.FromSeconds(3), TemperatureLimit = 95,
        }, null, CancellationToken.None);
        Assert.Equal(StressOutcome.Completed, result.Outcome);
        Assert.Equal(0, result.Errors);
        Assert.True(result.Iterations > 0);
    }

    [Fact]
    public async Task StopsWhenTemperatureLimitIsReached()
    {
        var result = await new CpuStressTester(() => 96f).RunAsync(new CpuStressOptions
        {
            Threads = 2, Duration = TimeSpan.FromSeconds(30), TemperatureLimit = 90,
        }, null, CancellationToken.None);
        Assert.Equal(StressOutcome.StoppedByTemperature, result.Outcome);
        Assert.True(result.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RefusesToRunWithoutTemperatureUnlessConfirmed()
    {
        var result = await new CpuStressTester(() => null).RunAsync(new CpuStressOptions { Threads = 1, Duration = TimeSpan.FromSeconds(2) },
            null, CancellationToken.None);
        Assert.Equal(StressOutcome.Failed, result.Outcome);

        var confirmed = await new CpuStressTester(() => null).RunAsync(new CpuStressOptions
        {
            Threads = 1, Duration = TimeSpan.FromSeconds(2), AllowWithoutTemperature = true,
        }, null, CancellationToken.None);
        Assert.Equal(StressOutcome.Completed, confirmed.Outcome);
        Assert.Contains(confirmed.Notes, n => n.Contains("sem corte térmico"));
    }
}

public class StorageBenchmarkTests
{
    [Fact]
    public async Task NonDestructiveBenchmarkCleansUpItsTemporaryFile()
    {
        string volume = Path.GetPathRoot(Path.GetTempPath())!;
        string dir = StorageBenchmark.ResolveTargetDirectory(volume);
        var result = await StorageBenchmark.RunAsync(new StorageBenchmarkOptions
        {
            VolumeRoot = volume, FileSizeBytes = 256L * 1024 * 1024, MaxPhaseDuration = TimeSpan.FromSeconds(2),
        }, "teste", null, CancellationToken.None);

        Assert.False(result.Cancelled);
        Assert.True(result.SequentialWriteMBs > 0);
        Assert.True(result.SequentialReadMBs > 0);
        Assert.True(result.Random4kReadIops > 0);
        Assert.Empty(Directory.Exists(dir) ? Directory.GetFiles(dir, "primepc-teste-*.tmp") : Array.Empty<string>());
    }
}

public class TestRunJournalTests
{
    [Fact]
    public void DetectsAnUnfinishedRunOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "primepc-journal-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new TestRunJournal(dir);
            journal.Begin("Teste de RAM");
            var run = new TestRunJournal(dir).TakeUnfinished();
            Assert.Equal("Teste de RAM", run!.TestName);
            Assert.Null(new TestRunJournal(dir).TakeUnfinished());

            journal.Begin("CPU");
            journal.End();
            Assert.Null(journal.TakeUnfinished());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

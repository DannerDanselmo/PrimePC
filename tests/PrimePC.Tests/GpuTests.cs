using ComputeSharp;
using PrimePC.GpuTesting;

namespace PrimePC.Tests;

/// <summary>Testes que rodam na placa de vídeo real (ignorados se não houver GPU com DirectX 12).</summary>
public class GpuTests
{
    private static GraphicsDevice? Device()
    {
        var devices = GpuDevices.List();
        return devices.Count == 0 ? null : GpuDevices.Get(GpuDevices.DefaultIndex(devices));
    }

    [Fact]
    public void VramVerificationDetectsAnInjectedFault()
    {
        var device = Device();
        if (device is null)
            return;

        const int length = 1 << 20;
        using var buffer = device.AllocateReadWriteBuffer<uint>(length);
        using var count = device.AllocateReadWriteBuffer<int>(new int[1]);
        using var log = device.AllocateReadWriteBuffer<uint>(new uint[64 * 3]);
        const uint baseIndex = 1000;

        foreach (var (_, mode, value) in VramTester.Patterns)
        {
            count.CopyFrom(new int[1]);
            device.For(length, new VramWriteShader(buffer, 0, mode, value, baseIndex));
            device.For(length, new VramVerifyShader(buffer, count, log, 0, mode, value, baseIndex));
            var c = new int[1];
            count.CopyTo(c);
            Assert.Equal(0, c[0]);

            // Corrompe uma palavra (simula um bit com defeito na VRAM) e verifica de novo.
            var word = new uint[1];
            buffer.CopyTo(word.AsSpan(), 12345);
            uint good = word[0];
            word[0] ^= 1u << 9;
            buffer.CopyFrom(word, 12345);
            device.For(length, new VramVerifyShader(buffer, count, log, 0, mode, value, baseIndex));
            count.CopyTo(c);
            Assert.Equal(1, c[0]);
            var entries = new uint[3];
            log.CopyTo(entries.AsSpan(), 0);
            Assert.Equal(baseIndex + 12345u, entries[0]);
            Assert.Equal(good, entries[1]);
            Assert.Equal(good ^ (1u << 9), entries[2]);
        }
    }

    [Fact]
    public void StressComparisonCountsDivergentResults()
    {
        var device = Device();
        if (device is null)
            return;

        const int length = 64 * 1024;
        using var output = device.AllocateReadWriteBuffer<uint>(length);
        using var reference = device.AllocateReadWriteBuffer<uint>(length);
        using var errors = device.AllocateReadWriteBuffer<uint>(new uint[1]);

        device.For(length, new StressShader(reference, 500, 7u));
        device.For(length, new StressShader(output, 500, 7u));
        device.For(length, new CompareShader(output, reference, errors));
        var e = new uint[1];
        errors.CopyTo(e);
        Assert.Equal(0u, e[0]); // mesma carga, mesmo resultado: a GPU é determinística

        output.CopyFrom(new uint[] { 1, 2, 3 }, 100);
        device.For(length, new CompareShader(output, reference, errors));
        errors.CopyTo(e);
        Assert.Equal(3u, e[0]);
    }

    [Fact]
    public async Task ShortStressAndVramRunsOnRealGpu()
    {
        var devices = GpuDevices.List();
        if (devices.Count == 0)
            return;
        int index = GpuDevices.DefaultIndex(devices);

        var stress = await new GpuStressTester(() => 50f).RunAsync(new GpuStressOptions
        {
            DeviceIndex = index, Duration = TimeSpan.FromSeconds(3), TemperatureLimit = 95,
        }, null, CancellationToken.None);
        Assert.Equal(GpuTestOutcome.Completed, stress.Outcome);
        Assert.Equal(0, stress.Errors);
        Assert.True(stress.Dispatches > 0);

        var hot = await new GpuStressTester(() => 99f).RunAsync(new GpuStressOptions
        {
            DeviceIndex = index, Duration = TimeSpan.FromSeconds(30), TemperatureLimit = 90,
        }, null, CancellationToken.None);
        Assert.Equal(GpuTestOutcome.StoppedByTemperature, hot.Outcome);

        var vram = await new VramTester().RunAsync(new VramTestOptions { DeviceIndex = index, BytesToTest = 512L * 1024 * 1024 },
            null, CancellationToken.None);
        Assert.Equal(GpuTestOutcome.Completed, vram.Outcome);
        Assert.Equal(512L * 1024 * 1024, vram.BytesTested);
        Assert.Equal(VramTester.Patterns.Count, vram.PatternsCompleted);
    }
}

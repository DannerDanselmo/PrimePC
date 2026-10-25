using PrimePC.Sensors;

namespace PrimePC.Tests;

public class SensorStateEvaluatorTests
{
    private static SensorDescriptor D(HardwareCategory c, SensorKind k, string name = "Sensor", string hw = "HW") =>
        new($"/t/{c}/{k}/{name}", name, "/t", hw, c, k, SensorUnits.UnitFor(k), "teste");

    [Fact]
    public void X3dCpuUsesLowerThermalLimit()
    {
        var e = new SensorStateEvaluator { CpuModel = "AMD Ryzen 7 7800X3D 8-Core Processor" };
        Assert.Equal((80f, 89f), e.CpuTempLimits);
        Assert.Equal(SensorState.Normal, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Temperature), 70).State);
        Assert.Equal(SensorState.Warning, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Temperature), 85).State);
        Assert.Equal(SensorState.Critical, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Temperature), 89).State);
    }

    [Theory]
    [InlineData(127f)]
    [InlineData(0f)]
    [InlineData(-40f)]
    public void ImplausibleTemperaturesAreUnavailable(float value)
    {
        var (state, reason) = new SensorStateEvaluator().Evaluate(D(HardwareCategory.Motherboard, SensorKind.Temperature), value);
        Assert.Equal(SensorState.Unavailable, state);
        Assert.NotNull(reason);
    }

    [Fact]
    public void MissingValueIsUnavailableNeverZero()
    {
        var (state, _) = new SensorStateEvaluator().Evaluate(D(HardwareCategory.Gpu, SensorKind.Temperature), null);
        Assert.Equal(SensorState.Unavailable, state);
    }

    [Fact]
    public void WithoutLowLevelAccessCpuRegistersAreUnavailableButLoadIsNot()
    {
        var e = new SensorStateEvaluator { LowLevelAccess = false };
        Assert.Equal(SensorState.Unavailable, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Power, "Package"), 0).State);
        Assert.Equal(SensorState.Unavailable, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Voltage, "Core #1 VID"), 1.55f).State);
        Assert.Equal(SensorState.Normal, e.Evaluate(D(HardwareCategory.Cpu, SensorKind.Load, "CPU Total"), 30).State);
        Assert.Equal(SensorState.Normal, e.Evaluate(D(HardwareCategory.Gpu, SensorKind.Power, "GPU Package"), 45).State);
    }

    [Fact]
    public void ZeroClockIsUnavailable() =>
        Assert.Equal(SensorState.Unavailable, new SensorStateEvaluator().Evaluate(D(HardwareCategory.Gpu, SensorKind.Clock, "GPU Core"), 0).State);

    [Theory]
    [InlineData("+12V", 12.1f, SensorState.Normal)]
    [InlineData("+12V", 12.9f, SensorState.Warning)]
    [InlineData("+12V", 13.5f, SensorState.Critical)]
    [InlineData("+12V", 2.4f, SensorState.Unavailable)]
    [InlineData("+3.3V", 3.31f, SensorState.Normal)]
    [InlineData("Vcore", 1.3f, SensorState.Normal)]
    public void PowerRailsUseAtxTolerance(string name, float value, SensorState expected) =>
        Assert.Equal(expected, new SensorStateEvaluator().Evaluate(D(HardwareCategory.Motherboard, SensorKind.Voltage, name), value).State);

    [Fact]
    public void SsdHealthLevels()
    {
        var e = new SensorStateEvaluator();
        Assert.Equal(SensorState.Critical, e.Evaluate(D(HardwareCategory.Storage, SensorKind.Level, "Available Spare"), 5).State);
        Assert.Equal(SensorState.Normal, e.Evaluate(D(HardwareCategory.Storage, SensorKind.Level, "Available Spare Threshold"), 5).State);
        Assert.Equal(SensorState.Warning, e.Evaluate(D(HardwareCategory.Storage, SensorKind.Level, "Percentage Used"), 85).State);
    }

    [Theory]
    [InlineData(HardwareCategory.Storage, "Warning Temperature", 83f)]
    [InlineData(HardwareCategory.Storage, "Critical Temperature", 88f)]
    [InlineData(HardwareCategory.Memory, "Thermal Sensor Critical High Limit", 85f)]
    [InlineData(HardwareCategory.Memory, "Temperature Sensor Resolution", 0.25f)]
    public void DeviceReportedLimitsAreReferenceValuesNotReadings(HardwareCategory category, string name, float value)
    {
        var (state, reason) = new SensorStateEvaluator().Evaluate(D(category, SensorKind.Temperature, name), value);
        Assert.Equal(SensorState.Normal, state);
        Assert.Contains("referência", reason);
    }

    [Fact]
    public void StorageTemperatureUsesTheDrivesOwnThresholds()
    {
        var e = new SensorStateEvaluator();
        var sensor = D(HardwareCategory.Storage, SensorKind.Temperature, "Temperature #2");
        Assert.Equal(SensorState.Warning, e.Evaluate(sensor, 72).State); // limite genérico (70 °C)

        e.LearnDeviceLimits(new[]
        {
            new RawReading(D(HardwareCategory.Storage, SensorKind.Temperature, "Warning Temperature"), 83),
            new RawReading(D(HardwareCategory.Storage, SensorKind.Temperature, "Critical Temperature"), 88),
        });
        Assert.Equal(SensorState.Normal, e.Evaluate(sensor, 72).State);
        Assert.Equal(SensorState.Warning, e.Evaluate(sensor, 84).State);
        Assert.Equal(SensorState.Critical, e.Evaluate(sensor, 88).State);
    }

    [Fact]
    public void FormatShowsNaoDisponivelForMissingValues() =>
        Assert.Equal("Não disponível", SensorUnits.Format(SensorKind.Temperature, null));
}

public class RingBufferTests
{
    [Fact]
    public void KeepsMostRecentItemsInOrder()
    {
        var rb = new RingBuffer<int>(3);
        for (int i = 1; i <= 5; i++)
            rb.Add(i);
        Assert.Equal(new[] { 3, 4, 5 }, rb.ToArray());
        rb.Resize(2);
        Assert.Equal(new[] { 4, 5 }, rb.ToArray());
        rb.Resize(4);
        rb.Add(6);
        Assert.Equal(new[] { 4, 5, 6 }, rb.ToArray());
    }
}

public class AlertEngineTests
{
    private static readonly SensorDescriptor Temp =
        new("/gpu/0/temperature/0", "GPU Core", "/gpu/0", "GPU", HardwareCategory.Gpu, SensorKind.Temperature, "°C", "teste");

    private static SensorSnapshot Snap(DateTime t, float v) =>
        new(t, new[] { new SensorReading(Temp, v, v, v, v, SensorState.Normal, null) });

    [Fact]
    public void FiresOnceAfterMinimumDurationAndRearms()
    {
        var engine = new AlertEngine { AlertOnCriticalState = false };
        engine.SetRules(new[] { new AlertRule { SensorId = Temp.Id, SensorName = "GPU Core", Threshold = 80, MinDurationSeconds = 3, Kind = SensorKind.Temperature } });
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0);

        Assert.Empty(engine.Evaluate(Snap(t0, 85)));
        Assert.Empty(engine.Evaluate(Snap(t0.AddSeconds(2), 85)));
        Assert.Single(engine.Evaluate(Snap(t0.AddSeconds(3), 86)));
        Assert.Empty(engine.Evaluate(Snap(t0.AddSeconds(10), 90)));   // não repete enquanto continua acima
        Assert.Empty(engine.Evaluate(Snap(t0.AddSeconds(11), 70)));   // rearma
        Assert.Empty(engine.Evaluate(Snap(t0.AddSeconds(12), 85)));
        Assert.Single(engine.Evaluate(Snap(t0.AddSeconds(15), 85)));
        Assert.Equal(2, engine.Log.Count);
    }
}

public class KeySensorMapTests
{
    private static SensorReading R(string id, string name, string hwId, string hw, HardwareCategory c, SensorKind k, float? v) =>
        new(new SensorDescriptor(id, name, hwId, hw, c, k, SensorUnits.UnitFor(k), "teste"), v, v, v, v,
            v is null ? SensorState.Unavailable : SensorState.Normal, null);

    [Fact]
    public void PicksDiscreteGpuThreadsAndPreferredCpuTemperature()
    {
        var readings = new List<SensorReading>
        {
            R("/cpu/load/total", "CPU Total", "/cpu", "Ryzen", HardwareCategory.Cpu, SensorKind.Load, 30),
            R("/cpu/load/max", "CPU Core Max", "/cpu", "Ryzen", HardwareCategory.Cpu, SensorKind.Load, 90),
            R("/cpu/temp/ccd", "CCD1 (Tdie)", "/cpu", "Ryzen", HardwareCategory.Cpu, SensorKind.Temperature, 60),
            R("/cpu/temp/tctl", "Core (Tctl/Tdie)", "/cpu", "Ryzen", HardwareCategory.Cpu, SensorKind.Temperature, 65),
            R("/igpu/load", "GPU Core", "/igpu", "AMD Radeon(TM) Graphics", HardwareCategory.Gpu, SensorKind.Load, 1),
            R("/igpu/vram", "GPU Memory Total", "/igpu", "AMD Radeon(TM) Graphics", HardwareCategory.Gpu, SensorKind.SmallData, 512),
            R("/dgpu/load", "GPU Core", "/dgpu", "NVIDIA GeForce RTX 5080", HardwareCategory.Gpu, SensorKind.Load, 97),
            R("/dgpu/vram", "GPU Memory Total", "/dgpu", "NVIDIA GeForce RTX 5080", HardwareCategory.Gpu, SensorKind.SmallData, 16303),
            R("/dgpu/pl", "GPU Power", "/dgpu", "NVIDIA GeForce RTX 5080", HardwareCategory.Gpu, SensorKind.Load, 99),
        };
        for (int i = 1; i <= 16; i++)
            readings.Add(R($"/cpu/load/{i}", $"CPU Core #{i}", "/cpu", "Ryzen", HardwareCategory.Cpu, SensorKind.Load, i == 3 ? 95 : 20));

        var snap = new SensorSnapshot(DateTime.Now, readings);
        var map = KeySensorMap.Build(readings);

        Assert.Equal("/cpu/temp/tctl", map.CpuTemperature);
        Assert.Equal(16, map.CpuThreadLoads.Count);
        Assert.Equal(95, map.BusiestThreadLoad(snap));
        var gpu = map.PrimaryGpu(snap);
        Assert.Equal("NVIDIA GeForce RTX 5080", gpu!.Name);
        Assert.Equal("/dgpu/pl", gpu.PowerLimitPercent);
        Assert.True(map.Gpus.Single(g => g.HardwareId == "/igpu").LikelyIntegrated);
    }
}

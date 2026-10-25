using PrimePC.Drivers;
using PrimePC.Reports;

namespace PrimePC.Tests;

public class DriverTests
{
    private static InstalledDriver Driver(string id = @"PCI\VEN_10DE&DEV_2C02\4&1", string cls = "DISPLAY", string? provider = "NVIDIA",
        string? version = "32.0.16.1742", int problem = 0, bool? signed = true, string? inf = "oem8.inf", string name = "NVIDIA GeForce RTX 5080",
        params string[] hardwareIds) =>
        new(name, id, cls, DriverInventory.CategoryFor(cls, id), provider, provider, version, new DateTime(2026, 9, 29), signed,
            "Microsoft Windows Hardware Compatibility Publisher", inf,
            hardwareIds.Length == 0 ? new[] { @"PCI\VEN_10DE&DEV_2C02&SUBSYS_00000000", @"PCI\VEN_10DE&DEV_2C02" } : hardwareIds, problem);

    [Theory]
    [InlineData("NVIDIA", "32.0.16.1742", "617.42")]
    [InlineData("NVIDIA", "31.0.15.5222", "552.22")]
    [InlineData("NVIDIA Corporation", "32.0.15.6094", "560.94")]
    [InlineData("Advanced Micro Devices, Inc.", "32.0.21030.2001", "32.0.21030.2001")]
    [InlineData(null, "10.0.26100.1", "10.0.26100.1")]
    public void FriendlyVersionMatchesVendorNumbering(string? provider, string version, string expected) =>
        Assert.Equal(expected, DriverInventory.FriendlyVersion(provider, version));

    [Theory]
    [InlineData("DISPLAY", DriverCategory.Video)]
    [InlineData("MEDIA", DriverCategory.Audio)]
    [InlineData("NET", DriverCategory.Network)]
    [InlineData("HIDCLASS", DriverCategory.Input)]
    [InlineData("SYSTEM", DriverCategory.System)]
    [InlineData("SOFTWARECOMPONENT", DriverCategory.Software)]
    [InlineData("ALGO-NOVO", DriverCategory.Other)]
    public void ClassesAreGroupedIntoCategories(string cls, DriverCategory expected) =>
        Assert.Equal(expected, DriverInventory.CategoryFor(cls));

    [Fact]
    public void HardwareErrorIsAProblemButSoftwareComponentIsOnlyAttention()
    {
        Assert.Equal(DriverStatus.Problem, DriverEvaluator.Assess(Driver(problem: 43), null).Status);

        var voiceClarity = Driver(id: @"SWD\DRIVERENUM\{96BEDF2C}#VOCAEFFECTPACK&1", cls: "", provider: null, version: null, problem: 28,
            name: @"SWC\VEN_MSFT&AUDIO_EFFECTPACK_VOICECLARITY", inf: null, signed: null);
        var a = DriverEvaluator.Assess(voiceClarity, null);
        Assert.Equal(DriverStatus.Warning, a.Status);
        Assert.Contains("componente de software", a.Detail);
        Assert.Equal("Componente sem nome (AUDIO_EFFECTPACK_VOICECLARITY)", DriverInventory.DisplayName(voiceClarity));
    }

    [Fact]
    public void DisabledUnsignedAndUpdateStates()
    {
        Assert.Equal("Desativado", DriverEvaluator.Assess(Driver(problem: 22), null).Label);
        Assert.Equal(DriverStatus.Warning, DriverEvaluator.Assess(Driver(signed: false), null).Status);
        var update = new DriverUpdate("NVIDIA - Display - 32.0.15.9999", "Display", @"PCI\VEN_10DE&DEV_2C02", "NVIDIA", "RTX", "NVIDIA",
            new DateTime(2026, 10, 15), 900_000_000, null);
        var a = DriverEvaluator.Assess(Driver(), update);
        Assert.Equal(DriverStatus.UpdateAvailable, a.Status);
        Assert.Contains("15/10/2026", a.Detail);
        Assert.Equal(DriverStatus.Ok, DriverEvaluator.Assess(Driver(), null).Status);
    }

    [Fact]
    public void WindowsUpdateResultsAreMatchedByHardwareId()
    {
        var gpu = Driver();
        var audio = Driver(id: @"HDAUDIO\FUNC_01&VEN_10EC", cls: "MEDIA", provider: "Realtek", name: "Realtek Audio",
            hardwareIds: new[] { @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897" });
        var updates = new[]
        {
            new DriverUpdate("Realtek - Audio", "MEDIA", @"hdaudio\func_01&ven_10ec&dev_0897", "Realtek", null, "Realtek", null, 0, null),
            new DriverUpdate("Dispositivo ausente", "USB", @"USB\VID_FFFF&PID_0001", null, null, null, null, 0, null),
        };
        var (byDevice, unmatched) = DriverEvaluator.Match(new[] { gpu, audio }, updates);
        Assert.Single(byDevice);
        Assert.Equal("Realtek - Audio", byDevice[audio.DeviceId].Title);
        Assert.Equal("Dispositivo ausente", Assert.Single(unmatched).Title);
    }

    [Fact]
    public void InventoryOnThisPcListsDriversAndVideo()
    {
        var scan = DriverInventory.Collect();
        Assert.Empty(scan.Notes);
        Assert.True(scan.Drivers.Count > 50);
        Assert.Contains(scan.Drivers, d => d.Category == DriverCategory.Video && d.HasDriver);
        Assert.Contains(scan.Drivers, d => d.IsInbox);
        Assert.Contains(scan.Drivers, d => !d.IsInbox && d.HasDriver);
    }

    [Fact]
    public void ReportSummarizesDrivers()
    {
        var scan = new DriverScan(DateTime.Now, new[] { Driver(), Driver(id: "X", name: "Placa com defeito", problem: 43, cls: "NET", provider: "Intel") }, Array.Empty<string>());
        var report = ReportBuilder.BuildDrivers(scan, null);
        Assert.Equal(2, report.Total);
        Assert.Equal(1, report.Problems);
        Assert.Equal("617.42 (32.0.16.1742)", Assert.Single(report.Video).Version);
        Assert.Equal("Placa com defeito", Assert.Single(report.Attention).Device);
        Assert.Contains("não foi consultado", report.UpdateStatus);
    }
}

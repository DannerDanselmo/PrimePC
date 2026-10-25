using System.Text;
using PrimePC.Diagnostics;
using PrimePC.Reports;
using PrimePC.Sensors;
using PrimePC.Sensors.Inventory;

namespace PrimePC.Tests;

public class ReportTests
{
    private static ReportInputs Inputs(HardwareInventory? inventory)
    {
        var d = new SensorDescriptor("/gpu/0/temperature/0", "GPU Core", "/gpu/0", "NVIDIA GeForce RTX 5080", HardwareCategory.Gpu,
            SensorKind.Temperature, "°C", "LibreHardwareMonitor · NVIDIA NVAPI/NVML");
        var session = new BottleneckSession { Workload = "Jogo; teste \"aspas\"", StartedAt = DateTime.Now.AddMinutes(-3), IntervalSeconds = 1 };
        for (int i = 0; i < 90; i++)
            session.Samples.Add(new TelemetrySample { Time = session.StartedAt.AddSeconds(i), GpuLoad = 98, CpuTotalLoad = 40, CpuBusiestThread = 60 });
        session.Report = BottleneckAnalyzer.Analyze(session);

        return new ReportInputs
        {
            Inventory = inventory,
            SensorStats = new[] { new SensorSessionStats(d, 41.5f, 55.2f, 71.9f, 120, 0, 3, 0) },
            SessionStart = DateTime.Now.AddMinutes(-10),
            UpdateIntervalSeconds = 1,
            BottleneckSession = session,
            Limitations = new[] { "Driver PawnIO não instalado." },
        };
    }

    [Fact]
    public void ExportsPdfCsvAndJsonWithoutComputerNameByDefault()
    {
        var inventory = HardwareInventoryCollector.Collect();
        var report = ReportBuilder.Build(Inputs(inventory), new ReportOptions());
        Assert.Null(report.Inventory!.Os.ComputerName);

        var dir = Path.Combine(Path.GetTempPath(), "primepc-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string pdf = Path.Combine(dir, "r.pdf"), csv = Path.Combine(dir, "r.csv"), json = Path.Combine(dir, "r.json");
            PdfReportExporter.Export(report, pdf);
            CsvExporter.ExportSensorStatistics(report, csv);
            JsonReportExporter.Export(report, json);

            Assert.Equal("%PDF", Encoding.ASCII.GetString(File.ReadAllBytes(pdf), 0, 4));
            Assert.True(new FileInfo(pdf).Length > 5_000);

            string csvText = File.ReadAllText(csv);
            Assert.StartsWith("Hardware", csvText.TrimStart('﻿'));
            Assert.Contains("GPU Core", csvText);

            string jsonText = File.ReadAllText(json);
            Assert.Contains("\"Headline\"", jsonText);
            Assert.DoesNotContain(Environment.MachineName, jsonText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Environment.MachineName, csvText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ComputerNameOnlyWhenExplicitlyRequested()
    {
        var inventory = HardwareInventoryCollector.Collect();
        var report = ReportBuilder.Build(Inputs(inventory), new ReportOptions { IncludeIdentifiers = true });
        Assert.False(string.IsNullOrEmpty(report.Inventory!.Os.ComputerName));
    }

    [Theory]
    [InlineData("simples", "simples")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    public void CsvEscaping(string input, string expected) => Assert.Equal(expected, CsvExporter.Escape(input, ";"));

    [Fact]
    public void InventoryIdentifiesTheMainComponents()
    {
        var inv = HardwareInventoryCollector.Collect();
        Assert.NotEqual("Não disponível", inv.Cpu.Name);
        Assert.True(inv.Cpu.Threads >= inv.Cpu.Cores && inv.Cpu.Cores > 0);
        Assert.True(inv.Memory.UsableBytes > 0);
        Assert.NotEmpty(inv.Gpus);
        Assert.NotEmpty(inv.Disks);
    }
}

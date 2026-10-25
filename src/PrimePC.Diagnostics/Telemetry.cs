using PrimePC.Sensors;

namespace PrimePC.Diagnostics;

/// <summary>Amostra de telemetria usada pela análise de gargalos. Campos nulos = não disponível.</summary>
public sealed class TelemetrySample
{
    public DateTime Time { get; set; }
    public float? CpuTotalLoad { get; set; }

    /// <summary>Uso da thread lógica mais ocupada no instante da amostra.</summary>
    public float? CpuBusiestThread { get; set; }

    /// <summary>Quantidade de threads lógicas com uso ≥ 85%.</summary>
    public int? CpuSaturatedThreads { get; set; }

    public float? CpuClockMHz { get; set; }
    public float? CpuTemperature { get; set; }
    public float? CpuPower { get; set; }
    public float? GpuLoad { get; set; }
    public float? GpuClockMHz { get; set; }
    public float? GpuTemperature { get; set; }
    public float? GpuHotSpot { get; set; }
    public float? GpuPower { get; set; }
    public float? GpuPowerLimitPercent { get; set; }
    public float? VramUsedMB { get; set; }
    public float? VramTotalMB { get; set; }
    public float? RamLoad { get; set; }

    /// <summary>Maior "atividade total" entre os discos (%).</summary>
    public float? DiskActivity { get; set; }

    /// <summary>
    /// FPS no instante da amostra: medido a cada quadro (eventos do Windows) quando disponível; senão, o informado pelo
    /// driver da GPU, quando existir.
    /// </summary>
    public float? Fps { get; set; }

    /// <summary>Tempo médio de quadro no instante da amostra (ms), quando medido a cada quadro.</summary>
    public float? FrameTimeMs { get; set; }
}

/// <summary>Converte leituras de sensores em amostras de telemetria.</summary>
public static class TelemetryMapper
{
    public const float SaturatedThreadThreshold = 85f;

    public static TelemetrySample Capture(SensorSnapshot snapshot, KeySensorMap map, string? gpuHardwareId)
    {
        var gpu = map.PrimaryGpu(snapshot, gpuHardwareId);

        int? saturated = null;
        if (map.CpuThreadLoads.Count > 0)
        {
            int count = 0;
            bool any = false;
            foreach (var id in map.CpuThreadLoads)
            {
                if (snapshot.ValueOf(id) is not { } v)
                    continue;
                any = true;
                if (v >= SaturatedThreadThreshold)
                    count++;
            }

            saturated = any ? count : null;
        }

        float? disk = null;
        foreach (var d in map.Disks)
        {
            if (snapshot.ValueOf(d.TotalActivity) is { } a)
                disk = disk is null ? a : Math.Max(disk.Value, a);
        }

        return new TelemetrySample
        {
            Time = snapshot.Timestamp,
            CpuTotalLoad = snapshot.ValueOf(map.CpuTotalLoad),
            CpuBusiestThread = map.BusiestThreadLoad(snapshot),
            CpuSaturatedThreads = saturated,
            CpuClockMHz = map.AverageCpuClock(snapshot),
            CpuTemperature = snapshot.ValueOf(map.CpuTemperature),
            CpuPower = snapshot.ValueOf(map.CpuPower),
            GpuLoad = snapshot.ValueOf(gpu?.Load),
            GpuClockMHz = snapshot.ValueOf(gpu?.CoreClock),
            GpuTemperature = snapshot.ValueOf(gpu?.Temperature),
            GpuHotSpot = snapshot.ValueOf(gpu?.HotSpot),
            GpuPower = snapshot.ValueOf(gpu?.Power),
            GpuPowerLimitPercent = snapshot.ValueOf(gpu?.PowerLimitPercent),
            VramUsedMB = snapshot.ValueOf(gpu?.VramUsed),
            VramTotalMB = snapshot.ValueOf(gpu?.VramTotal),
            RamLoad = snapshot.ValueOf(map.RamLoad),
            DiskActivity = disk,
            Fps = snapshot.ValueOf(gpu?.Fps),
        };
    }
}

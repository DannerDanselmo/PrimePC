using ComputeSharp;

namespace PrimePC.GpuTesting;

/// <summary>Placas de vídeo com aceleração por hardware disponíveis via DirectX 12.</summary>
public static class GpuDevices
{
    public static IReadOnlyList<GpuDeviceInfo> List()
    {
        try
        {
            return Hardware()
                .Select((d, i) => new GpuDeviceInfo(i, d.Name, (long)d.DedicatedMemorySize, (long)d.SharedMemorySize, (int)d.ComputeUnits))
                .ToList();
        }
        catch
        {
            return Array.Empty<GpuDeviceInfo>();
        }
    }

    /// <summary>Índice da GPU com mais memória dedicada (normalmente a placa de vídeo dedicada).</summary>
    public static int DefaultIndex(IReadOnlyList<GpuDeviceInfo> devices) =>
        devices.Count == 0 ? -1 : devices.OrderByDescending(d => d.DedicatedMemoryBytes).First().Index;

    internal static IReadOnlyList<GraphicsDevice> Hardware() =>
        GraphicsDevice.EnumerateDevices().Where(d => d.IsHardwareAccelerated).ToList();

    internal static GraphicsDevice Get(int index)
    {
        var all = Hardware();
        if (index < 0 || index >= all.Count)
            throw new InvalidOperationException("A placa de vídeo selecionada não está disponível.");
        return all[index];
    }

    /// <summary>Erros do DirectX que indicam que a GPU parou de responder (TDR / dispositivo removido).</summary>
    internal static bool IsDeviceLost(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            uint hr = unchecked((uint)e.HResult);
            if (hr is 0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020
                || e.Message.Contains("device removed", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("device lost", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("DEVICE_REMOVED", StringComparison.OrdinalIgnoreCase)
                || e.Message.Contains("DEVICE_HUNG", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    internal const string DeviceLostMessage =
        "A placa de vídeo deixou de responder durante o teste e o Windows reiniciou o driver. Isso indica instabilidade " +
        "(overclock/undervolt, temperatura, fonte de alimentação ou driver).";
}

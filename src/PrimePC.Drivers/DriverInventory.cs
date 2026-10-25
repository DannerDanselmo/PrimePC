using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace PrimePC.Drivers;

/// <summary>
/// Lista os drivers instalados (Win32_PnPSignedDriver) e os dispositivos com problema (Win32_PnPEntity),
/// inclusive os que estão sem driver. Somente leitura: nada é alterado no sistema.
/// </summary>
public static partial class DriverInventory
{
    public static DriverScan Collect()
    {
        var notes = new List<string>();
        var entities = new Dictionary<string, (string? Name, string[] HardwareIds, int Problem, string? PnpClass)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in Query("SELECT DeviceID, Name, HardwareID, CompatibleID, ConfigManagerErrorCode, PNPClass FROM Win32_PnPEntity"))
            {
                if (Str(r, "DeviceID") is not { } id)
                    continue;
                var ids = Array(r, "HardwareID").Concat(Array(r, "CompatibleID")).ToArray();
                entities[id] = (Str(r, "Name"), ids, Int(r, "ConfigManagerErrorCode") ?? 0, Str(r, "PNPClass"));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Não foi possível ler o estado dos dispositivos: {ex.Message}");
        }

        var drivers = new List<InstalledDriver>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var r in Query("SELECT DeviceName, DeviceID, DeviceClass, DriverProviderName, Manufacturer, DriverVersion, DriverDate, " +
                                    "IsSigned, Signer, InfName, HardWareID FROM Win32_PnPSignedDriver"))
            {
                string id = Str(r, "DeviceID") ?? "";
                if (id.Length == 0 || !seen.Add(id))
                    continue;
                entities.TryGetValue(id, out var e);
                var hw = (e.HardwareIds ?? System.Array.Empty<string>()).ToList();
                if (Str(r, "HardWareID") is { } own && !hw.Contains(own, StringComparer.OrdinalIgnoreCase))
                    hw.Insert(0, own);
                string cls = (Str(r, "DeviceClass") ?? e.PnpClass ?? "").ToUpperInvariant();
                drivers.Add(new InstalledDriver(
                    DeviceName: Str(r, "DeviceName") ?? e.Name ?? hw.FirstOrDefault() ?? "Dispositivo sem nome",
                    DeviceId: id,
                    DeviceClass: cls,
                    Category: CategoryFor(cls, id),
                    Provider: Str(r, "DriverProviderName"),
                    Manufacturer: Str(r, "Manufacturer"),
                    Version: Str(r, "DriverVersion"),
                    Date: Date(r, "DriverDate"),
                    IsSigned: r.GetValueOrDefault("IsSigned") as bool?,
                    Signer: Str(r, "Signer"),
                    InfName: Str(r, "InfName"),
                    HardwareIds: hw,
                    ProblemCode: e.Problem));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Não foi possível ler a lista de drivers: {ex.Message}");
        }

        // Dispositivos com problema que não aparecem na lista de drivers (por exemplo, sem driver instalado).
        foreach (var (id, e) in entities)
        {
            if (e.Problem == 0 || seen.Contains(id))
                continue;
            string cls = (e.PnpClass ?? "").ToUpperInvariant();
            drivers.Add(new InstalledDriver(
                DeviceName: e.Name ?? (e.HardwareIds.Length > 0 ? e.HardwareIds[0] : id),
                DeviceId: id,
                DeviceClass: cls,
                Category: CategoryFor(cls, id),
                Provider: null, Manufacturer: null, Version: null, Date: null, IsSigned: null, Signer: null, InfName: null,
                HardwareIds: e.HardwareIds,
                ProblemCode: e.Problem));
        }

        return new DriverScan(DateTime.Now, drivers.OrderBy(d => d.Category).ThenBy(d => d.DeviceName, StringComparer.CurrentCultureIgnoreCase).ToList(), notes);
    }

    /// <summary>
    /// Nome para exibição. Alguns componentes não têm nome e o Windows mostra só o identificador
    /// (ex.: SWC\VEN_MSFT&amp;AUDIO_EFFECTPACK_VOICECLARITY); nesse caso, mostra a parte final legível.
    /// </summary>
    public static string DisplayName(InstalledDriver d)
    {
        string name = d.DeviceName;
        bool looksLikeId = name.Contains('\\') && !name.Contains(' ');
        if (!looksLikeId)
            return name;
        string tail = name.Split('\\', '&').Last(p => p.Length > 0);
        return $"Componente sem nome ({tail})";
    }

    /// <summary>Agrupa as classes de dispositivo do Windows em categorias amigáveis.</summary>
    public static DriverCategory CategoryFor(string deviceClass, string deviceId = "") => deviceClass.ToUpperInvariant() switch
    {
        "DISPLAY" => DriverCategory.Video,
        "MEDIA" or "AUDIOENDPOINT" or "AUDIOPROCESSINGOBJECT" or "MIDIENDPOINT" => DriverCategory.Audio,
        "NET" or "NETSERVICE" or "NETTRANS" or "NETCLIENT" => DriverCategory.Network,
        "BLUETOOTH" => DriverCategory.Bluetooth,
        "SCSIADAPTER" or "HDC" or "DISKDRIVE" or "VOLUME" or "VOLUMESNAPSHOT" or "CDROM" or "STORAGEVOLUME" or "FDC" => DriverCategory.Storage,
        "USB" or "USBDEVICE" => DriverCategory.Usb,
        "HIDCLASS" or "KEYBOARD" or "MOUSE" or "XNACOMPOSITE" or "XBOXCOMPOSITE" or "RAZER DEVICE" => DriverCategory.Input,
        "SYSTEM" or "COMPUTER" or "FIRMWARE" or "SECURITYDEVICES" or "SMARTCARDREADER" => DriverCategory.System,
        "PROCESSOR" => DriverCategory.Processor,
        "MONITOR" => DriverCategory.Monitor,
        "PRINTER" or "PRINTQUEUE" => DriverCategory.Printer,
        "SOFTWARECOMPONENT" or "SOFTWAREDEVICE" or "EXTENSION" => DriverCategory.Software,
        _ when deviceId.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase) => DriverCategory.Software,
        _ => DriverCategory.Other,
    };

    public static string CategoryName(DriverCategory c) => c switch
    {
        DriverCategory.Video => "Vídeo",
        DriverCategory.Audio => "Áudio",
        DriverCategory.Network => "Rede",
        DriverCategory.Bluetooth => "Bluetooth",
        DriverCategory.Storage => "Armazenamento",
        DriverCategory.Usb => "USB",
        DriverCategory.Input => "Teclado, mouse e controles",
        DriverCategory.System => "Sistema e chipset",
        DriverCategory.Processor => "Processador",
        DriverCategory.Monitor => "Monitores",
        DriverCategory.Printer => "Impressoras",
        DriverCategory.Software => "Componentes de software",
        _ => "Outros",
    };

    /// <summary>
    /// Versão no formato divulgado pelo fabricante. NVIDIA: os 5 últimos dígitos das duas últimas partes
    /// (32.0.16.1742 → "61742" → 617.42). Para os demais fabricantes, a própria versão do Windows.
    /// </summary>
    public static string? FriendlyVersion(string? provider, string? version)
    {
        if (version is null)
            return null;
        if (provider is not null && provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            var parts = version.Split('.');
            if (parts.Length == 4 && parts[3].Length <= 4 && parts.All(p => p.All(char.IsDigit)))
            {
                string digits = parts[2] + parts[3].PadLeft(4, '0');
                if (digits.Length >= 5)
                {
                    digits = digits[^5..];
                    return $"{int.Parse(digits[..3], CultureInfo.InvariantCulture)}.{digits[3..]}";
                }
            }
        }

        return version;
    }

    /// <summary>Site oficial de drivers do fabricante, quando conhecido.</summary>
    public static string? VendorDriverPage(string? provider, string? manufacturer = null)
    {
        string s = $"{provider} {manufacturer}";
        if (s.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "https://www.nvidia.com/pt-br/drivers/";
        if (AmdRegex().IsMatch(s)) return "https://www.amd.com/en/support/download/drivers.html";
        if (s.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "https://www.intel.com/content/www/us/en/support/detect.html";
        if (s.Contains("Realtek", StringComparison.OrdinalIgnoreCase)) return "https://www.realtek.com/";
        if (s.Contains("MediaTek", StringComparison.OrdinalIgnoreCase)) return "https://www.mediatek.com/";
        if (s.Contains("Razer", StringComparison.OrdinalIgnoreCase)) return "https://www.razer.com/synapse-4";
        return null;
    }

    [GeneratedRegex(@"\bAMD\b|Advanced Micro Devices", RegexOptions.IgnoreCase)]
    private static partial Regex AmdRegex();

    // ---------------- WMI ----------------

    private static List<Dictionary<string, object?>> Query(string wql)
    {
        var options = new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(30), ReturnImmediately = true, Rewindable = false };
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery(wql), options);
        var rows = new List<Dictionary<string, object?>>();
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in obj.Properties)
                    row[p.Name] = p.Value;
                rows.Add(row);
            }
        }

        return rows;
    }

    private static string? Str(Dictionary<string, object?> r, string key)
    {
        var s = r.GetValueOrDefault(key)?.ToString()?.Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    private static string[] Array(Dictionary<string, object?> r, string key) =>
        r.GetValueOrDefault(key) is string[] a ? a.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() : System.Array.Empty<string>();

    private static int? Int(Dictionary<string, object?> r, string key)
    {
        try
        {
            return r.GetValueOrDefault(key) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? Date(Dictionary<string, object?> r, string key)
    {
        if (Str(r, key) is not { } s)
            return null;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(s);
        }
        catch
        {
            return null;
        }
    }
}

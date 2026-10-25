using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using PrimePC.Sensors.Sources;

namespace PrimePC.Sensors.Inventory;

/// <summary>
/// Identifica automaticamente o hardware via WMI/SMBIOS, registro e APIs do Windows. Cada seção é
/// coletada de forma independente: uma falha vira uma nota explicativa em vez de interromper tudo.
/// </summary>
public static partial class HardwareInventoryCollector
{
    public static HardwareInventory Collect()
    {
        var notes = new List<string>();
        var cpu = Safe(CollectCpu, notes, "processador", FallbackCpu);
        var board = Safe(CollectBoard, notes, "placa-mãe/BIOS",
            () => new BoardInfo("Não disponível", "Não disponível", null, null, null, null, FirmwareMode(), null, ChipsetNote));
        var memory = Safe(CollectMemory, notes, "memória",
            () => new MemoryInfo(0, (long)WindowsSystemSource.GetPhysicalMemory().Total, Array.Empty<MemoryModuleInfo>(),
                "Não detectável", "Não disponível"));
        var gpus = Safe(CollectGpus, notes, "placas de vídeo", () => (IReadOnlyList<GpuInfo>)Array.Empty<GpuInfo>());
        var disks = Safe(() => CollectDisks(notes), notes, "armazenamento", () => (IReadOnlyList<DiskInfo>)Array.Empty<DiskInfo>());
        var os = Safe(CollectOs, notes, "sistema operacional",
            () => new OsInfo(RuntimeInformation.OSDescription, null, Environment.OSVersion.Version.ToString(), "",
                RuntimeInformation.OSArchitecture.ToString(), null, Environment.MachineName));

        return new HardwareInventory(cpu, board, memory, gpus, disks, os, DateTime.Now, notes);
    }

    private const string ChipsetNote = "Não disponível (o Windows não expõe o chipset de forma padronizada)";

    private static T Safe<T>(Func<T> collect, List<string> notes, string section, Func<T> fallback)
    {
        try
        {
            return collect();
        }
        catch (Exception ex)
        {
            notes.Add($"Não foi possível ler informações de {section}: {ex.Message}");
            return fallback();
        }
    }

    private static CpuInfo FallbackCpu() => new(
        "Não disponível", "Não disponível", RuntimeInformation.ProcessArchitecture.ToString(), 1, 0,
        Environment.ProcessorCount, null, null, null, null, null, InstructionSets());

    private static CpuInfo CollectCpu()
    {
        var rows = Wmi.Query("SELECT Name, Manufacturer, Architecture, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed, " +
                             "L2CacheSize, L3CacheSize, SocketDesignation, VirtualizationFirmwareEnabled FROM Win32_Processor");
        if (rows.Count == 0)
            return FallbackCpu();

        var first = rows[0];
        string arch = first.Int("Architecture") switch
        {
            0 => "x86",
            5 => "ARM",
            9 => "x64",
            12 => "ARM64",
            _ => RuntimeInformation.ProcessArchitecture.ToString(),
        };

        return new CpuInfo(
            Name: first.Str("Name") ?? "Não disponível",
            Manufacturer: first.Str("Manufacturer") ?? "Não disponível",
            Architecture: arch,
            Sockets: rows.Count,
            Cores: rows.Sum(r => r.Int("NumberOfCores") ?? 0),
            Threads: rows.Sum(r => r.Int("NumberOfLogicalProcessors") ?? 0) is > 0 and var t ? t : Environment.ProcessorCount,
            MaxClockMHz: first.Int("MaxClockSpeed"),
            L2CacheKB: first.Int("L2CacheSize"),
            L3CacheKB: first.Int("L3CacheSize"),
            Socket: first.Str("SocketDesignation"),
            VirtualizationEnabled: first.Bool("VirtualizationFirmwareEnabled"),
            InstructionSets: InstructionSets());
    }

    /// <summary>Conjuntos de instruções detectados diretamente pelo runtime (CPUID).</summary>
    public static IReadOnlyList<string> InstructionSets()
    {
        var list = new List<string>();
        void Add(bool supported, string name)
        {
            if (supported)
                list.Add(name);
        }

        Add(Sse3.IsSupported, "SSE3");
        Add(Ssse3.IsSupported, "SSSE3");
        Add(Sse41.IsSupported, "SSE4.1");
        Add(Sse42.IsSupported, "SSE4.2");
        Add(Popcnt.IsSupported, "POPCNT");
        Add(Lzcnt.IsSupported, "LZCNT");
        Add(Bmi1.IsSupported, "BMI1");
        Add(Bmi2.IsSupported, "BMI2");
        Add(Aes.IsSupported, "AES-NI");
        Add(Pclmulqdq.IsSupported, "PCLMULQDQ");
        Add(Avx.IsSupported, "AVX");
        Add(Avx2.IsSupported, "AVX2");
        Add(Fma.IsSupported, "FMA3");
        Add(Avx512F.IsSupported, "AVX-512F");
        Add(Avx512BW.IsSupported, "AVX-512BW");
        Add(Avx512CD.IsSupported, "AVX-512CD");
        Add(Avx512DQ.IsSupported, "AVX-512DQ");
        Add(Avx512Vbmi.IsSupported, "AVX-512VBMI");
        return list;
    }

    private static BoardInfo CollectBoard()
    {
        var board = Wmi.Query("SELECT Manufacturer, Product, Version FROM Win32_BaseBoard").FirstOrDefault() ?? new();
        var bios = Wmi.Query("SELECT Manufacturer, SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault() ?? new();
        string? version = board.Str("Version");
        if (version is not null && (version.Contains("Default string", StringComparison.OrdinalIgnoreCase)
                                    || version.Contains("To be filled", StringComparison.OrdinalIgnoreCase)))
            version = null;

        return new BoardInfo(
            Manufacturer: board.Str("Manufacturer") ?? "Não disponível",
            Product: board.Str("Product") ?? "Não disponível",
            Version: version,
            BiosVendor: bios.Str("Manufacturer"),
            BiosVersion: bios.Str("SMBIOSBIOSVersion"),
            BiosDate: bios.Date("ReleaseDate"),
            FirmwareMode: FirmwareMode(),
            SecureBoot: SecureBootEnabled(),
            Chipset: ChipsetNote);
    }

    private static string FirmwareMode()
    {
        try
        {
            if (GetFirmwareType(out var type))
                return type switch { 1 => "BIOS legado", 2 => "UEFI", _ => "Desconhecido" };
        }
        catch
        {
            // API indisponível
        }

        return "Desconhecido";
    }

    private static bool? SecureBootEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int v ? v == 1 : null;
        }
        catch
        {
            return null;
        }
    }

    private static MemoryInfo CollectMemory()
    {
        var rows = Wmi.Query("SELECT Capacity, Speed, ConfiguredClockSpeed, Manufacturer, PartNumber, DeviceLocator, BankLabel, " +
                             "SMBIOSMemoryType, FormFactor FROM Win32_PhysicalMemory");
        var modules = rows.Select(r => new MemoryModuleInfo(
            Slot: r.Str("DeviceLocator") ?? "?",
            Bank: r.Str("BankLabel"),
            CapacityBytes: r.Long("Capacity") ?? 0,
            RatedSpeedMTs: r.Int("Speed") is > 0 and var s ? s : null,
            ConfiguredSpeedMTs: r.Int("ConfiguredClockSpeed") is > 0 and var c ? c : null,
            Manufacturer: CleanVendor(r.Str("Manufacturer")),
            PartNumber: r.Str("PartNumber"),
            MemoryType: MemoryTypeName(r.Int("SMBIOSMemoryType")),
            FormFactor: r.Int("FormFactor") switch { 8 => "DIMM", 12 => "SO-DIMM", 0 or null => "Desconhecido", var f => $"Tipo {f}" })).ToList();

        long installed = modules.Sum(m => m.CapacityBytes);
        long usable = (long)WindowsSystemSource.GetPhysicalMemory().Total;

        var channels = modules
            .Select(m => ChannelLetter(m.Bank) ?? ChannelLetter(m.Slot))
            .Where(c => c is not null)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        string channelEstimate = channels.Count > 0
            ? $"Provavelmente {channels.Count} canal(is) em uso (estimado pelos rótulos dos slots: {string.Join(", ", channels)})"
            : "Não detectável pelos rótulos informados pelo firmware";

        var rated = modules.Select(m => m.RatedSpeedMTs).FirstOrDefault(s => s is not null);
        var configured = modules.Select(m => m.ConfiguredSpeedMTs).FirstOrDefault(s => s is not null);
        string xmp = "Os perfis XMP/EXPO não são expostos pelo Windows (exigem leitura direta do SPD). " +
                     $"Velocidade configurada: {(configured is null ? "não informada" : configured + " MT/s")}; " +
                     $"velocidade informada pelo SMBIOS: {(rated is null ? "não informada" : rated + " MT/s")}.";

        return new MemoryInfo(installed, usable, modules, channelEstimate, xmp);
    }

    private static string? ChannelLetter(string? label)
    {
        if (label is null)
            return null;
        var m = ChannelRegex().Match(label);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex(@"(?:CHANNEL\s*|DIMM[_\s-]?|Channel)([A-H])(?:\d|\b|-)", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelRegex();

    private static string? CleanVendor(string? vendor) =>
        vendor is null || vendor.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || vendor.All(c => c == '0') ? null : vendor;

    private static string MemoryTypeName(int? smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        27 => "LPDDR",
        28 => "LPDDR2",
        29 => "LPDDR3",
        30 => "LPDDR4",
        34 => "DDR5",
        35 => "LPDDR5",
        null or 0 => "Desconhecido",
        var t => $"Tipo SMBIOS {t}",
    };

    private static IReadOnlyList<GpuInfo> CollectGpus()
    {
        var rows = Wmi.Query("SELECT Name, AdapterCompatibility, DriverVersion, DriverDate, VideoProcessor, " +
                             "CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
        var memory = GpuMemoryFromRegistry();
        return rows.Select(r =>
        {
            string name = r.Str("Name") ?? "Não disponível";
            int? w = r.Int("CurrentHorizontalResolution"), h = r.Int("CurrentVerticalResolution"), hz = r.Int("CurrentRefreshRate");
            return new GpuInfo(
                Name: name,
                Vendor: r.Str("AdapterCompatibility"),
                DriverVersion: r.Str("DriverVersion"),
                DriverDate: r.Date("DriverDate"),
                DedicatedMemoryBytes: memory.GetValueOrDefault(name),
                VideoProcessor: r.Str("VideoProcessor"),
                CurrentMode: w is > 0 && h is > 0 ? $"{w}×{h}{(hz is > 0 ? $" @ {hz} Hz" : "")}" : null);
        }).ToList();
    }

    /// <summary>
    /// O campo AdapterRAM do WMI é limitado a 4 GB; o tamanho real da VRAM é lido do registro do driver
    /// (HardwareInformation.qwMemorySize).
    /// </summary>
    private static Dictionary<string, long> GpuMemoryFromRegistry()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls is null)
                return result;
            foreach (var sub in cls.GetSubKeyNames().Where(n => n.Length == 4 && n.All(char.IsDigit)))
            {
                try
                {
                    using var key = cls.OpenSubKey(sub);
                    if (key?.GetValue("DriverDesc") is not string desc)
                        continue;
                    long? size = key.GetValue("HardwareInformation.qwMemorySize") switch
                    {
                        long l => l,
                        byte[] { Length: >= 8 } b => BitConverter.ToInt64(b, 0),
                        _ => key.GetValue("HardwareInformation.MemorySize") switch
                        {
                            int i => (uint)i,
                            byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
                            _ => null,
                        },
                    };
                    if (size is > 0)
                        result[desc] = size.Value;
                }
                catch
                {
                    // Chave protegida: ignora este adaptador.
                }
            }
        }
        catch
        {
            // Sem acesso ao registro de classes de vídeo.
        }

        return result;
    }

    private static IReadOnlyList<DiskInfo> CollectDisks(List<string> notes)
    {
        var volumesByDisk = VolumesByDisk(notes);
        var reliability = ReliabilityCounters(notes);
        List<Dictionary<string, object?>> rows;
        try
        {
            rows = Wmi.Query("SELECT DeviceId, FriendlyName, Model, MediaType, BusType, Size, HealthStatus, FirmwareVersion FROM MSFT_PhysicalDisk",
                @"root\Microsoft\Windows\Storage");
        }
        catch (Exception ex)
        {
            notes.Add($"API de armazenamento do Windows indisponível ({ex.Message}); usando Win32_DiskDrive.");
            return Wmi.Query("SELECT Index, Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive")
                .Select(r => new DiskInfo(r.Int("Index") ?? -1, r.Str("Model") ?? "Disco", r.Long("Size") ?? 0,
                    r.Str("MediaType") ?? "Desconhecido", r.Str("InterfaceType") ?? "Desconhecido", null, null,
                    volumesByDisk.GetValueOrDefault(r.Int("Index") ?? -1) ?? (IReadOnlyList<VolumeInfo>)Array.Empty<VolumeInfo>(), null))
                .OrderBy(d => d.Number)
                .ToList();
        }

        return rows.Select(r =>
        {
            int number = int.TryParse(r.Str("DeviceId"), out var n) ? n : -1;
            return new DiskInfo(
                Number: number,
                Model: r.Str("FriendlyName") ?? r.Str("Model") ?? "Disco",
                SizeBytes: r.Long("Size") ?? 0,
                MediaType: r.Int("MediaType") switch { 3 => "HDD", 4 => "SSD", 5 => "SCM", _ => "Não especificado" },
                BusType: r.Int("BusType") switch
                {
                    1 => "SCSI", 3 => "ATA", 7 => "USB", 8 => "SSA", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD",
                    13 => "MMC", 15 => "Virtual", 16 => "Espaços de Armazenamento", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
                    _ => "Desconhecido",
                },
                HealthStatus: r.Int("HealthStatus") switch { 0 => "Saudável", 1 => "Atenção", 2 => "Não saudável", _ => null },
                FirmwareVersion: r.Str("FirmwareVersion"),
                Volumes: volumesByDisk.GetValueOrDefault(number) ?? (IReadOnlyList<VolumeInfo>)Array.Empty<VolumeInfo>(),
                Reliability: reliability.GetValueOrDefault(number));
        }).OrderBy(d => d.Number).ToList();
    }

    private static Dictionary<int, IReadOnlyList<VolumeInfo>> VolumesByDisk(List<string> notes)
    {
        var result = new Dictionary<int, List<VolumeInfo>>();
        try
        {
            var partitions = Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", @"root\Microsoft\Windows\Storage");
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady).ToDictionary(d => char.ToUpperInvariant(d.Name[0]));
            foreach (var p in partitions)
            {
                char letter = p.GetValueOrDefault("DriveLetter") switch
                {
                    char c => c,
                    ushort u => (char)u,
                    _ => '\0',
                };
                if (letter == '\0' || !char.IsLetter(letter) || p.Int("DiskNumber") is not { } disk)
                    continue;
                if (!drives.TryGetValue(char.ToUpperInvariant(letter), out var drive))
                    continue;
                if (!result.TryGetValue(disk, out var list))
                    result[disk] = list = new List<VolumeInfo>();
                list.Add(new VolumeInfo($"{char.ToUpperInvariant(letter)}:", NullIfEmpty(drive.VolumeLabel), drive.DriveFormat,
                    drive.TotalSize, drive.AvailableFreeSpace));
            }
        }
        catch (Exception ex)
        {
            notes.Add($"Não foi possível associar volumes aos discos: {ex.Message}");
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<VolumeInfo>)kv.Value.OrderBy(v => v.Letter).ToList());
    }

    private static Dictionary<int, DiskReliability> ReliabilityCounters(List<string> notes)
    {
        var result = new Dictionary<int, DiskReliability>();
        try
        {
            var rows = Wmi.Query("SELECT DeviceId, Temperature, TemperatureMax, Wear, PowerOnHours, ReadErrorsTotal, ReadErrorsUncorrected, " +
                                 "WriteErrorsTotal, WriteErrorsUncorrected FROM MSFT_StorageReliabilityCounter",
                @"root\Microsoft\Windows\Storage");
            foreach (var r in rows)
            {
                if (!int.TryParse(r.Str("DeviceId"), out var n))
                    continue;
                result[n] = new DiskReliability(
                    TemperatureC: r.Int("Temperature") is > 0 and var t ? t : null,
                    TemperatureMaxC: r.Int("TemperatureMax") is > 0 and var tm ? tm : null,
                    WearPercent: r.Int("Wear"),
                    PowerOnHours: r.Long("PowerOnHours"),
                    ReadErrorsTotal: r.Long("ReadErrorsTotal"),
                    ReadErrorsUncorrected: r.Long("ReadErrorsUncorrected"),
                    WriteErrorsTotal: r.Long("WriteErrorsTotal"),
                    WriteErrorsUncorrected: r.Long("WriteErrorsUncorrected"));
            }
        }
        catch (Exception ex)
        {
            notes.Add(PrivilegeInfo.IsAdministrator
                ? $"Contadores de confiabilidade dos discos indisponíveis: {ex.Message}"
                : "Contadores de confiabilidade dos discos (horas ligadas, desgaste, erros) exigem execução como administrador.");
        }

        return result;
    }

    private static OsInfo CollectOs()
    {
        var os = Wmi.Query("SELECT Caption, Version, BuildNumber, OSArchitecture, LastBootUpTime, CSName FROM Win32_OperatingSystem")
            .FirstOrDefault() ?? new();
        string? displayVersion = null;
        string build = os.Str("BuildNumber") ?? Environment.OSVersion.Version.Build.ToString();
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            displayVersion = key?.GetValue("DisplayVersion") as string;
            if (key?.GetValue("UBR") is int ubr)
                build = $"{build}.{ubr}";
        }
        catch
        {
            // Registro inacessível: mantém o build do WMI.
        }

        return new OsInfo(
            Caption: os.Str("Caption") ?? RuntimeInformation.OSDescription,
            DisplayVersion: displayVersion,
            Version: os.Str("Version") ?? Environment.OSVersion.Version.ToString(),
            Build: build,
            Architecture: os.Str("OSArchitecture") ?? RuntimeInformation.OSArchitecture.ToString(),
            LastBoot: os.Date("LastBootUpTime"),
            ComputerName: os.Str("CSName") ?? Environment.MachineName);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out uint firmwareType);
}

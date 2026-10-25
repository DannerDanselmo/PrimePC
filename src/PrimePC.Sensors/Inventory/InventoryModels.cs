namespace PrimePC.Sensors.Inventory;

public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    string Architecture,
    int Sockets,
    int Cores,
    int Threads,
    int? MaxClockMHz,
    int? L2CacheKB,
    int? L3CacheKB,
    string? Socket,
    bool? VirtualizationEnabled,
    IReadOnlyList<string> InstructionSets);

public sealed record BoardInfo(
    string Manufacturer,
    string Product,
    string? Version,
    string? BiosVendor,
    string? BiosVersion,
    DateTime? BiosDate,
    string FirmwareMode,
    bool? SecureBoot,
    string Chipset);

public sealed record MemoryModuleInfo(
    string Slot,
    string? Bank,
    long CapacityBytes,
    int? RatedSpeedMTs,
    int? ConfiguredSpeedMTs,
    string? Manufacturer,
    string? PartNumber,
    string MemoryType,
    string FormFactor);

public sealed record MemoryInfo(
    long InstalledBytes,
    long UsableBytes,
    IReadOnlyList<MemoryModuleInfo> Modules,
    string ChannelEstimate,
    string XmpExpoStatus);

public sealed record GpuInfo(
    string Name,
    string? Vendor,
    string? DriverVersion,
    DateTime? DriverDate,
    long? DedicatedMemoryBytes,
    string? VideoProcessor,
    string? CurrentMode);

public sealed record VolumeInfo(string Letter, string? Label, string? FileSystem, long SizeBytes, long FreeBytes);

public sealed record DiskReliability(
    int? TemperatureC,
    int? TemperatureMaxC,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrorsTotal,
    long? ReadErrorsUncorrected,
    long? WriteErrorsTotal,
    long? WriteErrorsUncorrected);

public sealed record DiskInfo(
    int Number,
    string Model,
    long SizeBytes,
    string MediaType,
    string BusType,
    string? HealthStatus,
    string? FirmwareVersion,
    IReadOnlyList<VolumeInfo> Volumes,
    DiskReliability? Reliability);

public sealed record OsInfo(
    string Caption,
    string? DisplayVersion,
    string Version,
    string Build,
    string Architecture,
    DateTime? LastBoot,
    string? ComputerName);

/// <summary>Inventário completo do computador, coletado de fontes documentadas do Windows.</summary>
public sealed record HardwareInventory(
    CpuInfo Cpu,
    BoardInfo Board,
    MemoryInfo Memory,
    IReadOnlyList<GpuInfo> Gpus,
    IReadOnlyList<DiskInfo> Disks,
    OsInfo Os,
    DateTime CollectedAt,
    IReadOnlyList<string> Notes);

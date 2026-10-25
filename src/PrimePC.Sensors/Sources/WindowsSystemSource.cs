using System.Runtime.InteropServices;

namespace PrimePC.Sensors.Sources;

/// <summary>
/// Sensores do sistema obtidos diretamente de APIs documentadas do Windows (sem driver, sem administrador):
/// memória física, memória comprometida, processos, threads, handles e tempo ligado.
/// </summary>
public sealed class WindowsSystemSource : ISensorSource
{
    private const string HardwareId = "/windows/system";
    private const string HardwareName = "Windows";

    private static readonly SensorDescriptor MemoryLoad = D("memory/load", "Memória física em uso", SensorKind.Load, "GlobalMemoryStatusEx");
    private static readonly SensorDescriptor MemoryUsed = D("memory/used", "Memória física usada", SensorKind.Data, "GlobalMemoryStatusEx");
    private static readonly SensorDescriptor MemoryAvailable = D("memory/available", "Memória física disponível", SensorKind.Data, "GlobalMemoryStatusEx");
    private static readonly SensorDescriptor CommitCharge = D("memory/commit", "Memória comprometida", SensorKind.Data, "GetPerformanceInfo");
    private static readonly SensorDescriptor CommitLimit = D("memory/commitlimit", "Limite de memória comprometida", SensorKind.Data, "GetPerformanceInfo");
    private static readonly SensorDescriptor SystemCache = D("memory/cache", "Cache do sistema", SensorKind.Data, "GetPerformanceInfo");
    private static readonly SensorDescriptor Processes = D("count/processes", "Processos", SensorKind.Count, "GetPerformanceInfo");
    private static readonly SensorDescriptor Threads = D("count/threads", "Threads", SensorKind.Count, "GetPerformanceInfo");
    private static readonly SensorDescriptor Handles = D("count/handles", "Identificadores (handles)", SensorKind.Count, "GetPerformanceInfo");
    private static readonly SensorDescriptor Uptime = D("time/uptime", "Tempo ligado", SensorKind.TimeSpan, "GetTickCount64");

    private float? _memLoad, _memUsed, _memAvail, _commit, _commitLimit, _cache, _proc, _threads, _handles, _uptime;

    public string Name => "Windows";
    public IReadOnlyList<string> Limitations { get; } = Array.Empty<string>();
    public int Generation => 1;

    /// <summary>Identificador do sensor de uso de memória física (fallback confiável sem driver).</summary>
    public static string MemoryLoadId => MemoryLoad.Id;
    public static string MemoryUsedId => MemoryUsed.Id;
    public static string MemoryAvailableId => MemoryAvailable.Id;
    public static string CommitChargeId => CommitCharge.Id;
    public static string UptimeId => Uptime.Id;
    public static string ProcessesId => Processes.Id;

    public void Open() => Update();

    public void Update()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref status) && status.ullTotalPhys > 0)
        {
            double used = status.ullTotalPhys - status.ullAvailPhys;
            _memLoad = (float)(used / status.ullTotalPhys * 100.0);
            _memUsed = (float)(used / Gb);
            _memAvail = (float)(status.ullAvailPhys / Gb);
        }
        else
        {
            _memLoad = _memUsed = _memAvail = null;
        }

        var perf = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        if (K32GetPerformanceInfo(ref perf, perf.cb))
        {
            double page = (double)perf.PageSize;
            _commit = (float)(perf.CommitTotal * page / Gb);
            _commitLimit = (float)(perf.CommitLimit * page / Gb);
            _cache = (float)(perf.SystemCache * page / Gb);
            _proc = perf.ProcessCount;
            _threads = perf.ThreadCount;
            _handles = perf.HandleCount;
        }
        else
        {
            _commit = _commitLimit = _cache = _proc = _threads = _handles = null;
        }

        _uptime = Environment.TickCount64 / 1000f;
    }

    public IEnumerable<RawReading> Read()
    {
        yield return new RawReading(MemoryLoad, _memLoad);
        yield return new RawReading(MemoryUsed, _memUsed);
        yield return new RawReading(MemoryAvailable, _memAvail);
        yield return new RawReading(CommitCharge, _commit);
        yield return new RawReading(CommitLimit, _commitLimit);
        yield return new RawReading(SystemCache, _cache);
        yield return new RawReading(Processes, _proc);
        yield return new RawReading(Threads, _threads);
        yield return new RawReading(Handles, _handles);
        yield return new RawReading(Uptime, _uptime);
    }

    public void Dispose()
    {
    }

    /// <summary>Lê a memória física total e disponível (bytes). Usado também pelo teste de RAM.</summary>
    public static (ulong Total, ulong Available) GetPhysicalMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (status.ullTotalPhys, status.ullAvailPhys) : (0, 0);
    }

    private const double Gb = 1024d * 1024 * 1024;

    private static SensorDescriptor D(string id, string name, SensorKind kind, string api) =>
        new($"{HardwareId}/{id}", name, HardwareId, HardwareName, HardwareCategory.System, kind,
            SensorUnits.UnitFor(kind), $"Windows · {api}");

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetPerformanceInfo(ref PERFORMANCE_INFORMATION pPerformanceInformation, uint cb);
}

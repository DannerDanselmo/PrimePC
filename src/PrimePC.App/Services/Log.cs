using System.Diagnostics;
using System.Text;

namespace PrimePC.App.Services;

/// <summary>Log técnico em arquivo (um por dia), para investigar problemas. Nada é enviado pela rede.</summary>
public static class Log
{
    private static readonly object Sync = new();

    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"primepc-{DateTime.Now:yyyyMMdd}.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Logs);
            foreach (var old in new DirectoryInfo(AppPaths.Logs).GetFiles("primepc-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-14)))
                old.Delete();
        }
        catch
        {
            // Sem log não impede o uso do programa.
        }

        Trace.Listeners.Add(new LogTraceListener());
        Info($"PrimePC iniciado · versão {Reports.ReportBuilder.AppVersion} · {Environment.OSVersion} · " +
             $"administrador={Sensors.PrivilegeInfo.IsAdministrator} · PawnIO={Sensors.PrivilegeInfo.IsPawnIoInstalled}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null) => Write("ERRO", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
                File.AppendAllText(CurrentFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
            // Ignorado.
        }
    }

    private sealed class LogTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (message is not null)
                Info(message);
        }
    }
}

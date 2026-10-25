using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PrimePC.Gaming;

/// <summary>Programa em primeiro plano (a janela que está recebendo o teclado, normalmente o jogo).</summary>
public static class ForegroundProcess
{
    public static int? GetId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return null;
        GetWindowThreadProcessId(window, out uint pid);
        return pid == 0 ? null : (int)pid;
    }

    public static IntPtr GetWindow() => GetForegroundWindow();

    public static string? NameOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}

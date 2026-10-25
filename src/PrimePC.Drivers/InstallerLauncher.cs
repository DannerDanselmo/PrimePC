using System.ComponentModel;
using System.Diagnostics;
using System.Management;

namespace PrimePC.Drivers;

/// <summary>
/// Abre o instalador oficial de um fabricante (com a janela dele, nada silencioso) e espera ele terminar. Instaladores
/// como o da NVIDIA primeiro se extraem e depois abrem outros programas; por isso acompanha toda a árvore de processos.
/// </summary>
public static class InstallerLauncher
{
    /// <summary>Retorna falso se o usuário recusou a permissão de administrador (UAC).</summary>
    public static async Task<bool> RunAndWaitAsync(string path, CancellationToken token)
    {
        Process? root;
        try
        {
            root = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path) });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED: UAC recusado
        {
            return false;
        }

        if (root is null)
            return true;
        using (root)
            await WaitForTreeAsync(root.Id, token).ConfigureAwait(false);
        return true;
    }

    /// <summary>Espera o processo e todos os que ele abrir (filhos, netos…) terminarem.</summary>
    public static async Task WaitForTreeAsync(int rootPid, CancellationToken token)
    {
        var tracked = new HashSet<int> { rootPid };
        while (true)
        {
            var parents = Snapshot();
            bool grew;
            do
            {
                grew = false;
                foreach (var (pid, parent) in parents)
                {
                    if (tracked.Contains(parent) && tracked.Add(pid))
                        grew = true;
                }
            }
            while (grew);

            // Mantém só os que ainda existem (evita confundir com um número de processo reutilizado depois).
            tracked.IntersectWith(parents.Keys);
            if (tracked.Count == 0)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
        }
    }

    private static Dictionary<int, int> Snapshot()
    {
        var map = new Dictionary<int, int>();
        using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
        foreach (var obj in searcher.Get())
        {
            using (obj)
                map[Convert.ToInt32(obj["ProcessId"])] = Convert.ToInt32(obj["ParentProcessId"]);
        }

        return map;
    }
}

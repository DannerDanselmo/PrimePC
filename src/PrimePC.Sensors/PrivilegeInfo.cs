using System.Security.Principal;
using Microsoft.Win32;

namespace PrimePC.Sensors;

/// <summary>Informações sobre permissões e componentes de baixo nível que afetam o acesso a sensores.</summary>
public static class PrivilegeInfo
{
    public static bool IsAdministrator
    {
        get
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// O PawnIO é o driver assinado usado pelo LibreHardwareMonitor para ler sensores de baixo nível
    /// (temperatura da CPU, chip Super I/O da placa-mãe, SMBus da RAM). Ele é instalado separadamente
    /// pelo usuário; o PrimePC nunca instala drivers silenciosamente.
    /// </summary>
    public static bool IsPawnIoInstalled
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
                return key is not null;
            }
            catch
            {
                return false;
            }
        }
    }

    public const string PawnIoWebsite = "https://pawnio.eu/";
    public const string PawnIoReleases = "https://github.com/namazso/PawnIO.Setup/releases";
}

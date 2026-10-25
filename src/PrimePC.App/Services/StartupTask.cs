using System.Diagnostics;
using System.Security;
using System.Security.Principal;

namespace PrimePC.App.Services;

/// <summary>
/// Iniciar com o Windows. Como o PrimePC precisa de administrador para todos os sensores, a chave "Executar" do registro
/// não serve (o Windows não eleva programas da inicialização); usa uma tarefa agendada com "privilégios mais altos",
/// disparada no logon do usuário, que abre o PrimePC perto do relógio. Criar ou remover exige administrador.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "PrimePC";

    /// <summary>Tarefa criada pelo nome anterior do programa (PulsoPC), que apontava para o executável antigo.</summary>
    private const string LegacyTaskName = "Pulso" + "PC";

    public static bool Exists() => Run($"/Query /TN \"{TaskName}\"").ExitCode == 0;

    /// <summary>
    /// Troca a tarefa do PulsoPC pela do PrimePC (o executável antigo foi removido na atualização). Exige administrador;
    /// sem ele, não faz nada e tenta de novo na próxima vez.
    /// </summary>
    public static string? MigrateLegacy(bool startWithWindows)
    {
        if (!Sensors.PrivilegeInfo.IsAdministrator || Run($"/Query /TN \"{LegacyTaskName}\"").ExitCode != 0)
            return null;
        Run($"/Delete /TN \"{LegacyTaskName}\" /F");
        if (!startWithWindows || Exists())
            return "Tarefa de inicialização antiga (PulsoPC) removida.";
        var (ok, message) = Create();
        return ok ? "Tarefa de inicialização do PulsoPC trocada pela do PrimePC." : message;
    }

    public static (bool Ok, string Message) Create()
    {
        if (!Sensors.PrivilegeInfo.IsAdministrator)
            return (false, "Para iniciar com o Windows, abra o PrimePC como administrador e marque a opção de novo.");
        string user = WindowsIdentity.GetCurrent().Name;
        string exe = Environment.ProcessPath!;
        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Abre o PrimePC perto do relógio ao entrar no Windows (sensores, sobreposição e sessões de jogo).</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId><Delay>PT15S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exe)}</Command><Arguments>--bandeja</Arguments></Exec></Actions>
            </Task>
            """;
        string file = Path.Combine(Path.GetTempPath(), "PrimePC-tarefa.xml");
        try
        {
            File.WriteAllText(file, xml, System.Text.Encoding.Unicode);
            var r = Run($"/Create /TN \"{TaskName}\" /XML \"{file}\" /F");
            return r.ExitCode == 0
                ? (true, "O PrimePC vai abrir perto do relógio sempre que você entrar no Windows.")
                : (false, $"O Windows não criou a tarefa de inicialização: {r.Output.Trim()}");
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // arquivo temporário
            }
        }
    }

    public static (bool Ok, string Message) Delete()
    {
        if (!Exists())
            return (true, "O PrimePC não inicia com o Windows.");
        var r = Run($"/Delete /TN \"{TaskName}\" /F");
        return r.ExitCode == 0
            ? (true, "O PrimePC não inicia mais com o Windows.")
            : (false, $"Não foi possível remover a tarefa de inicialização: {r.Output.Trim()}");
    }

    private static (int ExitCode, string Output) Run(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            return (p.ExitCode, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}

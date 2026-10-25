using System.Management;

namespace PrimePC.Drivers;

/// <summary>
/// Cria um ponto de restauração do sistema antes de instalar drivers (opcional, escolhido pelo usuário), para poder voltar
/// atrás em Painel de Controle › Recuperação. Usa a API do próprio Windows (WMI SystemRestore) e exige administrador.
/// </summary>
public static class RestorePoint
{
    private const uint DeviceDriverInstall = 10;
    private const uint BeginSystemChange = 100;

    public static RestorePointResult Create(string description)
    {
        try
        {
            var before = Latest();
            using var restore = new ManagementClass(new ManagementScope(@"\\.\root\default"), new ManagementPath("SystemRestore"), null);
            using var input = restore.GetMethodParameters("CreateRestorePoint");
            input["Description"] = description;
            input["RestorePointType"] = DeviceDriverInstall;
            input["EventType"] = BeginSystemChange;
            using var output = restore.InvokeMethod("CreateRestorePoint", input, null);
            uint code = Convert.ToUInt32(output?["ReturnValue"] ?? 1u);
            if (code != 0)
            {
                return new RestorePointResult(false, false, code == 1058
                    ? "A Proteção do Sistema (pontos de restauração) está desativada neste computador."
                    : $"O Windows não criou o ponto de restauração (código {code}). A Proteção do Sistema pode estar desativada.");
            }

            var after = Latest();
            if (after is not null && (before is null || after.Value.Sequence > before.Value.Sequence))
                return new RestorePointResult(true, true, $"Ponto de restauração criado ({after.Value.Created:dd/MM/yyyy HH:mm}).");

            // O Windows cria no máximo um ponto a cada 24 horas por esta API; o ponto recente continua valendo para voltar atrás.
            if (before is { } recent && DateTime.Now - recent.Created < TimeSpan.FromHours(24))
            {
                return new RestorePointResult(false, true,
                    $"O Windows não criou um ponto novo porque já existe um recente ({recent.Created:dd/MM/yyyy HH:mm}); ele permite no máximo um a cada 24 horas. Esse ponto pode ser usado para voltar atrás.");
            }

            return new RestorePointResult(false, false, "O Windows não confirmou a criação do ponto de restauração. A Proteção do Sistema pode estar desativada.");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            return new RestorePointResult(false, false, "Criar pontos de restauração exige abrir o PrimePC como administrador.");
        }
        catch (Exception ex)
        {
            return new RestorePointResult(false, false, $"Não foi possível criar o ponto de restauração: {ex.Message}");
        }
    }

    private static (uint Sequence, DateTime Created)? Latest()
    {
        (uint, DateTime)? latest = null;
        using var searcher = new ManagementObjectSearcher(new ManagementScope(@"\\.\root\default"), new ObjectQuery("SELECT SequenceNumber, CreationTime FROM SystemRestore"));
        foreach (var obj in searcher.Get())
        {
            using (obj)
            {
                uint seq = Convert.ToUInt32(obj["SequenceNumber"] ?? 0u);
                if (latest is null || seq > latest.Value.Item1)
                    latest = (seq, ManagementDateTimeConverter.ToDateTime(obj["CreationTime"]?.ToString() ?? ""));
            }
        }

        return latest;
    }
}

namespace PrimePC.Drivers;

/// <summary>Classifica cada driver e associa as atualizações do Windows Update aos dispositivos instalados.</summary>
public static class DriverEvaluator
{
    /// <summary>Texto do código de problema do Gerenciador de Dispositivos (CM_PROB_*).</summary>
    public static string ProblemText(int code) => code switch
    {
        1 => "O dispositivo não está configurado corretamente (código 1).",
        3 => "O driver pode estar corrompido ou o sistema está com pouca memória (código 3).",
        10 => "O dispositivo não pode ser iniciado (código 10).",
        12 => "Não há recursos livres suficientes para o dispositivo (código 12).",
        14 => "É necessário reiniciar o computador para o dispositivo funcionar (código 14).",
        18 => "Os drivers deste dispositivo precisam ser reinstalados (código 18).",
        19 => "Informações de configuração do dispositivo incompletas ou corrompidas no registro (código 19).",
        21 => "O Windows está removendo este dispositivo (código 21).",
        22 => "O dispositivo está desativado (código 22).",
        24 => "O dispositivo não está presente ou não está funcionando corretamente (código 24).",
        28 => "Os drivers deste dispositivo não estão instalados (código 28).",
        29 => "O dispositivo foi desativado pelo firmware (código 29).",
        31 => "O dispositivo não está funcionando corretamente; o Windows não conseguiu carregar o driver (código 31).",
        32 => "O serviço do driver deste dispositivo está desativado (código 32).",
        37 => "O driver do dispositivo falhou ao iniciar (código 37).",
        39 => "O driver está corrompido ou ausente (código 39).",
        43 => "O Windows parou este dispositivo porque ele relatou problemas (código 43).",
        45 => "O dispositivo não está conectado no momento (código 45).",
        47 => "O dispositivo foi preparado para remoção segura (código 47).",
        48 => "O driver foi bloqueado por problemas de compatibilidade (código 48).",
        52 => "A assinatura digital do driver não pôde ser verificada (código 52).",
        _ => $"O dispositivo informou o código de problema {code}.",
    };

    private static readonly HashSet<int> InformationalCodes = new() { 14, 22, 29, 45, 47 };

    public static DriverAssessment Assess(InstalledDriver d, DriverUpdate? update)
    {
        if (d.ProblemCode != 0)
        {
            string text = ProblemText(d.ProblemCode);
            if (d.IsSoftwareDevice)
            {
                return new DriverAssessment(DriverStatus.Warning, "Atenção",
                    text + " É um componente de software do Windows (não um hardware); normalmente é instalado pelo Windows Update e não afeta o funcionamento do PC.");
            }

            if (InformationalCodes.Contains(d.ProblemCode))
                return new DriverAssessment(DriverStatus.Warning, d.ProblemCode == 22 ? "Desativado" : "Atenção", text);

            return new DriverAssessment(DriverStatus.Problem, "Problema",
                text + " Abra o Gerenciador de Dispositivos para ver detalhes ou reinstalar o driver.");
        }

        if (update is not null)
        {
            string date = update.Date is { } dt ? $" de {dt:dd/MM/yyyy}" : "";
            return new DriverAssessment(DriverStatus.UpdateAvailable, "Atualização disponível",
                $"O Windows Update oferece um driver mais recente{date} para este dispositivo: {update.Title}.");
        }

        if (d.IsSigned == false)
        {
            return new DriverAssessment(DriverStatus.Warning, "Sem assinatura",
                "O driver não tem assinatura digital válida. Prefira drivers assinados, obtidos do fabricante ou do Windows Update.");
        }

        return new DriverAssessment(DriverStatus.Ok, "OK", d.HasDriver ? "Nenhum problema informado pelo Windows." : "Dispositivo sem driver próprio.");
    }

    /// <summary>
    /// Associa cada atualização do Windows Update ao dispositivo instalado com o mesmo identificador de hardware.
    /// Retorna as associações e as atualizações que não correspondem a nenhum dispositivo listado.
    /// </summary>
    public static (Dictionary<string, DriverUpdate> ByDevice, List<DriverUpdate> Unmatched) Match(
        IReadOnlyList<InstalledDriver> drivers, IReadOnlyList<DriverUpdate> updates)
    {
        var byDevice = new Dictionary<string, DriverUpdate>(StringComparer.OrdinalIgnoreCase);
        var unmatched = new List<DriverUpdate>();
        foreach (var u in updates)
        {
            var matches = u.HardwareId is null
                ? new List<InstalledDriver>()
                : drivers.Where(d => d.HardwareIds.Any(h => h.Equals(u.HardwareId, StringComparison.OrdinalIgnoreCase))).ToList();
            if (matches.Count == 0)
            {
                unmatched.Add(u);
                continue;
            }

            foreach (var d in matches)
                byDevice[d.DeviceId] = u;
        }

        return (byDevice, unmatched);
    }
}

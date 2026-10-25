namespace PrimePC.Drivers;

/// <summary>
/// Baixa e instala, pelo Windows Update Agent, as atualizações de driver escolhidas pelo usuário. É o mesmo caminho de
/// Configurações › Windows Update › Atualizações opcionais: só instala drivers que a Microsoft oferece para este hardware.
/// Exige administrador e só é chamado depois da confirmação do usuário.
/// </summary>
public static class WindowsUpdateDriverInstaller
{
    public static async Task<IReadOnlyList<DriverInstallResult>> InstallAsync(
        IReadOnlyList<DriverUpdate> updates, IProgress<DriverInstallProgress>? progress, CancellationToken token)
    {
        // O Windows Update Agent é síncrono; roda numa tarefa própria e o cancelamento vale entre uma atualização e outra.
        return await Task.Run(() => Install(updates, progress, token), CancellationToken.None).ConfigureAwait(false);
    }

    private static List<DriverInstallResult> Install(IReadOnlyList<DriverUpdate> wanted, IProgress<DriverInstallProgress>? progress, CancellationToken token)
    {
        var results = new List<DriverInstallResult>();
        dynamic session;
        Dictionary<string, dynamic> available;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                       ?? throw new InvalidOperationException("o serviço Windows Update não está disponível neste sistema");
            session = Activator.CreateInstance(type)!;
            session.ClientApplicationID = "PrimePC";
            progress?.Report(new DriverInstallProgress(0, wanted.Count, "", "Consultando o Windows Update…"));

            // Os objetos de atualização não sobrevivem entre consultas: busca de novo e pega as escolhidas pelo identificador.
            dynamic result = session.CreateUpdateSearcher().Search(WindowsUpdateDriverSearch.Criteria);
            available = new Dictionary<string, dynamic>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (int)result.Updates.Count; i++)
            {
                dynamic u = result.Updates[i];
                available[(string)u.Identity.UpdateID] = u;
            }
        }
        catch (Exception ex)
        {
            string message = $"Não foi possível consultar o Windows Update: {Explain(ex)}";
            return wanted.Select(w => new DriverInstallResult(w.Title, false, false, message)).ToList();
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (token.IsCancellationRequested)
            {
                results.Add(new DriverInstallResult(w.Title, false, false, "Cancelado antes de começar."));
                continue;
            }

            if (w.UpdateId is null || !available.TryGetValue(w.UpdateId, out var update))
            {
                results.Add(new DriverInstallResult(w.Title, false, false,
                    "O Windows Update não oferece mais esta atualização (talvez já tenha sido instalada). Procure atualizações de novo."));
                continue;
            }

            if (update.EulaAccepted == false)
            {
                results.Add(new DriverInstallResult(w.Title, false, false,
                    "Esta atualização exige aceitar termos de licença. Instale-a por Configurações › Windows Update › Atualizações opcionais."));
                continue;
            }

            try
            {
                dynamic collection = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
                collection.Add(update);

                progress?.Report(new DriverInstallProgress(i + 1, wanted.Count, w.Title, "Baixando"));
                dynamic downloader = session.CreateUpdateDownloader();
                downloader.Updates = collection;
                dynamic download = downloader.Download();
                int downloadCode = (int)download.ResultCode;
                if (downloadCode is not (2 or 3) || update.IsDownloaded == false)
                {
                    results.Add(new DriverInstallResult(w.Title, false, false,
                        $"O download falhou ({ResultText(downloadCode)}, {HResultText((int)download.HResult)})."));
                    continue;
                }

                progress?.Report(new DriverInstallProgress(i + 1, wanted.Count, w.Title, "Instalando"));
                dynamic installer = session.CreateUpdateInstaller();
                installer.AllowSourcePrompts = false;
                installer.Updates = collection;
                dynamic install = installer.Install();
                int code = (int)install.ResultCode;
                bool reboot = (bool)install.RebootRequired;
                int hresult = (int)install.GetUpdateResult(0).HResult;
                bool ok = code is 2 or 3;
                string message = ok
                    ? reboot ? "Instalado. Reinicie o computador para concluir." : "Instalado com sucesso."
                    : $"A instalação falhou ({ResultText(code)}, {HResultText(hresult)}).";
                results.Add(new DriverInstallResult(w.Title, ok, reboot, message));
            }
            catch (Exception ex)
            {
                results.Add(new DriverInstallResult(w.Title, false, false, $"Erro: {Explain(ex)}"));
            }
        }

        return results;
    }

    /// <summary>Texto de OperationResultCode do Windows Update Agent.</summary>
    public static string ResultText(int code) => code switch
    {
        0 => "não iniciado",
        1 => "em andamento",
        2 => "concluído",
        3 => "concluído com erros",
        4 => "falhou",
        5 => "interrompido",
        _ => $"código {code}",
    };

    /// <summary>Explica os códigos de erro mais comuns do Windows Update.</summary>
    public static string HResultText(int hresult) => unchecked((uint)hresult) switch
    {
        0 => "sem código de erro",
        0x80070005 => "acesso negado: abra o PrimePC como administrador",
        0x80240016 => "outra instalação do Windows Update está em andamento; tente de novo em alguns minutos",
        0x8024001E => "o serviço Windows Update foi interrompido",
        0x80240022 => "todas as atualizações da lista falharam",
        0x8024402C or 0x80072EE7 or 0x80072EFD or 0x80072EE2 => "sem conexão com os servidores do Windows Update",
        0x80070422 => "o serviço Windows Update está desativado",
        0x800F0922 or 0x800F0247 => "o driver não pôde ser instalado neste sistema",
        0x80240017 => "a atualização não se aplica a este computador",
        var h => $"0x{h:X8}",
    };

    private static string Explain(Exception ex) => ex.HResult != 0 && unchecked((uint)ex.HResult) >= 0x80000000
        ? $"{HResultText(ex.HResult)} ({ex.Message.TrimEnd('.')})"
        : ex.Message;
}

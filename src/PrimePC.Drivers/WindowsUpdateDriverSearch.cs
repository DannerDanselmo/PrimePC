using System.Globalization;

namespace PrimePC.Drivers;

/// <summary>
/// Consulta o Windows Update (Windows Update Agent, a mesma API usada pelas Configurações do Windows) para saber
/// quais drivers têm versão mais recente disponível para este computador. Apenas consulta: a instalação fica em
/// <see cref="WindowsUpdateDriverInstaller"/> e só acontece quando o usuário pede.
/// </summary>
public static class WindowsUpdateDriverSearch
{
    public const string Criteria = "IsInstalled=0 and Type='Driver' and IsHidden=0";

    public static async Task<DriverUpdateSearch> SearchAsync(CancellationToken token)
    {
        var search = Task.Run(Search, CancellationToken.None);
        var finished = await Task.WhenAny(search, Task.Delay(Timeout.Infinite, token)).ConfigureAwait(false);
        if (finished != search)
            return new DriverUpdateSearch(DateTime.Now, false, Array.Empty<DriverUpdate>(), "Busca cancelada.");
        return await search.ConfigureAwait(false);
    }

    private static DriverUpdateSearch Search()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session")
                       ?? throw new InvalidOperationException("o serviço Windows Update não está disponível neste sistema");
            dynamic session = Activator.CreateInstance(type)!;
            session.ClientApplicationID = "PrimePC";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search(Criteria);
            dynamic collection = result.Updates;
            int count = collection.Count;
            var list = new List<DriverUpdate>(count);
            for (int i = 0; i < count; i++)
            {
                dynamic u = collection[i];
                list.Add(new DriverUpdate(
                    Title: (string)u.Title,
                    DriverClass: S(u.DriverClass),
                    HardwareId: S(u.DriverHardwareID),
                    Manufacturer: S(u.DriverManufacturer),
                    Model: S(u.DriverModel),
                    Provider: S(u.DriverProvider),
                    Date: D(u.DriverVerDate),
                    SizeBytes: L(u.MaxDownloadSize),
                    SupportUrl: S(u.SupportUrl),
                    UpdateId: S(u.Identity.UpdateID),
                    RequiresEula: u.EulaAccepted == false));
            }

            string message = list.Count == 0
                ? "Nenhuma atualização de driver disponível no Windows Update para este computador."
                : $"{list.Count} atualização(ões) de driver disponível(is) no Windows Update.";
            return new DriverUpdateSearch(DateTime.Now, true, list, message);
        }
        catch (Exception ex)
        {
            return new DriverUpdateSearch(DateTime.Now, false, Array.Empty<DriverUpdate>(),
                $"Não foi possível consultar o Windows Update: {ex.Message}. Verifique a conexão com a internet e se o serviço Windows Update está ativo.");
        }
    }

    private static string? S(object? v) => v is null ? null : string.IsNullOrWhiteSpace(v.ToString()) ? null : v.ToString();

    private static DateTime? D(object? v) => v is DateTime dt && dt.Year > 1601 ? dt : null;

    private static long L(object? v)
    {
        try
        {
            return v is null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }
}

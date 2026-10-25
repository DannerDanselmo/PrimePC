namespace PrimePC.App.Services;

/// <summary>Locais de dados do aplicativo. Tudo fica no computador do usuário (%LOCALAPPDATA%\PrimePC).</summary>
public static class AppPaths
{
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrimePC");

    public static string SettingsFile => Path.Combine(DataRoot, "configuracoes.json");
    public static string Sessions => Path.Combine(DataRoot, "sessoes-gargalo");
    public static string Results => Path.Combine(DataRoot, "resultados");
    public static string Logs => Path.Combine(DataRoot, "logs");

    /// <summary>Instaladores de driver baixados a pedido do usuário (apagados depois de instalados).</summary>
    public static string Downloads => Path.Combine(DataRoot, "downloads");

    /// <summary>Instalação de driver de vídeo à espera de conferência (o PrimePC reinicia depois do instalador).</summary>
    public static string PendingDriverCheck => Path.Combine(DataRoot, "verificar-driver.json");

    public static string DefaultExportFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PrimePC");

    /// <summary>Pasta de dados do nome anterior do programa (PulsoPC, até a versão 1.5).</summary>
    public static string LegacyDataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pulso" + "PC");

    /// <summary>
    /// Na primeira execução como PrimePC, traz configurações, histórico e resultados da pasta antiga (PulsoPC).
    /// Move a pasta inteira; se algum arquivo estiver em uso, copia o que der e deixa a antiga como está.
    /// </summary>
    public static string? MigrateLegacyData()
    {
        try
        {
            if (Directory.Exists(DataRoot) || !Directory.Exists(LegacyDataRoot))
                return null;
            try
            {
                Directory.Move(LegacyDataRoot, DataRoot);
                return "Configurações e histórico do PulsoPC movidos para a pasta do PrimePC.";
            }
            catch (IOException)
            {
                CopyTree(LegacyDataRoot, DataRoot);
                return "Configurações e histórico do PulsoPC copiados para a pasta do PrimePC.";
            }
        }
        catch (Exception ex)
        {
            return $"Não foi possível trazer os dados do PulsoPC: {ex.Message}";
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            try
            {
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
            }
            catch (IOException)
            {
                // arquivo em uso (ex.: log aberto): não é essencial
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}

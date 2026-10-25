using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace PrimePC.App.Services;

/// <summary>Interações com o sistema: elevação, abrir pastas/links e diálogos de confirmação.</summary>
public static class Shell
{
    /// <summary>Reabre o PrimePC como administrador (o Windows mostra o UAC). Retorna falso se o usuário recusar.</summary>
    public static bool RestartElevated()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>Abre uma nova instância do PrimePC com os mesmos privilégios (sem novo UAC). Retorna falso se falhar.</summary>
    public static bool StartNewInstance(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, arguments) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao reabrir o PrimePC", ex);
            return false;
        }
    }

    public static void Open(string pathOrUrl)
    {
        try
        {
            Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Falha ao abrir {pathOrUrl}", ex);
            Error($"Não foi possível abrir \"{pathOrUrl}\": {ex.Message}");
        }
    }

    public static void ShowInExplorer(string file)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Falha ao abrir o Explorer", ex);
        }
    }

    public static bool Confirm(string message, string title = "PrimePC") =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public static void Info(string message, string title = "PrimePC") =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public static void Error(string message, string title = "PrimePC") =>
        MessageBox.Show(Application.Current.MainWindow!, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public static string? SaveFile(string title, string filter, string defaultName)
    {
        Directory.CreateDirectory(AppPaths.DefaultExportFolder);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = defaultName,
            InitialDirectory = AppPaths.DefaultExportFolder,
            AddExtension = true,
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(Application.Current.MainWindow) == true ? dialog.FileName : null;
    }
}

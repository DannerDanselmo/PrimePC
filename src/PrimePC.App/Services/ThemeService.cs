using System.Windows;
using Microsoft.Win32;

namespace PrimePC.App.Services;

/// <summary>
/// Alterna entre claro e escuro: aplica o tema Fluent nativo do WPF (controles) e a paleta própria do
/// PrimePC (cartões, estados, gráficos). No modo "Sistema", acompanha a preferência do Windows.
/// </summary>
public static class ThemeService
{
    private const string PaletteMarker = "/Themes/Palette.";
    private static ThemeChoice _choice = ThemeChoice.System;
    private static bool _listening;

    public static bool IsDark { get; private set; }

    public static event EventHandler? ThemeChanged;

    public static void Apply(ThemeChoice choice)
    {
        _choice = choice;
        bool dark = choice == ThemeChoice.Dark || (choice == ThemeChoice.System && SystemPrefersDark());
        var app = Application.Current;

        app.ThemeMode = dark ? ThemeMode.Dark : ThemeMode.Light;

        var dicts = app.Resources.MergedDictionaries;
        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/PrimePC;component/Themes/Palette.{(dark ? "Dark" : "Light")}.xaml"),
        };
        // A paleta fica sempre por último: assim as cores de destaque dela substituem as do tema Fluent
        // (que, por padrão, segue a cor de destaque do Windows).
        var existing = dicts.FirstOrDefault(d => d.Source?.OriginalString.Contains(PaletteMarker, StringComparison.Ordinal) == true);
        if (existing is not null)
            dicts.Remove(existing);
        dicts.Add(palette);

        IsDark = dark;
        if (!_listening)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _choice == ThemeChoice.System)
                    app.Dispatcher.BeginInvoke(() => Apply(ThemeChoice.System));
            };
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }
}

using System.Windows;
using Forms = System.Windows.Forms;

namespace PrimePC.App.Services;

/// <summary>Ícone perto do relógio: abre o PrimePC, liga/desliga a sobreposição, inicia o benchmark e sai.</summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _overlayItem;
    private readonly Forms.ToolStripMenuItem _benchmarkItem;
    private readonly Func<bool> _overlayOn;
    private readonly Func<bool> _benchmarkRunning;

    public TrayService(Action open, Action toggleOverlay, Func<bool> overlayOn, Action toggleBenchmark, Func<bool> benchmarkRunning, Action exit)
    {
        _overlayOn = overlayOn;
        _benchmarkRunning = benchmarkRunning;
        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Abrir o PrimePC", null, (_, _) => open()) { Font = new System.Drawing.Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) };
        _overlayItem = new Forms.ToolStripMenuItem("Sobreposição nos jogos", null, (_, _) => toggleOverlay());
        _benchmarkItem = new Forms.ToolStripMenuItem("Iniciar benchmark de jogo", null, (_, _) => toggleBenchmark());
        menu.Items.AddRange(new Forms.ToolStripItem[] { openItem, _overlayItem, _benchmarkItem, new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem("Sair do PrimePC", null, (_, _) => exit()) });
        menu.Opening += (_, _) =>
        {
            _overlayItem.Checked = _overlayOn();
            _benchmarkItem.Text = _benchmarkRunning() ? "Parar benchmark de jogo" : "Iniciar benchmark de jogo";
        };

        _icon = new Forms.NotifyIcon
        {
            Text = "PrimePC",
            ContextMenuStrip = menu,
            Icon = LoadIcon(),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                open();
        };
    }

    public void ShowBalloon(string title, string text) => _icon.ShowBalloonTip(4000, title, text, Forms.ToolTipIcon.Info);

    private static System.Drawing.Icon LoadIcon()
    {
        var info = Application.GetResourceStream(new Uri("pack://application:,,,/PrimePC;component/Assets/PrimePC.ico"));
        using var stream = info!.Stream;
        return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}

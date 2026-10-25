using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PrimePC.App.Services;
using PrimePC.Gaming;

namespace PrimePC.App.Views;

/// <summary>
/// Janela transparente, sempre por cima, que deixa os cliques passarem para o jogo e nunca recebe o foco.
/// É uma janela comum do Windows: nada é injetado no jogo (por isso não interfere com anti-cheat), mas não aparece
/// sobre jogos em tela cheia exclusiva — só em janela ou tela cheia sem bordas.
/// </summary>
public partial class OverlayWindow : Window
{
    private IntPtr _hwnd;

    public OverlayWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        int style = GetWindowLong(_hwnd, GwlExStyle);
        SetWindowLong(_hwnd, GwlExStyle, style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate);
    }

    /// <summary>Esconde (ou não) a sobreposição de capturas de tela, gravações e transmissões.</summary>
    public void SetHideFromCapture(bool hide)
    {
        if (_hwnd != IntPtr.Zero)
            SetWindowDisplayAffinity(_hwnd, hide ? WdaExcludeFromCapture : WdaNone);
    }

    /// <summary>Mostra no canto escolhido do monitor onde está o jogo (a janela em primeiro plano) e reafirma "por cima".</summary>
    public void Place(OverlayCorner corner)
    {
        if (!IsVisible)
            Show();
        if (_hwnd == IntPtr.Zero)
            return;

        var monitor = MonitorFromWindow(ForegroundProcess.GetWindow(), MonitorDefaultToPrimary);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(_hwnd, out var r))
            return;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        int margin = (int)Math.Round(14 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var m = info.Monitor;
        int x = corner is OverlayCorner.TopLeft or OverlayCorner.BottomLeft ? m.Left + margin : m.Right - width - margin;
        int y = corner is OverlayCorner.TopLeft or OverlayCorner.TopRight ? m.Top + margin : m.Bottom - height - margin;
        SetWindowPos(_hwnd, HwndTopmost, x, y, 0, 0, SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    // ---------------- Win32 ----------------

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExToolWindow = 0x80;
    private const int WsExLayered = 0x80000;
    private const int WsExNoActivate = 0x08000000;
    private const uint WdaNone = 0;
    private const uint WdaExcludeFromCapture = 0x11;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint SwpNoSize = 0x1, SwpNoActivate = 0x10, SwpNoOwnerZOrder = 0x200;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}

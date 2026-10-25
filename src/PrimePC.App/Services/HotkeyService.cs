using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace PrimePC.App.Services;

/// <summary>Atalhos de teclado globais (funcionam dentro do jogo e com o PrimePC minimizado).</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private readonly HwndSource _sink;
    private readonly Dictionary<int, (OverlayHotkey Key, Action Action)> _registered = new();

    public HotkeyService()
    {
        // Janela invisível só para mensagens.
        _sink = new HwndSource(new HwndSourceParameters("PrimePC-Atalhos") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _sink.AddHook(Hook);
    }

    public static string Text(OverlayHotkey h) => h switch
    {
        OverlayHotkey.CtrlShiftF9 => "Ctrl + Shift + F9",
        OverlayHotkey.CtrlShiftF10 => "Ctrl + Shift + F10",
        OverlayHotkey.CtrlShiftF12 => "Ctrl + Shift + F12",
        OverlayHotkey.CtrlShiftB => "Ctrl + Shift + B",
        OverlayHotkey.CtrlShiftF11 => "Ctrl + Shift + F11",
        OverlayHotkey.CtrlShiftO => "Ctrl + Shift + O",
        OverlayHotkey.AltShiftO => "Alt + Shift + O",
        _ => "Nenhum",
    };

    /// <summary>
    /// Registra (ou troca) o atalho de um identificador. Retorna falso se outro programa já usa a combinação; nesse caso
    /// tenta de novo uma vez após 3 s (ao reabrir o PrimePC, a instância anterior pode ainda estar fechando).
    /// </summary>
    public bool Register(int id, OverlayHotkey key, Action action, Action<bool>? retried = null)
    {
        UnregisterHotKey(_sink.Handle, id);
        _registered.Remove(id);
        if (key == OverlayHotkey.None)
            return true;
        var (mods, vk) = key switch
        {
            OverlayHotkey.CtrlShiftF9 => (ModControl | ModShift, 0x78u),
            OverlayHotkey.CtrlShiftF10 => (ModControl | ModShift, 0x79u),
            OverlayHotkey.CtrlShiftF12 => (ModControl | ModShift, 0x7Bu),
            OverlayHotkey.CtrlShiftB => (ModControl | ModShift, 0x42u),
            OverlayHotkey.CtrlShiftF11 => (ModControl | ModShift, 0x7Au),
            OverlayHotkey.CtrlShiftO => (ModControl | ModShift, 0x4Fu),
            _ => (ModAlt | ModShift, 0x4Fu),
        };
        bool ok = RegisterHotKey(_sink.Handle, id, mods | ModNoRepeat, vk);
        if (ok)
        {
            _registered[id] = (key, action);
        }
        else if (retried is not null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                bool again = RegisterHotKey(_sink.Handle, id, mods | ModNoRepeat, vk);
                if (again)
                    _registered[id] = (key, action);
                retried(again);
            };
            timer.Start();
        }

        return ok;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var entry))
        {
            handled = true;
            try
            {
                entry.Action();
            }
            catch (Exception ex)
            {
                Log.Error("Falha ao executar atalho", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys.ToList())
            UnregisterHotKey(_sink.Handle, id);
        _sink.Dispose();
    }

    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RazerLite;

/// <summary>
/// Ctrl+Alt+1..5 global kisayollari. Pencere gizliyken de calisir.
/// </summary>
internal sealed class HotKeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const int BaseId = 0x5A10;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Action<int> _onProfile;
    private readonly List<int> _registered = new();

    public HotKeys(Window window, Action<int> onProfile)
    {
        _onProfile = onProfile;
        // Pencere henuz gosterilmemis olsa bile (tepside baslatma) HWND olustur.
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(hwnd)
            ?? throw new InvalidOperationException("Pencere HWND kaynagi alinamadi.");
        _source.AddHook(Hook);
    }

    public static string Label(int slot) => $"Ctrl+Alt+{slot}";

    /// <summary>Slot 1..5 icin kayit; basarisiz olanlarin listesini dondurur.</summary>
    public List<int> RegisterAll(int count)
    {
        UnregisterAll();
        var failed = new List<int>();
        for (int slot = 1; slot <= count; slot++)
        {
            uint vk = (uint)('0' + slot);
            if (RegisterHotKey(_source.Handle, BaseId + slot, ModControl | ModAlt | ModNoRepeat, vk))
            {
                _registered.Add(slot);
            }
            else
            {
                failed.Add(slot);
            }
        }

        return failed;
    }

    public void UnregisterAll()
    {
        foreach (var slot in _registered)
        {
            UnregisterHotKey(_source.Handle, BaseId + slot);
        }

        _registered.Clear();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            int slot = wParam.ToInt32() - BaseId;
            if (slot >= 1 && slot <= Settings.MaxProfiles)
            {
                _onProfile(slot);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(Hook);
    }
}

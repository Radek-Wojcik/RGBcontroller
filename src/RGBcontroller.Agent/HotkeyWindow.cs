using System.Runtime.InteropServices;
using RGBcontroller.Core.Config;
using RGBcontroller.Core.Hotkeys;
using Serilog;

namespace RGBcontroller.Agent;

/// <summary>
/// Global hotkeys through RegisterHotKey on a message-only window (SPEC 7.2): Windows delivers WM_HOTKEY, so there
/// is no keyboard hook and no CPU cost. A combination another program already owns cannot be registered; it is
/// reported and skipped.
/// </summary>
internal sealed partial class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr MessageOnlyParent = new(-3);

    private readonly ILogger _log;
    private readonly Action<HotkeyBinding> _pressed;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];

    public HotkeyWindow(Action<HotkeyBinding> pressed, ILogger log)
    {
        _pressed = pressed;
        _log = log.ForContext<HotkeyWindow>();
        CreateHandle(new CreateParams { Parent = MessageOnlyParent });
    }

    /// <summary>Replaces all hotkeys.</summary>
    /// <returns>Combinations that could not be registered (taken by another program, or invalid).</returns>
    public IReadOnlyList<string> Register(IReadOnlyList<HotkeyBinding> bindings)
    {
        UnregisterAll();
        var failed = new List<string>();
        var id = 1;
        foreach (var binding in bindings)
        {
            if (!HotkeyGesture.TryParse(binding.Keys, out var gesture, out var error))
            {
                _log.Warning("Hotkey skipped: {Error}", error);
                failed.Add(binding.Keys);
                continue;
            }

            if (!RegisterHotKey(Handle, id, (uint)gesture.Modifier | ModNoRepeat, (uint)gesture.VirtualKey))
            {
                _log.Warning("Hotkey {Gesture} ({Action}) is used by another program (error {Error})", gesture, binding.Action, Marshal.GetLastPInvokeError());
                failed.Add(gesture.ToString());
                continue;
            }

            _registered[id++] = binding;
        }

        _log.Information("Hotkeys registered: {Hotkeys}", string.Join(", ", _registered.Values.Select(b => $"{b.Keys}={b.Action}{(b.Profile is null ? "" : $" {b.Profile}")}")));
        return failed;
    }

    public void Dispose()
    {
        UnregisterAll();
        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey && _registered.TryGetValue((int)m.WParam, out var binding))
        {
            _log.Information("Hotkey {Keys}: {Action}", binding.Keys, binding.Action);
            _pressed(binding);
            return;
        }

        base.WndProc(ref m);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys)
        {
            UnregisterHotKey(Handle, id);
        }

        _registered.Clear();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr window, int id);
}

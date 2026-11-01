using System.Globalization;

namespace RGBcontroller.Core.Hotkeys;

/// <summary>Modifier flags exactly as RegisterHotKey expects them (MOD_ALT, MOD_CONTROL, MOD_SHIFT, MOD_WIN).</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>
/// A global hotkey of exactly two keys (SPEC 7.2): one modifier plus one key, written like "Ctrl+F9". Parsing is
/// case-insensitive; <see cref="ToString"/> gives the canonical spelling.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifier, int VirtualKey, string KeyName)
{
    private static readonly Dictionary<string, (int VirtualKey, string Name)> NamedKeys = BuildKeyTable();

    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        var parts = (text ?? "").Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            error = $"'{text}' must be one modifier and one key, e.g. Ctrl+F9.";
            return false;
        }

        if (!Enum.TryParse<HotkeyModifiers>(parts[0], ignoreCase: true, out var modifier)
            || modifier is not (HotkeyModifiers.Alt or HotkeyModifiers.Ctrl or HotkeyModifiers.Shift or HotkeyModifiers.Win)
            || int.TryParse(parts[0], out _))
        {
            error = $"'{parts[0]}' in '{text}' is not a modifier (Ctrl, Alt, Shift or Win).";
            return false;
        }

        if (!NamedKeys.TryGetValue(parts[1], out var key))
        {
            error = $"'{parts[1]}' in '{text}' is not a supported key (A-Z, 0-9, F1-F24, Num0-Num9, arrows, PageUp, Pause...).";
            return false;
        }

        gesture = new HotkeyGesture(modifier, key.VirtualKey, key.Name);
        error = null;
        return true;
    }

    public override string ToString() => $"{Modifier}+{KeyName}";

    private static Dictionary<string, (int VirtualKey, string Name)> BuildKeyTable()
    {
        var codes = new Dictionary<string, int>();
        for (var c = 'A'; c <= 'Z'; c++)
        {
            codes[c.ToString()] = c;
        }

        for (var d = 0; d <= 9; d++)
        {
            codes[d.ToString(CultureInfo.InvariantCulture)] = '0' + d;
            codes[$"Num{d}"] = 0x60 + d;
        }

        for (var f = 1; f <= 24; f++)
        {
            codes[$"F{f}"] = 0x6F + f;
        }

        codes["Space"] = 0x20;
        codes["PageUp"] = 0x21;
        codes["PageDown"] = 0x22;
        codes["End"] = 0x23;
        codes["Home"] = 0x24;
        codes["Left"] = 0x25;
        codes["Up"] = 0x26;
        codes["Right"] = 0x27;
        codes["Down"] = 0x28;
        codes["Insert"] = 0x2D;
        codes["Delete"] = 0x2E;
        codes["Pause"] = 0x13;
        codes["ScrollLock"] = 0x91;
        codes["NumMultiply"] = 0x6A;
        codes["NumAdd"] = 0x6B;
        codes["NumSubtract"] = 0x6D;
        codes["NumDecimal"] = 0x6E;
        codes["NumDivide"] = 0x6F;
        return codes.ToDictionary(k => k.Key, k => (k.Value, k.Key), StringComparer.OrdinalIgnoreCase);
    }
}

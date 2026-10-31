using RGBcontroller.Core.OpenRgb;

namespace RGBcontroller.Core.Lighting;

/// <summary>Mode selection on OpenRGB devices.</summary>
public static class DeviceModes
{
    private const uint PerLedColors = 1;
    private const uint ModeSpecificColors = 2;

    /// <summary>Same choice as RGBController::SetCustomMode: Direct, then Custom, then Static, with per-LED or mode-specific colors.</summary>
    /// <returns>The mode index, or -1 when the device has no such mode.</returns>
    public static int FindPerLedMode(ControllerData device)
    {
        ArgumentNullException.ThrowIfNull(device);
        foreach (var name in (ReadOnlySpan<string>)["Direct", "Custom", "Static"])
        {
            for (var i = 0; i < device.Modes.Count; i++)
            {
                var mode = device.Modes[i];
                if (mode.Name == name && mode.ColorMode is PerLedColors or ModeSpecificColors)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <returns>The index of the mode called <paramref name="name"/> (case-insensitive), or -1.</returns>
    public static int FindByName(ControllerData device, string? name)
    {
        ArgumentNullException.ThrowIfNull(device);
        for (var i = 0; i < device.Modes.Count; i++)
        {
            if (string.Equals(device.Modes[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

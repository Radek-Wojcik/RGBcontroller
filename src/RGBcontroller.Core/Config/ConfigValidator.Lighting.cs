namespace RGBcontroller.Core.Config;

/// <summary>Checks for the <c>lighting</c> section: names are unique and every reference resolves.</summary>
public static partial class ConfigValidator
{
    public const double MaxEffectSpeed = 10;

    private static void ValidateLighting(List<string> errors, LightingConfig lighting)
    {
        foreach (var layout in lighting.Layouts)
        {
            var name = $"lighting.layouts '{layout.Device}/{layout.Zone}'";
            if (string.IsNullOrWhiteSpace(layout.Device) || string.IsNullOrWhiteSpace(layout.Zone))
            {
                errors.Add("lighting.layouts entries need a device and a zone name.");
            }

            if (layout.Segments.Count == 0)
            {
                errors.Add($"{name} needs at least one segment.");
            }

            foreach (var segment in layout.Segments)
            {
                if (segment.Leds is { } leds)
                {
                    CheckRange(errors, $"{name} segment leds", leds, 1, MaxStripLeds);
                }

                CheckRange(errors, $"{name} segment offset", segment.Offset, 0, MaxStripLeds);
                CheckEnum(errors, $"{name} segment shape", segment.Shape);
            }
        }

        var groupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in lighting.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name) || !groupNames.Add(group.Name))
            {
                errors.Add($"lighting.groups name '{group.Name}' is empty or used twice.");
            }

            CheckRange(errors, $"lighting.groups '{group.Name}' maxFps", group.MaxFps, 1, MaxStripFps);
            if (group.Members.Any(m => string.IsNullOrWhiteSpace(m.Device)))
            {
                errors.Add($"lighting.groups '{group.Name}' has a member without a device name.");
            }
        }

        if (lighting.DefaultGroup is not null && !groupNames.Contains(lighting.DefaultGroup))
        {
            errors.Add($"lighting.defaultGroup '{lighting.DefaultGroup}' is not a group.");
        }

        var paletteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var palette in lighting.Palettes)
        {
            var name = $"lighting.palettes '{palette.Name}'";
            if (string.IsNullOrWhiteSpace(palette.Name) || palette.Name.StartsWith('#') || !paletteNames.Add(palette.Name))
            {
                errors.Add($"{name}: the name is empty, starts with '#' or is used twice.");
            }

            if (palette.Stops.Count == 0)
            {
                errors.Add($"{name} needs at least one stop.");
            }

            foreach (var stop in palette.Stops)
            {
                if (!(stop.Position is >= 0 and <= 1))
                {
                    errors.Add($"{name} stop position {stop.Position} must be between 0 and 1.");
                }

                CheckColor(errors, $"{name} stop", stop.Color);
            }
        }

        var profileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in lighting.Profiles)
        {
            var name = $"lighting.profiles '{profile.Name}'";
            if (string.IsNullOrWhiteSpace(profile.Name) || !profileNames.Add(profile.Name))
            {
                errors.Add($"{name}: the name is empty or used twice.");
            }

            CheckRange(errors, $"{name} masterBrightnessPercent", profile.MasterBrightnessPercent, 0, 100);
            foreach (var effect in profile.Groups)
            {
                ValidateEffect(errors, $"{name} group '{effect.Group}'", effect, groupNames, paletteNames);
            }

            ValidateAmbilight(errors, $"{name} ambilight", profile.Ambilight, paletteNames);
        }

        CheckProfileReference(errors, "lighting.activeProfile", lighting.ActiveProfile, profileNames);
        CheckProfileReference(errors, "lighting.logonProfile", lighting.LogonProfile, profileNames);
        if (lighting.LockProfile is not null)
        {
            CheckProfileReference(errors, "lighting.lockProfile", lighting.LockProfile, profileNames);
        }
    }

    private static void ValidateEffect(
        List<string> errors, string name, GroupEffectConfig effect, HashSet<string> groupNames, HashSet<string> paletteNames)
    {
        if (!groupNames.Contains(effect.Group))
        {
            errors.Add($"{name} is not a group.");
        }

        CheckEnum(errors, $"{name} effect", effect.Effect);
        CheckEnum(errors, $"{name} radialMode", effect.RadialMode);
        CheckPaletteReference(errors, name, effect.Palette, paletteNames);
        CheckSpeed(errors, $"{name} speed", effect.Speed);
        if (!(effect.Scale is >= 0.01 and <= 100))
        {
            errors.Add($"{name} scale must be between 0.01 and 100, found {effect.Scale}.");
        }

        if (!(effect.Angle is >= -360 and <= 360))
        {
            errors.Add($"{name} angle must be between -360 and 360, found {effect.Angle}.");
        }

        CheckRange(errors, $"{name} brightnessPercent", effect.BrightnessPercent, 0, 100);
        if (effect.Effect == EffectKind.Hardware && string.IsNullOrWhiteSpace(effect.HardwareMode))
        {
            errors.Add($"{name} uses the Hardware effect but names no hardwareMode.");
        }
    }

    private static void ValidateAmbilight(List<string> errors, string name, AmbilightConfig strip, HashSet<string> paletteNames)
    {
        CheckEnum(errors, $"{name} mode", strip.Mode);
        CheckEnum(errors, $"{name} fallback", strip.Fallback);
        if (strip.Fallback == AmbilightMode.Screen)
        {
            errors.Add($"{name} fallback cannot be Screen.");
        }

        CheckRange(errors, $"{name} brightnessPercent", strip.BrightnessPercent, 0, 100);
        CheckRange(errors, $"{name} solidBrightnessPercent", strip.SolidBrightnessPercent, 0, 100);
        CheckColor(errors, $"{name} solidColor", strip.SolidColor);
        CheckPaletteReference(errors, name, strip.Palette, paletteNames);
        CheckSpeed(errors, $"{name} speed", strip.Speed);
    }

    private static void CheckProfileReference(List<string> errors, string name, string profile, HashSet<string> profileNames)
    {
        if (!profileNames.Contains(profile))
        {
            errors.Add($"{name} '{profile}' is not a profile.");
        }
    }

    private static void CheckPaletteReference(List<string> errors, string name, string palette, HashSet<string> paletteNames)
    {
        var valid = palette.StartsWith('#') ? RgbColor.TryParseHex(palette, out _) : paletteNames.Contains(palette);
        if (!valid)
        {
            errors.Add($"{name} palette '{palette}' is neither a palette name nor #RRGGBB.");
        }
    }

    private static void CheckSpeed(List<string> errors, string name, double speed)
    {
        if (!(speed is >= 0 and <= MaxEffectSpeed))
        {
            errors.Add($"{name} must be between 0 and {MaxEffectSpeed} cycles per second, found {speed}.");
        }
    }

    private static void CheckColor(List<string> errors, string name, string color)
    {
        if (!RgbColor.TryParseHex(color, out _))
        {
            errors.Add($"{name} color '{color}' must be #RRGGBB.");
        }
    }

    private static void CheckEnum<T>(List<string> errors, string name, T value)
        where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            errors.Add($"{name} '{value}' is not supported.");
        }
    }
}

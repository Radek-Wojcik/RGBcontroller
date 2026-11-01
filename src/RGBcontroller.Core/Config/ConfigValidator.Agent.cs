using RGBcontroller.Core.Hotkeys;

namespace RGBcontroller.Core.Config;

/// <summary>Checks of the strip layout, the screen capture pipeline and the hotkeys (stage 4).</summary>
public static partial class ConfigValidator
{
    private static void ValidateLayout(List<string> errors, StripConfig strip)
    {
        var layout = strip.Layout;
        CheckRange(errors, "strip.layout.top", layout.Top, 0, MaxStripLeds);
        CheckRange(errors, "strip.layout.right", layout.Right, 0, MaxStripLeds);
        CheckRange(errors, "strip.layout.bottom", layout.Bottom, 0, MaxStripLeds);
        CheckRange(errors, "strip.layout.left", layout.Left, 0, MaxStripLeds);
        CheckRange(errors, "strip.layout.offset", layout.Offset, -MaxStripLeds, MaxStripLeds);
        if (!Enum.IsDefined(layout.StartCorner))
        {
            errors.Add($"strip.layout.startCorner '{layout.StartCorner}' is not supported.");
        }

        if (layout.Gaps.Any(g => g < 0 || g >= strip.LedCount) || layout.Gaps.Distinct().Count() != layout.Gaps.Count)
        {
            errors.Add($"strip.layout.gaps must list distinct LED indices from 0 to {strip.LedCount - 1}.");
        }
    }

    private static void ValidateCapture(List<string> errors, ScreenCaptureConfig capture)
    {
        CheckRange(errors, "capture.captureFps", capture.CaptureFps, 1, 60);
        CheckRange(errors, "capture.outputFps", capture.OutputFps, 1, MaxStripFps);
        CheckRange(errors, "capture.minimumSampleWidth", capture.MinimumSampleWidth, 64, 1920);
        CheckRange(errors, "capture.segmentOverlapPercent", capture.SegmentOverlapPercent, 0, 200);
        CheckRange(errors, "capture.blackThreshold", capture.BlackThreshold, 0, 128);
        CheckRange(errors, "capture.blackKnee", capture.BlackKnee, 0, 128);
        CheckRange(errors, "capture.attackMilliseconds", capture.AttackMilliseconds, 0, 5000);
        CheckRange(errors, "capture.releaseMilliseconds", capture.ReleaseMilliseconds, 0, 5000);
        CheckRange(errors, "capture.blackReleaseMilliseconds", capture.BlackReleaseMilliseconds, 0, 5000);
        CheckRange(errors, "capture.letterboxStableMilliseconds", capture.LetterboxStableMilliseconds, 0, 30_000);
        CheckRange(errors, "capture.hdrSdrWhiteNits", capture.HdrSdrWhiteNits, 0, 1000);
        CheckRange(errors, "capture.fallbackGraceMilliseconds", capture.FallbackGraceMilliseconds, 0, 30_000);
        CheckNumber(errors, "capture.edgeDepthCm", capture.EdgeDepthCm, 0.2, 20);
        CheckNumber(errors, "capture.gamma", capture.Gamma, 0.2, 5);
        CheckNumber(errors, "capture.saturation", capture.Saturation, 0, 3);
        if (capture.ScreenWidthCm != 0)
        {
            CheckNumber(errors, "capture.screenWidthCm", capture.ScreenWidthCm, 10, 500);
        }

        if (capture.Monitor is { } monitor && !monitor.StartsWith(@"\\.\DISPLAY", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($@"capture.monitor '{monitor}' must be a display name such as \\.\DISPLAY1, or null for the primary monitor.");
        }
    }

    private static void ValidateHotkeys(List<string> errors, HotkeysConfig hotkeys, LightingConfig lighting)
    {
        CheckRange(errors, "hotkeys.brightnessStepPercent", hotkeys.BrightnessStepPercent, 1, 50);
        var seen = new HashSet<(HotkeyModifiers, int)>();
        foreach (var binding in hotkeys.Bindings)
        {
            if (!HotkeyGesture.TryParse(binding.Keys, out var gesture, out var error))
            {
                errors.Add($"hotkeys: {error}");
                continue;
            }

            if (!seen.Add((gesture.Modifier, gesture.VirtualKey)))
            {
                errors.Add($"hotkeys: {gesture} is assigned twice.");
            }

            if (!Enum.IsDefined(binding.Action))
            {
                errors.Add($"hotkeys: {gesture} has an unknown action '{binding.Action}'.");
            }
            else if (binding.Action == HotkeyAction.Profile && lighting.FindProfile(binding.Profile) is null)
            {
                errors.Add($"hotkeys: {gesture} switches to profile '{binding.Profile}', which does not exist.");
            }
        }
    }

    private static void CheckNumber(List<string> errors, string name, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            errors.Add($"{name} must be between {min} and {max}, found {value}.");
        }
    }
}

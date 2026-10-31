namespace RGBcontroller.Core.Config;

/// <summary>Changes to one group's tuning, as sent by the command line now and the UI tiles later (SPEC 7.3).</summary>
/// <param name="Profile">Profile whose effect settings change; null means the active profile.</param>
public sealed record GroupTuning(
    string Group,
    string? Profile = null,
    int? MaxFps = null,
    double? Speed = null,
    double? Scale = null,
    int? BrightnessPercent = null);

/// <summary>Pure edits of <see cref="LightingConfig"/>; the caller validates the result and saves it.</summary>
public static class LightingEdits
{
    /// <summary>
    /// The frame rate belongs to the group (all profiles); speed, scale and brightness belong to the group's effect in
    /// one profile. A group with no effect in that profile cannot be tuned there.
    /// </summary>
    /// <returns>The changed config, or null with <paramref name="error"/> set.</returns>
    public static LightingConfig? Apply(LightingConfig config, GroupTuning tuning, out string? error)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(tuning);
        error = null;
        var group = config.Groups.FirstOrDefault(g => string.Equals(g.Name, tuning.Group, StringComparison.OrdinalIgnoreCase));
        if (group is null)
        {
            error = $"No group named '{tuning.Group}'. Groups: {string.Join(", ", config.Groups.Select(g => g.Name))}.";
            return null;
        }

        var result = config;
        if (tuning.MaxFps is { } fps)
        {
            result = result with { Groups = [.. result.Groups.Select(g => ReferenceEquals(g, group) ? g with { MaxFps = fps } : g)] };
        }

        if (tuning.Speed is null && tuning.Scale is null && tuning.BrightnessPercent is null)
        {
            return result;
        }

        var profile = result.FindProfile(tuning.Profile ?? result.ActiveProfile);
        if (profile is null)
        {
            error = $"No profile named '{tuning.Profile}'.";
            return null;
        }

        var effect = profile.FindGroup(group.Name);
        if (effect is null)
        {
            error = $"Profile '{profile.Name}' has no effect for group '{group.Name}'.";
            return null;
        }

        var tuned = effect with
        {
            Speed = tuning.Speed ?? effect.Speed,
            Scale = tuning.Scale ?? effect.Scale,
            BrightnessPercent = tuning.BrightnessPercent ?? effect.BrightnessPercent,
        };
        var changed = profile with { Groups = [.. profile.Groups.Select(e => ReferenceEquals(e, effect) ? tuned : e)] };
        return result with { Profiles = [.. result.Profiles.Select(p => ReferenceEquals(p, profile) ? changed : p)] };
    }
}

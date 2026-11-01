using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;
using RGBcontroller.Core.OpenRgb;
using Serilog;

namespace RGBcontroller.Core.Lighting;

/// <summary>Where rendered frames go. The Service implementation writes to the OpenRGB SDK and the strip writer.</summary>
public interface ILightingOutput
{
    /// <summary>Activates and pushes a device mode (UPDATEMODE). Sent once per enumeration or when the mode changes.</summary>
    void SetDeviceMode(int deviceIndex, int modeIndex, ControllerMode mode);

    /// <summary>All LEDs of one device, exactly its LED count.</summary>
    void SetDeviceLeds(int deviceIndex, ReadOnlySpan<RgbColor> colors);

    /// <summary>All strip LEDs, brightness already applied.</summary>
    void SetStripLeds(ReadOnlySpan<RgbColor> colors);
}

/// <summary>
/// The effect engine (SPEC 5.1): one clock for all devices, a per-device frame rate cap, writes only when colors
/// changed, brightness applied last (group x master). Static profiles render once and then report no further work,
/// so the render loop sleeps (0% CPU). <see cref="Render"/> does not allocate; rebuilding after a profile or device
/// change does, which is rare.
/// <para>Thread-safe: the render loop calls <see cref="Render"/>, other threads change the profile or devices.</para>
/// </summary>
public sealed class LightingEngine
{
    private const int DefaultFps = 30;

    /// <summary>Everything off, strip included; used while the PC sleeps.</summary>
    private static readonly ProfileConfig BlackoutProfile = new() { Name = "(blackout)", Ambilight = new() { Mode = AmbilightMode.Off } };

    private readonly Lock _gate = new();
    private readonly ILogger _log;
    private readonly int _stripLedCount;
    private readonly int _stripMaxFps;
    private readonly Dictionary<int, int> _sentModes = [];
    private LightingConfig _config;
    private IReadOnlyList<ControllerData> _devices = [];
    private Target[] _targets = [];
    private bool _rebuild = true;
    private bool _blackout;
    private LightingSituation _situation = LightingSituation.Default;

    /// <param name="stripLedCount">0 when the strip is disabled.</param>
    public LightingEngine(LightingConfig config, int stripLedCount, int stripMaxFps, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _stripLedCount = stripLedCount;
        _stripMaxFps = Math.Max(1, stripMaxFps);
        _log = log.ForContext<LightingEngine>();
    }

    public LightingConfig Config
    {
        get
        {
            lock (_gate)
            {
                return _config;
            }
        }
    }

    /// <summary>Switches profile or any other lighting setting. Takes effect on the next <see cref="Render"/>.</summary>
    public void SetConfig(LightingConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        lock (_gate)
        {
            _config = config;
            _rebuild = true;
        }
    }

    /// <summary>
    /// True sends black to every LED once and then idles (suspend); false returns to the profile. Takes effect on the
    /// next <see cref="Render"/>.
    /// </summary>
    public void SetBlackout(bool blackout)
    {
        lock (_gate)
        {
            _blackout = blackout;
            _rebuild = true;
        }
    }

    /// <summary>
    /// Session and display state (SPEC 4): selects the logon, lock or active profile and the outputs forced off while
    /// the display is off. Takes effect on the next <see cref="Render"/>.
    /// </summary>
    public void SetSituation(LightingSituation situation)
    {
        lock (_gate)
        {
            if (_situation != situation)
            {
                _situation = situation;
                _rebuild = true;
            }
        }
    }

    /// <summary>
    /// Devices of a fresh enumeration; the list index is the SDK device index. Modes are re-sent because a rescan or
    /// restart may have reset the hardware.
    /// </summary>
    public void SetDevices(IReadOnlyList<ControllerData> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        lock (_gate)
        {
            _devices = devices;
            _sentModes.Clear();
            _rebuild = true;
        }
    }

    /// <summary>Stops all device writes (rescan in progress, connection lost). Returns after any frame in flight is done.</summary>
    public void ClearDevices() => SetDevices([]);

    /// <summary>Renders every target that is due at <paramref name="now"/> (time on the engine clock).</summary>
    /// <returns>Time until the next target is due, or null when nothing is animated.</returns>
    public TimeSpan? Render(TimeSpan now, ILightingOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_gate)
        {
            if (_rebuild)
            {
                _rebuild = false;
                _targets = Build(now);
            }

            TimeSpan? next = null;
            var seconds = now.TotalSeconds;
            foreach (var target in _targets)
            {
                if (target.ModeIndex >= 0 && (!_sentModes.TryGetValue(target.DeviceIndex, out var sent) || sent != target.ModeIndex))
                {
                    output.SetDeviceMode(target.DeviceIndex, target.ModeIndex, target.Mode!);
                    _sentModes[target.DeviceIndex] = target.ModeIndex;
                }

                if (!target.WritesLeds)
                {
                    continue;
                }

                if (target.Dirty || (target.Animated && now >= target.NextDue))
                {
                    RenderTarget(target, seconds, output);
                    if (target.Animated)
                    {
                        target.NextDue += target.Interval;
                        if (target.NextDue <= now)
                        {
                            target.NextDue = now + target.Interval;
                        }
                    }
                }

                if (target.Animated)
                {
                    var wait = target.NextDue - now;
                    next = next is null || wait < next ? wait : next;
                }
            }

            return next;
        }
    }

    private static void RenderTarget(Target target, double seconds, ILightingOutput output)
    {
        var buffer = target.Buffer.AsSpan();
        foreach (var binding in target.Bindings)
        {
            var leds = buffer.Slice(binding.Start, binding.Count);
            binding.Effect.Render(target.Points.AsSpan(binding.Start, binding.Count), leds, seconds);
            if (binding.Brightness < 255)
            {
                for (var i = 0; i < leds.Length; i++)
                {
                    leds[i] = BreathingEffect.Scale(leds[i], binding.Brightness);
                }
            }
        }

        if (!target.Dirty && buffer.SequenceEqual(target.Sent))
        {
            return;
        }

        if (target.DeviceIndex == Target.Strip)
        {
            output.SetStripLeds(buffer);
        }
        else
        {
            output.SetDeviceLeds(target.DeviceIndex, buffer);
        }

        buffer.CopyTo(target.Sent);
        target.Dirty = false;
    }

    private Target[] Build(TimeSpan now)
    {
        var config = _config;
        var profileName = SessionPolicy.ProfileFor(config, _situation.Session);
        var profile = _blackout ? BlackoutProfile : config.FindProfile(profileName) ?? new ProfileConfig { Name = "(none)" };
        if (!_blackout && !string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning("Profile {Profile} does not exist; everything stays off", profileName);
        }

        var off = SessionPolicy.TurnedOff(config, _situation);

        var effects = new Dictionary<string, Effect>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in profile.Groups)
        {
            effects.TryAdd(group.Group, Effect.Create(group, config.Palettes));
        }

        var targets = new List<Target>(_devices.Count + 1);
        if (_stripLedCount > 0)
        {
            targets.Add(BuildStrip(profile, config, off.Strip, now));
        }

        for (var index = 0; index < _devices.Count; index++)
        {
            var device = _devices[index];
            targets.Add(BuildDevice(index, device, profile, config, effects, off.Contains(device.Name), now));
        }

        _log.Information("Rendering profile {Profile} ({Situation:l}): {Targets}", profile.Name,
            _blackout ? "blackout" : _situation.ToString(),
            string.Join("; ", targets.Select(t => t.Describe())));
        return [.. targets];
    }

    private Target BuildStrip(ProfileConfig profile, LightingConfig config, bool turnedOff, TimeSpan now)
    {
        var ambilight = profile.Ambilight;
        // Screen mode needs the Agent's capture (stage 4); until a frame source exists the fallback is shown.
        var mode = turnedOff ? AmbilightMode.Off : ambilight.Mode == AmbilightMode.Screen ? ambilight.Fallback : ambilight.Mode;
        Effect effect = mode switch
        {
            AmbilightMode.Radial => new RadialEffect(
                Palette.Resolve(ambilight.Palette, config.Palettes) ?? Palette.Solid(new RgbColor(255, 255, 255)),
                RadialMode.Rotate, reverse: false, scale: 1, ambilight.Speed),
            AmbilightMode.Solid => new StaticEffect(
                Palette.Solid(RgbColor.TryParseHex(ambilight.SolidColor, out var solid) ? solid : new RgbColor(255, 255, 255)), 0, 1),
            _ => OffEffect.Instance,
        };
        var brightness = mode == AmbilightMode.Solid ? ambilight.SolidBrightnessPercent : ambilight.BrightnessPercent;
        var points = LedLayoutBuilder.Ring(_stripLedCount);
        var bindings = new[] { new Binding(0, _stripLedCount, effect, Brightness(brightness, profile.MasterBrightnessPercent)) };
        return new Target(Target.Strip, $"strip {mode}", points, bindings, _stripMaxFps, now);
    }

    private Target BuildDevice(
        int index, ControllerData device, ProfileConfig profile, LightingConfig config, Dictionary<string, Effect> effects, bool turnedOff,
        TimeSpan now)
    {
        var points = LedLayoutBuilder.Build(device, config.Layouts);
        var zones = new List<(int Start, int Count, GroupConfig? Group, GroupEffectConfig? Effect)>(device.Zones.Count);
        var start = 0;
        foreach (var zone in device.Zones)
        {
            var count = (int)Math.Min(zone.LedsCount, (uint)Math.Max(0, points.Length - start));
            var group = FindGroup(config, device.Name, zone.Name);
            // A turned-off device keeps its per-LED mode and shows black, even if its profile runs a hardware mode.
            zones.Add((start, count, group, group is null || turnedOff ? null : profile.FindGroup(group.Name)));
            start += count;
        }

        var lit = zones.Where(z => z.Count > 0).ToList();
        var hardwareMode = lit.Count > 0 && lit.All(z => z.Effect?.Effect == EffectKind.Hardware)
            ? lit.Select(z => z.Effect!.HardwareMode).Distinct(StringComparer.OrdinalIgnoreCase).SingleOrDefault()
            : null;
        var hardwareIndex = DeviceModes.FindByName(device, hardwareMode);
        if (hardwareIndex >= 0)
        {
            return new Target(index, $"{device.Name} hardware '{device.Modes[hardwareIndex].Name}'", points, [], DefaultFps, now)
            {
                ModeIndex = hardwareIndex,
                Mode = device.Modes[hardwareIndex],
                WritesLeds = false,
            };
        }

        var bindings = new List<Binding>();
        var fps = 0;
        foreach (var (zoneStart, count, group, effectConfig) in lit)
        {
            if (effectConfig?.Effect == EffectKind.Hardware)
            {
                _log.Warning("{Device}: hardware mode '{Mode}' needs every zone of the device in such a group; zone stays off",
                    device.Name, effectConfig.HardwareMode);
            }

            var effect = effectConfig is not null && effects.TryGetValue(effectConfig.Group, out var e) ? e : OffEffect.Instance;
            bindings.Add(new Binding(zoneStart, count, effect, Brightness(effectConfig?.BrightnessPercent ?? 0, profile.MasterBrightnessPercent)));
            if (effect.IsAnimated)
            {
                fps = Math.Max(fps, group!.MaxFps);
            }
        }

        var modeIndex = DeviceModes.FindPerLedMode(device);
        if (modeIndex < 0)
        {
            _log.Warning("{Device} has no Direct/Custom/Static per-LED mode; colors may not show", device.Name);
        }

        var label = turnedOff ? $"{device.Name} turned off" : $"{device.Name} {string.Join(", ", lit.Select(z => $"{z.Group?.Name ?? "-"}:{z.Effect?.Effect.ToString() ?? "Off"}").Distinct())}";
        return new Target(index, label, points, [.. bindings], fps > 0 ? fps : DefaultFps, now)
        {
            ModeIndex = modeIndex,
            Mode = modeIndex >= 0 ? device.Modes[modeIndex] : null,
        };
    }

    /// <summary>First group that lists the zone (or the whole device), else the configured default group.</summary>
    private static GroupConfig? FindGroup(LightingConfig config, string device, string zone)
    {
        foreach (var group in config.Groups)
        {
            foreach (var member in group.Members)
            {
                if (string.Equals(member.Device, device, StringComparison.OrdinalIgnoreCase)
                    && (member.Zone is null || string.Equals(member.Zone, zone, StringComparison.OrdinalIgnoreCase)))
                {
                    return group;
                }
            }
        }

        return config.DefaultGroup is null
            ? null
            : config.Groups.FirstOrDefault(g => string.Equals(g.Name, config.DefaultGroup, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Group percent x master percent as a 0-255 factor; 100% x 100% is 255 (full diode power).</summary>
    private static int Brightness(int percent, int masterPercent) =>
        (int)Math.Round(Math.Clamp(percent, 0, 100) * Math.Clamp(masterPercent, 0, 100) * 255 / 10_000.0);

    private readonly record struct Binding(int Start, int Count, Effect Effect, int Brightness);

    private sealed class Target
    {
        public const int Strip = -1;

        public Target(int deviceIndex, string name, LedPoint[] points, Binding[] bindings, int fps, TimeSpan now)
        {
            DeviceIndex = deviceIndex;
            Name = name;
            Points = points;
            Bindings = bindings;
            Buffer = new RgbColor[points.Length];
            Sent = new RgbColor[points.Length];
            Interval = TimeSpan.FromSeconds(1.0 / fps);
            Animated = bindings.Any(b => b.Effect.IsAnimated);
            NextDue = now;
        }

        public int DeviceIndex { get; }

        public string Name { get; }

        public LedPoint[] Points { get; }

        public Binding[] Bindings { get; }

        public RgbColor[] Buffer { get; }

        public RgbColor[] Sent { get; }

        public TimeSpan Interval { get; }

        public bool Animated { get; }

        public int ModeIndex { get; init; } = -1;

        public ControllerMode? Mode { get; init; }

        public bool WritesLeds { get; init; } = true;

        /// <summary>Must be sent once even if unchanged (new target after a rebuild).</summary>
        public bool Dirty { get; set; } = true;

        public TimeSpan NextDue { get; set; }

        public string Describe() => Animated ? $"{Name} @{Math.Round(1 / Interval.TotalSeconds)} fps" : $"{Name} (static)";
    }
}

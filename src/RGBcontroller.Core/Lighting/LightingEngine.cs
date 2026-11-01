using RGBcontroller.Core.Ambilight;
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

    /// <summary>
    /// A static frame is sent this many extra times. OpenRGB pushes some devices in several USB packets (the Razer
    /// keyboard row by row) and a lost packet left the F-key row lit after "turn off"; animations heal on the next frame.
    /// The strip writer has its own keep-alive.
    /// </summary>
    private const int StaticRepeats = 2;

    private static readonly TimeSpan StaticRepeatInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Everything off, strip included; used while the PC sleeps.</summary>
    private static readonly ProfileConfig BlackoutProfile = new() { Name = "(blackout)", Ambilight = new() { Mode = AmbilightMode.Off } };

    private readonly Lock _gate = new();
    private readonly ILogger _log;
    private readonly int _stripLedCount;
    private readonly int _stripMaxFps;
    private readonly LedPoint[] _stripPoints;
    private readonly IReadOnlyList<(int Start, int Count)> _stripRuns;
    private readonly RgbColor[] _screen;
    private readonly Dictionary<int, int> _sentModes = [];
    private LightingConfig _config;
    private IReadOnlyList<ControllerData> _devices = [];
    private Target[] _targets = [];
    private bool _rebuild = true;
    private bool _blackout;
    private LightingSituation _situation = LightingSituation.Default;
    private bool _allOff;
    private bool _stripTest;
    private bool _screenLive;
    private Target? _screenTarget;

    /// <param name="stripLedCount">0 when the strip is disabled.</param>
    /// <param name="stripLayout">LED positions around the monitor; null spreads the LEDs on a ring.</param>
    public LightingEngine(LightingConfig config, int stripLedCount, int stripMaxFps, ILogger log, StripLayout? stripLayout = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _stripLedCount = stripLedCount;
        _stripMaxFps = Math.Max(1, stripMaxFps);
        _stripPoints = stripLayout?.LedCount == stripLedCount ? stripLayout.ToPoints() : LedLayoutBuilder.Ring(stripLedCount);
        _stripRuns = stripLayout?.LedCount == stripLedCount ? stripLayout.LitRuns() : [(0, stripLedCount)];
        _screen = new RgbColor[stripLedCount];
        _log = log.ForContext<LightingEngine>();
    }

    /// <summary>
    /// True while the strip should mirror the screen (SPEC 4 and 6.2): the shown profile is in Screen mode, the session
    /// is unlocked, the display is on and nothing forces the strip off. The Agent captures only then.
    /// </summary>
    public bool IsScreenWanted
    {
        get
        {
            lock (_gate)
            {
                var profile = _config.FindProfile(SessionPolicy.ProfileFor(_config, _situation.Session));
                return !_blackout && !_allOff && _stripLedCount > 0 && profile?.Ambilight.Mode == AmbilightMode.Screen
                    && _situation is { Session: SessionKind.Unlocked, DisplayOn: true } && !SessionPolicy.TurnedOff(_config, _situation).Strip;
            }
        }
    }

    /// <summary>Everything off (tray, hotkey) or back to the profile. Takes effect on the next <see cref="Render"/>.</summary>
    public void SetAllOff(bool off)
    {
        lock (_gate)
        {
            if (_allOff != off)
            {
                _allOff = off;
                _rebuild = true;
            }
        }
    }

    /// <summary>
    /// Shows the strip identification pattern instead of the profile (SPEC 7.5): the first 3 LEDs green, the last 3
    /// red and every 10th LED white, so the start, the direction and the corners can be read off the strip.
    /// </summary>
    public void SetStripTest(bool on)
    {
        lock (_gate)
        {
            if (_stripTest != on)
            {
                _stripTest = on;
                _rebuild = true;
            }
        }
    }

    /// <summary>
    /// Whether the Agent delivers screen colors. While false a strip in Screen mode shows the profile's fallback.
    /// Takes effect on the next <see cref="Render"/>.
    /// </summary>
    public void SetScreenFeed(bool live)
    {
        lock (_gate)
        {
            if (_screenLive != live)
            {
                _screenLive = live;
                _rebuild = true;
            }
        }
    }

    /// <summary>New screen colors from the Agent, in strip LED order before brightness. Does not allocate.</summary>
    /// <returns>True when the strip shows the screen and a render is due.</returns>
    public bool PushScreenFrame(ReadOnlySpan<RgbColor> colors)
    {
        lock (_gate)
        {
            var count = Math.Min(colors.Length, _screen.Length);
            colors[..count].CopyTo(_screen);
            if (_screenTarget is null)
            {
                return false;
            }

            _screenTarget.Dirty = true;
            return true;
        }
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

    /// <summary>
    /// Sends every device mode and frame again on the next <see cref="Render"/>, without a rebuild. A mode write over
    /// SMBus can be lost at cold boot (one RAM stick stayed dark until its Direct mode was pushed again).
    /// </summary>
    public void ResendAll()
    {
        lock (_gate)
        {
            _sentModes.Clear();
            foreach (var target in _targets)
            {
                target.Dirty = true;
            }
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
                _screenTarget = null;
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
                    if (RenderTarget(target, seconds, output) && !target.Animated && target.DeviceIndex != Target.Strip)
                    {
                        target.PendingRepeats = StaticRepeats;
                        target.NextDue = now + StaticRepeatInterval;
                    }

                    if (target.Animated)
                    {
                        target.NextDue += target.Interval;
                        if (target.NextDue <= now)
                        {
                            target.NextDue = now + target.Interval;
                        }
                    }
                }
                else if (target.PendingRepeats > 0 && now >= target.NextDue)
                {
                    output.SetDeviceLeds(target.DeviceIndex, target.Sent);
                    target.PendingRepeats--;
                    target.NextDue = now + StaticRepeatInterval;
                }

                if (target.Animated || target.PendingRepeats > 0)
                {
                    var wait = target.NextDue - now;
                    next = next is null || wait < next ? wait : next;
                }
            }

            return next;
        }
    }

    /// <returns>True when the frame was sent (it differed from the last one or the target is new).</returns>
    private static bool RenderTarget(Target target, double seconds, ILightingOutput output)
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
            return false;
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
        return true;
    }

    private Target[] Build(TimeSpan now)
    {
        var config = _config;
        var profileName = SessionPolicy.ProfileFor(config, _situation.Session);
        var dark = _blackout || _allOff;
        var profile = dark ? BlackoutProfile : config.FindProfile(profileName) ?? new ProfileConfig { Name = "(none)" };
        if (!dark && !string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase))
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
            targets.Add(_stripTest && !_blackout ? BuildStripTest(now) : BuildStrip(profile, config, off.Strip, now));
        }

        for (var index = 0; index < _devices.Count; index++)
        {
            var device = _devices[index];
            targets.Add(BuildDevice(index, device, profile, config, effects, off.Contains(device.Name), now));
        }

        _log.Information("Rendering profile {Profile} ({Situation:l}): {Targets}", profile.Name,
            _blackout ? "blackout" : _allOff ? "all off" : _situation.ToString(),
            string.Join("; ", targets.Select(t => t.Describe())));
        return [.. targets];
    }

    private Target BuildStrip(ProfileConfig profile, LightingConfig config, bool turnedOff, TimeSpan now)
    {
        var ambilight = profile.Ambilight;
        // Screen mode needs the Agent's capture, which exists only in an unlocked session with the display on.
        var screen = _screenLive && _situation is { Session: SessionKind.Unlocked, DisplayOn: true };
        var mode = turnedOff ? AmbilightMode.Off : ambilight.Mode == AmbilightMode.Screen && !screen ? ambilight.Fallback : ambilight.Mode;
        Effect effect = mode switch
        {
            AmbilightMode.Screen => new ScreenFeedEffect(_screen),
            AmbilightMode.Radial => new RadialEffect(
                Palette.Resolve(ambilight.Palette, config.Palettes) ?? Palette.Solid(new RgbColor(255, 255, 255)),
                RadialMode.Rotate, reverse: false, scale: 1, ambilight.Speed),
            AmbilightMode.Solid => new StaticEffect(
                Palette.Solid(RgbColor.TryParseHex(ambilight.SolidColor, out var solid) ? solid : new RgbColor(255, 255, 255)), 0, 1),
            _ => OffEffect.Instance,
        };
        var brightness = mode == AmbilightMode.Solid ? ambilight.SolidBrightnessPercent : ambilight.BrightnessPercent;
        var level = Brightness(brightness, profile.MasterBrightnessPercent);

        // The Agent already sends black for dark LEDs; the effects leave the LEDs outside the lit runs untouched (black).
        Binding[] bindings = mode == AmbilightMode.Screen
            ? [new Binding(0, _stripLedCount, effect, level)]
            : [.. _stripRuns.Select(run => new Binding(run.Start, run.Count, effect, level))];
        var target = new Target(Target.Strip, $"strip {mode}", _stripPoints, bindings, _stripMaxFps, now);
        if (mode == AmbilightMode.Screen)
        {
            _screenTarget = target;
        }

        return target;
    }

    private Target BuildStripTest(TimeSpan now)
    {
        var pattern = new RgbColor[_stripLedCount];
        for (var i = 0; i < pattern.Length; i++)
        {
            pattern[i] = i < 3 ? new RgbColor(0, 255, 0)
                : i >= pattern.Length - 3 ? new RgbColor(255, 0, 0)
                : i % 10 == 0 ? new RgbColor(255, 255, 255)
                : default;
        }

        Binding[] bindings = [new Binding(0, _stripLedCount, new ScreenFeedEffect(pattern), Brightness(50, 100))];
        return new Target(Target.Strip, "strip test pattern", _stripPoints, bindings, _stripMaxFps, now);
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

    /// <summary>Copies the latest screen colors from the Agent; renders only when a new frame marks the strip dirty.</summary>
    private sealed class ScreenFeedEffect(RgbColor[] screen) : Effect
    {
        public override bool IsAnimated => false;

        public override void Render(ReadOnlySpan<LedPoint> points, Span<RgbColor> destination, double seconds) =>
            screen.AsSpan(0, destination.Length).CopyTo(destination);
    }

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

        /// <summary>Static targets: extra sends of <see cref="Sent"/> still due (see <see cref="StaticRepeats"/>).</summary>
        public int PendingRepeats { get; set; }

        public string Describe() => Animated ? $"{Name} @{Math.Round(1 / Interval.TotalSeconds)} fps" : $"{Name} (static)";
    }
}

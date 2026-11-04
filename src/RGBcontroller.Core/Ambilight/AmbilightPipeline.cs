using System.Numerics;
using RGBcontroller.Core.Config;

namespace RGBcontroller.Core.Ambilight;

/// <summary>
/// The CPU side of Screen mode (SPEC 6.2). <see cref="OnImage"/> (capture rate, about 20 FPS) turns a downscaled
/// desktop frame into one target color per LED: letterbox detection, edge band average, black threshold with a soft
/// knee, gamma, saturation. <see cref="Step"/> (output rate, about 30 FPS) moves the output towards the targets with
/// separate attack and release time constants; towards black it uses the short black release and snaps to exactly 0,
/// so a black screen means the strip is off and a fade to black leaves no glow. A new pipeline fades its output in over
/// <see cref="ScreenCaptureConfig.FadeInMilliseconds"/>, so starting Screen mode is not an abrupt flash. Brightness and
/// the current limiter are applied by the Service. Nothing here allocates after construction.
/// </summary>
public sealed class AmbilightPipeline
{
    /// <summary>Output within this distance of its target (0..255 scale) is snapped onto it; the strip settles and the Agent goes idle.</summary>
    private const float SnapDistance = 0.5f;

    /// <summary>Towards black, output this dim is cut to 0 at once (no trailing glow).</summary>
    private const float BlackSnap = 2f;

    private readonly ScreenCaptureConfig _config;
    private readonly EdgeSampler _sampler;
    private readonly LetterboxDetector? _letterbox;
    private readonly Vector3[] _sampled;
    private readonly Vector3[] _target;
    private readonly Vector3[] _current;
    private readonly RgbColor[] _output;
    private float _fadeInElapsed;

    /// <param name="screenWidthCm">Physical width of the visible screen (EDID or the setting).</param>
    public AmbilightPipeline(StripLayout layout, ScreenCaptureConfig config, double screenWidthCm)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
        _sampler = new EdgeSampler(layout, config.EdgeDepthCm / screenWidthCm, config.SegmentOverlapPercent / 100.0);
        _letterbox = config.LetterboxDetection ? new LetterboxDetector(TimeSpan.FromMilliseconds(config.LetterboxStableMilliseconds)) : null;
        _sampled = new Vector3[layout.LedCount];
        _target = new Vector3[layout.LedCount];
        _current = new Vector3[layout.LedCount];
        _output = new RgbColor[layout.LedCount];
    }

    /// <summary>scRGB value of SDR white, used for HDR frames only.</summary>
    public float HdrScale { get; set; } = 1;

    public ContentRect Content { get; private set; }

    /// <summary>True when the output has reached the targets; until a new image arrives there is nothing to send.</summary>
    public bool IsSettled { get; private set; } = true;

    public ReadOnlySpan<RgbColor> Output => _output;

    /// <summary>Takes a new desktop frame and sets the LED targets.</summary>
    public void OnImage(in ScreenImage image, TimeSpan now)
    {
        Content = _letterbox?.Update(image, HdrScale, now) ?? ContentRect.Full(image.Width, image.Height);
        _sampler.Configure(image.Width, image.Height, Content);
        _sampler.Sample(image, _sampled, HdrScale);
        for (var i = 0; i < _sampled.Length; i++)
        {
            _target[i] = Shape(_sampled[i]);
        }

        IsSettled = false;
    }

    /// <summary>All targets black at once (capture lost or stopped); the next steps fade out quickly.</summary>
    public void Clear()
    {
        Array.Clear(_target);
        _letterbox?.Reset();
        IsSettled = false;
    }

    /// <summary>Advances the smoothing by <paramref name="elapsed"/>.</summary>
    /// <returns>True when <see cref="Output"/> changed and should be sent.</returns>
    public bool Step(TimeSpan elapsed)
    {
        if (IsSettled)
        {
            return false;
        }

        var dt = (float)Math.Max(0, elapsed.TotalMilliseconds);
        var attack = Alpha(dt, _config.AttackMilliseconds);
        var release = Alpha(dt, _config.ReleaseMilliseconds);
        var blackRelease = Alpha(dt, _config.BlackReleaseMilliseconds);
        var fadeIn = 1f;
        if (_config.FadeInMilliseconds > 0 && _fadeInElapsed < _config.FadeInMilliseconds)
        {
            _fadeInElapsed += dt;
            fadeIn = Math.Min(1, _fadeInElapsed / _config.FadeInMilliseconds);
        }

        var settled = fadeIn >= 1;
        var changed = false;
        for (var i = 0; i < _current.Length; i++)
        {
            var target = _target[i];
            var black = target == Vector3.Zero;
            var current = new Vector3(
                Approach(_current[i].X, target.X, attack, black ? blackRelease : release),
                Approach(_current[i].Y, target.Y, attack, black ? blackRelease : release),
                Approach(_current[i].Z, target.Z, attack, black ? blackRelease : release));
            if (black && Math.Max(current.X, Math.Max(current.Y, current.Z)) < BlackSnap)
            {
                current = Vector3.Zero;
            }

            _current[i] = current;
            settled &= current == target;
            var shown = current * fadeIn;
            var color = new RgbColor(ToByte(shown.X), ToByte(shown.Y), ToByte(shown.Z));
            if (color != _output[i])
            {
                _output[i] = color;
                changed = true;
            }
        }

        IsSettled = settled;
        return changed;
    }

    /// <summary>Black threshold with a soft knee, gamma, saturation; 0..255 in and out.</summary>
    private Vector3 Shape(Vector3 c)
    {
        var max = Math.Max(c.X, Math.Max(c.Y, c.Z));
        var threshold = _config.BlackThreshold;
        if (max <= threshold)
        {
            return Vector3.Zero;
        }

        if (_config.BlackKnee > 0 && max < threshold + _config.BlackKnee)
        {
            c *= (max - threshold) / _config.BlackKnee;
        }

        var gamma = (float)_config.Gamma;
        c = new Vector3(
            255 * MathF.Pow(Math.Clamp(c.X / 255, 0, 1), gamma),
            255 * MathF.Pow(Math.Clamp(c.Y / 255, 0, 1), gamma),
            255 * MathF.Pow(Math.Clamp(c.Z / 255, 0, 1), gamma));

        if (_config.Saturation != 1)
        {
            var luma = 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
            c = Vector3.Clamp(new Vector3(luma) + (c - new Vector3(luma)) * (float)_config.Saturation, Vector3.Zero, new Vector3(255));
        }

        return c;
    }

    private static float Alpha(float dtMilliseconds, int timeConstantMilliseconds) =>
        timeConstantMilliseconds <= 0 ? 1 : 1 - MathF.Exp(-dtMilliseconds / timeConstantMilliseconds);

    private static float Approach(float current, float target, float attack, float release)
    {
        var next = current + (target - current) * (target > current ? attack : release);
        return Math.Abs(target - next) < SnapDistance ? target : next;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0, 255);
}

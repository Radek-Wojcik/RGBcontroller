namespace RGBcontroller.Core.Lighting;

/// <summary>
/// One LED range the mapping wizard asks the hardware to show (SPEC 7.4).
/// </summary>
/// <param name="DeviceIndex">SDK device index (<see cref="LightingEngine.Devices"/> order).</param>
/// <param name="Zone">Zone name; null counts <paramref name="Start"/> over the whole device (zones one after another).</param>
/// <param name="Start">First LED of the range inside the zone, 0-based.</param>
/// <param name="Count">LEDs in the range; a range past the end of the zone is cut.</param>
public sealed record IdentifyTarget(int DeviceIndex, string? Zone, int Start, int Count);

/// <summary>
/// The marker pattern of an identified range: dim white, the first LEDs green and the last LEDs red (a third of the
/// range at most, 3 LEDs at most), so the start and the direction of a chain can be read off the hardware.
/// </summary>
public static class IdentifyPattern
{
    private const int MaxMarkers = 3;

    public static readonly RgbColor Body = new(255, 255, 255);

    public static readonly RgbColor Start = new(0, 255, 0);

    public static readonly RgbColor End = new(255, 0, 0);

    public static void Fill(Span<RgbColor> range)
    {
        range.Fill(Body);
        if (range.Length < 2)
        {
            return;
        }

        var markers = Math.Clamp(range.Length / 3, 1, MaxMarkers);
        range[..markers].Fill(Start);
        range[^markers..].Fill(End);
    }
}

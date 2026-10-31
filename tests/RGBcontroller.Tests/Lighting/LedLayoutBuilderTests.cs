using RGBcontroller.Core.Config;
using RGBcontroller.Core.Effects;
using RGBcontroller.Core.Lighting;
using RGBcontroller.Core.OpenRgb;
using RGBcontroller.Tests.OpenRgb;

namespace RGBcontroller.Tests.Lighting;

public sealed class LedLayoutBuilderTests
{
    private const float Tolerance = 1e-5f;

    [Fact]
    public void KeyboardMatrix_GivesEachKeyItsGridPosition()
    {
        var keyboard = ControllerDataWriter.Keyboard(); // 6 x 21 matrix, LED i at row i / 21, column i % 21

        var points = LedLayoutBuilder.Build(keyboard, []);

        Assert.Equal(new LedPoint(0, 0), points[0]);
        Assert.Equal(new LedPoint(1, 0), points[20]);
        Assert.Equal(new LedPoint(0, 0.2f), points[21]);
        Assert.Equal(new LedPoint(1, 1), points[125]);
    }

    [Fact]
    public void Ring_StartsAtTwelveOClockAndRunsClockwise()
    {
        var points = LedLayoutBuilder.Ring(4);

        AssertNear(0.5f, 0, points[0]);
        AssertNear(1, 0.5f, points[1]);
        AssertNear(0.5f, 1, points[2]);
        AssertNear(0, 0.5f, points[3]);
        Assert.Equal(0.25f, points[1].Angle, Tolerance);
    }

    [Fact]
    public void Ring_ReverseAndOffset()
    {
        var reversed = LedLayoutBuilder.Ring(4, reverse: true);
        AssertNear(0, 0.5f, reversed[1]);

        var shifted = LedLayoutBuilder.Ring(4, offset: 1);
        AssertNear(0.5f, 0, shifted[1]);
        AssertNear(0, 0.5f, shifted[0]);
    }

    [Fact]
    public void VerticalLine_RunsTopToBottom()
    {
        var ram = Ram();
        var layouts = DefaultLighting.Layouts;

        var points = LedLayoutBuilder.Build(ram, layouts);

        Assert.Equal(20, points.Length);
        Assert.Equal(new LedPoint(0.5f, 0), points[0]);
        Assert.Equal(new LedPoint(0.5f, 0.5f), points[2]);
        Assert.Equal(new LedPoint(0.5f, 1), points[4]);
        Assert.Equal(new LedPoint(0.5f, 0), points[5]);
    }

    [Fact]
    public void Segments_SplitOneHeaderIntoTwoRings()
    {
        var board = Board(argbLeds: 20);
        ZoneLayoutConfig[] layouts =
        [
            new() { Device = board.Name, Zone = "Aura Addressable 1", Segments = [new() { Leds = 8, Shape = LedShape.Ring }, new() { Shape = LedShape.Ring }] },
        ];

        var points = LedLayoutBuilder.Build(board, layouts);

        AssertNear(0.5f, 0, points[1]); // first LED of the 8-LED ring (after the 1 onboard LED)
        AssertNear(1, 0.5f, points[1 + 2]);
        AssertNear(0.5f, 0, points[1 + 8]); // first LED of the 12-LED ring
        AssertNear(1, 0.5f, points[1 + 8 + 3]);
    }

    [Fact]
    public void SingleLedAndUnlistedZones_AreAutoShaped()
    {
        var points = LedLayoutBuilder.Build(ControllerDataWriter.Mouse(), []);

        Assert.Equal([LedPoint.Center, LedPoint.Center], points);

        var line = LedLayoutBuilder.Build(Board(argbLeds: 3), []);
        Assert.Equal(new LedPoint(0, 0.5f), line[1]);
        Assert.Equal(new LedPoint(1, 0.5f), line[3]);
    }

    /// <summary>HyperX Predator: 4 slots x 5 LEDs, names as the owner's OpenRGB reports them.</summary>
    internal static ControllerData Ram() => new(
        1, DefaultLighting.Ram, "HyperX", "", "", "", "I2C: i801, address 0x27", 0,
        [new("Direct", 0, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, []), new("Rainbow", 3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, [])],
        [.. Enumerable.Range(1, 4).Select(i => new ControllerZone($"HyperX Slot {i}", 1, 5, 5, 5, 0, 0, [], [], 0))],
        [.. Enumerable.Range(0, 20).Select(i => new ControllerLed($"LED {i}", (uint)i))],
        20, [], 0);

    /// <summary>Owner's board after zone resizing: 1 onboard LED and one ARGB header.</summary>
    internal static ControllerData Board(int argbLeds) => new(
        0, DefaultLighting.Motherboard, "ASUS", "", "", "", "HID: aura", 0,
        [new("Direct", 0, 0x20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, [])],
        [new("Aura Mainboard", 0, 1, 1, 1, 0, 0, [], [], 0), new("Aura Addressable 1", 1, 0, 120, (uint)argbLeds, 0, 0, [], [], 0)],
        [.. Enumerable.Range(0, 1 + argbLeds).Select(i => new ControllerLed($"LED {i}", (uint)i))],
        1 + argbLeds, [], 0);

    private static void AssertNear(float x, float y, LedPoint point)
    {
        Assert.Equal(x, point.X, Tolerance);
        Assert.Equal(y, point.Y, Tolerance);
    }
}

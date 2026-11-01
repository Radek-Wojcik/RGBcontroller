using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using RGBcontroller.Core;
using RGBcontroller.Core.Ambilight;
using RGBcontroller.Core.Config;
using Serilog;

namespace RGBcontroller.Agent.Capture;

/// <summary>
/// <c>--capture-probe &lt;seconds&gt; &lt;folder&gt;</c> (spike R7): runs the Screen pipeline without the Service, then writes
/// the downscaled desktop with the sampled bands, the last strip colors and the counters into the folder.
/// </summary>
internal static class CaptureProbe
{
    public static int Run(int seconds, string folder, AppConfig config, ILogger log)
    {
        Directory.CreateDirectory(folder);
        var report = new StringBuilder();
        var recorder = new Recorder(config.Strip.LedCount);
        var started = Stopwatch.StartNew();
        var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        using (var loop = new ScreenCaptureLoop(config.Capture, config.Strip.Layout, config.Strip.LedCount, recorder, log))
        {
            loop.Start();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            var stats = loop.Stats;
            loop.Stop();
            var cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;
            var share = cpu.TotalMilliseconds / started.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
            report.AppendLine(CultureInfo.InvariantCulture, $"Duration {started.Elapsed.TotalSeconds:0.0} s, process CPU {cpu.TotalMilliseconds:0} ms = {share:0.000}% of the whole CPU ({Environment.ProcessorCount} threads)");
            report.AppendLine(CultureInfo.InvariantCulture, $"Frames {stats.Frames} ({stats.Frames / started.Elapsed.TotalSeconds:0.0}/s), sent {stats.Sent} ({stats.Sent / started.Elapsed.TotalSeconds:0.0}/s), unavailable {recorder.Unavailable}, losses {stats.Lost}");
            report.AppendLine(CultureInfo.InvariantCulture, $"Monitor {stats.Monitor}, format {stats.Format}, sample {stats.SampleSize.Width}x{stats.SampleSize.Height}, screen {stats.ScreenWidthCm:0.0} cm, HDR scale {stats.HdrScale:0.00}, content {stats.Content}");
        }

        report.AppendLine("Last strip colors (LED index: R G B):");
        var colors = recorder.Last;
        for (var i = 0; i < colors.Length; i++)
        {
            report.Append(CultureInfo.InvariantCulture, $"{i}:{colors[i].R},{colors[i].G},{colors[i].B} ");
        }

        report.AppendLine();
        SaveSnapshot(config, Path.Combine(folder, "capture-probe.png"), report);
        File.WriteAllText(Path.Combine(folder, "capture-probe.txt"), report.ToString());
        log.Information("Capture probe finished:{NewLine}{Report}", Environment.NewLine, report.ToString());
        return 0;
    }

    /// <summary>One more frame as a PNG with every LED's band outlined, to check the layout and the letterbox detection.</summary>
    private static void SaveSnapshot(AppConfig config, string path, StringBuilder report)
    {
        try
        {
            using var duplicator = DesktopDuplicator.Open(config.Capture.Monitor, config.Capture.MinimumSampleWidth);
            var sink = new SnapshotSink(config, duplicator.Monitor);
            for (var attempt = 0; attempt < 20 && sink.Bitmap is null; attempt++)
            {
                duplicator.TryCapture(100, sink);
            }

            if (sink.Bitmap is null)
            {
                report.AppendLine("Snapshot: no desktop change within 2 s (move a window and run the probe again).");
                return;
            }

            sink.Bitmap.Save(path, ImageFormat.Png);
            sink.Bitmap.Dispose();
            report.AppendLine(CultureInfo.InvariantCulture, $"Snapshot saved to {path}");
        }
        catch (Exception ex)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Snapshot failed: {ex.Message}");
        }
    }

    private sealed class Recorder(int ledCount) : IStripFrameSink
    {
        public RgbColor[] Last { get; } = new RgbColor[ledCount];

        public int Unavailable { get; private set; }

        public void SendColors(ReadOnlySpan<RgbColor> colors) => colors.CopyTo(Last);

        public void SendUnavailable() => Unavailable++;
    }

    private sealed class SnapshotSink(AppConfig config, MonitorInfo monitor) : IScreenImageSink
    {
        public Bitmap? Bitmap { get; private set; }

        public void OnImage(in ScreenImage image)
        {
            var hdrScale = ColorMath.HdrScale(monitor.Hdr ? DisplayInfo.SdrWhiteNits(monitor.Name) ?? 80 : 80);
            var bitmap = new Bitmap(image.Width, image.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            for (var y = 0; y < image.Height; y++)
            {
                for (var x = 0; x < image.Width; x++)
                {
                    var raw = image.Raw(x, y);
                    var c = image.Format == ScreenPixelFormat.Bgra8 ? raw : ColorMath.HdrToSrgb255(raw, hdrScale);
                    bitmap.SetPixel(x, y, Color.FromArgb((int)Math.Clamp(c.X, 0, 255), (int)Math.Clamp(c.Y, 0, 255), (int)Math.Clamp(c.Z, 0, 255)));
                }
            }

            var width = config.Capture.ScreenWidthCm > 0 ? config.Capture.ScreenWidthCm : DisplayInfo.ScreenWidthCm(monitor.Name) ?? 60;
            var layout = new StripLayout(config.Strip.Layout, config.Strip.LedCount);
            var sampler = new EdgeSampler(layout, config.Capture.EdgeDepthCm / width, config.Capture.SegmentOverlapPercent / 100.0);
            var detector = new LetterboxDetector(TimeSpan.Zero);
            detector.Update(image, hdrScale, TimeSpan.Zero);
            sampler.Configure(image.Width, image.Height, detector.Update(image, hdrScale, TimeSpan.FromSeconds(1)));
            using (var graphics = Graphics.FromImage(bitmap))
            using (var pen = new Pen(Color.Magenta))
            using (var first = new Pen(Color.Lime))
            {
                var bands = sampler.Bands;
                for (var i = 0; i < bands.Length; i++)
                {
                    var b = bands[i];
                    if (!b.IsEmpty)
                    {
                        graphics.DrawRectangle(i < 3 ? first : pen, b.X0, b.Y0, b.X1 - b.X0 - 1, b.Y1 - b.Y0 - 1);
                    }
                }
            }

            Bitmap = bitmap;
        }
    }
}

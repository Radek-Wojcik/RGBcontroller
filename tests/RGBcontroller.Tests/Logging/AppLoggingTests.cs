using RGBcontroller.Core.Config;
using RGBcontroller.Core.Logging;

namespace RGBcontroller.Tests.Logging;

public sealed class AppLoggingTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void CreateLogger_WritesStructuredRollingFile()
    {
        var logDir = _dir.Combine("logs");

        using (var logger = AppLogging.CreateLogger("service", logDir, LogLevelSetting.Information))
        {
            logger.ForContext<AppLoggingTests>().Information("Strip on {Port} with {LedCount} LEDs", "COM3", 113);
            logger.Debug("filtered out");
        }

        var file = Assert.Single(Directory.GetFiles(logDir, "service-*.log"));
        var text = File.ReadAllText(file);
        Assert.Contains("[INF] RGBcontroller.Tests.Logging.AppLoggingTests: Strip on COM3 with 113 LEDs", text);
        Assert.DoesNotContain("filtered out", text);
    }
}

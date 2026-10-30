using RGBcontroller.Core.Config;

namespace RGBcontroller.Tests.Config;

public sealed class ConfigValidatorTests
{
    [Fact]
    public void Defaults_AreValid()
    {
        Assert.Empty(ConfigValidator.Validate(AppConfig.CreateDefault()));
    }

    [Theory]
    [InlineData("COM3", true)]
    [InlineData("com12", true)]
    [InlineData("COM0", false)]
    [InlineData("COM3; del", false)]
    [InlineData(@"\\.\COM3", false)]
    public void PortName_MustBeAComPort(string port, bool valid)
    {
        var config = AppConfig.CreateDefault() with { Strip = new StripConfig { PortName = port } };

        Assert.Equal(valid, ConfigValidator.Validate(config).Count == 0);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("192.168.0.5", false)]
    public void OpenRgbHost_MustBeLoopback(string host, bool valid)
    {
        var config = AppConfig.CreateDefault() with { OpenRgb = new OpenRgbConfig { Host = host } };

        Assert.Equal(valid, ConfigValidator.Validate(config).Count == 0);
    }

    [Fact]
    public void ReportsEveryProblem()
    {
        var config = AppConfig.CreateDefault() with
        {
            Strip = new StripConfig { LedCount = 0, MaxFps = 0, UsbVendorId = "xyz" },
            OpenRgb = new OpenRgbConfig { Port = 80 },
        };

        Assert.Equal(4, ConfigValidator.Validate(config).Count);
    }
}

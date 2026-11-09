using RGBcontroller.Service;

namespace RGBcontroller.Tests.Service;

public sealed class LogTailReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rgbc-logtail-" + Guid.NewGuid().ToString("N"));

    public LogTailReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Read_ReturnsTheLastLinesOfTheNewestFile()
    {
        File.WriteAllLines(Path.Combine(_directory, "service-20261108.log"), ["old"]);
        var newest = Path.Combine(_directory, "service-20261109.log");
        File.WriteAllLines(newest, ["one", "two", "three", "four"]);
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddMinutes(1));

        var tail = LogTailReader.Read(_directory, "service", 2);

        Assert.Equal("service-20261109.log", tail.File);
        Assert.Equal(["three", "four"], tail.Lines);
    }

    [Fact]
    public void Read_WorksWhileTheLoggerHoldsTheFileOpen()
    {
        var path = Path.Combine(_directory, "agent-20261109.log");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        writer.Write("a\nb\n"u8);
        writer.Flush();

        Assert.Equal(["a", "b"], LogTailReader.Read(_directory, "agent", 10).Lines);
    }

    [Fact]
    public void Read_ReturnsNothingWithoutAFile()
    {
        Assert.Empty(LogTailReader.Read(_directory, "service", 5).Lines);
        Assert.Empty(LogTailReader.Read(Path.Combine(_directory, "missing"), "service", 5).Lines);
    }

    [Fact]
    public void Read_DoesNotMixComponents()
    {
        File.WriteAllLines(Path.Combine(_directory, "agent-20261109.log"), ["agent line"]);

        Assert.Empty(LogTailReader.Read(_directory, "service", 5).Lines);
    }
}

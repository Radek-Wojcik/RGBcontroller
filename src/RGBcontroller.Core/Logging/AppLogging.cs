using RGBcontroller.Core.Config;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace RGBcontroller.Core.Logging;

/// <summary>Creates the structured, rolling-file logger shared by all RGBcontroller processes.</summary>
public static class AppLogging
{
    public const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    public const int RetainedFileCount = 14;
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;

    /// <param name="component">Process name used as the log file prefix, e.g. "service".</param>
    /// <param name="logDirectory">Target folder; created if missing.</param>
    /// <param name="minimumLevel">Minimum level written to the file.</param>
    /// <param name="alsoWriteToConsole">Mirror output to stdout (console/debug runs only).</param>
    public static Logger CreateLogger(
        string component,
        string logDirectory,
        LogLevelSetting minimumLevel = LogLevelSetting.Information,
        bool alsoWriteToConsole = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        Directory.CreateDirectory(logDirectory);

        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(ToSerilog(minimumLevel))
            .Enrich.WithProperty("Component", component)
            .WriteTo.File(
                Path.Combine(logDirectory, component + "-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedFileCount,
                fileSizeLimitBytes: FileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                outputTemplate: OutputTemplate,
                buffered: true,
                flushToDiskInterval: TimeSpan.FromSeconds(1));

        if (alsoWriteToConsole)
        {
            configuration = configuration.WriteTo.Console(outputTemplate: OutputTemplate);
        }

        return configuration.CreateLogger();
    }

    public static LogEventLevel ToSerilog(LogLevelSetting level) => level switch
    {
        LogLevelSetting.Verbose => LogEventLevel.Verbose,
        LogLevelSetting.Debug => LogEventLevel.Debug,
        LogLevelSetting.Information => LogEventLevel.Information,
        LogLevelSetting.Warning => LogEventLevel.Warning,
        LogLevelSetting.Error => LogEventLevel.Error,
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, null),
    };
}

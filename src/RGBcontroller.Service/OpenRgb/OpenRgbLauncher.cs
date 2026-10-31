using System.Diagnostics;
using RGBcontroller.Core.Config;
using RGBcontroller.Service.Hardware;
using Serilog;

namespace RGBcontroller.Service.OpenRgb;

/// <summary>A running OpenRGB process owned by the supervisor.</summary>
internal interface IOpenRgbProcess : IDisposable
{
    int Id { get; }

    /// <summary>Completes when the process exits.</summary>
    Task Exited { get; }

    void Kill();
}

/// <summary>Starts the pinned OpenRGB and removes instances the Service did not start.</summary>
internal interface IOpenRgbLauncher
{
    IOpenRgbProcess Start();

    /// <summary>Terminates every OpenRGB process this launcher did not start. Returns how many were killed.</summary>
    int KillStrays();
}

/// <summary>
/// Launches the pinned OpenRGB headless with an isolated config folder, bound to loopback, inside a
/// kill-on-close job object. The pinned copy has no plugins folder, so the Effects plugin cannot load.
/// </summary>
internal sealed class OpenRgbLauncher : IOpenRgbLauncher, IDisposable
{
    /// <summary>Written once if missing: keep OpenRGB's own log file on for diagnostics.</summary>
    private const string SeedSettings = """
        {
            "LogManager": {
                "log_file": true
            }
        }
        """;

    private readonly string _executable;
    private readonly string _configDirectory;
    private readonly OpenRgbConfig _config;
    private readonly ILogger _log;
    private readonly JobObject _job = new();

    public OpenRgbLauncher(string executable, string configDirectory, OpenRgbConfig config, ILogger log)
    {
        _executable = executable;
        _configDirectory = configDirectory;
        _config = config;
        _log = log.ForContext<OpenRgbLauncher>();
    }

    public IReadOnlyList<string> Arguments =>
    [
        "--server",
        "--server-host", _config.Host,
        "--server-port", _config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--config", _configDirectory,
        "--noautoconnect",
    ];

    public IOpenRgbProcess Start()
    {
        if (!File.Exists(_executable))
        {
            throw new FileNotFoundException("Pinned OpenRGB executable not found.", _executable);
        }

        Directory.CreateDirectory(_configDirectory);
        var settings = Path.Combine(_configDirectory, "OpenRGB.json");
        if (!File.Exists(settings))
        {
            File.WriteAllText(settings, SeedSettings);
        }

        var startInfo = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_executable)!,
        };
        foreach (var argument in Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("OpenRGB did not start.");
        try
        {
            _job.Assign(process);
        }
        catch
        {
            process.Kill();
            process.Dispose();
            throw;
        }

        _log.Information("Started OpenRGB pid {Pid}: {Executable} {Arguments}", process.Id, _executable, string.Join(' ', Arguments));
        return new OpenRgbProcess(process);
    }

    public int KillStrays()
    {
        var killed = 0;
        foreach (var process in Process.GetProcessesByName("OpenRGB"))
        {
            using (process)
            {
                try
                {
                    if (_job.Contains(process))
                    {
                        continue;
                    }

                    _log.Warning("Terminating stray OpenRGB pid {Pid} (session {Session}) that the Service did not start",
                        process.Id, process.SessionId);
                    process.Kill();
                    process.WaitForExit(5000);
                    killed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _log.Error("Could not terminate stray OpenRGB pid {Pid}: {Error}", process.Id, ex.Message);
                }
            }
        }

        return killed;
    }

    public void Dispose() => _job.Dispose();

    private sealed class OpenRgbProcess(Process process) : IOpenRgbProcess
    {
        public int Id => process.Id;

        public Task Exited { get; } = process.WaitForExitAsync();

        public void Kill()
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }
        }

        public void Dispose() => process.Dispose();
    }
}

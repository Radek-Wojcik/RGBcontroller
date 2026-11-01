using System.ServiceProcess;
using Serilog;

namespace RGBcontroller.Service;

/// <summary>
/// Windows Service shell (LocalSystem, automatic start). A plain ServiceBase rather than the generic host
/// because stage 3 needs power, session and display notifications that WindowsServiceLifetime does not surface.
/// </summary>
internal sealed class RgbControllerService : ServiceBase
{
    public const string Name = "RGBcontroller";

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(4);

    private readonly Func<LightingHost> _createHost;
    private readonly ILogger _log;
    private LightingHost? _host;

    public RgbControllerService(Func<LightingHost> createHost, ILogger log)
    {
        _createHost = createHost;
        _log = log.ForContext<RgbControllerService>();
        ServiceName = Name;
        CanStop = true;
        CanShutdown = true;
        CanHandlePowerEvent = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        _log.Information("Service start requested");
        _host = _createHost();
        _host.Start();
    }

    protected override void OnStop()
    {
        RequestAdditionalTime((int)StopTimeout.TotalMilliseconds + 2000);
        StopHost(StopTimeout);
    }

    protected override void OnShutdown() => StopHost(ShutdownTimeout);

    protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
    {
        var host = _host;
        switch (powerStatus)
        {
            case PowerBroadcastStatus.Suspend:
                host?.OnSuspend();
                break;
            case PowerBroadcastStatus.ResumeAutomatic or PowerBroadcastStatus.ResumeSuspend or PowerBroadcastStatus.ResumeCritical:
                host?.OnResume(powerStatus.ToString());
                break;
        }

        return true;
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription) =>
        _host?.OnSessionChange(changeDescription.Reason.ToString(), changeDescription.SessionId);

    private void StopHost(TimeSpan timeout)
    {
        var host = Interlocked.Exchange(ref _host, null);
        if (host is null)
        {
            return;
        }

        host.StopAsync(timeout).GetAwaiter().GetResult();
        host.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

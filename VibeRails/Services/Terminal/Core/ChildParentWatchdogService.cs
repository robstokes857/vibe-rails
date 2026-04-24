using System.Diagnostics;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Terminal-tab child processes are spawned by the root VibeRails backend with
/// --parent-pid {rootPid}. If the root dies ungracefully (native crash, taskkill /F)
/// its ApplicationStopping hook never runs, so TerminalTabHostService.StopAllAsync
/// is never called and children become orphans. This watchdog holds a handle to
/// the parent process and exits the child when the parent is gone.
///
/// Only registered when --parent-pid is present, i.e., inside tab children. The root
/// backend never runs this.
/// </summary>
public sealed class ChildParentWatchdogService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(1);

    private readonly int _parentPid;
    // Captured at startup so we're bound to the ORIGINAL parent process. A later
    // Process.GetProcessById(pid) lookup is vulnerable to OS PID reuse once the
    // root dies; holding the handle keeps HasExited answering about the right one.
    private readonly Process? _parent;

    public ChildParentWatchdogService()
    {
        _parentPid = TryParseParentPid(Environment.GetCommandLineArgs());
        if (_parentPid > 0)
        {
            try
            {
                _parent = Process.GetProcessById(_parentPid);
            }
            catch
            {
                _parent = null;
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_parent is null)
        {
            Serilog.Log.Warning(
                "[ChildParentWatchdog] Could not attach to parent process; watchdog will not run. parentPid={ParentPid} pid={Pid}",
                _parentPid, Environment.ProcessId);
            return;
        }

        Serilog.Log.Information(
            "[ChildParentWatchdog] Started. parentPid={ParentPid} pollIntervalSeconds={Seconds} pid={Pid}",
            _parentPid, (int)PollInterval.TotalSeconds, Environment.ProcessId);

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (!_parent.HasExited)
                {
                    continue;
                }

                Serilog.Log.Warning(
                    "[ChildParentWatchdog] Parent process is gone. Exiting child to avoid orphan. parentPid={ParentPid} pid={Pid}",
                    _parentPid, Environment.ProcessId);
                ShutdownDiagnostics.RecordStopRequest(
                    "ChildParentWatchdog",
                    $"parentPid={_parentPid}; pid={Environment.ProcessId}");
                Serilog.Log.CloseAndFlush();
                Environment.Exit(0);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public override void Dispose()
    {
        _parent?.Dispose();
        base.Dispose();
    }

    private static int TryParseParentPid(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
                {
                    return pid;
                }
            }
            else if (arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(arg.AsSpan("--parent-pid=".Length), out var pid))
            {
                return pid;
            }
        }
        return 0;
    }
}

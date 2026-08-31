using System.Text.Json;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.DTOs;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Hidden lifecycle surface used by release installers before and after replacing application
/// files. It deliberately builds no web host and emits machine-readable JSON when requested.
/// </summary>
public static class JobDaemonMaintenanceProcessHost
{
    public const string Argument = "--job-daemon-service";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        TryGetCommand(args, out _);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter? output = null,
        TextWriter? error = null,
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;
        if (!TryGetCommand(args, out var command))
        {
            await error.WriteLineAsync(
                "Usage: vb --job-daemon-service <status|stop|repair|start> [--json]").ConfigureAwait(false);
            return 2;
        }

        if (string.IsNullOrWhiteSpace(command) || command.StartsWith("--", StringComparison.Ordinal))
        {
            await error.WriteLineAsync(
                "Usage: vb --job-daemon-service <status|stop|repair|start> [--json]").ConfigureAwait(false);
            return 2;
        }

        var knownCommand = command.Equals("status", StringComparison.OrdinalIgnoreCase)
            || command.Equals("stop", StringComparison.OrdinalIgnoreCase)
            || command.Equals("repair", StringComparison.OrdinalIgnoreCase)
            || command.Equals("start", StringComparison.OrdinalIgnoreCase);
        if (!knownCommand)
        {
            await error.WriteLineAsync(
                $"Unknown VBD lifecycle command '{command}'. Expected status, stop, repair, or start.")
                .ConfigureAwait(false);
            return 2;
        }

        var identityProvider = new CurrentUserIdentityProvider();
        var controlClient = new DaemonControlClient();
        var service = new JobDaemonLifecycleService(
            new JobDaemonRegistrationProvider(),
            controlClient,
            identityProvider);
        var json = args.Any(argument => argument.Equals("--json", StringComparison.OrdinalIgnoreCase));

        if (command.Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            var status = await service.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (json)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    status,
                    AppJsonSerializerContext.Default.JobDaemonStatusResponse)).ConfigureAwait(false);
            }
            else
            {
                await output.WriteLineAsync(
                    $"VibeRails Demon: {status.State} (installed={status.IsInstalled}, running={status.IsRunning})")
                    .ConfigureAwait(false);
            }
            return 0;
        }

        JobDaemonActionResponse result;
        if (command.Equals("stop", StringComparison.OrdinalIgnoreCase))
            result = await service.StopAsync(cancellationToken).ConfigureAwait(false);
        else if (command.Equals("repair", StringComparison.OrdinalIgnoreCase))
            result = await service.RepairAsync(cancellationToken).ConfigureAwait(false);
        else
            result = await service.StartAsync(cancellationToken).ConfigureAwait(false);

        if (json)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                result,
                AppJsonSerializerContext.Default.JobDaemonActionResponse)).ConfigureAwait(false);
        }
        else
        {
            await (result.Success ? output : error).WriteLineAsync(result.Message).ConfigureAwait(false);
        }
        return result.Success ? 0 : 1;
    }

    private static bool TryGetCommand(IReadOnlyList<string> args, out string command)
    {
        command = string.Empty;
        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument == "--")
                return false;
            if (argument.Equals(Argument, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Count && args[index + 1] != "--")
                    command = args[index + 1];
                return true;
            }
            if (argument.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))
            {
                command = argument[(Argument.Length + 1)..];
                return true;
            }
        }

        return false;
    }
}

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookCommandParser
{
    VcaHookInvocation Parse(string[] args);
}

public sealed class VcaHookCommandParser : IVcaHookCommandParser
{
    private static readonly TimeSpan DefaultDemoDuration = TimeSpan.FromSeconds(3);

    public static bool IsRequested(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--", StringComparison.Ordinal))
            {
                return false;
            }

            if (arg.Equals("--vca-hook", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--validate-vca", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--commit-msg", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public VcaHookInvocation Parse(string[] args)
    {
        var kind = VcaHookKind.PreCommit;
        string? commitMessagePath = null;
        string? workingDirectory = null;
        var demoUi = false;
        var demoDuration = DefaultDemoDuration;
        var promptForAcknowledgment = false;
        var showConsoleWindow = false;
        var consoleWindowAttached = false;
        var coAuthorsAlreadyCleaned = false;
        var enqueueAutomatedJobs = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--", StringComparison.Ordinal))
            {
                break;
            }

            if (arg.Equals("--vca-hook", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    kind = ParseKind(args[++i]);
                }
                continue;
            }

            if (arg.Equals("--validate-vca", StringComparison.OrdinalIgnoreCase))
            {
                kind = VcaHookKind.PreCommit;
                continue;
            }

            if (arg.Equals("--commit-msg", StringComparison.OrdinalIgnoreCase))
            {
                kind = VcaHookKind.CommitMessage;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    commitMessagePath = args[++i];
                }
                continue;
            }

            if (arg.Equals("--commit-message", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--commit-message-path", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    commitMessagePath = args[++i];
                }
                continue;
            }

            if (arg.Equals("--workdir", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--working-directory", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length)
                {
                    workingDirectory = args[++i];
                }
                continue;
            }

            if (arg.Equals("--demo-ui", StringComparison.OrdinalIgnoreCase))
            {
                demoUi = true;
                continue;
            }

            if (arg.Equals("--prompt-ack", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--prompt-acknowledgment", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--prompt-acknowledgement", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--prompt-acknowledgments", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--prompt-acknowledgements", StringComparison.OrdinalIgnoreCase))
            {
                promptForAcknowledgment = true;
                continue;
            }

            if (arg.Equals("--console-window", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--show-console", StringComparison.OrdinalIgnoreCase))
            {
                showConsoleWindow = true;
                continue;
            }

            if (arg.Equals("--console-window-attached", StringComparison.OrdinalIgnoreCase))
            {
                showConsoleWindow = true;
                consoleWindowAttached = true;
                continue;
            }

            if (arg.Equals("--co-authors-cleaned", StringComparison.OrdinalIgnoreCase))
            {
                coAuthorsAlreadyCleaned = true;
                continue;
            }

            if (arg.Equals("--enqueue-automations", StringComparison.OrdinalIgnoreCase))
            {
                enqueueAutomatedJobs = true;
                continue;
            }

            if (arg.Equals("--demo-duration-ms", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var ms) && ms > 0)
                {
                    demoDuration = TimeSpan.FromMilliseconds(ms);
                }
            }
        }

        if (kind == VcaHookKind.Preview)
        {
            demoUi = true;
        }

        return new VcaHookInvocation(
            kind,
            commitMessagePath,
            workingDirectory,
            demoUi,
            demoDuration,
            promptForAcknowledgment,
            showConsoleWindow,
            consoleWindowAttached,
            WorkingTreeScope: false,
            CoAuthorsAlreadyCleaned: coAuthorsAlreadyCleaned,
            EnqueueAutomatedJobs: enqueueAutomatedJobs);
    }

    private static VcaHookKind ParseKind(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "pre-commit" => VcaHookKind.PreCommit,
            "precommit" => VcaHookKind.PreCommit,
            "commit-msg" => VcaHookKind.CommitMessage,
            "commit-message" => VcaHookKind.CommitMessage,
            "clean-commit-msg" => VcaHookKind.CleanCommitMessage,
            "clean-commit-message" => VcaHookKind.CleanCommitMessage,
            "ack" => VcaHookKind.AcknowledgeCommitMessage,
            "acknowledge" => VcaHookKind.AcknowledgeCommitMessage,
            "acknowledge-commit-msg" => VcaHookKind.AcknowledgeCommitMessage,
            "acknowledge-commit-message" => VcaHookKind.AcknowledgeCommitMessage,
            "preview" => VcaHookKind.Preview,
            "demo" => VcaHookKind.Preview,
            _ => VcaHookKind.PreCommit
        };
    }
}

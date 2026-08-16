using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class AutomationTerminalTabArgumentsTests
{
    [Fact]
    public void InteractiveAutomationChild_KeepsHostFlagsBeforeCliPassthrough()
    {
        var run = new JobRunRecord(
            "run-123", 4, JobTriggerKind.Manual, "manual:key", JobRunStatus.Queued,
            "Review", @"C:\source\repo", LLM.Codex, 19, "Review Worker", 45, null,
            DateTime.UtcNow, null, null, null, null, false, null);
        var environment = new LLM_Environment
        {
            Id = 19,
            LLM = LLM.Codex,
            CustomName = "Review Worker",
            CustomArgs = "--model gpt-5.6"
        };

        var args = TerminalTabHostService.BuildAutomationChildArguments(
            run,
            environment,
            @"C:\workspaces\review",
            parentProcessId: 4242);

        var separator = Array.IndexOf(args, "--");
        Assert.True(separator > 0);
        Assert.Equal("Review Worker", ValueAfter(args, "--env"));
        Assert.Equal(@"C:\workspaces\review", ValueAfter(args, "--workdir"));
        Assert.Equal("run-123", ValueAfter(args, "--job-run"));
        Assert.Equal("45", ValueAfter(args, "--max-runtime"));
        Assert.Equal("19", ValueAfter(args, "--env-id"));
        Assert.Equal("4242", ValueAfter(args, "--parent-pid"));
        Assert.True(Array.IndexOf(args, "--vs-code-v1") < separator);
        Assert.True(Array.IndexOf(args, "--job-run") < separator);
        Assert.Equal(["--model", "gpt-5.6"], args[(separator + 1)..]);

        var parsed = ArgumentParser.Parse(args);
        Assert.True(parsed.IsLMBootstrap);
        Assert.True(parsed.IsVsCodeMode);
        Assert.Equal("run-123", parsed.JobRunId);
        Assert.Equal(19, parsed.EnvId);
        Assert.Equal(45, parsed.MaxRuntimeMinutes);
        Assert.Equal(["--model", "gpt-5.6"], parsed.ExtraArgs);
    }

    private static string ValueAfter(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        Assert.InRange(index, 0, args.Length - 2);
        return args[index + 1];
    }
}

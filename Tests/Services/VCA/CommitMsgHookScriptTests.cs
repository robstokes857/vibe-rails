using System.Diagnostics;
using System.Text;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// Runs the real commit-msg hook script with a stub <c>vb</c> that records each launch (VIBE-327).
/// Message cleanup is its own launch only when a chained hook has to see the cleaned message;
/// otherwise the validation launch cleans it, saving one VibeRails start on every commit.
/// </summary>
public sealed class CommitMsgHookScriptTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-commit-msg-script-" + Guid.NewGuid().ToString("N"));

    public CommitMsgHookScriptTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task WithoutAChainedHookTheValidationLaunchCleansTheMessage()
    {
        var launches = await RunHookAsync(chained: false);
        var launch = Assert.Single(launches);
        Assert.StartsWith("vb --vca-hook commit-msg --enqueue-automations --commit-message ", launch);
        Assert.DoesNotContain("--co-authors-cleaned", launch);
    }

    [Fact]
    public async Task AChainedHookSeesTheCleanedMessageAndValidationIsToldItWasCleaned()
    {
        var launches = await RunHookAsync(chained: true);
        Assert.Equal(3, launches.Count);
        Assert.StartsWith("vb --vca-hook clean-commit-msg --commit-message ", launches[0]);
        Assert.StartsWith("chained ", launches[1]);
        Assert.StartsWith("vb --vca-hook commit-msg --enqueue-automations --co-authors-cleaned --commit-message ", launches[2]);
    }

    private async Task<IReadOnlyList<string>> RunHookAsync(bool chained)
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : "/bin/sh";
        if (!File.Exists(shell)) Assert.Skip("A POSIX shell is required to run the hook script.");
        var log = Path.Combine(_directory, "launches.log");
        var stub = await WriteScriptAsync("vb-stub", $"#!/bin/sh\necho \"vb $*\" >> '{ToShellPath(log)}'\nexit 0\n");
        var chainedHook = chained
            ? await WriteScriptAsync("chained-hook", $"#!/bin/sh\necho \"chained $*\" >> '{ToShellPath(log)}'\nexit 0\n")
            : "";
        var template = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "VibeRails", "scripts", "commit-msg-hook.sh"),
            TestContext.Current.CancellationToken);
        var hook = await WriteScriptAsync("commit-msg", template.Replace("\r\n", "\n")
            .Replace("__VIBERAILS_HOOK_VERSION__", "1.0.0")
            .Replace("__VIBERAILS_EXECUTABLE__", ToShellPath(stub))
            .Replace("__VIBERAILS_EXECUTABLE_ARGUMENT__", "")
            .Replace("__VIBERAILS_CHAINED_HOOK__", chainedHook.Length == 0 ? "" : ToShellPath(chainedHook)));
        var message = Path.Combine(_directory, "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(message, "Message\n", TestContext.Current.CancellationToken);

        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = _directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(ToShellPath(hook));
        start.ArgumentList.Add(ToShellPath(message));
        // A CI marker keeps the Windows console-window branch from ever opening a popup here.
        start.Environment["CI"] = "1";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"hook exited {process.ExitCode}\n{await output}\n{await error}");
        return File.Exists(log) ? await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken) : [];
    }

    private async Task<string> WriteScriptAsync(string name, string text)
    {
        var path = Path.Combine(_directory, name);
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static string ToShellPath(string path) => OperatingSystem.IsWindows() && path.Length > 2 && path[1] == ':'
        ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
        : path;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}

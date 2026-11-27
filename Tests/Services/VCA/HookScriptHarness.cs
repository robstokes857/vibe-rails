using System.Diagnostics;
using System.Text;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// Runs a real hook script template from <c>VibeRails/scripts</c> with stubs that record each launch:
/// <c>vb</c> (the recorded executable), and <c>git</c> and <c>uname</c> first on PATH. Every launch is
/// one line in the log, so a test can count the programs a commit starts (VIBE-327, VIBE-374).
/// </summary>
internal sealed class HookScriptHarness : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-hook-script-" + Guid.NewGuid().ToString("N"));

    public HookScriptHarness() => System.IO.Directory.CreateDirectory(_directory);

    public string Directory => _directory;

    public string LogPath => Path.Combine(_directory, "launches.log");

    /// <summary>The fake commit the stub git reports for <c>rev-parse HEAD</c>.</summary>
    public const string HeadCommit = "0123456789abcdef0123456789abcdef01234567";

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_directory, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public static string Shell()
    {
        var shell = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe")
            : "/bin/sh";
        if (!File.Exists(shell)) Assert.Skip("A POSIX shell is required to run the hook script.");
        return shell;
    }

    /// <summary>
    /// Runs <paramref name="script"/> with the given hook arguments and returns its exit code once the
    /// hook script itself has exited. A background launch may still be running; read the log later.
    /// </summary>
    public async Task<int> RunAsync(string script, IReadOnlyList<string> arguments, string vbStub, string chainedHook = "")
    {
        var shell = Shell();
        var stubs = Path.Combine(_directory, "bin");
        System.IO.Directory.CreateDirectory(stubs);
        var log = ToShellPath(LogPath);
        await WriteScriptAsync(Path.Combine(stubs, "git"),
            $"#!/bin/sh\necho \"git $*\" >> '{log}'\n"
            + $"if [ \"$1\" = rev-parse ] && [ \"$2\" = HEAD ]; then echo {HeadCommit}; exit 0; fi\nexit 1\n");
        await WriteScriptAsync(Path.Combine(stubs, "uname"), $"#!/bin/sh\necho \"uname $*\" >> '{log}'\necho Stub\n");
        var vb = await WriteScriptAsync(Path.Combine(_directory, "vb-stub"), vbStub);
        var template = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "VibeRails", "scripts", script),
            TestContext.Current.CancellationToken);
        var hook = await WriteScriptAsync(Path.Combine(_directory, Path.GetFileNameWithoutExtension(script)),
            template.Replace("\r\n", "\n")
                .Replace("__VIBERAILS_HOOK_VERSION__", "1.0.0")
                .Replace("__VIBERAILS_EXECUTABLE__", ToShellPath(vb))
                .Replace("__VIBERAILS_EXECUTABLE_ARGUMENT__", "")
                .Replace("__VIBERAILS_CHAINED_HOOK__", chainedHook.Length == 0 ? "" : ToShellPath(chainedHook)));

        var start = new ProcessStartInfo(shell)
        {
            WorkingDirectory = _directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // The stubs go first on PATH from inside the shell: Git for Windows' bash.exe puts its own
        // directories, and so its real git, ahead of anything set here. The hook then runs under
        // /bin/sh, as its shebang asks Git to run it.
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"PATH='{ToShellPath(stubs)}':\"$PATH\"; export PATH; exec /bin/sh \"$0\" \"$@\"");
        start.ArgumentList.Add(ToShellPath(hook));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        // A CI marker keeps the Windows console-window branch from ever opening a popup here.
        start.Environment["CI"] = "1";
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        // WaitForExitAsync also waits for both streams to close, as a Git GUI reading the hook's output
        // does, so a background launch that kept them open shows up here as a hang.
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => { lock (output) output.AppendLine(line.Data); };
        process.ErrorDataReceived += (_, line) => { lock (output) output.AppendLine(line.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(
            TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        if (process.ExitCode != 0)
        {
            lock (output) Assert.Fail($"{script} exited {process.ExitCode}\n{output}");
        }
        return process.ExitCode;
    }

    public async Task<IReadOnlyList<string>> LaunchesAsync() =>
        File.Exists(LogPath) ? await File.ReadAllLinesAsync(LogPath, TestContext.Current.CancellationToken) : [];

    /// <summary>A stub vb that records its arguments and exits 0.</summary>
    public string RecordingVb() => $"#!/bin/sh\necho \"vb $*\" >> '{ToShellPath(LogPath)}'\nexit 0\n";

    public async Task<string> WriteScriptAsync(string path, string text)
    {
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    public static string ToShellPath(string path) => OperatingSystem.IsWindows() && path.Length > 2 && path[1] == ':'
        ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/')
        : path;

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }
}

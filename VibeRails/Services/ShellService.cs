using System.Diagnostics;
using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services;

public interface IShellService
{
    Task<string> RunAsync(CancellationToken cancellationToken, params string[] args);
}

public class ShellService(ILogger<ShellService> logger) : IShellService
{
    public async Task<string> RunAsync(CancellationToken cancellationToken, params string[] args)
    {
        var psi = OperatingSystem.IsWindows()
            ? BuildWindowsStartInfo(args)
            : BuildUnixStartInfo(args);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0)
        {
            logger.LogWarning("Process {Command} exited with code {ExitCode}: {Stderr}",
                args[0], process.ExitCode, stderr);
            throw new InvalidOperationException(
                $"{args[0]} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return stdout;
    }

    // Windows: open pwsh and pass the command via -EncodedCommand (UTF-16LE base64)
    // so quoting is safe regardless of arg content.
    private static ProcessStartInfo BuildWindowsStartInfo(string[] args)
    {
        var psArgs = string.Join(" ", args.Select(a => "'" + a.Replace("'", "''") + "'"));
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes($"& {psArgs}"));

        var psi = new ProcessStartInfo
        {
            FileName = ShellDefaults.WindowsCommandShell,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(encoded);
        return psi;
    }

    // Unix-like: open the platform command shell, pass args as positional
    // parameters via "$@" so each arg is forwarded verbatim without shell
    // re-quoting issues.
    // zsh/bash -c '"$@"' -- cmd arg1 arg2 ...
    //   $0 = "--" (placeholder script name)
    //   $@ = cmd arg1 arg2 ... → exec'd by the shell
    private static ProcessStartInfo BuildUnixStartInfo(string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ShellDefaults.GetUnixCommandShellPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("\"$@\"");
        psi.ArgumentList.Add("--");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        return psi;
    }
}

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails.Services.Setup;

/// <summary>
/// Runs one catalog install command in a private PTY window. The viewer names a tool ID and a
/// method ID; the command text comes from <see cref="SetupToolCatalog"/> and nothing else. It
/// runs in a shell because vendor installers are pipelines (<c>irm … | iex</c>,
/// <c>curl … | bash</c>) and npm is a script shim, but no request field reaches that text.
/// </summary>
public static class SetupInstallTerminalSession
{
    private static readonly PrivatePtyText Text = new(
        "Invalid install request.", "Could not start the installer.", "Install finished");

    private const string HomebrewBin = "/opt/homebrew/bin";

    public static Task RunAsync(WebSocket socket, CancellationToken cancellationToken) =>
        PrivatePtySession.RunAsync(socket, (message, _) => Task.FromResult(Prepare(message)), Text, cancellationToken);

    private static PrivatePtyLaunch Prepare(ReadOnlyMemory<byte> message)
    {
        var request = JsonSerializer.Deserialize(message.Span, AppJsonSerializerContext.Default.SetupInstallTerminalRequest)
            ?? throw new ArgumentException("Missing install request.");
        PrivatePtySession.ValidateSize(request.Cols, request.Rows);
        var entry = SetupToolCatalog.FindMethod(SetupToolCatalog.Current, request.Tool, request.Method)
            ?? throw new PrivatePtyRejectedException("That install option is not available on this machine.");
        return new PrivatePtyLaunch(CreateStartInfo(entry.Method), request.Cols, request.Rows);
    }

    /// <summary>The shell process for one catalog command. Public for tests.</summary>
    public static ProcessStartInfo CreateStartInfo(SetupInstallMethod method)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (OperatingSystem.IsWindows())
        {
            // PowerShell 7 may be the very thing being installed, so fall back to the Windows
            // PowerShell 5.1 every Windows install has, by absolute path rather than PATH.
            start.FileName = FindPwsh() ?? Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe");
            // Bypass is process-scoped. 5.1's default Restricted policy would refuse npm's .ps1 shim.
            foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", method.Command })
                start.ArgumentList.Add(argument);
        }
        else
        {
            // A login shell, so Homebrew, nvm and ~/.local/bin are on PATH as in the user's terminal.
            start.FileName = ShellDefaults.GetUnixCommandShellPath();
            foreach (var argument in new[] { "-l", "-c", method.Command })
                start.ArgumentList.Add(argument);
        }

        // An installer that ran a minute ago (Node.js before npm, say) changed the PATH Windows
        // stores, not the one this process started with.
        start.Environment["PATH"] = SetupProbeEnvironment.CurrentPathValue();
        // Homebrew installed from this page a minute ago is only in the login profile, which
        // this VibeRails process has not read.
        if (OperatingSystem.IsMacOS())
            start.Environment["PATH"] = $"{HomebrewBin}{Path.PathSeparator}{start.Environment["PATH"]}";
        return start;
    }

    private static string? FindPwsh()
    {
        var tool = SetupToolCatalog.For(SetupPlatform.Windows).First(candidate => candidate.Id == SetupToolCatalog.PowerShell);
        var found = SetupToolProbe.Probe(tool, SetupProbeEnvironment.ForCurrentMachine());
        return found.Path is { } path && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }
}

using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.PythonScripts;

/// <summary>
/// The code-sign helper: <c>vb --sign-script &lt;name&gt;.py</c>. Runs without
/// Kestrel/auth/browser startup — signing state is file-based, so this process talks
/// straight to <see cref="PythonScriptService"/>. The PIN is read from an interactive
/// console only, with echo suppressed, and never printed or logged. Piped/redirected
/// stdin is refused so that <c>echo pin | vb --sign-script x.py</c> cannot enrol or
/// approve unattended — a speed bump, not a boundary: an agent running as the user can
/// still drive a pseudo-console (see the threat model on <see cref="PythonScriptService"/>).
/// </summary>
public static class PythonScriptSignProcessHost
{
    public const string Flag = "--sign-script";
    private const string ArgumentSeparator = "--";

    public static bool IsRequested(string[] args) => FindFlagIndex(args) >= 0;

    /// <summary>
    /// The flag's position among vb's own top-level arguments — i.e. before the first
    /// <c>--</c> separator. Anything after <c>--</c> is forwarded verbatim to the CLI
    /// vb launches (e.g. <c>vb --env x -- --sign-script</c>) and must never switch vb
    /// into signing mode. Mirrors <see cref="VCA.Hooks.VcaHookCommandParser.IsRequested"/>.
    /// </summary>
    internal static int FindFlagIndex(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], ArgumentSeparator, StringComparison.Ordinal))
            {
                return -1;
            }

            if (string.Equals(args[index], Flag, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    /// <param name="installDirectory">Overrides <c>~/.vibe_rails</c>; tests only.</param>
    public static async Task<int> RunAsync(string[] args, string? installDirectory = null)
    {
        var flagIndex = FindFlagIndex(args);
        var name = flagIndex >= 0 && flagIndex + 1 < args.Length ? args[flagIndex + 1] : null;
        if (string.Equals(name, ArgumentSeparator, StringComparison.Ordinal))
        {
            name = null;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            Console.Error.WriteLine($"Usage: vb {Flag} <script-name>.py");
            return 1;
        }

        // Accept a full path to a script that already lives in the scripts folder; the
        // service validates the bare name either way.
        name = Path.GetFileName(name.Trim());

        var service = new PythonScriptService(installDirectory: installDirectory);
        try
        {
            var status = await service.GetStatusAsync();
            if (!status.PinConfigured)
            {
                Console.WriteLine("No signing PIN is configured yet. Create one now.");
                var first = ReadPin("New PIN (4+ characters): ");
                var second = ReadPin("Confirm PIN: ");
                if (!string.Equals(first, second, StringComparison.Ordinal))
                {
                    Console.Error.WriteLine("PINs did not match. Nothing was changed.");
                    return 1;
                }

                await service.SetPinAsync(new SetPythonScriptPinRequest(null, first));
                Console.WriteLine("Signing PIN created.");
            }

            var pin = ReadPin("Signing PIN: ");
            var result = await service.ApproveAsync(new PythonScriptApprovalRequest(name, pin));
            // Approvals key off the file's on-disk casing; prefer the exact entry and only
            // fall back to a case-insensitive match for the typed name.
            var script = result.Scripts.FirstOrDefault(entry =>
                    string.Equals(entry.Name, name, StringComparison.Ordinal))
                ?? result.Scripts.FirstOrDefault(entry =>
                    string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            Console.WriteLine(script is { Status: PythonScriptService.StatusApproved }
                ? $"Signed: {script.Name} is approved to run."
                : $"Approval recorded, but '{name}' reports status '{script?.Status ?? "missing"}'.");
            return 0;
        }
        catch (PythonScriptValidationException exception)
        {
            Console.Error.WriteLine($"Sign failed: {exception.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Masked interactive PIN prompt. Redirected stdin is refused rather than read: the
    /// PIN exists to require a person at the keyboard, so accepting it from a pipe would
    /// make first-time enrolment and every approval scriptable. Echo suppression only —
    /// the PIN itself never leaves this process except as the PBKDF2 check inside the
    /// service.
    /// </summary>
    private static string ReadPin(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            throw new PythonScriptValidationException(
                "The signing PIN must be typed at an interactive console; it cannot be piped in. "
                + "Run vb --sign-script from a terminal, or sign from the dashboard's Automation page.");
        }

        Console.Write(prompt);
        var builder = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return builder.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0) builder.Length--;
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }
    }
}

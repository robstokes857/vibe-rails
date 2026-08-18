using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.PythonScripts;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Teaches an agent the signed-Python-script workflow and reports current signing
/// state. Deliberately read-only: the entire point of hash pinning is that only the
/// human, by entering their PIN, can bless a script — so this tool explains the rules
/// and lists statuses, and there is no MCP surface that accepts a PIN, approves a
/// script, or runs one. Keep it that way: adding a signing or run tool here would let
/// an agent bless its own code and defeat the gate.
/// </summary>
[McpServerToolType]
public sealed class PythonScriptTool
{
    private readonly IPythonScriptService _pythonScriptService;

    public PythonScriptTool(IPythonScriptService pythonScriptService)
    {
        _pythonScriptService = pythonScriptService;
    }

    [McpServerTool]
    [Description(
        "Explains how VibeRails' signed Python scripts work — where to save a script, how it "
        + "gets signed (approved) by the user, and why you cannot sign or run one yourself — "
        + "and lists every current script with its signing status (approved / modified / "
        + "unapproved). Call this before creating or editing a script in the VibeRails scripts "
        + "folder, and afterwards to confirm what the user still needs to approve.")]
    public async Task<string> PythonScriptSigningHelp(CancellationToken cancellationToken = default)
    {
        var status = await _pythonScriptService.GetStatusAsync(cancellationToken);
        var builder = new StringBuilder();
        builder.AppendLine("# VibeRails signed Python scripts");
        builder.AppendLine();
        builder.AppendLine($"Scripts folder: {status.ScriptsDirectory}");
        builder.AppendLine();
        builder.AppendLine("## How it works");
        builder.AppendLine(
            "1. A script is one self-contained .py file saved directly in the scripts folder "
            + "(no subfolders, no sibling imports — anything it imports must be stdlib or "
            + "installed packages).");
        builder.AppendLine(
            "2. Before a script can run, the USER must sign (approve) it by entering their "
            + "signing PIN in the VibeRails Automation page, or with the CLI helper: "
            + "vb --sign-script <name>.py");
        builder.AppendLine(
            "3. Signing records the script's canonical SHA-256 (strict UTF-8 — files with "
            + "invalid UTF-8 bytes are refused — BOM stripped, line endings normalized to LF, "
            + "file name mixed in). Any edit — yours included — invalidates the signature; "
            + "the user must re-approve before the new version runs.");
        builder.AppendLine(
            "4. At run time VibeRails re-hashes the file and runs it only on an exact match, "
            + "executing the verified bytes.");
        builder.AppendLine();
        builder.AppendLine("## What you (the agent) can and cannot do");
        builder.AppendLine("- You MAY create or edit .py files in the scripts folder.");
        builder.AppendLine(
            "- You CANNOT sign, approve, or run a script: only the user can, by entering their "
            + "PIN. Never ask the user to tell you the PIN — you never need it, and it must not "
            + "enter this conversation.");
        builder.AppendLine(
            "- After saving or changing a script, tell the user which script needs approval and "
            + "where (Automation page → Python scripts, or vb --sign-script).");
        builder.AppendLine();
        builder.AppendLine(status.PinConfigured
            ? "A signing PIN is configured."
            : "No signing PIN is configured yet — the user must set one in the Automation page "
              + "before any script can be approved.");
        builder.AppendLine();
        builder.AppendLine("## Current scripts");
        if (status.Scripts.Count == 0)
        {
            builder.AppendLine("(none yet)");
        }
        else
        {
            foreach (var script in status.Scripts)
            {
                builder.AppendLine($"- {script.Name}: {script.Status}");
            }
        }

        return builder.ToString();
    }
}

using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.PythonScripts;

namespace VibeRails.Services.Mcp.Tools;

/// <summary>
/// Teaches an agent the signed-Python-script workflow and reports current signing
/// state. This guidance tool is deliberately read-only: no MCP call accepts a PIN or
/// approves code. A separate dynamic tool may run a script only after the user has both
/// signed that exact content and explicitly enabled its MCP mapping in the dashboard.
/// </summary>
[McpServerToolType]
public sealed class PythonScriptTool
{
    private readonly IPythonScriptService _pythonScriptService;
    private readonly IPythonScriptMcpService _pythonScriptMcpService;

    public PythonScriptTool(
        IPythonScriptService pythonScriptService,
        IPythonScriptMcpService pythonScriptMcpService)
    {
        _pythonScriptService = pythonScriptService;
        _pythonScriptMcpService = pythonScriptMcpService;
    }

    [McpServerTool]
    [Description(
        "Explains how VibeRails' signed Python scripts work — where to save a script, how it "
        + "gets signed (approved) by the user, and why you cannot sign or expose one yourself. "
        + "Lists every script's signing status plus the scripts the user explicitly exposed as "
        + "dynamic MCP tools. Call this before creating or editing a script in the VibeRails "
        + "scripts folder, and afterwards to confirm what the user still needs to approve.")]
    public async Task<string> PythonScriptSigningHelp(CancellationToken cancellationToken = default)
    {
        var status = await _pythonScriptService.GetStatusAsync(cancellationToken);
        var mcp = await _pythonScriptMcpService.GetAsync(cancellationToken);
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
            "- You CANNOT sign, approve, or expose a script to MCP: only the user can, by entering "
            + "their PIN in the dashboard. Never ask the user to tell you the PIN — you never "
            + "need it, and it must not enter this conversation.");
        builder.AppendLine(
            "- You MAY call a separately advertised Python script tool. Each call re-checks that "
            + "the script bytes still match the user's signed hash and passes its declared inputs "
            + "as argv values without a shell.");
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

        builder.AppendLine();
        builder.AppendLine("## Scripts explicitly exposed as MCP tools");
        if (mcp.Configurations.Count == 0)
        {
            builder.AppendLine("(none)");
        }
        else
        {
            foreach (var configuration in mcp.Configurations)
            {
                builder.AppendLine($"- {configuration.ToolName}: {configuration.ScriptName}");
            }
        }

        return builder.ToString();
    }
}

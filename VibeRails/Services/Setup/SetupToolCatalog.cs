namespace VibeRails.Services.Setup;

/// <summary>The operating system a catalog is built for.</summary>
public enum SetupPlatform { Windows, MacOS, Linux }

/// <summary>How a tool is presented on the Setup page.</summary>
public enum SetupToolGroup
{
    /// <summary>VibeRails cannot launch anything without it.</summary>
    Required,
    /// <summary>The AI coding CLIs; the user needs at least one.</summary>
    AgentCli,
    /// <summary>Unlocks a feature (scripts, npm installs) but nothing breaks without it.</summary>
    Optional,
    /// <summary>A package manager other install methods depend on. Listed only while missing.</summary>
    PackageManager
}

/// <summary>
/// One way to install a tool. <see cref="Command"/> is a compile-time constant: the install
/// terminal runs exactly this text, selected by tool and method ID, and the page shows it
/// verbatim for copying. No request field is ever spliced into it.
/// </summary>
public sealed record SetupInstallMethod(
    string Id,
    string Label,
    string Command,
    string? Requires = null,
    string? Note = null);

/// <summary>
/// A tool the Setup page checks. <see cref="Commands"/> are looked up on PATH (with Windows
/// launcher extensions); <see cref="KnownPaths"/> are exact files vendors install to, which may
/// not be on PATH yet. Install methods are in preference order; the first is the recommended one.
/// <see cref="WindowsAppsStubs"/> are command names whose Microsoft\WindowsApps alias is a Store
/// redirect rather than the tool, so finding one there does not count.
/// <see cref="AppleDeveloperShims"/> are command names whose macOS <c>/usr/bin</c> copy is Apple's
/// shim, which counts only once the Command Line Tools or Xcode provide the real tool.
/// </summary>
public sealed record SetupTool(
    string Id,
    string Name,
    SetupToolGroup Group,
    string Why,
    string DocsUrl,
    IReadOnlyList<string> Commands,
    IReadOnlyList<string> KnownPaths,
    IReadOnlyList<SetupInstallMethod> Methods,
    string? Brand = null,
    IReadOnlyList<string>? WindowsAppsStubs = null,
    IReadOnlyList<string>? AppleDeveloperShims = null);

/// <summary>
/// The fixed list of things VibeRails can check for and install. Commands are the vendors' own
/// documented install commands (checked against each linked page on 2026-10-14); keep them in
/// step with those pages. A vendor's native installer comes first where one exists, because it
/// needs neither Node.js nor a package manager.
/// </summary>
public static class SetupToolCatalog
{
    public const string Git = "git";
    public const string PowerShell = "pwsh";
    public const string Node = "node";
    public const string Python = "python";
    public const string Winget = "winget";
    public const string Homebrew = "brew";

    public static SetupPlatform Current =>
        OperatingSystem.IsWindows() ? SetupPlatform.Windows
        : OperatingSystem.IsMacOS() ? SetupPlatform.MacOS
        : SetupPlatform.Linux;

    /// <summary>Looks up one tool's install method; null when either ID is not in the catalog.</summary>
    public static (SetupTool Tool, SetupInstallMethod Method)? FindMethod(SetupPlatform platform, string? toolId, string? methodId)
    {
        var tool = For(platform).FirstOrDefault(candidate => string.Equals(candidate.Id, toolId, StringComparison.Ordinal));
        var method = tool?.Methods.FirstOrDefault(candidate => string.Equals(candidate.Id, methodId, StringComparison.Ordinal));
        return tool is null || method is null ? null : (tool, method);
    }

    public static IReadOnlyList<SetupTool> For(SetupPlatform platform)
    {
        var windows = platform == SetupPlatform.Windows;
        var mac = platform == SetupPlatform.MacOS;
        var tools = new List<SetupTool>();

        // ---- Required --------------------------------------------------------------------
        tools.Add(new SetupTool(Git, "Git", SetupToolGroup.Required,
            windows
                ? "Reads your repositories, runs the commit-rule hooks and clones sandboxes. Git for Windows also brings Git Bash for .sh scripts."
                : "Reads your repositories, runs the commit-rule hooks and clones sandboxes.",
            windows ? "https://git-scm.com/install/windows" : mac ? "https://git-scm.com/install/mac" : "https://git-scm.com/install/linux",
            [Git],
            windows ? [@"%ProgramFiles%\Git\cmd\git.exe"] : [],
            windows
                ? [WingetPackage("Git.Git")]
                : mac
                    ? [new("xcode", "Apple Command Line Tools", "xcode-select --install",
                            Note: "Opens Apple's installer dialog."),
                       BrewFormula("git")]
                    : [],
            // A fresh Mac has /usr/bin/git before it has Git: running it offers the installer.
            AppleDeveloperShims: [Git]));

        if (windows)
        {
            tools.Add(new SetupTool(PowerShell, "PowerShell 7", SetupToolGroup.Required,
                "Every VibeRails terminal on Windows runs in PowerShell 7 (pwsh). Windows PowerShell 5.1 is not enough.",
                "https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows",
                [PowerShell],
                [@"%ProgramFiles%\PowerShell\7\pwsh.exe"],
                [new(Winget, "winget", "winget install --id Microsoft.PowerShell --source winget", Requires: Winget)]));
        }

        // ---- AI coding CLIs --------------------------------------------------------------
        tools.Add(new SetupTool("claude", "Claude Code", SetupToolGroup.AgentCli,
            "Anthropic's coding agent.",
            "https://code.claude.com/docs/en/setup",
            ["claude"],
            windows ? [@"%USERPROFILE%\.local\bin\claude.exe"] : ["~/.local/bin/claude"],
            windows
                ? [Native("irm https://claude.ai/install.ps1 | iex", "Self-updating native build. No Node.js needed."),
                   new(Winget, "winget", "winget install Anthropic.ClaudeCode", Requires: Winget),
                   NpmPackage("@anthropic-ai/claude-code")]
                : [Native("curl -fsSL https://claude.ai/install.sh | bash", "Self-updating native build. No Node.js needed."),
                   .. mac ? [BrewCask("claude-code")] : Array.Empty<SetupInstallMethod>(),
                   NpmPackage("@anthropic-ai/claude-code")],
            Brand: "claude"));

        tools.Add(new SetupTool("codex", "Codex", SetupToolGroup.AgentCli,
            "OpenAI's coding agent.",
            "https://github.com/openai/codex#readme",
            ["codex"],
            windows ? [@"%LOCALAPPDATA%\Programs\OpenAI\Codex\bin\codex.exe"] : ["~/.local/bin/codex"],
            windows
                ? [Native("irm https://chatgpt.com/codex/install.ps1 | iex"),
                   NpmPackage("@openai/codex")]
                : [Native("curl -fsSL https://chatgpt.com/codex/install.sh | sh"),
                   .. mac ? [BrewCask("codex")] : Array.Empty<SetupInstallMethod>(),
                   NpmPackage("@openai/codex")],
            Brand: "codex"));

        tools.Add(new SetupTool("copilot", "GitHub Copilot CLI", SetupToolGroup.AgentCli,
            "GitHub's coding agent. Needs a Copilot subscription.",
            "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli",
            ["copilot"],
            windows ? [] : ["~/.local/bin/copilot"],
            windows
                ? [new(Winget, "winget", "winget install GitHub.Copilot", Requires: Winget),
                   NpmPackage("@github/copilot")]
                : [Native("curl -fsSL https://gh.io/copilot-install | bash"),
                   .. mac ? [BrewCask("copilot-cli")] : Array.Empty<SetupInstallMethod>(),
                   NpmPackage("@github/copilot")],
            Brand: "copilot"));

        // On Windows VibeRails registers its MCP server through npm's opencode.cmd shim
        // (CommandService.ResolveOpenCodeMcpExecutable), so npm is the only install offered there.
        tools.Add(new SetupTool("opencode", "OpenCode", SetupToolGroup.AgentCli,
            "Open-source coding agent. VibeRails also uses it for GLM, DeepSeek and Kimi models.",
            "https://opencode.ai/docs/",
            ["opencode"],
            windows ? [] : ["~/.opencode/bin/opencode"],
            windows
                ? [NpmPackage("opencode-ai")]
                : [Native("curl -fsSL https://opencode.ai/install | bash"),
                   .. mac ? [BrewFormula("anomalyco/tap/opencode")] : Array.Empty<SetupInstallMethod>(),
                   NpmPackage("opencode-ai")],
            Brand: "opencode"));

        tools.Add(new SetupTool("agy", "Antigravity", SetupToolGroup.AgentCli,
            "Google's coding agent (the agy command).",
            "https://antigravity.google/docs/getting-started?tab=cli",
            ["agy"],
            windows ? [@"%LOCALAPPDATA%\agy\bin\agy.exe"] : ["~/.local/bin/agy"],
            [Native(windows
                ? "irm https://antigravity.google/cli/install.ps1 | iex"
                : "curl -fsSL https://antigravity.google/cli/install.sh | bash")],
            Brand: "antigravity"));

        tools.Add(new SetupTool("grok", "Grok", SetupToolGroup.AgentCli,
            "xAI's Grok Build coding agent.",
            "https://docs.x.ai/build/overview",
            ["grok"],
            windows ? [@"%USERPROFILE%\.grok\bin\grok.exe"] : ["~/.grok/bin/grok"],
            [Native(windows
                ? "irm https://x.ai/cli/install.ps1 | iex"
                : "curl -fsSL https://x.ai/cli/install.sh | bash")],
            Brand: "grok"));

        // ---- Optional --------------------------------------------------------------------
        tools.Add(new SetupTool(Node, "Node.js", SetupToolGroup.Optional,
            "Provides npm, the alternative install route for Claude Code, Codex, Copilot and OpenCode.",
            "https://nodejs.org/en/download",
            [Node],
            windows ? [@"%ProgramFiles%\nodejs\node.exe"] : [],
            windows ? [WingetPackage("OpenJS.NodeJS.LTS")] : mac ? [BrewFormula("node")] : []));

        tools.Add(new SetupTool(Python, "Python 3", SetupToolGroup.Optional,
            "Runs .py scripts from the Scripts library and Automations.",
            windows ? "https://docs.python.org/3/using/windows.html" : "https://www.python.org/downloads/",
            windows ? [Python, "py"] : ["python3", Python],
            [],
            windows ? [WingetPackage("Python.Python.3.14")] : mac ? [BrewFormula("python")] : [],
            // The WindowsApps python.exe / python3.exe aliases open the Microsoft Store when
            // Python is absent. The Python install manager's py.exe alias there is real.
            WindowsAppsStubs: ["python", "python3"],
            // macOS /usr/bin/python3 is the same developer-tools shim as /usr/bin/git.
            AppleDeveloperShims: ["python3"]));

        if (!windows)
        {
            tools.Add(new SetupTool(PowerShell, "PowerShell 7", SetupToolGroup.Optional,
                "Runs .ps1 scripts from the Scripts library and Automations.",
                "https://learn.microsoft.com/powershell/scripting/install/installing-powershell",
                [PowerShell],
                [],
                mac ? [BrewFormula("powershell")] : []));
        }

        // ---- Package managers (shown only while missing) ---------------------------------
        if (windows)
        {
            tools.Add(new SetupTool(Winget, "winget (App Installer)", SetupToolGroup.PackageManager,
                "Windows' package manager. The Git, PowerShell, Node.js and Python installs use it.",
                "https://apps.microsoft.com/detail/9nblggh4nns1",
                [Winget],
                [@"%LOCALAPPDATA%\Microsoft\WindowsApps\winget.exe"],
                []));
        }
        if (mac)
        {
            tools.Add(new SetupTool(Homebrew, "Homebrew", SetupToolGroup.PackageManager,
                "macOS package manager. Several installs here use it.",
                "https://brew.sh/",
                [Homebrew],
                ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"],
                [Native("/bin/bash -c \"$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)\"",
                    "Asks for your password.")]));
        }

        return tools;
    }

    private static SetupInstallMethod Native(string command, string? note = null) =>
        new("native", "Official installer", command, Note: note);

    // winget shows any package agreement in the terminal and the user answers it there.
    private static SetupInstallMethod WingetPackage(string id) =>
        new(Winget, "winget", $"winget install --id {id} -e --source winget", Requires: Winget);

    private static SetupInstallMethod NpmPackage(string package) =>
        new("npm", "npm", $"npm install -g {package}", Requires: Node);

    private static SetupInstallMethod BrewFormula(string formula) =>
        new(Homebrew, "Homebrew", $"brew install {formula}", Requires: Homebrew);

    private static SetupInstallMethod BrewCask(string cask) =>
        new(Homebrew, "Homebrew", $"brew install --cask {cask}", Requires: Homebrew);
}

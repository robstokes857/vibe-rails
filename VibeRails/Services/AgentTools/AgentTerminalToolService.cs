using VibeRails.DTOs;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.AgentTools;

public sealed class AgentTerminalToolService : IAgentTerminalToolService
{
    private readonly ITerminalTabHostService _tabHost;
    private readonly ILlmParser _llmParser;
    private readonly ILocalToolApiContext _toolApiContext;

    public AgentTerminalToolService(
        ITerminalTabHostService tabHost,
        ILlmParser llmParser,
        ILocalToolApiContext toolApiContext)
    {
        _tabHost = tabHost;
        _llmParser = llmParser;
        _toolApiContext = toolApiContext;
    }

    public async Task<AgentToolTerminalListResponse> ListTerminalsAsync(CancellationToken cancellationToken = default)
    {
        var tabs = await _tabHost.ListTabsAsync(cancellationToken);
        return new AgentToolTerminalListResponse(tabs.ToList(), _tabHost.MaxTabs);
    }

    public async Task<TerminalTabStatusResponse> OpenTerminalAsync(
        AgentToolOpenTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        request ??= new AgentToolOpenTerminalRequest();

        var cliName = string.IsNullOrWhiteSpace(request.Cli) ? LLM.Shell.ToString() : request.Cli.Trim();
        var llm = _llmParser.Parse(cliName);
        if (llm == LLM.NotSet)
        {
            throw new InvalidOperationException($"Unknown CLI type: {cliName}");
        }

        var tab = await _tabHost.CreateTabAsync(cancellationToken);
        try
        {
            var status = await _tabHost.StartSessionAsync(
                tab.TabId,
                new StartTerminalRequest(
                    WorkingDirectory: request.WorkingDirectory,
                    Cli: llm.ToString(),
                    EnvironmentName: request.EnvironmentName,
                    Title: request.Title,
                    InitialPrompt: request.InitialPrompt),
                cancellationToken);

            return new TerminalTabStatusResponse(
                tab.TabId,
                tab.CreatedUTC,
                status.HasActiveSession,
                status.SessionId,
                status.Cli,
                status.WorkingDirectory);
        }
        catch
        {
            await _tabHost.DeleteTabAsync(tab.TabId, CancellationToken.None);
            throw;
        }
    }

    public async Task<TerminalInputResponse> SendInputAsync(
        string? tabId,
        TerminalInputRequest request,
        CancellationToken cancellationToken = default)
    {
        var resolvedTabId = await ResolveTabIdAsync(tabId, cancellationToken);
        return await _tabHost.SendInputAsync(resolvedTabId, request, cancellationToken);
    }

    public async Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(
        string? tabId,
        CancellationToken cancellationToken = default)
    {
        var resolvedTabId = await ResolveTabIdAsync(tabId, cancellationToken);
        return await _tabHost.CaptureSnapshotAsync(resolvedTabId, cancellationToken);
    }

    private async Task<string> ResolveTabIdAsync(string? requestedTabId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedTabId))
        {
            return requestedTabId.Trim();
        }

        if (!string.IsNullOrWhiteSpace(_toolApiContext.CurrentTabId))
        {
            return _toolApiContext.CurrentTabId!;
        }

        var tabs = await _tabHost.ListTabsAsync(cancellationToken);
        var activeTabs = tabs.Where(tab => tab.HasActiveSession).ToList();
        if (activeTabs.Count == 1)
        {
            return activeTabs[0].TabId;
        }

        if (tabs.Count == 1)
        {
            return tabs[0].TabId;
        }

        throw new InvalidOperationException("tabId is required when there is not exactly one terminal tab.");
    }
}

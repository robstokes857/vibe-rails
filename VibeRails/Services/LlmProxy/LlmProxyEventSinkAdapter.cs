using Serilog;
using TokenSaver;
using VibeRails.DB;
using VibeRails.Interfaces;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Bridges the TokenSaver library's event seam back into the app: activity pings go to the app
/// event bus (→ the UI proxy indicator, with the running tokens-saved total attached), savings
/// reports go to the state.db tally, diagnostics go to Serilog. The library stays free of all
/// three dependencies.
/// </summary>
public sealed class LlmProxyEventSinkAdapter(IAppEventBus eventBus, ITokenSavingsStore savingsStore)
    : ILlmProxyEventSink
{
    public void ProxyActivity(
        string source,
        string? label,
        string? target,
        string? status,
        long? bytesSaved = null)
    {
        // The running totals are an in-memory read — the ping never waits on SQLite.
        var totals = savingsStore.GetTotals();
        eventBus.PublishProxyActivity(
            source, label, target, status, bytesSaved,
            totals.TokensSaved, totals.SessionTokensSaved, totals.MonthTokensSaved);
    }

    public void SavingsMeasured(LlmProxySavingsReport report)
    {
        savingsStore.Record(report.Provider, report.BytesBefore, report.BytesAfter);

        // The one log line the plan allows (token_saving_plan.md §6): counts only, never content.
        Log.Information(
            "Token saver: {Provider} minified {Minified}/{Seen} tool results, {BytesBefore}→{BytesAfter} bytes "
            + "(saved {BytesSaved}; cr={CrChars} ansi={AnsiChars} ws={WsChars} blank={BlankChars} "
            + "dedup={DedupRuns} elide={Elisions})",
            report.Provider,
            report.ToolResultsMinified,
            report.ToolResultsSeen,
            report.BytesBefore,
            report.BytesAfter,
            report.BytesSaved,
            report.Transforms.CrRedrawChars,
            report.Transforms.AnsiChars,
            report.Transforms.TrailingWhitespaceChars,
            report.Transforms.BlankEdgeChars + report.Transforms.BlankRunChars,
            report.Condensed.DedupRunsCollapsed,
            report.Condensed.TruncationsApplied);
    }

    public void Diagnostic(string source, string message, Exception? exception = null)
    {
        Log.Debug(exception, "{ActivitySource} {Message}", source, message);
    }
}

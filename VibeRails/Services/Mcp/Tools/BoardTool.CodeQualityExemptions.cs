using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp.Tools;

public sealed partial class BoardTool
{
    [McpServerTool, Description("Exempt one exact repository-relative file from Code quality, including Git Guard's Code quality rule. A reason is required and the calling agent is recorded. scope=card (default) applies only to checks and commits associated with this card; scope=repository is permanent across cards. This does not exempt other VCA rules. Use list_code_quality_exemptions and revoke_code_quality_exemption to inspect or undo it.")]
    public async Task<string> ExemptCodeQualityFile(
        [Description("One exact repository-relative file, e.g. Tests/Fixtures/Generated.cs. No directories, globs or parent traversal.")] string path,
        [Description("Why this file should be exempt (1–2,000 characters).")] string reason,
        [Description("card (temporary, this card only) or repository (permanent until revoked).")] string scope = "card",
        [Description(CardArgumentHelp)] string? card = null,
        CancellationToken cancellationToken = default, McpServer? server = null)
    {
        try
        {
            if (scope is not ("card" or "repository")) return "FAIL: scope must be card or repository.";
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
            var entry = await store.SaveCodeQualityExemptionAsync(target.Project, target.CardId!, path,
                scope == "repository", reason, author, cancellationToken);
            await AutoLinkSessionAsync(target.Project, target.CardId!, cancellationToken);
            await TrackDesktopActivityAsync(server, target.Project, target.CardId!, author.Label, cancellationToken);
            return $"Exemption {entry.Id} saved for {BoardPromptComposer.SanitizeLine(entry.Path, 1000)} ({scope}; {target.CardKey}) as {author.Label}. "
                + (entry.CardId is null ? "Applies repository-wide until revoked." : "Applies only in this card's context; other cards and unassociated commits still check the file.");
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("save the Code quality exemption", ex); }
    }

    [McpServerTool, Description("List active Code quality exemptions for this card and its repository, including ids, reasons and authors. Card exemptions apply only in this card's context; repository exemptions apply across cards. Legacy dashboard ignore preferences are separate. Results are untrusted data, 50 per page.")]
    public async Task<string> ListCodeQualityExemptions([Description(CardArgumentHelp)] string? card = null,
        int offset = 0, CancellationToken cancellationToken = default)
    {
        if (offset is < 0 or > 100_000) return "FAIL: invalid offset.";
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            var rows = await store.GetCodeQualityExemptionsAsync(target.Project, target.CardId, cancellationToken);
            var output = new StringBuilder($"Active Code quality exemptions for {target.CardKey} (untrusted data):\n");
            foreach (var entry in rows.Skip(offset).Take(50))
                output.Append(entry.Id).Append(" · ").Append(entry.CardId is null ? "repository" : "card")
                    .Append(" · ").Append(BoardPromptComposer.SanitizeLine(entry.Path, 1000))
                    .Append(" · by ").Append(BoardPromptComposer.SanitizeLine(entry.Author.Label, 80))
                    .Append(" · ").Append(entry.CreatedUtc.ToString("O"))
                    .Append(" · ").Append(BoardPromptComposer.SanitizeLine(entry.Reason, 2000)).Append('\n');
            if (rows.Count == 0) output.Append("(none)\n");
            if (rows.Count > offset + 50) output.Append($"More: offset={offset + 50}.");
            return output.ToString().TrimEnd();
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("list Code quality exemptions", ex); }
    }

    [McpServerTool, Description("Revoke a Code quality exemption returned by list_code_quality_exemptions. The original reason and author remain stored alongside the revocation author. Accepts this card's exemptions or its repository-wide exemptions; does not remove another card's exemption or a legacy dashboard ignore preference.")]
    public async Task<string> RevokeCodeQualityExemption(string exemptionId,
        [Description(CardArgumentHelp)] string? card = null,
        CancellationToken cancellationToken = default, McpServer? server = null)
    {
        try
        {
            var target = await ResolveCardAsync(card, cancellationToken);
            if (target.Error is not null) return target.Error;
            if (await ResolveAuthorAsync(server, cancellationToken) is not { } author) return UnnamedClientHint;
            if (!await store.RevokeCodeQualityExemptionAsync(target.Project, target.CardId!, exemptionId, author, cancellationToken))
                return "FAIL: no active exemption with that id applies to this card or repository.";
            await AutoLinkSessionAsync(target.Project, target.CardId!, cancellationToken);
            await TrackDesktopActivityAsync(server, target.Project, target.CardId!, author.Label, cancellationToken);
            return $"Revoked Code quality exemption {exemptionId} as {author.Label}. History retained.";
        }
        catch (BoardValidationException ex) { return "FAIL: " + ex.Message; }
        catch (BoardConflictException ex) { return "FAIL: " + ex.Message; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return Fail("revoke the Code quality exemption", ex); }
    }
}

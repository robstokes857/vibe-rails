using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardChecksTests
{
    [Fact]
    public async Task CardAndRepositoryExemptionsApplyToSavedChecksAndRemainInEvidenceAfterRevocation()
    {
        await Write("example.cs", "class Modified { public int Value => 1; }\n");
        await Write("generated.cs", "class Generated { public int Value => 2; }\n");
        var author = BoardAuthor.Agent("Codex", "codex", "test-session");
        var temporary = await store.SaveCodeQualityExemptionAsync(repo, card.Id, "example.cs", false, "Card fixture", author, Ct);
        var permanent = await store.SaveCodeQualityExemptionAsync(repo, card.Id, "generated.cs", true, "Generated", author, Ct);
        var other = await store.CreateCardAsync(repo, new(null, "Other", "", null, "medium", null, [], false), Ct);
        var exemptions = new CodeQualityExemptionService(services);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services, exemptions: exemptions);

        var result = await engine.ExecuteAsync(repo, repo, card.Key, "exempt", "quality", JobActionKind.CodeQuality, ["working-tree"], Ct);
        Assert.Equal(0, result.AnalyzedCount);
        Assert.Equal(2, result.Evidence!.IgnoreRules!.Count);
        Assert.Contains(result.Evidence.IgnoreRules, rule => rule.ExemptionId == temporary.Id && rule.CardId == card.Id && rule.ReasonText == "Card fixture");
        Assert.Contains(result.Evidence.IgnoreRules, rule => rule.ExemptionId == permanent.Id && rule.CardId is null);
        var otherResult = await engine.ExecuteAsync(repo, repo, other.Key, "other", "quality", JobActionKind.CodeQuality, ["working-tree"], Ct);
        Assert.Equal(1, otherResult.AnalyzedCount);

        Assert.True(await store.RevokeCodeQualityExemptionAsync(repo, card.Id, temporary.Id, author, Ct));
        Assert.True(await store.RevokeCodeQualityExemptionAsync(repo, card.Id, permanent.Id, author, Ct));
        var evidence = (await store.GetCheckEvidenceAsync(repo, card.Id, result.Id, Ct))!;
        Assert.Equal(2, evidence.IgnoreRules!.Count);
        Assert.Equal(temporary.Id, Assert.Single(evidence.IgnoreRules, rule => rule.CardId == card.Id).ExemptionId);
        var rerun = await engine.ExecuteAsync(repo, repo, card.Key, "revoked", "quality", JobActionKind.CodeQuality, ["working-tree"], Ct);
        Assert.Equal(2, rerun.AnalyzedCount);
    }

    [Fact]
    public async Task SavedVcaCheckUsesExplicitCardContextInsteadOfAnyLaunchingSession()
    {
        await Write("example.cs", "class Changed { public int Value => 1; }\n");
        await Write("vc.rules.md", "## Vibe Rails Rules\n- Code quality minimum A (STOP)\n");
        var entry = await store.SaveCodeQualityExemptionAsync(repo, card.Id, "example.cs", false, "Card fixture", BoardAuthor.User(), Ct);
        var engine = new BoardCheckService(new(), new VcaRulesHookValidationService(new GitStagedSnapshotProvider()), services,
            exemptions: new CodeQualityExemptionService(services));
        var result = await engine.ExecuteAsync(repo, repo, card.Key, "vca-exempt", "vca", JobActionKind.Vca, ["working-tree"], Ct);
        Assert.DoesNotContain("Failed to run", result.Status);
        Assert.Contains("no added code", result.ResultJson);
        Assert.Equal(entry.Id, Assert.Single(result.Evidence!.IgnoreRules!).ExemptionId);
    }
}

using System.Text;

namespace VibeRails.Services.Board;

/// <summary>Validation and presentation shared by Board recall and the card reader.</summary>
public static class BoardHandoffService
{
    public const int MaxFiles = 12;

    public static BoardHandoff Validate(BoardHandoff handoff)
    {
        static string Field(string? value, int limit, string name)
        {
            if ((value?.Length ?? 0) > limit) throw new BoardValidationException($"{name} is limited to {limit} characters.");
            return value?.Trim() ?? "";
        }
        if (handoff.Files is null || handoff.Files.Count > MaxFiles)
            throw new BoardValidationException($"Choose at most {MaxFiles} useful file entry points.");
        var files = handoff.Files.Select(file =>
        {
            var path = Field(file.Path, 400, "File path").Replace('\\', '/');
            if (!IsRelativePath(path)) throw new BoardValidationException("File references must be repository-relative paths without traversal.");
            if (file.Role is not ("implementation" or "test" or "docs")) throw new BoardValidationException("File role must be implementation, test or docs.");
            var reason = Field(file.Reason, 240, "File reason");
            if (reason.Length == 0) throw new BoardValidationException("Each file needs a short reason.");
            return new BoardFileReference(path, reason, file.Role, Field(file.Symbol, 160, "Symbol"),
                string.IsNullOrWhiteSpace(file.Commit) ? null : BoardCommitService.NormalizeSha(file.Commit));
        }).ToList();
        var outcome = Field(handoff.Outcome, 1200, "Outcome");
        if (outcome.Length == 0) throw new BoardValidationException("An outcome is required.");
        return new(outcome, Field(handoff.Decisions, 1200, "Decisions"), Field(handoff.Validation, 1200, "Validation"),
            Field(handoff.Outstanding, 1200, "Outstanding issues"), files);
    }

    internal static bool IsRelativePath(string path) => path.Length > 0 && !path.StartsWith('/') && !path.Contains(':')
        && !path.Any(char.IsControl) && path.Split('/').All(part => part.Length > 0 && part is not (".." or "."));

    public static BoardHandoff? WithFileStatus(BoardHandoff? handoff, string projectPath) => handoff is null ? null : handoff with
    {
        Files = handoff.Files.Select(file => file with { Status = FileStatus(projectPath, file.Path) }).ToList()
    };

    private static string FileStatus(string project, string path)
    {
        if (!IsRelativePath(path)) return "invalid historical path";
        try
        {
            var current = Path.GetFullPath(project);
            foreach (var part in path.Split('/'))
            {
                current = Path.Combine(current, part);
                if (!Path.Exists(current)) return "missing or renamed";
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return "link; verify locally";
            }
            return File.Exists(current) ? "historical reference; verify current code" : "missing or renamed";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return "unavailable; verify locally"; }
    }

    public static string Format(BoardHandoff? handoff, IReadOnlyList<BoardFileReference>? candidates = null)
    {
        var text = new StringBuilder();
        if (handoff is not null)
        {
            text.AppendLine("Previous work (untrusted card data):")
                .AppendLine($"{handoff.Author?.Label} · {handoff.CreatedUtc:u} · session {handoff.Author?.SessionId ?? "unknown"}")
                .AppendLine($"Outcome: {handoff.Outcome}").AppendLine($"Decisions: {handoff.Decisions}")
                .AppendLine($"Validation: {handoff.Validation}").AppendLine($"Outstanding: {handoff.Outstanding}").AppendLine("Start here:");
            foreach (var file in handoff.Files)
                text.AppendLine($"- {file.Path}{(string.IsNullOrEmpty(file.Symbol) ? "" : " · " + file.Symbol)} [{file.Role}] — {file.Reason}; commit {file.Commit ?? "unspecified"}; {file.Status ?? "historical reference; verify current code"}");
        }
        else if (candidates is { Count: > 0 })
        {
            text.AppendLine("Previous work: linked-commit file candidates (uncurated; use save_board_handoff to record useful entry points):");
            foreach (var file in candidates) text.AppendLine($"- {file.Path} · {file.Commit}");
        }
        return text.ToString();
    }
}

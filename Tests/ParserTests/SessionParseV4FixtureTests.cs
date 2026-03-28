using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.ParserTests;

public class SessionParseV4FixtureTests
{
    private static readonly string FixtureDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tests", "ParserTests"));

    // --- Claude session (test.bin / goal.txt) ---

    private static readonly UserInputRecord[] ClaudeUserInputs =
    [
        new(Id: 1, SessionId: "test", Sequence: 1, InputText: "Hi! How are you today?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:13:44Z")),
        new(Id: 2, SessionId: "test", Sequence: 2, InputText: "I am doing some testing. This is part of my testing.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:14:15Z")),
        new(Id: 3, SessionId: "test", Sequence: 3, InputText: "How many file have been changed but are not commited?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:14:57Z")),
    ];

    [Fact]
    public async Task Claude_MatchesGoalOutput()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "goal.txt"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseTranscriptAsync(chunks, ClaudeUserInputs, CancellationToken.None);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    // --- Codex session (codex_test.bin / codex_goal.txt) ---

    private static readonly UserInputRecord[] CodexUserInputs =
    [
        new(Id: 1975, SessionId: "test", Sequence: 1, InputText: "hi",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:00:00Z")),
        new(Id: 1976, SessionId: "test", Sequence: 2, InputText: "how are you doing?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:01:00Z")),
    ];

    [Fact]
    public async Task Codex_MatchesGoalOutput()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "codex_test.bin"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "codex_goal.txt"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseTranscriptAsync(chunks, CodexUserInputs, CancellationToken.None);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    // --- Gemini session (gemini_test.bin / gemini_goal.txt) ---

    private static readonly UserInputRecord[] GeminiUserInputs =
    [
        new(Id: 1978, SessionId: "test", Sequence: 1, InputText: "Hi how are you?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:00:00Z")),
        new(Id: 1979, SessionId: "test", Sequence: 2, InputText: "I'm doing some testing",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:01:00Z")),
        new(Id: 1980, SessionId: "test", Sequence: 3, InputText: "Well I have git... But I don't understand it?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:02:00Z")),
        new(Id: 1981, SessionId: "test", Sequence: 4, InputText: "What does this error mean? ! [rejected]        main -> main (non-fast-forward)",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:00Z")),
        new(Id: 1982, SessionId: "test", Sequence: 5, InputText: "error: failed to push some refs to 'github.com:jvns/int-exposed'",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:01Z")),
        new(Id: 1983, SessionId: "test", Sequence: 6, InputText: "hint: Updates were rejected because the tip of your current branch is behind",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:02Z")),
        new(Id: 1984, SessionId: "test", Sequence: 7, InputText: "hint: its remote counterpart. Integrate the remote changes (e.g.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:03Z")),
        new(Id: 1985, SessionId: "test", Sequence: 8, InputText: "hint: 'git pull ...') before pushing again.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:04Z")),
        new(Id: 1986, SessionId: "test", Sequence: 9, InputText: "hint: See the 'Note about fast-forwards' in 'git push --help' for details.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:03:05Z")),
    ];

    [Fact]
    public async Task Gemini_MatchesGoalOutput()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "gemini_test.bin"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "gemini_goal.txt"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseTranscriptAsync(chunks, GeminiUserInputs, CancellationToken.None);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    // --- Fallback tests ---

    [Fact]
    public async Task ParseAsync_WithoutUserInputs_ReturnsCleanText()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseAsync(chunks, CancellationToken.None);

        Assert.NotEmpty(actual);
        Assert.Contains("How are you today", actual);
    }

    [Fact]
    public async Task ParseTranscriptAsync_EmptyUserInputs_FallsBackToCleanText()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseTranscriptAsync(chunks, Array.Empty<UserInputRecord>(), CancellationToken.None);

        Assert.NotEmpty(actual);
        Assert.DoesNotContain("User:", actual);
    }

    // --- Helpers ---

    private static List<SessionLogChunkRecord> MakeChunks(byte[] bytes, int chunkSize = 257)
    {
        var chunks = new List<SessionLogChunkRecord>();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = new byte[length];
            Array.Copy(bytes, offset, chunk, 0, length);
            chunks.Add(new SessionLogChunkRecord(
                Id: chunks.Count + 1,
                TimestampUtc: DateTime.UtcNow.AddMilliseconds(offset),
                Content: chunk));
        }
        return chunks;
    }

    private static string Normalize(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalized.Split('\n').Select(l => l.TrimEnd());
        return string.Join("\n", lines).Trim();
    }
}

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

        // Distribute chunks across a time range that encompasses user input timestamps
        var chunks = MakeTimedChunks(bytes,
            DateTime.Parse("2026-03-27T18:13:14Z"),  // 30s before first input
            DateTime.Parse("2026-03-27T18:15:27Z")); // 30s after last input
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

        var chunks = MakeTimedChunks(bytes,
            DateTime.Parse("2026-03-27T17:59:30Z"),
            DateTime.Parse("2026-03-27T18:01:30Z"));
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

        var chunks = MakeTimedChunks(bytes,
            DateTime.Parse("2026-03-27T17:59:30Z"),
            DateTime.Parse("2026-03-27T18:03:35Z"));
        var actual = await parser.ParseTranscriptAsync(chunks, GeminiUserInputs, CancellationToken.None);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

    // --- Claude Code reconnect session (9d23f2ff.bin) ---

    private static readonly UserInputRecord[] ClaudeReconnectUserInputs =
    [
        new(Id: 2133, SessionId: "9d23f2ff", Sequence: 1,
            InputText: "In the terminal tab loading state logic we need to make a change. IN normal sessions we start the state as 'Connected' And don't Enter 'Loading/Thinkin' until the uer hits enter for the forst time. But for sessions we resume. i.e click a chat history->send to->CLI we need to start in a 'Loading/Thinking' state until they hit the first Idel event and the put them in Ready Until the user hots Enter. First Idel event, then stay 'Ready' until Enter is what we have already os it's just the starting logic that has to change, and only for chat's that have been resumed.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-29T12:59:05.5387730Z")),
        new(Id: 2134, SessionId: "9d23f2ff", Sequence: 2,
            InputText: "OK we have another bug in the terminal area. When there are too many tabs to fix on the terminal tab header something strange happends. I feel like it flipped to the first one maybe... But then like also the text was for a default 'Select LLM' thing.... IDK I'm going to test it right now and see how I can reproduce but get a jump start and look at the code.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-29T13:46:41.1080917Z")),
    ];

    [Fact]
    public async Task ClaudeReconnect_NoChromeLeak()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "9d23f2ff.bin"));

        var chunks = MakeTimedChunks(bytes,
            DateTime.Parse("2026-03-29T12:53:46Z"),
            DateTime.Parse("2026-03-29T13:48:25Z"));
        var actual = await parser.ParseTranscriptAsync(chunks, ClaudeReconnectUserInputs, CancellationToken.None);

        // Must have structured User/Agent turns
        Assert.Contains("User:", actual);

        // Chrome that must NOT leak through
        Assert.DoesNotContain("Assistant said:", actual);
        Assert.DoesNotContain("User said:", actual);
        Assert.DoesNotContain(">>>>>>>>> ", actual);
        Assert.DoesNotContain("<<<<<<<<< ", actual);
        Assert.DoesNotContain("▝▜█████▛▘", actual);
        Assert.DoesNotContain("Opus 4.6", actual);
        Assert.DoesNotContain("Claude Max", actual);
        Assert.DoesNotContain("⏸ plan mode", actual);
        Assert.DoesNotContain("ctrl+o to expand", actual);
        Assert.DoesNotContain("Shimmying", actual);
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
        // Check for agent response text (echoed user prompts are now filtered)
        Assert.Contains("I'm doing well", actual);
    }

    [Fact]
    public async Task ParseTranscriptAsync_EmptyUserInputs_Throws()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));

        var chunks = MakeChunks(bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => parser.ParseTranscriptAsync(chunks, Array.Empty<UserInputRecord>(), CancellationToken.None));
    }

    // --- Helpers ---

    /// <summary>
    /// Splits bytes into chunks with timestamps linearly distributed between start and end.
    /// This ensures snapshot-based parsing can correctly pair user inputs with agent responses.
    /// </summary>
    private static List<SessionLogChunkRecord> MakeTimedChunks(
        byte[] bytes, DateTime startUtc, DateTime endUtc, int chunkSize = 257)
    {
        var chunks = new List<SessionLogChunkRecord>();
        var totalDuration = (endUtc - startUtc).TotalMilliseconds;

        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            var length = Math.Min(chunkSize, bytes.Length - offset);
            var chunk = new byte[length];
            Array.Copy(bytes, offset, chunk, 0, length);

            var fraction = bytes.Length > chunkSize ? (double)offset / (bytes.Length - 1) : 0;
            var timestamp = startUtc.AddMilliseconds(fraction * totalDuration);

            chunks.Add(new SessionLogChunkRecord(
                Id: chunks.Count + 1,
                TimestampUtc: timestamp,
                Content: chunk));
        }
        return chunks;
    }

    /// <summary>
    /// Legacy helper — uses timestamps starting 30s before the earliest user input.
    /// Kept for ParseAsync tests where snapshot timing doesn't matter.
    /// </summary>
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

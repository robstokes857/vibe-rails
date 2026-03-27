using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.ParserTests;

public class SessionParseV4FixtureTests
{
    private static readonly string FixtureDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tests", "ParserTests"));

    private static readonly UserInputRecord[] TestUserInputs =
    [
        new(Id: 1, SessionId: "test", Sequence: 1, InputText: "Hi! How are you today?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:13:44Z")),
        new(Id: 2, SessionId: "test", Sequence: 2, InputText: "I am doing some testing. This is part of my testing.",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:14:15Z")),
        new(Id: 3, SessionId: "test", Sequence: 3, InputText: "How many file have been changed but are not commited?",
            GitCommitHash: null, TimestampUTC: DateTime.Parse("2026-03-27T18:14:57Z")),
    ];

    [Fact]
    public async Task ParseTranscriptAsync_MatchesGoalOutput()
    {
        var parser = new SessionParseV4();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "goal.txt"));

        var chunks = MakeChunks(bytes);
        var actual = await parser.ParseTranscriptAsync(chunks, TestUserInputs, CancellationToken.None);

        Assert.Equal(Normalize(expected), Normalize(actual));
    }

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

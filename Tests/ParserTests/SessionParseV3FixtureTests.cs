using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.ParserTests;

public class SessionParseV3FixtureTests
{
    private static readonly string FixtureDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Tests", "ParserTests"));

    [Fact]
    public async Task ParseAsync_TransformsTerminalBytesIntoGoalTranscript()
    {
        var parser = new SessionParseV3();
        var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, "test.bin"));
        var expected = File.ReadAllText(Path.Combine(FixtureDir, "goal.txt"));

        var chunks = Chunk(bytes, chunkSize: 257);
        var actual = await parser.ParseAsync(chunks, CancellationToken.None);

        Assert.Equal(NormalizeForComparison(expected), NormalizeForComparison(actual));
    }

    private static List<SessionLogChunkRecord> Chunk(byte[] bytes, int chunkSize)
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

    private static string NormalizeForComparison(string text)
    {
        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalized
            .Split('\n')
            .Select(line => line.TrimEnd());

        return string.Join("\n", lines).Trim();
    }
}

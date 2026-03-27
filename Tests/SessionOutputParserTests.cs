using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests;

public class SessionParseV3Tests
{
    private static readonly string FixturesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures"));

    private readonly SessionParseV3 _parser = new();

    [Theory]
    [InlineData(
        "ea7f03be-af77-41d1-9392-ddf63205cabd",
        "Hi. What would you like to work on in the VibeRails codebase?",
        "OpenAI Codex (v")]
    [InlineData(
        "7ff27bce-b880-4340-a0e4-0de6be75ad29",
        "Hello! How can I help you with the VibeRails project today?",
        "Gemini CLI v")]
    [InlineData(
        "3cafeb67-0eef-4f53-97db-0ba4d8874c68",
        "Root cause: Gemini added visibility: hidden",
        "Claude Code")]
    public async Task ParseAsync_ExtractsReadableAssistantTextFromFixture(
        string sessionId,
        string expectedSnippet,
        string excludedSnippet)
    {
        var chunks = LoadFixtureChunks(sessionId, chunkSize: 512);

        var text = await _parser.ParseAsync(chunks, CancellationToken.None);

        Assert.NotEmpty(text);
        Assert.Contains(expectedSnippet, text);
        Assert.DoesNotContain(excludedSnippet, text);
        Assert.DoesNotContain("PowerShell 7.5.4", text);
        Assert.DoesNotContain("? for shortcuts", text);
    }

    [Fact]
    public async Task ParseAsync_FiltersShellAndChromeOnlyOutput()
    {
        var chunks = new List<SessionLogChunkRecord>
        {
            new(
                1,
                DateTime.UtcNow,
                System.Text.Encoding.UTF8.GetBytes(
                "PowerShell 7.5.4\r\n" +
                "Loading personal and system profiles took 1234ms.\r\n" +
                "(base) PS C:\\source\\VibeControl2> codex\r\n" +
                "Tip: Use /skills to list available skills.\r\n" +
                "› hi\r\n"))
        };

        var text = await _parser.ParseAsync(chunks, CancellationToken.None);

        Assert.Equal(string.Empty, text);
    }

    private static List<SessionLogChunkRecord> LoadFixtureChunks(string sessionId, int chunkSize)
    {
        var fixturePath = Path.Combine(FixturesDir, $"{sessionId}.bin");
        Assert.True(File.Exists(fixturePath), $"Fixture not found: {fixturePath}");

        var bytes = File.ReadAllBytes(fixturePath);
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
}

using Microsoft.ML.Tokenizers;
using VibeRails.Services.BertBaseClasses;

namespace VibeRails.Services.BertV2;

/// <summary>Uses the inference model's WordPiece vocabulary and complete 512-token input budget.</summary>
public sealed class SearchTextChunker
{
    private readonly BertTokenizer _tokenizer;
    public SearchTextChunker(IBertSettings settings) : this(settings.VocabPath) { }
    public SearchTextChunker(string vocabPath)
    {
        using var vocab = File.OpenRead(vocabPath);
        _tokenizer = BertTokenizer.Create(vocab, new BertOptions { LowerCaseBeforeTokenization = true,
            SeparatorToken = "[SEP]", ClassificationToken = "[CLS]", UnknownToken = "[UNK]", PaddingToken = "[PAD]" });
    }

    public int CountTokens(string text) => _tokenizer.EncodeToIds(text, addSpecialTokens: true).Count;

    public IReadOnlyList<string> Split(string text, string title = "", CancellationToken ct = default) =>
        SplitChunks(text, title, ct).Select(chunk => chunk.Text).ToArray();

    internal sealed record Chunk(string Text, int Start, int Length);

    internal IReadOnlyList<Chunk> SplitChunks(string text, string title = "", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return [];
        var titleEnd = Prefix(title, "", 34, 256);
        var context = titleEnd > 0 ? title[..titleEnd] + "\n" : "";
        var chunks = new List<Chunk>();
        for (var start = 0; start < text.Length;)
        {
            ct.ThrowIfCancellationRequested();
            // Bound the tokenizer input and allocation even for a very long session source.
            var segmentEnd = Boundary(text, Math.Min(text.Length, start + 2049));
            var remaining = text[start..segmentEnd];
            // A 192-token retrieval target stays well within the 512-token model window
            // while keeping one deep decision from being diluted by a page of unrelated text.
            var length = Prefix(remaining, context, 192, 2048);
            if (length < remaining.Length)
            {
                var boundary = remaining.LastIndexOfAny(['\n', '.', '!', '?'], length - 1, Math.Max(1, length / 2));
                if (boundary >= length / 2) length = boundary + 1;
            }
            if (length <= 0) throw new InvalidOperationException("Tokenizer could not fit one source character in the model window.");
            chunks.Add(new(context + remaining[..length], start, length));
            if (start + length == text.Length) break;
            var overlap = 0;
            for (var candidate = Math.Max(1, length - 256); candidate < length; candidate++)
            {
                if (char.IsLowSurrogate(remaining[candidate])) continue;
                if (CountTokens(remaining[candidate..length]) <= 26) { overlap = length - candidate; break; }
            }
            start += length - overlap;
        }
        return chunks;
    }

    private int Prefix(string text, string context, int budget, int maxChars)
    {
        var high = Boundary(text, Math.Min(text.Length, maxChars));
        if (CountTokens(context + text[..high]) <= budget) return high;
        var low = 0;
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = Boundary(text, middle);
            if (CountTokens(context + text[..end]) <= budget) low = middle;
            else high = middle - 1;
        }
        return Boundary(text, low);
    }

    private static int Boundary(string text, int index) => index > 0 && index < text.Length
        && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]) ? index - 1 : index;
}

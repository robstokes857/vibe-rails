using System.Buffers.Binary;
using System.Text;

namespace MCP_Server.Services;

/// <summary>
/// AOT-compatible in-memory vector database using feature hashing.
/// Ported from VibeControl's VectorSearchService.
/// </summary>
public sealed class SimpleVectorDb
{
    private readonly HashingVectorizer _vectorizer;
    private readonly InMemoryIndex _index;
    private readonly object _lock = new();

    public SimpleVectorDb(int dimensions = 2048)
    {
        _vectorizer = new HashingVectorizer(
            dimensions: dimensions,
            useWordTokens: true,
            useCharNGrams: true,
            minCharN: 3,
            maxCharN: 5);
        _index = new InMemoryIndex(_vectorizer.Dimensions);
    }

    public void AddText(string text, string? metadata = null)
    {
        var sanitized = Sanitize(text);
        if (sanitized.Length == 0) return;

        var id = metadata ?? Guid.NewGuid().ToString("N");
        var vec = _vectorizer.Embed(sanitized);

        lock (_lock)
        {
            _index.Upsert(id, sanitized, vec);
        }
    }

    public SearchResults Search(string query, int pageCount = 5)
    {
        var q = Sanitize(query);
        if (q.Length == 0) return new SearchResults();

        var qv = _vectorizer.Embed(q);

        List<SearchHit> hits;
        lock (_lock)
        {
            hits = _index.Search(qv, pageCount);
        }

        var results = new SearchResults
        {
            Texts = hits.Select(h => new TextResult
            {
                Text = h.Text,
                Metadata = h.Id,
                Score = h.Score
            }).ToList()
        };

        return results;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _index.Clear();
        }
    }

    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return string.Empty;

        var x = s.Replace("\0", "")
                 .Replace("\r\n", "\n")
                 .Replace('\r', '\n')
                 .Normalize(NormalizationForm.FormKC);

        x = string.Join(' ', x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return x;
    }

    // =====================================================================
    // Result types
    // =====================================================================

    public sealed class SearchResults
    {
        public List<TextResult>? Texts { get; set; }
    }

    public sealed class TextResult
    {
        public string? Text { get; set; }
        public string? Metadata { get; set; }
        public float Score { get; set; }
    }

    // =====================================================================
    // Internal: In-memory index
    // =====================================================================

    private sealed class InMemoryIndex
    {
        private readonly int _dims;
        private readonly Dictionary<string, Entry> _byId = new(StringComparer.Ordinal);

        public InMemoryIndex(int dims) => _dims = dims;

        public void Clear() => _byId.Clear();

        public void Upsert(string id, string text, float[] vector)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            if (vector == null || vector.Length != _dims) return;

            if (_byId.TryGetValue(id, out var existing))
            {
                existing.Text = text;
                existing.Vector = vector;
            }
            else
            {
                _byId[id] = new Entry { Id = id, Text = text, Vector = vector };
            }
        }

        public void Delete(string id)
        {
            if (!string.IsNullOrWhiteSpace(id))
                _byId.Remove(id);
        }

        public List<SearchHit> Search(float[] queryVec, int maxResults)
        {
            if (queryVec == null || queryVec.Length != _dims) return new();

            var top = new List<SearchHit>(maxResults);

            foreach (var kv in _byId)
            {
                var e = kv.Value;
                var score = VectorMath.Dot(queryVec, e.Vector);

                if (top.Count < maxResults)
                {
                    top.Add(new SearchHit(e.Id, e.Text, score));
                    if (top.Count == maxResults)
                        top.Sort((a, b) => b.Score.CompareTo(a.Score));
                    continue;
                }

                if (score <= top[^1].Score)
                    continue;

                top[^1] = new SearchHit(e.Id, e.Text, score);
                top.Sort((a, b) => b.Score.CompareTo(a.Score));
            }

            return top;
        }

        private sealed class Entry
        {
            public required string Id;
            public required string Text;
            public required float[] Vector;
        }
    }

    private sealed class SearchHit
    {
        public SearchHit(string id, string text, float score)
        {
            Id = id;
            Text = text;
            Score = score;
        }
        public string Id { get; }
        public string Text { get; }
        public float Score { get; }
    }

    // =====================================================================
    // Internal: Feature hashing vectorizer
    // =====================================================================

    private sealed class HashingVectorizer
    {
        public int Dimensions { get; }
        private readonly bool _useWords;
        private readonly bool _useCharNgrams;
        private readonly int _minN;
        private readonly int _maxN;

        public HashingVectorizer(int dimensions, bool useWordTokens, bool useCharNGrams, int minCharN, int maxCharN)
        {
            Dimensions = dimensions < 64 ? 64 : dimensions;
            _useWords = useWordTokens;
            _useCharNgrams = useCharNGrams;
            _minN = Math.Max(1, minCharN);
            _maxN = Math.Max(_minN, maxCharN);
        }

        public float[] Embed(string text)
        {
            var v = new float[Dimensions];
            var s = text.ToLowerInvariant().Normalize(NormalizationForm.FormKC);

            if (_useWords)
                AddWordTokens(v, s);

            if (_useCharNgrams)
                AddCharNgrams(v, s);

            VectorMath.L2NormalizeInPlace(v);
            return v;
        }

        private void AddWordTokens(float[] v, string s)
        {
            var span = s.AsSpan();
            var tokenStart = -1;

            for (int i = 0; i <= span.Length; i++)
            {
                var c = i < span.Length ? span[i] : ' ';
                var isTokenChar = char.IsLetterOrDigit(c) || c == '_' || c == '-';

                if (isTokenChar)
                {
                    if (tokenStart < 0) tokenStart = i;
                }
                else if (tokenStart >= 0)
                {
                    AddHashed(v, span.Slice(tokenStart, i - tokenStart));
                    tokenStart = -1;
                }
            }
        }

        private void AddCharNgrams(float[] v, string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if (char.IsWhiteSpace(c) && sb.Length > 0 && sb[^1] != ' ')
                    sb.Append(' ');
            }

            var cs = sb.ToString().Trim();
            if (cs.Length == 0) return;

            var span = cs.AsSpan();
            int start = 0;

            while (start < span.Length)
            {
                while (start < span.Length && span[start] == ' ') start++;
                if (start >= span.Length) break;

                int end = start;
                while (end < span.Length && span[end] != ' ') end++;

                var word = span.Slice(start, end - start);
                EmitNgrams(v, word);

                start = end + 1;
            }
        }

        private void EmitNgrams(float[] v, ReadOnlySpan<char> word)
        {
            var buf = new char[word.Length + 2];
            buf[0] = '^';
            word.CopyTo(buf.AsSpan(1));
            buf[^1] = '$';

            var w = buf.AsSpan();

            for (int n = _minN; n <= _maxN; n++)
            {
                if (w.Length < n) continue;
                for (int i = 0; i <= w.Length - n; i++)
                {
                    AddHashed(v, w.Slice(i, n));
                }
            }
        }

        private void AddHashed(float[] v, ReadOnlySpan<char> token)
        {
            if (token.Length == 0) return;

            // FNV-1a hash
            uint h = 2166136261;
            for (int i = 0; i < token.Length; i++)
            {
                h ^= token[i];
                h *= 16777619;
            }

            var idx = (int)(h % (uint)Dimensions);
            var sign = ((h & 1) == 0) ? 1f : -1f;

            v[idx] += sign;
        }
    }

    // =====================================================================
    // Internal: Vector math utilities
    // =====================================================================

    private static class VectorMath
    {
        public static float Dot(float[] a, float[] b)
        {
            float sum = 0f;
            for (int i = 0; i < a.Length; i++)
                sum += a[i] * b[i];
            return sum;
        }

        public static void L2NormalizeInPlace(float[] v)
        {
            double ss = 0;
            for (int i = 0; i < v.Length; i++)
                ss += (double)v[i] * v[i];

            if (ss <= 1e-12) return;

            var inv = (float)(1.0 / Math.Sqrt(ss));
            for (int i = 0; i < v.Length; i++)
                v[i] *= inv;
        }
    }
}

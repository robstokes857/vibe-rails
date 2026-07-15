using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// Benchmarks the current production embedder (BGE-small-en-v1.5 padded to seq=512)
/// against the previously-used model (all-MiniLM-L6-v2 padded to seq=256). Uses the
/// same corpus dumped from CleanedUserInput so results reflect real workload.
/// MiniLM weights are downloaded on first run and cached under
/// %USERPROFILE%/.vibe_rails/test_cache/all-MiniLM-L6-v2/.
/// </summary>
public class EmbedderBenchmarkTests
{
    private static readonly string CorpusPath = Path.Combine(
        AppContext.BaseDirectory, "Services", "BertV2", "Fixtures", "cleaned_input_corpus.jsonl");

    private const string MiniLmBaseUrl = "https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2/resolve/main";
    private const int IterationsPerCorpusPass = 3;
    private const int WarmupRuns = 5;

    private readonly Xunit.ITestOutputHelper _out;

    public EmbedderBenchmarkTests(Xunit.ITestOutputHelper output)
    {
        _out = output;
    }

    [Fact]
    public async Task Compare_Bge_vs_MiniLm_OverRealCorpus()
    {
        // Opt-in only. This is a report, not a test: it asserts nothing about product behaviour,
        // it prints a comparison table for a human to read. But it is CPU-bound ONNX inference
        // (4 model configs × corpus × 3 passes) and it is the single longest thing in the suite —
        // it alone sets the suite's wall clock, and because ONNX's intra-op pool spin-waits, its
        // cost explodes under load (measured: 14s idle, 63s with the cores busy) and drags the
        // whole run with it. Running it by default charges every unrelated `dotnet test` a
        // variable multi-minute toll for numbers nobody is reading. Set VIBERAILS_RUN_BENCHMARKS=1
        // to run it deliberately.
        if (Environment.GetEnvironmentVariable("VIBERAILS_RUN_BENCHMARKS") != "1")
            Assert.Skip("Benchmark is opt-in: set VIBERAILS_RUN_BENCHMARKS=1 to run it.");

        // Corpus is gitignored — it's a local dump of real CleanedUserInput.
        // Skip when absent so the benchmark stays a manual/local exercise and
        // doesn't fail CI or contributor runs that don't have the fixture.
        if (!File.Exists(CorpusPath))
            Assert.Skip($"Benchmark corpus not present: {CorpusPath}. Dump from CleanedUserInput locally to run.");

        var corpus = LoadCorpus(CorpusPath);
        Assert.NotEmpty(corpus);
        _out.WriteLine($"Corpus: {corpus.Count} entries, avg {corpus.Average(c => c.Length):F0} chars, max {corpus.Max(c => c.Length)} chars");
        _out.WriteLine($"Iterations per model: {corpus.Count * IterationsPerCorpusPass} (corpus × {IterationsPerCorpusPass})");
        _out.WriteLine("");

        var bgeDir = await EnsureBgeModelAsync();
        var minilmDir = await EnsureMiniLmModelAsync();

        var bgeStats = RunBenchmark(
            modelDir: bgeDir,
            displayName: "BGE-small @ seq=512 fixed pad (current prod)",
            maxSeq: 512,
            corpus: corpus,
            padToMaxSeq: true);

        var bgeRightSized = RunBenchmark(
            modelDir: bgeDir,
            displayName: "BGE-small @ right-sized pad (proposed fix)",
            maxSeq: 512,
            corpus: corpus,
            padToMaxSeq: false);

        var minilmStats = RunBenchmark(
            modelDir: minilmDir,
            displayName: "MiniLM-L6 @ seq=256 fixed pad",
            maxSeq: 256,
            corpus: corpus,
            padToMaxSeq: true);

        var minilmRightSized = RunBenchmark(
            modelDir: minilmDir,
            displayName: "MiniLM-L6 @ right-sized pad",
            maxSeq: 256,
            corpus: corpus,
            padToMaxSeq: false);

        _out.WriteLine("");
        _out.WriteLine("===== Summary =====");
        PrintRow(bgeStats);
        PrintRow(bgeRightSized);
        PrintRow(minilmStats);
        PrintRow(minilmRightSized);
        _out.WriteLine("");
        _out.WriteLine($"Right-sizing BGE pad alone: {bgeStats.TotalMs / bgeRightSized.TotalMs:F2}x faster than current prod");
        _out.WriteLine($"Switching to MiniLM (fixed pad): {bgeStats.TotalMs / minilmStats.TotalMs:F2}x faster than current prod");
        _out.WriteLine($"MiniLM with right-sized pad:    {bgeStats.TotalMs / minilmRightSized.TotalMs:F2}x faster than current prod");
    }

    private BenchStats RunBenchmark(
        string modelDir,
        string displayName,
        int maxSeq,
        IReadOnlyList<string> corpus,
        bool padToMaxSeq)
    {
        var modelPath = Path.Combine(modelDir, "model.onnx");
        var vocabPath = Path.Combine(modelDir, "vocab.txt");

        _out.WriteLine($"--- {displayName} ---");

        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        options.AppendExecutionProvider_CPU(1);

        var loadSw = Stopwatch.StartNew();
        using var session = new InferenceSession(modelPath, options);
        loadSw.Stop();
        var requiresTokenTypeIds = session.InputMetadata.ContainsKey("token_type_ids");

        using var vocabStream = File.OpenRead(vocabPath);
        var tokenizer = BertTokenizer.Create(vocabStream, new BertOptions
        {
            LowerCaseBeforeTokenization = false,
            SeparatorToken = "[SEP]",
            ClassificationToken = "[CLS]",
            UnknownToken = "[UNK]",
            PaddingToken = "[PAD]",
        });

        _out.WriteLine($"  model load: {loadSw.ElapsedMilliseconds} ms, inputs: {string.Join(",", session.InputMetadata.Keys)}");

        // Warmup
        for (int i = 0; i < WarmupRuns; i++)
        {
            RunOne(session, tokenizer, corpus[i % corpus.Count], maxSeq, requiresTokenTypeIds, padToMaxSeq);
        }

        // Bench — track per-call latency for percentiles
        var latencies = new List<double>(corpus.Count * IterationsPerCorpusPass);
        var totalSw = Stopwatch.StartNew();
        for (int pass = 0; pass < IterationsPerCorpusPass; pass++)
        {
            foreach (var text in corpus)
            {
                var sw = Stopwatch.StartNew();
                RunOne(session, tokenizer, text, maxSeq, requiresTokenTypeIds, padToMaxSeq);
                sw.Stop();
                latencies.Add(sw.Elapsed.TotalMilliseconds);
            }
        }
        totalSw.Stop();

        latencies.Sort();
        var stats = new BenchStats(
            DisplayName: displayName,
            ModelLoadMs: loadSw.ElapsedMilliseconds,
            Count: latencies.Count,
            TotalMs: totalSw.Elapsed.TotalMilliseconds,
            MeanMs: latencies.Average(),
            P50Ms: latencies[latencies.Count / 2],
            P95Ms: latencies[(int)(latencies.Count * 0.95)],
            P99Ms: latencies[(int)(latencies.Count * 0.99)],
            MaxMs: latencies[^1]);

        _out.WriteLine($"  total: {stats.TotalMs:F0} ms over {stats.Count} calls");
        _out.WriteLine($"  per-call: mean={stats.MeanMs:F2} ms  p50={stats.P50Ms:F2}  p95={stats.P95Ms:F2}  p99={stats.P99Ms:F2}  max={stats.MaxMs:F2}");
        _out.WriteLine("");

        return stats;
    }

    private static void RunOne(
        InferenceSession session,
        BertTokenizer tokenizer,
        string text,
        int maxSeq,
        bool requiresTokenTypeIds,
        bool padToMaxSeq)
    {
        var encoded = tokenizer.EncodeToIds(text, maxSeq, addSpecialTokens: true, out _, out _);
        var tokenCount = encoded.Count;

        // Right-sized: tensor shape == actual token count. Fixed: shape == maxSeq.
        var seqLen = padToMaxSeq ? maxSeq : Math.Max(tokenCount, 1);

        var inputIds = new long[seqLen];
        var attentionMask = new long[seqLen];
        for (int i = 0; i < tokenCount; i++)
        {
            inputIds[i] = encoded[i];
            attentionMask[i] = 1;
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", new DenseTensor<long>(inputIds, [1, seqLen])),
            NamedOnnxValue.CreateFromTensor("attention_mask", new DenseTensor<long>(attentionMask, [1, seqLen])),
        };
        if (requiresTokenTypeIds)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids",
                new DenseTensor<long>(new long[seqLen], [1, seqLen])));
        }

        using var results = session.Run(inputs);
        // Touch the first output so the runtime can't elide the call.
        _ = results.First().AsTensor<float>().Dimensions[0];
    }

    private void PrintRow(BenchStats s)
    {
        _out.WriteLine($"{s.DisplayName,-55} load={s.ModelLoadMs,5}ms  total={s.TotalMs,7:F0}ms  mean={s.MeanMs,6:F2}ms  p50={s.P50Ms,6:F2}  p95={s.P95Ms,6:F2}  p99={s.P99Ms,6:F2}");
    }

    private static List<string> LoadCorpus(string jsonlPath)
    {
        if (!File.Exists(jsonlPath))
            throw new FileNotFoundException($"Corpus fixture not found: {jsonlPath}");

        var raw = File.ReadAllText(jsonlPath);
        // sqlite3 -json emits a JSON array of objects: [{"t":"..."}, ...]
        using var doc = JsonDocument.Parse(raw);
        var list = new List<string>();
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("t", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s);
            }
        }
        return list;
    }

    private static async Task<string> EnsureBgeModelAsync()
    {
        // Reuse the existing test asset bootstrapper so we don't duplicate the gz decompression.
        var dir = Path.Combine(Path.GetTempPath(), "bert_bench_bge");
        if (!File.Exists(Path.Combine(dir, "model.onnx")) || !File.Exists(Path.Combine(dir, "vocab.txt")))
        {
            await BertV2TestAssets.MaterializeRuntimeFilesAsync(dir);
        }
        return dir;
    }

    private static async Task<string> EnsureMiniLmModelAsync()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".vibe_rails", "test_cache", "all-MiniLM-L6-v2");
        Directory.CreateDirectory(cacheDir);

        var modelPath = Path.Combine(cacheDir, "model.onnx");
        var vocabPath = Path.Combine(cacheDir, "vocab.txt");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!File.Exists(vocabPath) || new FileInfo(vocabPath).Length < 100_000)
        {
            await DownloadAsync(http, $"{MiniLmBaseUrl}/vocab.txt", vocabPath);
        }
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < 50_000_000)
        {
            await DownloadAsync(http, $"{MiniLmBaseUrl}/onnx/model.onnx", modelPath);
        }

        return cacheDir;
    }

    private static async Task DownloadAsync(HttpClient http, string url, string outPath)
    {
        var tmp = outPath + ".part";
        using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dest = File.Create(tmp);
            await src.CopyToAsync(dest);
        }
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);
    }

    private record BenchStats(
        string DisplayName,
        long ModelLoadMs,
        int Count,
        double TotalMs,
        double MeanMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs);
}

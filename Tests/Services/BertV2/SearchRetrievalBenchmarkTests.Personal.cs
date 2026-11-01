using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// Opt-in benchmarks over a copy of a real search.db (VIBE-257). They never open the live search.db:
/// each run copies the snapshot it is given. The sweep reads a canonical state.db read-only. Query sets
/// with known answers are personal data and stay outside this repository (vibe-books/bert).
/// Set VIBERAILS_RUN_BENCHMARKS=1 plus the variables each test names.
/// </summary>
public sealed partial class SearchRetrievalBenchmarkTests
{
    private const string QueryInstruction = "Represent this sentence for searching relevant passages: ";

    /// <summary>Generated launch text the filter retires, plus the discussion bootstrap it keeps (reported separately).</summary>
    private static readonly string[] GeneratedMarkers =
        ["You are working on kanban card ", "You are managing kanban card ", "This Automation was triggered for kanban card ", "--job-run",
         "The user wants to talk with you about kanban card "];

    private static string? Setting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private static void RequireBenchmarkOptIn()
    {
        if (Setting("VIBERAILS_RUN_BENCHMARKS") != "1")
            Assert.Skip("Benchmark is opt-in: set VIBERAILS_RUN_BENCHMARKS=1 to run it.");
    }

    private static string RequirePath(string name, bool exists = true)
    {
        var path = Setting(name);
        if (path is null || exists && !File.Exists(path) && !Directory.Exists(path))
            Assert.Skip($"Set {name} to run this personal-snapshot benchmark.");
        var full = Path.GetFullPath(path!);
        var live = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe_rails", "search.db"));
        Assert.False(string.Equals(full, live, StringComparison.OrdinalIgnoreCase), "Benchmarks must use a copy, never the live search.db.");
        return full;
    }

    /// <summary>
    /// VIBERAILS_SEARCH_SNAPSHOT (a search.db copy, read only), VIBERAILS_SEARCH_SNAPSHOT_OUT (where the swept
    /// copy is written) and optionally VIBERAILS_SEARCH_STATE (state.db, opened read-only; defaults to
    /// ~/.vibe_rails/state.db). Runs the current history sweep over an index written by an older build, then
    /// embeds whatever it re-chunked, and reports what the filter retired.
    /// </summary>
    [Fact]
    public async Task PersonalSnapshotSweepRetiresGeneratedLaunchInputs()
    {
        RequireBenchmarkOptIn();
        var snapshot = RequirePath("VIBERAILS_SEARCH_SNAPSHOT");
        var copy = RequirePath("VIBERAILS_SEARCH_SNAPSHOT_OUT", exists: false);
        var state = Setting("VIBERAILS_SEARCH_STATE")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe_rails", "state.db");
        var ct = TestContext.Current.CancellationToken;
        SqliteConnection.ClearAllPools();
        File.Copy(snapshot, copy, overwrite: true);
        foreach (var suffix in new[] { "-wal", "-shm" }) File.Delete(copy + suffix);
        var before = Census(copy);
        // Only the history corpora are swept. The Board corpus would need board.db, which a copy cannot use.
        ExecuteSql(copy, $"UPDATE SearchCheckpoints SET Cursor='',CompletedUtc='{DateTime.UtcNow:O}',Error=NULL,RetryAt=0,LeaseUntil=0 WHERE Kind='board'");
        ExecuteSql(copy, "UPDATE SearchCheckpoints SET CompletedUtc=NULL,Error=NULL,RetryAt=0,LeaseUntil=0 WHERE Kind IN ('input','session')");
        var index = new SqliteSearchIndexStore(copy, state);
        var sweep = Stopwatch.StartNew();
        SearchReconcileResult result;
        var changed = 0;
        do { result = await index.ReconcileAsync(Mock.Of<IBoardStore>(), 25, ct, minimumInterval: TimeSpan.FromDays(1)); changed += result.Changed; }
        while (result.Incomplete);
        sweep.Stop();
        var swept = Census(copy);
        var embedding = await EmbedPendingAsync(index, ct);
        var after = Census(copy);
        output.WriteLine($"Sweep: {sweep.ElapsedMilliseconds} ms, changed documents {changed}. Embedding: {embedding}.");
        output.WriteLine($"{"",-10} {before.Header}");
        output.WriteLine($"{"before",-10} {before}");
        output.WriteLine($"{"swept",-10} {swept}");
        output.WriteLine($"{"embedded",-10} {after}");
        Assert.Equal(0, after.FilteredInputs);
        Assert.Equal(0, after.PendingChunks);
    }

    private async Task<string> EmbedPendingAsync(SqliteSearchIndexStore index, CancellationToken ct)
    {
        var runtime = Path.Combine(Path.GetTempPath(), "search-benchmark-model-" + Guid.NewGuid().ToString("N"));
        await BertV2TestAssets.MaterializeRuntimeFilesAsync(runtime);
        try
        {
            var chunker = new SearchTextChunker(Path.Combine(runtime, "vocab.txt"));
            using var model = new BertV2BgeEmbedder(Path.Combine(runtime, "model.onnx"), Path.Combine(runtime, "vocab.txt"));
            var timer = Stopwatch.StartNew();
            int sources = 0, chunks = 0;
            string[] kinds = ["board", "input", "session"];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var sourceWork = kinds.SelectMany(kind => index.ClaimSources(kind, 100, SearchIndexVersions.Chunks)).ToArray();
                foreach (var work in sourceWork) index.CompleteSource(work, chunker.Split(work.Text, work.Title, ct), SearchIndexVersions.Chunks);
                var chunkWork = kinds.SelectMany(kind => index.ClaimChunks(kind, 100, SearchIndexVersions.Model)).ToArray();
                if (sourceWork.Length == 0 && chunkWork.Length == 0) break;
                // ONNX Runtime sessions accept concurrent runs; six two-thread runs use a dozen cores.
                var vectors = new float[chunkWork.Length][];
                Parallel.For(0, chunkWork.Length, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
                    i => vectors[i] = model.GenerateEmbedding(chunkWork[i].Text));
                for (var i = 0; i < chunkWork.Length; i++) index.CompleteChunk(chunkWork[i], vectors[i], SearchIndexVersions.Model);
                sources += sourceWork.Length;
                chunks += chunkWork.Length;
            }
            return $"{sources} sources re-chunked, {chunks} chunks embedded in {timer.Elapsed.TotalSeconds:F0} s";
        }
        finally
        {
            try { Directory.Delete(runtime, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record IndexCensus(int Inputs, int Sessions, int InputChunks, int SessionChunks, int FilteredInputs,
        int FilteredInputChunks, int MarkedSessionSources, int DiscussionInputs, int PendingChunks)
    {
        public string Header => "inputs sessions inputChunks sessionChunks filterRejectsInputs(chunks) sessionsWithLaunchText discussionPromptInputs pendingChunks";
        public override string ToString() =>
            $"{Inputs,6} {Sessions,8} {InputChunks,11} {SessionChunks,13} {FilteredInputs,19}({FilteredInputChunks}) {MarkedSessionSources,22} {DiscussionInputs,22} {PendingChunks,13}";
    }

    private static IndexCensus Census(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        int Scalar(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt32(cmd.ExecuteScalar()); }
        var rejected = new List<string>();
        var discussion = 0;
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT s.Id,s.Text FROM SearchSources s JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind='input'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var text = reader.GetString(1);
                if (InputEtlFilter.Process(text) is null) rejected.Add(reader.GetString(0));
                else if (text.StartsWith("The user wants to talk with you about kanban card ", StringComparison.Ordinal)) discussion++;
            }
        }
        var rejectedChunks = 0;
        foreach (var id in rejected)
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM SearchChunks WHERE SourceId=$id";
            cmd.Parameters.AddWithValue("$id", id);
            rejectedChunks += Convert.ToInt32(cmd.ExecuteScalar());
        }
        const string chunksOf = "SELECT count(*) FROM SearchChunks c JOIN SearchSources s ON s.Id=c.SourceId JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind=";
        return new(Scalar("SELECT count(*) FROM SearchDocuments WHERE Kind='input'"), Scalar("SELECT count(*) FROM SearchDocuments WHERE Kind='session'"),
            Scalar(chunksOf + "'input'"), Scalar(chunksOf + "'session'"), rejected.Count, rejectedChunks,
            Scalar("SELECT count(*) FROM SearchSources s JOIN SearchDocuments d ON d.Id=s.DocumentId WHERE d.Kind='session' AND " +
                "(instr(s.Text,'You are working on kanban card ')>0 OR instr(s.Text,'--job-run')>0)"),
            discussion, Scalar($"SELECT count(*) FROM SearchChunks WHERE ModelVersion IS NULL OR ModelVersion<>'{SearchIndexVersions.Model}'")
                + Scalar($"SELECT count(*) FROM SearchSources WHERE ChunkVersion IS NULL OR ChunkVersion<>'{SearchIndexVersions.Chunks}'"));
    }

    private static void ExecuteSql(string path, string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Fixed queries with known answers, run through the same services as search_board_cards and search_history.
    /// VIBERAILS_SEARCH_QUALITY_QUERIES: the JSON query set. VIBERAILS_SEARCH_QUALITY_SNAPSHOTS: label=path pairs
    /// separated by ';'. Optional VIBERAILS_SEARCH_QUALITY_RERANKERS: label=directory[|modelFile[|maxTokens[|threads]]]
    /// pairs separated by ';'. VIBERAILS_SEARCH_QUALITY_INSTRUCTION=1 adds the BGE query instruction as a setup.
    /// VIBERAILS_SEARCH_QUALITY_REPEATS (default 3) timed passes after one warm-up pass. VIBERAILS_SEARCH_QUALITY_OUT
    /// writes the Markdown report.
    /// </summary>
    [Fact]
    public async Task PersonalSnapshotRetrievalQualityBySetup()
    {
        RequireBenchmarkOptIn();
        var queryFile = RequirePath("VIBERAILS_SEARCH_QUALITY_QUERIES");
        var snapshots = Pairs(Setting("VIBERAILS_SEARCH_QUALITY_SNAPSHOTS"));
        if (snapshots.Count == 0) Assert.Skip("Set VIBERAILS_SEARCH_QUALITY_SNAPSHOTS to label=search.db-copy pairs.");
        var rerankers = Pairs(Setting("VIBERAILS_SEARCH_QUALITY_RERANKERS"));
        var repeats = int.TryParse(Setting("VIBERAILS_SEARCH_QUALITY_REPEATS"), out var r) ? Math.Max(1, r) : 3;
        var ct = TestContext.Current.CancellationToken;
        using var spec = JsonDocument.Parse(File.ReadAllText(queryFile));
        var project = spec.RootElement.GetProperty("project").GetString()!;
        var queries = spec.RootElement.GetProperty("queries").EnumerateArray().Select(QualityQuery.Read).ToArray();

        var root = Path.Combine(Path.GetTempPath(), "search-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new StringBuilder();
        try
        {
            var runtime = Path.Combine(root, "model");
            await BertV2TestAssets.MaterializeRuntimeFilesAsync(runtime);
            using var embedder = new BertV2BgeEmbedder(Path.Combine(runtime, "model.onnx"), Path.Combine(runtime, "vocab.txt"));
            var setups = new List<(string Label, bool Instruction, Func<ISearchReranker>? Reranker)> { ("hybrid", false, null) };
            if (Setting("VIBERAILS_SEARCH_QUALITY_INSTRUCTION") == "1") setups.Add(("hybrid+instruction", true, null));
            foreach (var (label, value) in rerankers)
            {
                var parts = value.Split('|');
                var file = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "model.onnx";
                var max = parts.Length > 2 ? int.Parse(parts[2]) : 256;
                var threads = parts.Length > 3 ? int.Parse(parts[3]) : 2;
                setups.Add(($"{label} ({file}, {max} tok, {threads} thr)", false, () => new CrossEncoderReranker(parts[0], max, threads, file)));
            }

            report.AppendLine($"Queries: {queries.Length} ({queries.Count(q => q.Tool == "cards")} card, {queries.Count(q => q.Tool == "history")} history); "
                + $"{repeats} timed passes after one warm-up. Hit@5 and MRR@10 count queries with a known answer; noise@5 counts generated launch text among the top five history hits.");
            report.AppendLine();
            report.AppendLine("| snapshot | setup | hit@5 | MRR@10 | noise@5 | p50 ms | p95 ms | model load ms |");
            report.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
            var detail = new StringBuilder("| snapshot | setup | " + string.Join(" | ", queries.Select(q => q.Id)) + " |\n");
            detail.AppendLine("| --- | --- |" + string.Concat(queries.Select(_ => " --- |")));
            foreach (var (snapshotLabel, snapshotPath) in snapshots)
            {
                var copy = Path.Combine(root, snapshotLabel + ".db");
                SqliteConnection.ClearAllPools();
                File.Copy(RequireCopyOf(snapshotPath), copy, overwrite: true);
                var store = new SqliteSearchIndexStore(copy, Path.Combine(root, "no-state.db"));
                var history = new BertSearchDbService(store);
                foreach (var setup in setups)
                {
                    ISearchReranker? reranker = null;
                    double loadMs = 0;
                    if (setup.Reranker is { } factory)
                    {
                        var load = Stopwatch.StartNew();
                        reranker = factory();
                        reranker.Score("warm", ["up"]);
                        loadMs = load.Elapsed.TotalMilliseconds;
                    }
                    try
                    {
                        var unified = new UnifiedSearchService(() => embedder, history, new BertDocumentResponseMapper(), null, () => reranker);
                        var board = new BoardSearchService(store, () => embedder, NullLogger<BoardSearchService>.Instance, () => reranker);
                        var times = new List<double>();
                        QualityOutcome[] outcomes = [];
                        for (var pass = 0; pass <= repeats; pass++)
                        {
                            outcomes = new QualityOutcome[queries.Length];
                            for (var i = 0; i < queries.Length; i++)
                            {
                                var timer = Stopwatch.StartNew();
                                outcomes[i] = await RunQualityQueryAsync(queries[i], project, setup.Instruction, embedder, board, unified, history, ct);
                                if (pass > 0) times.Add(timer.Elapsed.TotalMilliseconds);
                            }
                        }
                        var scored = queries.Select((query, i) => (query, outcome: outcomes[i])).Where(entry => entry.query.HasAnswer).ToArray();
                        var hit5 = scored.Count(entry => entry.outcome.Rank is > 0 and <= 5);
                        var mrr = scored.Sum(entry => entry.outcome.Rank is > 0 and <= 10 ? 1.0 / entry.outcome.Rank.Value : 0) / Math.Max(1, scored.Length);
                        times.Sort();
                        report.AppendLine($"| {snapshotLabel} | {setup.Label} | {hit5}/{scored.Length} | {mrr:F3} | {outcomes.Sum(o => o.Noise)} | "
                            + $"{Percentile(times, .5):F0} | {Percentile(times, .95):F0} | {(setup.Reranker is null ? "-" : loadMs.ToString("F0"))} |");
                        detail.AppendLine($"| {snapshotLabel} | {setup.Label} | " + string.Join(" | ", outcomes.Select(o => o.ToString())) + " |");
                    }
                    finally { reranker?.Dispose(); }
                }
            }
            report.AppendLine();
            report.AppendLine("Rank of the first right answer per query (- = not in the top 10; n = generated hits in the top five history results):");
            report.AppendLine();
            report.Append(detail);
            output.WriteLine(report.ToString());
            if (Setting("VIBERAILS_SEARCH_QUALITY_OUT") is { } outPath) File.WriteAllText(outPath, report.ToString());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string RequireCopyOf(string path)
    {
        var full = Path.GetFullPath(path);
        var live = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vibe_rails", "search.db"));
        Assert.False(string.Equals(full, live, StringComparison.OrdinalIgnoreCase), "Benchmarks must use a copy, never the live search.db.");
        Assert.True(File.Exists(full), "Snapshot not found: " + full);
        return full;
    }

    private static async Task<QualityOutcome> RunQualityQueryAsync(QualityQuery query, string project, bool instruction,
        IBertV2BgeEmbedder embedder, BoardSearchService board, UnifiedSearchService unified, BertSearchDbService history, CancellationToken ct)
    {
        // The same order as the tools: one query embedding, card discovery, then history.
        var embedding = embedder.GenerateEmbedding(instruction ? QueryInstruction + query.Text : query.Text);
        if (query.Tool == "cards")
        {
            var hits = await board.SearchAsync(project, query.Text, 10, ct: ct, queryEmbedding: embedding, rerank: true);
            return new(RankOf(hits.Select(hit => query.Cards.Any(card => Matches(hit, card))).ToArray()), 0);
        }
        var cards = await board.SearchAsync(project, query.Text, 5, ct: ct, queryEmbedding: embedding, rerank: true);
        var fused = unified.Search(query.Text, 10, embedding).Groups.Single(group => group.Key == "fused").Hits;
        var texts = fused.Select(hit => (hit.Kind == "session-chunk" ? history.GetSessionCapture(hit.DocumentId) : history.GetCapture(hit.DocumentId))?.RawText
            ?? hit.UserTextPreview).ToArray();
        var noise = texts.Take(5).Count(text => GeneratedMarkers.Any(marker => text.Contains(marker, StringComparison.Ordinal)));
        if (query.Needles.Length > 0)
            return new(RankOf(texts.Select(text => query.Needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase))).ToArray()), noise);
        // A query whose only right answer is a card is scored on search_history's card section.
        return new(query.Cards.Length == 0 ? null : RankOf(cards.Select(hit => query.Cards.Any(card => Matches(hit, card))).ToArray()), noise);
    }

    private static bool Matches(BoardSearchHit hit, string card) =>
        string.Equals(hit.Key, card, StringComparison.OrdinalIgnoreCase) || string.Equals(hit.DisplayId, card, StringComparison.OrdinalIgnoreCase)
        || string.Equals(hit.Id, card, StringComparison.Ordinal);

    private static int? RankOf(bool[] relevant) => Array.IndexOf(relevant, true) is var index and >= 0 ? index + 1 : null;

    private static double Percentile(List<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private static List<(string Label, string Value)> Pairs(string? setting) => (setting ?? "")
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(pair => pair.Split('=', 2)).Select(parts => (parts[0], parts[1])).ToList();

    private sealed record QualityQuery(string Id, string Tool, string Text, string[] Cards, string[] Needles)
    {
        public bool HasAnswer => Needles.Length > 0 || Cards.Length > 0;

        public static QualityQuery Read(JsonElement element)
        {
            string[] Strings(string name) => element.TryGetProperty(name, out var value)
                ? value.EnumerateArray().Select(item => item.GetString()!).ToArray() : [];
            return new(element.GetProperty("id").GetString()!, element.GetProperty("tool").GetString()!,
                element.GetProperty("query").GetString()!, Strings("cards"), Strings("needles"));
        }
    }

    private sealed record QualityOutcome(int? Rank, int Noise)
    {
        public override string ToString() => (Rank?.ToString() ?? "-") + (Noise > 0 ? $" n{Noise}" : "");
    }
}

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.BertV2;

public sealed class SearchRetrievalBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public async Task RealBgeFindsLateCardAndDeepDiscussionWhileSourceWritesRemainResponsive()
    {
        var root = Path.Combine(Path.GetTempPath(), "search-benchmark-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            var runtime = Path.Combine(root, "model");
            await BertV2TestAssets.MaterializeRuntimeFilesAsync(runtime);
            var state = Path.Combine(root, "state.db");
            var repository = new Repository($"Data Source={state}");
            await repository.CreateSessionAsync("active", "codex", null, root, 1);
            var board = SqliteStorage.CreateBoardStore(state);
            await board.EnsureDefaultColumnsAsync(root, ct);
            var cards = new List<BoardCardRecord>();
            for (var i = 0; i < 96; i++)
                cards.Add(await board.CreateCardAsync(root, new(null, "Menu colors " + i, "Adjust dashboard theme colors and typography", null, "medium", null, [], false), ct));
            var target = cards.MaxBy(card => card.Id, StringComparer.Ordinal)!;
            var deep = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"Dashboard color sample {i}.")) +
                " Replace the damaged vehicle wheel and install a spare tire after a roadside puncture.";
            await board.UpdateCardAsync(root, target.Id, new(Title: "Service notes", Description: deep), ct);
            await board.AddCommentAsync(root, target.Id, BoardAuthor.User(), deep + " Check lug nut torque before driving.", ct);
            using var model = new BertV2BgeEmbedder(Path.Combine(runtime, "model.onnx"), Path.Combine(runtime, "vocab.txt"));
            Assert.Equal(model.GenerateEmbedding("Fix flat tyres"), model.GenerateEmbedding("fix flat tyres"));
            var measured = new MeasuredModel(model);
            var index = new SqliteSearchIndexStore(Path.Combine(root, "search.db"), state);
            var chunker = new SearchTextChunker(Path.Combine(runtime, "vocab.txt"));
            var worker = new SearchIndexingService(index, board, () => chunker, () => measured);
            var writeTimes = new List<double>();
            var indexing = Stopwatch.StartNew();
            for (var round = 0; round < 60; round++)
            {
                var work = worker.RunBatchAsync(ct, 25);
                var edit = Stopwatch.StartNew();
                await board.UpdateCardAsync(root, cards[0].Id == target.Id ? cards[1].Id : cards[0].Id,
                    new(Priority: round % 2 == 0 ? "low" : "medium"), ct);
                await repository.InsertUserInputAsync("active", round + 1, "Ordinary captured input " + round, null);
                writeTimes.Add(edit.Elapsed.TotalMilliseconds);
                await work;
                var progress = index.GetProgress();
                if (index.Count("board") == cards.Count && progress.PendingSources == 0 && progress.Embedded == progress.Chunks) break;
            }
            indexing.Stop();
            Assert.Equal(cards.Count, index.Count("board"));
            var search = new BoardSearchService(index, measured, NullLogger<BoardSearchService>.Instance);
            var query = "Fix flat tyres";
            var before = index.GetProgress();
            var timer = Stopwatch.StartNew();
            var cold = await search.SearchAsync(root, query, 5, ct: ct);
            var coldMs = timer.Elapsed.TotalMilliseconds;
            timer.Restart();
            var warm = await search.SearchAsync(root, query, 5, ct: ct);
            var warmMs = timer.Elapsed.TotalMilliseconds;
            Assert.Equal(target.Id, cold[0].Id);
            Assert.Equal(target.Id, warm[0].Id);
            Assert.Equal("semantic", cold[0].MatchKind);
            Assert.Equal(before, index.GetProgress());
            Assert.True(writeTimes.Max() < 5000, "Source writes exceeded the ordinary SQLite busy timeout.");
            output.WriteLine($"Cards={cards.Count}; chunks={before.Chunks}; embedded={before.Embedded}; indexingMs={indexing.ElapsedMilliseconds}; modelCalls={measured.Calls}; modelMs={measured.Milliseconds:F1}; coldQueryMs={coldMs:F1}; warmQueryMs={warmMs:F1}; maxBoardAndStateWriteMs={writeTimes.Max():F1}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    private sealed class MeasuredModel(IBertV2BgeEmbedder model) : IBertV2BgeEmbedder
    {
        public int Calls;
        public double Milliseconds;
        public float[] GenerateEmbedding(string text)
        {
            var timer = Stopwatch.StartNew();
            var result = model.GenerateEmbedding(text);
            Calls++;
            Milliseconds += timer.Elapsed.TotalMilliseconds;
            return result;
        }
        public void Dispose() { }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Moq;
using Tests.VcaRegression;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;

namespace Tests.Services.ReviewRequests;

/// <summary>Shared fixtures for the slice C tests: a scripted transport, a settable clock, a recording event bus and hosted answers.</summary>
internal static class ReviewRequestTestSupport
{
    public const string Reference = "rvw_0123456789abcdef0123456789abcdef";
    public const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    /// <summary>A staged index with one modified text file and one binary file, as the hook's snapshot reader would report it.</summary>
    public static Mock<IGitStagedSnapshotProvider> Snapshots(string project, string tree = Tree)
    {
        var snapshots = new Mock<IGitStagedSnapshotProvider>(MockBehavior.Strict);
        snapshots.Setup(s => s.CaptureAsync(project, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStagedSnapshot(
            project,
            [
                new GitStagedFileSnapshot("db.cs", Path.Combine(project, "db.cs"), GitStagedChangeKind.Modified, true, false, 1,
                    "class Db { int Version = 2; }\n", PreviousContent: "class Db { }\n"),
                new GitStagedFileSnapshot("logo.png", Path.Combine(project, "logo.png"), GitStagedChangeKind.Added, true, true, null, null)
            ],
            [],
            StagedIdentity: new GitStagedSnapshotIdentity(new string('c', 40), new string('d', 40), tree)));
        return snapshots;
    }

    public static ReviewRequestHostedStatus Hosted(string requestId, string tree = Tree, string status = "open",
        string reference = Reference, DateTime? created = null, params ReviewRequestHostedDecision[] decisions)
    {
        var at = created ?? new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        return new(reference, Guid.Parse(requestId), tree, status, at, at.AddMonths(1), at, decisions);
    }

    public static ReviewRequestHostedDecision Approved(long id, string reviewer, string tree = Tree, string? comment = null) =>
        new(id, reviewer, "approved", comment, HumanSignatureRuleTests.SignatureLine(tree), DateTime.UtcNow, DateTime.UtcNow);

    public static ReviewRequestHostedDecision Rejected(long id, string reviewer, string comment) =>
        new(id, reviewer, "rejected", comment, null, DateTime.UtcNow, null);

    public static HttpResponseMessage Json(HttpStatusCode status, ReviewRequestHostedStatus body) =>
        new(status) { Content = JsonContent.Create(body, ReviewRequestJsonContext.Default.ReviewRequestHostedStatus) };

    public static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = JsonContent.Create(body) };

    public static ReviewRequestTransport Transport(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) =>
        new(new HttpClient(new Handler((request, _) => callback(request))));

    /// <summary>A handler that sees the transport's own deadline token, for tests of cancellation and timeouts.</summary>
    public static ReviewRequestTransport Transport(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) =>
        new(new HttpClient(new Handler(callback)));

    public sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => callback(request, ct);
    }

    public sealed class Clock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Now, DateTimeKind.Utc));
    }

    public sealed class RecordingEventBus : IAppEventBus
    {
        public List<AppEvent> Events { get; } = [];

        public void Publish<T>(string type, T payload, JsonTypeInfo<T> typeInfo) =>
            Publish(new AppEvent(type, JsonSerializer.SerializeToElement(payload, typeInfo)));

        public void Publish(AppEvent appEvent)
        {
            lock (Events) Events.Add(appEvent);
        }

        public IDisposable Subscribe(Action<AppEvent> listener) => throw new NotSupportedException();
    }
}

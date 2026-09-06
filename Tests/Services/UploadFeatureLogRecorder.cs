using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services;

internal sealed class UploadFeatureLogRecorder : IFeatureLog
{
    public ConcurrentQueue<Entry> Entries { get; } = new();

    public void Write(
        string feature,
        string eventName,
        string message,
        string? operationId = null,
        string? subject = null,
        string? status = null,
        LogLevel level = LogLevel.Information) =>
        Entries.Enqueue(new Entry(feature, eventName, message, operationId, subject, status, level));

    public Entry AssertAttempt(string subject, string expectedStatus, string? operationId = null)
    {
        var entries = Entries.Where(entry => operationId is null || entry.OperationId == operationId).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.All(entries, entry =>
        {
            Assert.Equal("data-upload", entry.Feature);
            Assert.Equal(subject, entry.Subject);
            Assert.True(Guid.TryParse(entry.OperationId, out _));
            Assert.Equal(entries[0].OperationId, entry.OperationId);
        });
        Assert.Equal("started", entries[0].Status);
        Assert.Equal(expectedStatus, entries[1].Status);
        return entries[1];
    }

    public sealed record Entry(
        string Feature,
        string Event,
        string Message,
        string? OperationId,
        string? Subject,
        string? Status,
        LogLevel Level);
}

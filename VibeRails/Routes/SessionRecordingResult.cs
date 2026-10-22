using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Routes;

// Each process shares one lease pool across all three routes, covering loading and sending.
internal sealed class SessionRecordingResult(ISessionStore store, string sessionId, SessionRecordingKind kind) : IResult
{
    private static readonly SemaphoreSlim Requests = new(2, 2);

    public async Task ExecuteAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await Results.BadRequest(new ErrorResponse("Session id is required.")).ExecuteAsync(context);
            return;
        }
        if (!await Requests.WaitAsync(0, ct))
        {
            await Results.Json(new ErrorResponse("Recording exports are busy. Try again shortly."), statusCode: 429).ExecuteAsync(context);
            return;
        }
        try
        {
            SessionRecordingData? recording;
            try { recording = await store.GetSessionRecordingAsync(sessionId, kind, ct); }
            catch (SessionRecordingTooLargeException ex)
            {
                await Results.Json(new ErrorResponse(ex.Message), statusCode: 413).ExecuteAsync(context);
                return;
            }
            if (recording is null || (recording.Chunks.Count == 0 && kind != SessionRecordingKind.Replay))
            {
                await Results.NotFound(new ErrorResponse("No session recording data found.")).ExecuteAsync(context);
                return;
            }

            if (kind == SessionRecordingKind.Raw)
                await Results.Stream(output => WriteZipAsync(output, sessionId, recording, ct),
                    "application/zip", $"{sessionId}-raw-session.zip").ExecuteAsync(context);
            else if (kind == SessionRecordingKind.Replay)
            {
                context.Response.ContentType = "application/json";
                var prefix = "{\"sessionId\":" + JsonSerializer.Serialize(sessionId, AppJsonSerializerContext.Default.String) + ",\"content\":\"";
                await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(prefix), ct);
                await WriteBase64Async(context.Response.Body, recording.Chunks, ct);
                await context.Response.Body.WriteAsync("\"}"u8.ToArray(), ct);
            }
            else
            {
                var startTime = recording.Chunks[0].TimestampUtc;
                var frames = recording.Chunks.Select(c => new TerminalReplayFrame(
                    Convert.ToBase64String(c.Content), (long)(c.TimestampUtc - startTime).TotalMilliseconds)).ToList();
                var chunks = recording.TerminalLogs.Select(l => new TerminalReplayChunk(
                    l.Sequence, l.IsAlternateScreen, Convert.ToBase64String(l.Data), l.Cols, l.Rows)).ToList();
                await Results.Ok(new TerminalReplayResponse(sessionId,
                    chunks.Count > 0 ? chunks[0].Cols : 120, chunks.Count > 0 ? chunks[0].Rows : 40,
                    chunks, frames, DateTime.SpecifyKind(startTime, DateTimeKind.Utc))).ExecuteAsync(context);
            }
        }
        finally { Requests.Release(); }
    }

    internal static async Task WriteZipAsync(Stream output, string sessionId, SessionRecordingData recording, CancellationToken ct)
    {
        // .NET 10's entry stream still disposes its deflater synchronously. Stage only
        // those small headers/trailers; every write to Kestrel remains asynchronous.
        await using var bufferedOutput = new ZipResponseStream(output, ct);
        await using var archive = await ZipArchive.CreateAsync(bufferedOutput, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: null, ct);
        var utf8 = archive.CreateEntry("InUTF8.txt", CompressionLevel.Fastest);
        await using (var entry = await utf8.OpenAsync(ct))
        {
            await entry.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, ct);
            foreach (var chunk in recording.Chunks) await entry.WriteAsync(chunk.Content, ct);
        }
        var base64 = archive.CreateEntry("bytesBase64.txt", CompressionLevel.Fastest);
        await using (var entry = await base64.OpenAsync(ct)) await WriteBase64Async(entry, recording.Chunks, ct);

        var metadata = archive.CreateEntry("sessionLogsAndInputs.json", CompressionLevel.Fastest);
        await using var json = await metadata.OpenAsync(ct);
        await using var writer = new Utf8JsonWriter(json);
        writer.WriteStartObject();
        writer.WriteString("sessionId", sessionId);
        writer.WriteStartArray("sessionLogs");
        foreach (var chunk in recording.Chunks)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", chunk.Id);
            writer.WriteString("sessionId", sessionId);
            writer.WriteString("timestamp", chunk.TimestampUtc);
            writer.WriteBase64String("content", chunk.Content);
            writer.WriteBoolean("isError", chunk.IsError);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("userInputs");
        foreach (var input in recording.Inputs)
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", input.Id);
            writer.WriteString("sessionId", input.SessionId);
            writer.WriteNumber("sequence", input.Sequence);
            writer.WriteString("inputText", input.InputText);
            writer.WriteString("gitCommitHash", input.GitCommitHash);
            writer.WriteString("timestampUTC", input.TimestampUTC);
            writer.WriteEndObject();
            await writer.FlushAsync(ct);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);
    }

    private static async Task WriteBase64Async(Stream output, List<SessionRecordingChunk> chunks, CancellationToken ct)
    {
        using var transform = new ToBase64Transform();
        await using var encoded = new CryptoStream(output, transform, CryptoStreamMode.Write, leaveOpen: true);
        // One transform across chunks preserves incomplete three-byte groups at chunk boundaries.
        foreach (var chunk in chunks) await encoded.WriteAsync(chunk.Content, ct);
        await encoded.FlushFinalBlockAsync(ct);
    }

    private sealed class ZipResponseStream(Stream output, CancellationToken ct) : Stream
    {
        private readonly byte[] pending = new byte[64 * 1024];
        private int count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int length) => Write(buffer.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ct.ThrowIfCancellationRequested();
            if (buffer.Length > pending.Length - count) throw new IOException("ZIP synchronous staging limit exceeded.");
            buffer.CopyTo(pending.AsSpan(count));
            count += buffer.Length;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainAsync();
            await output.WriteAsync(buffer, ct);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int length, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, length), cancellationToken).AsTask();

        // A synchronous compressor flush only stages data; the next async write/disposal drains it.
        public override void Flush() { }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainAsync();
            await output.FlushAsync(ct);
        }

        private async ValueTask DrainAsync()
        {
            if (count == 0) return;
            await output.WriteAsync(pending.AsMemory(0, count), ct);
            count = 0;
        }

        public override async ValueTask DisposeAsync()
        {
            await DrainAsync();
            GC.SuppressFinalize(this);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

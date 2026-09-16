using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>Stages an indexed snapshot with bounded buffers so an optional read failure cannot corrupt an archive.</summary>
public sealed class SqliteProxyExchangeArchiveReader(string databasePath, ILogger<SqliteProxyExchangeArchiveReader>? logger = null)
    : IProxyExchangeArchiveReader
{
    public async Task<PreparedProxyArchive> PrepareAsync(string sessionId, CancellationToken cancellationToken)
    {
        FileStream? staged = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(databasePath))
                return new("unavailable");
            await using var connection = await SqliteConnectionFactory.OpenAsync(
                new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString(), cancellationToken, readOnly: true);
            await using var transaction = connection.BeginTransaction(deferred: true);
            using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "SELECT 1 FROM pragma_table_info('ProxyExchanges') WHERE name='SessionId';";
                if (await check.ExecuteScalarAsync(cancellationToken) is null)
                    return new("unavailable");
            }
            var snapshotUtc = DateTime.UtcNow;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT rowid, Id, SessionId, CreatedUTC, Provider, Method, Path, StatusCode,
                       RequestBefore, RequestAfter, ResponseBody, ResponseTruncated,
                       CharsBefore, CharsAfter, ResponseChars, ElapsedMs
                FROM ProxyExchanges WHERE SessionId=$sessionId ORDER BY rowid;
                """;
            command.Parameters.AddWithValue("$sessionId", sessionId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new("empty", 0, snapshotUtc);

            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose,
                BufferSize = 64 * 1024
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            staged = new FileStream(Path.Combine(Path.GetTempPath(), $"viberails-proxy-{Guid.NewGuid():N}.json"), options);
            long count = 0;
            long maxRowId = 0;
            await using (var writer = new Utf8JsonWriter(staged))
            {
                writer.WriteStartArray();
                do
                {
                    // Rows are read in rowid order inside one read transaction, so the last rowid
                    // seen is the snapshot's high-water mark: anything the proxy's write queue
                    // lands afterwards gets a larger rowid and is not in this envelope.
                    maxRowId = Math.Max(maxRowId, reader.GetInt64(0));
                    writer.WriteStartObject();
                    writer.WriteString("id", reader.GetString(1));
                    writer.WriteString("sessionId", reader.GetString(2));
                    writer.WriteString("createdUtc", reader.GetString(3));
                    writer.WriteString("provider", reader.GetString(4));
                    writer.WriteString("method", reader.GetString(5));
                    writer.WriteString("path", reader.GetString(6));
                    writer.WriteNumber("statusCode", reader.GetInt32(7));
                    await WriteTextAsync(writer, reader, 8, "requestBefore", cancellationToken);
                    await WriteTextAsync(writer, reader, 9, "requestAfter", cancellationToken);
                    await WriteTextAsync(writer, reader, 10, "responseBody", cancellationToken);
                    writer.WriteBoolean("responseTruncated", reader.GetBoolean(11));
                    writer.WriteNumber("charsBefore", reader.GetInt64(12));
                    writer.WriteNumber("charsAfter", reader.GetInt64(13));
                    writer.WriteNumber("responseChars", reader.GetInt64(14));
                    writer.WriteNumber("elapsedMs", reader.GetInt64(15));
                    writer.WriteEndObject();
                    count++;
                    await writer.FlushAsync(cancellationToken);
                } while (await reader.ReadAsync(cancellationToken));
                writer.WriteEndArray();
                await writer.FlushAsync(cancellationToken);
            }
            staged.Position = 0;
            var result = new PreparedProxyArchive("included", count, snapshotUtc, staged, maxRowId);
            staged = null; // The returned result owns and deletes the private staging file.
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Narrow on purpose. "Optional" means the proxy database may be missing, locked, corrupt
        // or unwritable -- not that any failure at all should be reported as coverage=unavailable.
        // A blanket catch (Exception) turned a NullReferenceException or a schema/ordinal mismatch
        // in the reader above into a silent, permanent omission of every proxy exchange from the
        // export, with only a warning in a log nobody reads.
        catch (Exception ex) when (ex is SqliteException or StorageException
            or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger?.LogWarning(ex, "Optional proxy archive unavailable for session {SessionId}.", sessionId);
            return new("unavailable");
        }
        finally
        {
            if (staged is not null)
                await staged.DisposeAsync();
        }
    }

    private static async Task WriteTextAsync(Utf8JsonWriter writer, SqliteDataReader reader, int ordinal,
        string property, CancellationToken cancellationToken)
    {
        writer.WritePropertyName(property);
        // Selecting rowid above allows Microsoft.Data.Sqlite to open an incremental TEXT stream.
        using var source = reader.GetStream(ordinal);
        // Encoding.UTF8 advertises a preamble which StreamReader strips even with BOM detection
        // disabled. A BOM inside stored body text is content and must round-trip unchanged.
        using var text = new StreamReader(source, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 16 * 1024);
        var buffer = ArrayPool<char>.Shared.Rent(16 * 1024 + 1);
        try
        {
            var carried = 0;
            while (true)
            {
                var read = await text.ReadAsync(buffer.AsMemory(carried, 16 * 1024), cancellationToken);
                if (read == 0)
                {
                    writer.WriteStringValueSegment(buffer.AsSpan(0, carried), isFinalSegment: true);
                    break;
                }
                var length = read + carried;
                carried = char.IsHighSurrogate(buffer[length - 1]) ? 1 : 0;
                writer.WriteStringValueSegment(buffer.AsSpan(0, length - carried), isFinalSegment: false);
                if (carried != 0)
                    buffer[0] = buffer[length - 1];
                if (writer.BytesPending >= 64 * 1024)
                    await writer.FlushAsync(cancellationToken);
            }
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }
}

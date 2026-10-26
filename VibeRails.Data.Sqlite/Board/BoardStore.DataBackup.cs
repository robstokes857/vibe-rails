using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeRails.Services.Board;

public sealed partial class BoardStore
{
    private const int BackupValuePartBytes = 2 * 1024 * 1024;
    private const int BackupInlineTextBytes = 64 * 1024;

    /// <inheritdoc />
    public async Task ReadDataBackupAsync(Func<BoardDataBackupItem, CancellationToken, Task> emit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emit);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction(deferred: true);
        var tables = new List<string>();
        using (var schema = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(schema))
            {
                writer.WriteStartObject(); writer.WriteString("table", "$schema"); writer.WriteString("key", "schema");
                writer.WriteStartObject("values"); writer.WriteStartArray("objects");
                using var command = connection.CreateCommand(); command.Transaction = transaction;
                command.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY type,name;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var type = reader.GetString(0); var name = reader.GetString(1);
                    if (type == "table") tables.Add(name);
                    writer.WriteStartObject(); writer.WriteString("type", type); writer.WriteString("name", name);
                    writer.WriteString("table", reader.GetString(2)); writer.WriteString("sql", reader.GetString(3)); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.WriteEndObject();
            }
            await EmitJson(schema.ToArray());
        }
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            var primary = new List<(int Order, string Name)>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction; command.CommandText = $"PRAGMA table_info({Quote(table)});";
                await using var columns = await command.ExecuteReaderAsync(cancellationToken);
                while (await columns.ReadAsync(cancellationToken))
                    if (columns.GetInt32(5) > 0) primary.Add((columns.GetInt32(5), columns.GetString(1)));
            }
            var keys = primary.OrderBy(x => x.Order).Select(x => x.Name).ToArray();
            using var rows = connection.CreateCommand(); rows.Transaction = transaction;
            rows.CommandText = keys.Length == 0
                ? $"SELECT rowid AS __backup_rowid__, * FROM {Quote(table)} ORDER BY rowid;"
                : $"SELECT * FROM {Quote(table)} ORDER BY {string.Join(',', keys.Select(Quote))};";
            await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var output = new MemoryStream();
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject(); writer.WriteString("table", table);
                    using (var keyBytes = new MemoryStream())
                    {
                        using (var keyWriter = new Utf8JsonWriter(keyBytes))
                        {
                            keyWriter.WriteStartArray();
                            foreach (var name in keys.Length == 0 ? new[] { "__backup_rowid__" } : keys)
                                WriteScalar(keyWriter, reader.GetValue(reader.GetOrdinal(name)));
                            keyWriter.WriteEndArray();
                        }
                        writer.WriteString("key", Encoding.UTF8.GetString(keyBytes.ToArray()));
                    }
                    writer.WriteStartObject("values");
                    for (var i = keys.Length == 0 ? 1 : 0; i < reader.FieldCount; i++)
                    {
                        writer.WritePropertyName(reader.GetName(i));
                        if (reader.IsDBNull(i)) { writer.WriteNullValue(); continue; }
                        if (reader.GetFieldType(i) == typeof(byte[]))
                        {
                            await using var content = reader.GetStream(i);
                            await WriteParts(writer, content, "blob");
                        }
                        else if (reader.GetFieldType(i) == typeof(string))
                        {
                            var text = reader.GetString(i);
                            if (Encoding.UTF8.GetByteCount(text) <= BackupInlineTextBytes) writer.WriteStringValue(text);
                            else
                            {
                                using var content = new MemoryStream(Encoding.UTF8.GetBytes(text), writable: false);
                                await WriteParts(writer, content, "text");
                            }
                        }
                        else WriteScalar(writer, reader.GetValue(i));
                    }
                    writer.WriteEndObject(); writer.WriteEndObject();
                }
                await EmitJson(output.ToArray());
            }
        }

        async Task EmitJson(byte[] bytes) => await emit(new(Hash(bytes), Encoding.UTF8.GetString(bytes), null), cancellationToken);
        async Task WriteParts(Utf8JsonWriter writer, Stream content, string kind)
        {
            writer.WriteStartObject(); writer.WriteString("$type", kind); writer.WriteStartArray("parts");
            var buffer = new byte[BackupValuePartBytes]; long length = 0;
            while (true)
            {
                var read = 0;
                while (read < buffer.Length)
                {
                    var count = await content.ReadAsync(buffer.AsMemory(read), cancellationToken);
                    if (count == 0) break;
                    read += count;
                }
                if (read == 0) break;
                var bytes = buffer.AsSpan(0, read).ToArray(); var hash = Hash(bytes);
                await emit(new(hash, null, bytes), cancellationToken);
                writer.WriteStringValue(hash); length += read;
            }
            writer.WriteEndArray(); writer.WriteNumber("bytes", length); writer.WriteEndObject();
        }
    }

    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WriteScalar(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case DBNull: writer.WriteNullValue(); break;
            case string text: writer.WriteStringValue(text); break;
            case long integer: writer.WriteNumberValue(integer); break;
            case double number: writer.WriteNumberValue(number); break;
            case byte[] bytes: writer.WriteBase64StringValue(bytes); break;
            default: throw new InvalidDataException("Unsupported SQLite backup value type.");
        }
    }
}

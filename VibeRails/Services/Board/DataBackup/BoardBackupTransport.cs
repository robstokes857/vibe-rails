using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace VibeRails.Services.Board.Backup;

/// <summary>Private PostgreSQL backup ingest through the fixed account service; never Azure or UI sync.</summary>
public sealed class BoardBackupTransport
{
    internal const string ClientName = "board-data-backup";
    internal static readonly Uri Endpoint = new("https://viberails.ai/api/v1/data-exports/board/");
    private readonly Func<HttpClient> client;
    public BoardBackupTransport(IHttpClientFactory factory) => client = () => factory.CreateClient(ClientName);
    internal BoardBackupTransport(HttpClient http) => client = () => http;

    public async Task<int> AccountAsync(string key, CancellationToken ct)
    {
        var bytes = await SendAsync(HttpMethod.Get, "account", key, null, null, ct);
        var account = JsonSerializer.Deserialize(bytes, BoardBackupJson.Default.BoardBackupAccount);
        return account is { AccountId: > 0 } ? account.AccountId : throw new InvalidDataException("Invalid Board backup account receipt.");
    }

    public async Task ContentAsync(string key, string hash, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is <= 0 or > BoardBackupProtocol.ContentBytes || BoardBackupProtocol.Hash(bytes) != hash)
            throw new InvalidDataException("Board backup content staging is invalid.");
        var response = await SendAsync(HttpMethod.Put, "content/" + hash, key, bytes, "application/octet-stream", ct);
        var receipt = JsonSerializer.Deserialize(response, BoardBackupJson.Default.BoardBackupContentReceipt);
        if (receipt is null || receipt.Sha256 != hash || receipt.Bytes != bytes.Length)
            throw new InvalidDataException("Board backup content was not acknowledged.");
    }

    public async Task RowsAsync(string key, IReadOnlyList<byte[]> rows, CancellationToken ct)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartArray(); foreach (var row in rows) writer.WriteRawValue(row); writer.WriteEndArray();
        }
        if (output.Length > BoardBackupProtocol.RequestBytes) throw new InvalidDataException("Board backup batch is too large.");
        var response = await SendAsync(HttpMethod.Post, "rows", key, output.ToArray(), "application/json", ct);
        var receipt = JsonSerializer.Deserialize(response, BoardBackupJson.Default.BoardBackupRowsReceipt);
        if (receipt is null || !receipt.Hashes.SequenceEqual(rows.Select(x => BoardBackupProtocol.Hash(x))))
            throw new InvalidDataException("Board backup row receipt did not match.");
    }

    public async Task<HashSet<string>> MissingAsync(string key, string kind, IReadOnlyList<string> hashes, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new BoardBackupInventoryRequest(kind, hashes.ToList()), BoardBackupJson.Default.BoardBackupInventoryRequest);
        var response = await SendAsync(HttpMethod.Post, "inventory", key, body, "application/json", ct);
        var inventory = JsonSerializer.Deserialize(response, BoardBackupJson.Default.BoardBackupInventory);
        if (inventory is null || inventory.Missing.Any(h => !hashes.Contains(h))
            || inventory.Missing.Distinct(StringComparer.Ordinal).Count() != inventory.Missing.Count)
            throw new InvalidDataException("Invalid Board backup inventory response.");
        return inventory.Missing.ToHashSet(StringComparer.Ordinal);
    }

    public async Task<BoardBackupReceipt> CommitAsync(string key, int account, BoardBackupManifest manifest, byte[] bytes, CancellationToken ct)
    {
        var hash = BoardBackupProtocol.Hash(bytes);
        var response = await SendAsync(HttpMethod.Post, "snapshots/" + manifest.Version, key, bytes, "application/json", ct, hash);
        var receipt = JsonSerializer.Deserialize(response, BoardBackupJson.Default.BoardBackupReceipt);
        if (receipt is null || receipt.AccountId != account || receipt.ComputerId != manifest.ComputerId
            || receipt.Version != manifest.Version || receipt.ManifestSha256 != hash
            || receipt.Records != manifest.Records.Count || receipt.Contents != manifest.Contents.Count
            || receipt.ReceivedUtc.Kind != DateTimeKind.Utc)
            throw new InvalidDataException("Board backup snapshot receipt did not match.");
        return receipt;
    }

    private async Task<byte[]> SendAsync(HttpMethod method, string path, string key, byte[]? body, string? type,
        CancellationToken ct, string? hash = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromMinutes(2));
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, path));
        request.Headers.Add("X-Api-Key", key);
        if (hash is not null) request.Headers.Add("X-Content-SHA256", hash);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(type!);
        }
        using var response = await client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Board data backup failed (HTTP {(int)response.StatusCode}); it will retry.", null, response.StatusCode);
        await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
        using var result = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token)) > 0)
        {
            if (result.Length + read > 64 * 1024) throw new InvalidDataException("Board backup receipt is too large.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
}

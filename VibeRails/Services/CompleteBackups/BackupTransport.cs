using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace VibeRails.Services.Backups;

/// <summary>Bounded, idempotent part uploads to the fixed account service. No session receipts enter this protocol.</summary>
public sealed class BackupTransport(HttpClient http)
{
    internal static readonly Uri Endpoint = new("https://viberails.ai/api/v1/data-exports/backups/");

    public async Task<int> AccountAsync(string key, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, "account", key, null, ct);
        var account = JsonSerializer.Deserialize(await BodyAsync(response, ct), BackupJson.Default.BackupAccount);
        return account is { AccountId: > 0 } ? account.AccountId : throw new InvalidDataException("Invalid backup account acknowledgement.");
    }

    public async Task UploadPartAsync(string key, BackupPart part, string path, CancellationToken ct)
    {
        // Re-hash before every send, including a restart. Corrupt staging bytes never become a receipt.
        await using var input = OpenStagedPart(path);
        if (input.Length != part.Bytes) throw new InvalidDataException("Backup staging length failed; the pending archive has been preserved.");
        var bytes = new byte[part.Bytes];
        await input.ReadExactlyAsync(bytes, ct);
        if (BackupFormat.Hash(bytes) != part.Sha256)
            throw new InvalidDataException("Backup staging checksum failed; the pending archive has been preserved.");
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(HttpMethod.Put, "parts/" + part.Sha256, key, content, ct);
        var receipt = JsonSerializer.Deserialize(await BodyAsync(response, ct), BackupJson.Default.BackupPartReceipt);
        if (receipt is null || receipt.Sha256 != part.Sha256 || receipt.Bytes != part.Bytes || !receipt.ChecksumsVerified)
            throw new InvalidDataException("The backup server did not verify the uploaded part.");
    }

    public async Task<BackupReceipt> CommitAsync(string key, int account, BackupManifest manifest, byte[] bytes, CancellationToken ct)
    {
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var hash = BackupFormat.Hash(bytes);
        using var response = await SendAsync(HttpMethod.Post, "versions/" + manifest.Version, key, content, ct, hash);
        var receipt = JsonSerializer.Deserialize(await BodyAsync(response, ct), BackupJson.Default.BackupReceipt);
        if (!MatchesReceipt(receipt, account, manifest, hash))
            throw new InvalidDataException("The backup receipt did not match this account, computer and archive version.");
        return receipt!;
    }

    internal static bool MatchesReceipt(BackupReceipt? receipt, int account, BackupManifest manifest, string hash) =>
        receipt is { ChecksumsVerified: true } && receipt.AccountId == account
        && receipt.ComputerId == manifest.ComputerId && receipt.Dataset == manifest.Dataset && receipt.Version == manifest.Version
        && receipt.ManifestSha256 == hash && receipt.PayloadBytes == manifest.PayloadBytes
        && receipt.SourceStartedUtc == manifest.SourceStartedUtc && receipt.SourceCompletedUtc == manifest.SourceCompletedUtc
        && receipt.ReceivedUtc.Kind == DateTimeKind.Utc && receipt.ReceivedUtc >= manifest.SourceStartedUtc.AddDays(-1);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relative, string key,
        HttpContent? content, CancellationToken ct, string? checksum = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(Endpoint, relative));
        request.Headers.Add("X-Api-Key", key);
        if (checksum is not null) request.Headers.Add("X-Content-SHA256", checksum);
        request.Content = content;
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode) return response;
        var code = response.StatusCode;
        response.Dispose();
        throw new HttpRequestException(code switch
        {
            HttpStatusCode.NotFound => "The server does not support complete backups yet.",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The backup account credential was rejected.",
            HttpStatusCode.Conflict => "The backup server found missing parts or a conflicting archive; delivery will retry.",
            _ => $"Backup delivery failed (HTTP {(int)code})."
        }, null, code);
    }

    private static FileStream OpenStagedPart(string path)
    {
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException("Backup staging part is missing; remaining files have been preserved.", e); }
    }

    private static async Task<byte[]> BodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await body.ReadAsync(buffer, timeout.Token)) > 0)
        {
            if (output.Length + read > 32 * 1024) throw new InvalidDataException("Backup acknowledgement exceeded its size limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

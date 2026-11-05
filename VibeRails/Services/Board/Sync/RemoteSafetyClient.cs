using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

public sealed record RemoteSafetyStatus(int AccountId, string Generation, RemoteSafetyWork RemoteWork, bool UploadsBlocked,
    bool LocalReviewRequired = false, string Destination = "");
public sealed record RemoteSafetyWork(string State, DateTime? UnlockUtc);
internal sealed record RemoteSafetyApproval(string Generation, bool Hold);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RemoteSafetyStatus))]
[JsonSerializable(typeof(RemoteSafetyApproval))]
internal partial class RemoteSafetyJson : JsonSerializerContext { }

/// <summary>Durable per-account/destination permission hold. It never consumes or acknowledges queue entries.</summary>
public sealed class RemoteSafetyClient(Func<HttpClient> openClient, Func<Uri?> endpoint, Func<string?> credential,
    string statusPath = "/api/v1/boards/remote-access", string? stateDirectory = null)
{
    public static bool IsSafetyCode(string? code) => code is "board_remote_sync_paused" or "account_uploads_blocked"
        or "account_remote_upgrade_required" or "account_remote_reapproval_required" or "account_remote_access_unavailable";
    private string DirectoryPath => stateDirectory ?? Path.Combine(Path.GetDirectoryName(ParserConfigs.GetStatePath())!, "remote-safety");
    private string StatePath(RemoteSafetyStatus status) => Path.Combine(DirectoryPath,
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(status.Destination + "\n" + status.AccountId))) + ".json");
    public async Task<RemoteSafetyStatus> StatusAsync(CancellationToken ct)
    {
        var origin = endpoint() ?? throw Failure("account_remote_access_unavailable");
        var key = credential();
        if (string.IsNullOrWhiteSpace(key)) throw Failure("account_remote_access_unavailable");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, statusPath));
        request.Headers.Add("X-Api-Key", key);
        using var client = openClient();
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) throw Failure("account_remote_access_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[8193]; var count = 0;
        while (count < bytes.Length) { var read = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token); if (read == 0) break; count += read; }
        if (count > 8192) throw Failure("account_remote_access_unavailable");
        var status = JsonSerializer.Deserialize(bytes.AsSpan(0, count), RemoteSafetyJson.Default.RemoteSafetyStatus);
        if (status is null || status.AccountId <= 0 || !long.TryParse(status.Generation, NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || generation < 0 || status.Generation != generation.ToString(CultureInfo.InvariantCulture)
            || status.RemoteWork.State is not ("enabled" or "locked" or "recovery_required")) throw Failure("account_remote_access_unavailable");
        status = status with { Destination = origin.GetLeftPart(UriPartial.Authority) };
        Directory.CreateDirectory(DirectoryPath);
        var path = StatePath(status);
        using var held = CrossProcessFileLock.Acquire(path + ".lock", TimeSpan.FromSeconds(3));
        var saved = Read(path);
        var hold = saved?.Hold == true || saved?.Generation != status.Generation && status.Generation != "0" || status.RemoteWork.State != "enabled";
        Save(path, new(status.Generation, hold));
        return status with { LocalReviewRequired = hold };
    }
    public async Task<string> EnsureAsync(bool remote, bool upload, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (remote && status.RemoteWork.State != "enabled") throw Failure("board_remote_sync_paused");
        if (upload && status.UploadsBlocked) throw Failure("account_uploads_blocked");
        if (status.LocalReviewRequired) throw Failure("account_remote_reapproval_required");
        return status.Generation;
    }
    public async Task<RemoteSafetyStatus> ApproveAsync(string expectedGeneration, int expectedAccountId, string expectedDestination, CancellationToken ct)
    {
        var status = await StatusAsync(ct);
        if (status.Generation != expectedGeneration || status.AccountId != expectedAccountId || status.Destination != expectedDestination || status.RemoteWork.State != "enabled" || status.UploadsBlocked)
            throw Failure("account_remote_reapproval_required");
        var path = StatePath(status);
        using var held = CrossProcessFileLock.Acquire(path + ".lock", TimeSpan.FromSeconds(3));
        Save(path, new(status.Generation, false));
        return status with { LocalReviewRequired = false };
    }
    private static RemoteSafetyApproval? Read(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize(File.ReadAllText(path), RemoteSafetyJson.Default.RemoteSafetyApproval) : null;
    private static void Save(string path, RemoteSafetyApproval value)
    {
        var pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(value, RemoteSafetyJson.Default.RemoteSafetyApproval));
        PrivateFilePermissions.EnsureFile(pending); File.Move(pending, path, true);
    }
    public static BoardSyncClientException Failure(string code) => new(code switch {
        "board_remote_sync_paused" => "Remote access is paused for 72 hours. Open Account on viberails.ai for the unlock time and recovery action. Local work is retained.",
        "account_uploads_blocked" => "Uploads are blocked for this account. Contact support. Local work is retained.",
        "account_remote_reapproval_required" => "Review pending changes in Remote boards before approving remote work. Nothing has been sent.",
        "account_remote_upgrade_required" => "Update VibeRails before approving remote work. Local work is retained.",
        _ => "Remote access status is temporarily unavailable. Local work is retained."
    }, code);
}

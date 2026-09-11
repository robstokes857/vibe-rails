using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Utils;

namespace VibeRails.Services.SigningKeys;

internal sealed record StoredSigningKey(Guid Id, string Name, string PublicKeyPem, string Fingerprint,
    DateTimeOffset CreatedUtc, string EncryptedPrivateKeyPem, Guid? CloudKeyId = null,
    DateTimeOffset? CloudSyncedUtc = null, string? CloudCredentialHash = null);

/// <summary>Readable keys plus one warning per skipped file, so one damaged file never hides the rest.</summary>
internal sealed record SigningKeyListing(StoredSigningKey[] Keys, string[] Warnings);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StoredSigningKey))]
internal sealed partial class SigningKeyStoreJsonContext : JsonSerializerContext;

/// <summary>Encrypted PKCS#8 only. Serialized across dashboard processes; atomic per-key writes.</summary>
public sealed class SigningKeyStore
{
    /// <summary>Upper bound on directory entries examined by a listing, well above the key limit.</summary>
    internal const int MaxListedFiles = 256;

    private readonly string _directory;
    public SigningKeyStore(string? directory = null) =>
        _directory = Path.GetFullPath(directory ?? Path.Combine(PathConstants.GetInstallDirPath(), "signing-keys"));

    internal async Task<FileStream> LockAsync(CancellationToken cancellationToken)
    {
        RejectLinks(_directory);
        PrivateFilePermissions.EnsureDirectory(_directory);
        var path = Path.Combine(_directory, ".lock");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(path);
            try { return new FileStream(path, PrivateOptions(FileMode.OpenOrCreate)); }
            catch (IOException) when (watch.Elapsed < TimeSpan.FromSeconds(20))
            { await Task.Delay(50, cancellationToken); }
        }
    }

    internal SigningKeyListing ReadAll()
    {
        var keys = new List<StoredSigningKey>();
        var warnings = new List<string>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json").Take(MaxListedFiles))
        {
            var fileName = Path.GetFileName(path);
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id))
            {
                warnings.Add($"Skipped '{fileName}' in the signing-keys folder: not a signing-key file.");
                continue;
            }
            try { keys.Add(Read(id)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
            {
                warnings.Add($"Skipped '{fileName}' in the signing-keys folder: the file is damaged or unreadable.");
            }
        }
        return new([.. keys.OrderByDescending(key => key.CreatedUtc)], [.. warnings]);
    }

    internal StoredSigningKey Read(Guid id)
    {
        var path = KeyPath(id);
        RejectLinks(path);
        if (!File.Exists(path)) throw new KeyNotFoundException("Signing key was not found.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 24 * 1024) throw new IOException("Invalid signing-key file.");
        var key = JsonSerializer.Deserialize(file, SigningKeyStoreJsonContext.Default.StoredSigningKey);
        if (key is null || key.Id != id || key.PublicKeyPem is null || key.Fingerprint is null ||
            key.Name is null || key.EncryptedPrivateKeyPem is null ||
            !key.EncryptedPrivateKeyPem.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", StringComparison.Ordinal))
            throw new IOException("Invalid signing-key file.");
        return key;
    }

    internal void Write(StoredSigningKey key)
    {
        var target = KeyPath(key.Id);
        RejectLinks(target);
        var temporary = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporary, PrivateOptions(FileMode.CreateNew)))
            {
                JsonSerializer.Serialize(file, key, SigningKeyStoreJsonContext.Default.StoredSigningKey);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string KeyPath(Guid id) => Path.Combine(_directory, $"{id:N}.json");

    private static FileStreamOptions PrivateOptions(FileMode mode)
    {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    /// <summary>
    /// Refuses a link in place of the store directory or of the entry itself. Ancestors are not
    /// examined: a relocated user profile, a junctioned Users folder, or macOS's /var symlink are
    /// legitimate and refusing them would disable the feature rather than protect anything here.
    /// </summary>
    private void RejectLinks(string path)
    {
        RejectLink(_directory);
        if (!string.Equals(path, _directory, StringComparison.Ordinal)) RejectLink(path);
    }

    private static void RejectLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Signing-key storage cannot use links or reparse points.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}

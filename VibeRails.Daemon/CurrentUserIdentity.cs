using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace VibeRails.Daemon;

/// <summary>
/// Stable identity for the operating-system user that owns a daemon instance. The short scope key
/// is safe to embed in mutex, pipe, task, unit, and LaunchAgent names without disclosing a SID.
/// </summary>
public sealed record CurrentUserIdentity
{
    public CurrentUserIdentity(
        string stableId,
        string userProfileDirectory,
        string? windowsSid = null,
        uint? unixUserId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userProfileDirectory);

        StableId = stableId.Trim();
        UserProfileDirectory = Path.GetFullPath(userProfileDirectory);
        WindowsSid = windowsSid;
        UnixUserId = unixUserId;
        ScopeKey = CreateScopeKey(StableId);
    }

    public string StableId { get; }
    public string ScopeKey { get; }
    public string UserProfileDirectory { get; }
    public string? WindowsSid { get; }
    public uint? UnixUserId { get; }

    internal static string CreateScopeKey(string stableId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableId));
        return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant();
    }
}

public interface ICurrentUserIdentityProvider
{
    CurrentUserIdentity GetCurrent();
}

/// <summary>Resolves a Windows SID or Unix effective UID without invoking a shell.</summary>
public sealed partial class CurrentUserIdentityProvider : ICurrentUserIdentityProvider
{
    public CurrentUserIdentity GetCurrent()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("Unable to locate the current user's profile directory.");

        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value
                ?? throw new InvalidOperationException("Unable to determine the current Windows user SID.");
            return new CurrentUserIdentity($"sid:{sid}", profile, windowsSid: sid);
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var uid = GetEffectiveUserId();
            return new CurrentUserIdentity($"uid:{uid}", profile, unixUserId: uid);
        }

        return new CurrentUserIdentity($"user:{Environment.UserName}:{Path.GetFullPath(profile)}", profile);
    }

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEffectiveUserId();
}

using System.Text;
using System.Text.Json;
using VibeRails.Services.Git;

namespace VibeRails.Services.VCA;

/// <summary>One pending or approved exception per Git worktree; no application database is opened.</summary>
public sealed class StopBypassStore
{
    private static async Task<string> PathAsync(string repository, CancellationToken ct)
    {
        var git = await GitCli.RunAsync(repository, ["rev-parse", "--absolute-git-dir"], ct);
        if (!git.Succeeded) throw new ArgumentException("A Git repository is required for a STOP bypass.");
        return Path.Combine(git.StdOut.Trim(), "viberails-stop-bypass.json");
    }

    public async Task<StopBypassRequest?> ReadAsync(string repository, CancellationToken ct)
    {
        var path = await PathAsync(repository, ct);
        return await ReadPathAsync(path, ct);
    }

    private static async Task<StopBypassRequest?> ReadPathAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return null;
        CheckFile(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > StopBypassFormat.MaxBytes) throw new ArgumentException("The local STOP bypass request is too large.");
        return await JsonSerializer.DeserializeAsync(stream, StopBypassJson.Default.StopBypassRequest, ct);
    }

    public async Task SaveAsync(string repository, StopBypassRequest request, string? expectedId, CancellationToken ct)
    {
        var path = await PathAsync(repository, ct);
        CheckFile(path);
        var lockPath = path + ".lock";
        CheckFile(lockPath);
        // A short cross-process critical section prevents an old popup replacing a newer request.
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (expectedId is not null && (await ReadPathAsync(path, ct))?.Id != expectedId)
            throw new ArgumentException("The STOP bypass request was replaced. Request approval again.");
        var json = JsonSerializer.Serialize(request, StopBypassJson.Default.StopBypassRequest);
        if (Encoding.UTF8.GetByteCount(json) > StopBypassFormat.MaxBytes)
            throw new ArgumentException("The local STOP bypass request is too large.");
        var temp = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void CheckFile(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The local STOP bypass file must not be a symbolic link.");
    }
}

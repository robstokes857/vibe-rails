using Moq;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.LlmClis;

public sealed class CodexEnvironmentCreationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"codex-env-copy-{Guid.NewGuid():N}");
    private readonly Mock<IFileService> files = new(MockBehavior.Strict);
    private readonly CodexLlmCliEnvironment service;
    private string Home => Path.Combine(root, "home");
    private string Source => Path.Combine(Home, ".codex");
    private string Destination => Path.Combine(root, "agent", "codex");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public CodexEnvironmentCreationTests()
    {
        Directory.CreateDirectory(Home);
        files.Setup(x => x.GetUserProfilePath()).Returns(Home);
        files.Setup(x => x.DirectoryExists(It.IsAny<string>())).Returns((string path) => Directory.Exists(path));
        files.Setup(x => x.CreateDirectory(It.IsAny<string>())).Callback((string path) => Directory.CreateDirectory(path));
        files.Setup(x => x.FileExists(It.IsAny<string>())).Returns((string path) => File.Exists(path));
        files.Setup(x => x.GetFileName(It.IsAny<string>())).Returns((string path) => Path.GetFileName(path));
        files.Setup(x => x.EnumerateFiles(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<EnumerationOptions>()))
            .Returns((string path, string pattern, EnumerationOptions options) => Directory.EnumerateFiles(path, pattern, options));
        files.Setup(x => x.EnumerateDirectories(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<EnumerationOptions>()))
            .Returns((string path, string pattern, EnumerationOptions options) => Directory.EnumerateDirectories(path, pattern, options));
        files.Setup(x => x.CopyFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .Callback((string source, string destination, bool overwrite) => File.Copy(source, destination, overwrite));
        files.Setup(x => x.WriteAllTextAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<FileMode>(), It.IsAny<FileShare>(), It.IsAny<CancellationToken>()))
            .Returns(async (string path, string content, FileMode mode, FileShare share, CancellationToken ct) =>
            {
                await using var stream = new FileStream(path, mode, FileAccess.Write, share);
                await using var writer = new StreamWriter(stream);
                await writer.WriteAsync(content.AsMemory(), ct);
            });
        service = new CodexLlmCliEnvironment(files.Object);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateEnvironment_CopiesConfigurationWithoutThreadWriterLocks(bool holdLockOpen)
    {
        await WriteSourceAsync("config.toml", "model = \"test-model\"\n");
        await WriteSourceAsync("auth.json", "{\"fixture\":true}");
        await WriteSourceAsync("AGENTS.md", "# Agent instructions\n");
        await WriteSourceAsync(Path.Combine("skills", "example", "SKILL.md"), "# Example skill\n");
        var lockPath = await WriteSourceAsync(Path.Combine("thread-writer-locks", "active.lock"), "runtime lock");

        using (var activeLock = holdLockOpen ? new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
        {
            await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);
        }

        Assert.False(Directory.Exists(Path.Combine(Destination, "thread-writer-locks")));
        Assert.Equal("runtime lock", await File.ReadAllTextAsync(lockPath, Ct));
        foreach (var relativePath in new[] { "config.toml", "auth.json", "AGENTS.md", Path.Combine("skills", "example", "SKILL.md") })
        {
            Assert.Equal(await File.ReadAllTextAsync(Path.Combine(Source, relativePath), Ct),
                await File.ReadAllTextAsync(Path.Combine(Destination, relativePath), Ct));
        }
        files.Verify(x => x.EnumerateFiles(Path.GetDirectoryName(lockPath)!, It.IsAny<string>(), It.IsAny<EnumerationOptions>()), Times.Never);
    }

    [Fact]
    public async Task CreateEnvironment_SkipsSourceRuntimeStateAtTopLevelOnly()
    {
        await WriteSourceAsync("config.toml", "model = \"test-model\"\n");
        string[] runtimeDirs = ["mcp-oauth-locks", "project-metadata-locks", "app-server-daemon", "app-server-control"];
        foreach (var dir in runtimeDirs)
            await WriteSourceAsync(Path.Combine(dir, "daemon.lock"), "runtime state");
        var package = Path.Combine("packages", "app-server-daemon", "auto-update-version");
        await WriteSourceAsync(package, "0.165.0");

        await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);

        foreach (var dir in runtimeDirs)
            Assert.False(Directory.Exists(Path.Combine(Destination, dir)), dir);
        Assert.Equal("0.165.0", await File.ReadAllTextAsync(Path.Combine(Destination, package), Ct));
    }

    [Fact]
    public async Task CreateEnvironment_LeavesOutOtherFilesHeldLocked()
    {
        await WriteSourceAsync("config.toml", "model = \"test-model\"\n");
        await WriteSourceAsync("history.jsonl", "{}\n");
        var heldPath = await WriteSourceAsync(Path.Combine("sqlite", "codex-dev.db-wal"), "wal");

        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);
        }

        Assert.False(File.Exists(Path.Combine(Destination, "sqlite", "codex-dev.db-wal")));
        Assert.Equal("{}\n", await File.ReadAllTextAsync(Path.Combine(Destination, "history.jsonl"), Ct));
        Assert.Equal("model = \"test-model\"\n", await File.ReadAllTextAsync(Path.Combine(Destination, "config.toml"), Ct));
        Assert.Equal("wal", await File.ReadAllTextAsync(heldPath, Ct));
    }

    [Fact]
    public async Task CreateEnvironment_LeavesOutALockedDatabaseTogetherWithItsWalAndShm()
    {
        await WriteSourceAsync("config.toml", "model = \"test-model\"\n");
        var database = await WriteSourceAsync(Path.Combine("sqlite", "state_5.sqlite"), "database");
        await WriteSourceAsync(Path.Combine("sqlite", "state_5.sqlite-wal"), "wal");
        await WriteSourceAsync(Path.Combine("sqlite", "state_5.sqlite-shm"), "shm");
        await WriteSourceAsync(Path.Combine("sqlite", "logs_1.sqlite"), "other database");

        using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);
        }

        // A WAL without its database would be replayed onto the fresh one the CLI creates.
        foreach (var name in new[] { "state_5.sqlite", "state_5.sqlite-wal", "state_5.sqlite-shm" })
            Assert.False(File.Exists(Path.Combine(Destination, "sqlite", name)), name);
        Assert.Equal("other database", await File.ReadAllTextAsync(Path.Combine(Destination, "sqlite", "logs_1.sqlite"), Ct));
    }

    [Fact]
    public async Task CreateEnvironment_ToleratesAFileTheRunningCliDeletesWhileCopying()
    {
        await WriteSourceAsync("config.toml", "model = \"test-model\"\n");
        await WriteSourceAsync("history.jsonl", "{}\n");
        var vanished = await WriteSourceAsync(Path.Combine("sessions", "rollout.jsonl.lock"), "lock");
        files.Setup(x => x.CopyFile(vanished, It.IsAny<string>(), It.IsAny<bool>()))
            .Throws(new FileNotFoundException("Removed by the running CLI.", vanished));

        await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);

        Assert.False(File.Exists(Path.Combine(Destination, "sessions", "rollout.jsonl.lock")));
        Assert.Equal("{}\n", await File.ReadAllTextAsync(Path.Combine(Destination, "history.jsonl"), Ct));
    }

    [Theory]
    [InlineData("config.toml")]
    [InlineData("auth.json")]
    [InlineData("AGENTS.md")]
    public async Task CreateEnvironment_FailsWhenRequiredFileIsHeldLocked(string name)
    {
        var heldPath = await WriteSourceAsync(name, "required");

        using (new FileStream(heldPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(
                () => service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct));
        }

        Assert.False(File.Exists(Path.Combine(Destination, name)));
    }

    [Fact]
    public async Task CreateEnvironment_PreservesExistingConfigurationOnRetry()
    {
        await WriteSourceAsync("config.toml", "model = \"default-model\"\n");
        await WriteSourceAsync("auth.json", "default auth fixture");
        await WriteSourceAsync(Path.Combine("thread-writer-locks", "active.lock"), "runtime lock");
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Destination, "config.toml"), "model = \"custom-model\"\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(Destination, "auth.json"), "agent auth fixture", Ct);

        await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);

        Assert.Equal("model = \"custom-model\"\n", await File.ReadAllTextAsync(Path.Combine(Destination, "config.toml"), Ct));
        Assert.Equal("agent auth fixture", await File.ReadAllTextAsync(Path.Combine(Destination, "auth.json"), Ct));
        Assert.True(File.Exists(Path.Combine(Destination, "AGENTS.md")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "thread-writer-locks")));
    }

    [Fact]
    public async Task CreateEnvironment_WithoutDefaultCodexDirectoryCreatesDefaults()
    {
        await service.CreateEnvironment(new LLM_Environment { Path = Path.Combine(root, "agent") }, Ct);

        Assert.Contains("# Codex CLI Configuration", await File.ReadAllTextAsync(Path.Combine(Destination, "config.toml"), Ct));
        Assert.Contains("# Custom Instructions for Codex", await File.ReadAllTextAsync(Path.Combine(Destination, "AGENTS.md"), Ct));
    }

    private async Task<string> WriteSourceAsync(string relativePath, string content)
    {
        var path = Path.Combine(Source, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, Ct);
        return path;
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

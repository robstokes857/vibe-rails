using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;
using Xunit;

namespace Tests.Routes;

[Collection("ProcessEnvIsolation")]
public sealed class EnvironmentRoutesDeletionTests
{
    [Fact]
    public async Task UpdateEnvironment_BlankPromptIsRejectedWhenJobReferencesWorker()
    {
        var environment = NewEnvironment(path: "unused");
        environment.CustomPrompt = "Review every commit for security issues.";
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.FindEnvironmentByNameAsync(
                "review",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        var jobStore = new Mock<IJobStore>();
        jobStore
            .Setup(item => item.CountJobsForEnvironmentAsync(
                environment.Id,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IRepository>(repository.Object);
        builder.Services.AddSingleton<IJobStore>(jobStore.Object);

        await using var app = builder.Build();
        EnvironmentRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Put,
                new Uri(new Uri(app.Urls.First()), "/api/v1/environments/review"))
            {
                Content = JsonContent.Create(new UpdateEnvironmentRequest(
                    "review",
                    CustomPrompt: "   "))
            };

            using var response = await client.SendAsync(
                request,
                TestContext.Current.CancellationToken);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.NotNull(error);
            Assert.Contains("used by a Job", error.Error, StringComparison.Ordinal);
            Assert.Equal("Review every commit for security issues.", environment.CustomPrompt);
            repository.Verify(
                item => item.UpdateEnvironmentAsync(
                    It.IsAny<LLM_Environment>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_StaleRequestCannotTouchRecreatedEnvironment()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var testRoot = NewTempRoot();
        var envRoot = Path.Combine(testRoot, "envs");
        var originalPath = Path.Combine(envRoot, "review");
        var databasePath = Path.Combine(testRoot, "state.db");
        var connectionString = $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Shared";
        Directory.CreateDirectory(envRoot);
        CreateEnvironmentTable(connectionString);
        var jobStore = new JobStore(connectionString);
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));

        Directory.CreateDirectory(originalPath);
        File.WriteAllText(Path.Combine(originalPath, "old.txt"), "old credentials");
        var oldId = await InsertEnvironmentAsync(connectionString, "review", originalPath);
        var staleEnvironment = NewEnvironment(originalPath, oldId);
        ParserConfigs.SetEnvPath(envRoot);

        try
        {
            // Request A deletes the old row/path.
            Assert.True(await EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                service,
                jobStore,
                staleEnvironment,
                TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(originalPath));

            // Request C recreates the same logical name/path under a new database identity.
            Directory.CreateDirectory(originalPath);
            var replacementMarker = Path.Combine(originalPath, "replacement.txt");
            File.WriteAllText(replacementMarker, "replacement credentials");
            var replacementId = await InsertEnvironmentAsync(connectionString, "review", originalPath);
            Assert.NotEqual(oldId, replacementId);

            // Stale request B still holds A's old object. Its row proof fails under the SQLite
            // writer lock, so JobStore must never invoke the filesystem-stage callback.
            Assert.False(await EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                service,
                jobStore,
                staleEnvironment,
                TestContext.Current.CancellationToken));

            Assert.Equal("replacement credentials", File.ReadAllText(replacementMarker));
            Assert.True(await EnvironmentExistsAsync(connectionString, replacementId));
            Assert.Empty(Directory.EnumerateDirectories(envRoot, ".viberails-delete-*"));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            SqliteConnection.ClearAllPools();
            Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_StagesBeforeDeleteAndPreservesRecreation()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var envRoot = NewTempRoot();
        var originalPath = Path.Combine(envRoot, "review");
        var recreatedMarker = Path.Combine(originalPath, "new.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));
        var jobStore = new Mock<IJobStore>();
        var environment = NewEnvironment(originalPath);

        Directory.CreateDirectory(originalPath);
        File.WriteAllText(Path.Combine(originalPath, "old.txt"), "old credentials");
        ParserConfigs.SetEnvPath(envRoot);
        jobStore.Setup(x => x.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                It.IsAny<Action>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, Action stage, CancellationToken _) =>
            {
                stage();
                Assert.False(Directory.Exists(originalPath));
                Directory.CreateDirectory(originalPath);
                File.WriteAllText(recreatedMarker, "new credentials");
            })
            .ReturnsAsync(true);

        try
        {
            var deleted = await EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                service,
                jobStore.Object,
                environment,
                TestContext.Current.CancellationToken);

            Assert.True(deleted);
            Assert.Equal("new credentials", File.ReadAllText(recreatedMarker));
            Assert.Empty(Directory.EnumerateDirectories(envRoot, ".viberails-delete-*"));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            Directory.Delete(envRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_CancellationAfterStageRestoresRolledBackDirectory()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var envRoot = NewTempRoot();
        var originalPath = Path.Combine(envRoot, "review");
        var originalMarker = Path.Combine(originalPath, "old.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));
        var jobStore = new Mock<IJobStore>();
        var environment = NewEnvironment(originalPath);

        Directory.CreateDirectory(originalPath);
        File.WriteAllText(originalMarker, "old credentials");
        ParserConfigs.SetEnvPath(envRoot);
        jobStore.Setup(x => x.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                It.IsAny<Action>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, Action stage, CancellationToken _) => stage())
            .ThrowsAsync(new OperationCanceledException("Cancellation observed before commit."));

        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                    service,
                    jobStore.Object,
                    environment,
                    new CancellationToken(canceled: true)));

            Assert.Equal("old credentials", File.ReadAllText(originalMarker));
            Assert.Empty(Directory.EnumerateDirectories(envRoot, ".viberails-delete-*"));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            Directory.Delete(envRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_RefusalLeavesDirectoryUnstaged()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var envRoot = NewTempRoot();
        var originalPath = Path.Combine(envRoot, "review");
        var originalMarker = Path.Combine(originalPath, "old.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));
        var jobStore = new Mock<IJobStore>();
        var environment = NewEnvironment(originalPath);

        Directory.CreateDirectory(originalPath);
        File.WriteAllText(originalMarker, "preserve me");
        ParserConfigs.SetEnvPath(envRoot);
        jobStore.Setup(x => x.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                It.IsAny<Action>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        try
        {
            var deleted = await EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                service,
                jobStore.Object,
                environment,
                TestContext.Current.CancellationToken);

            Assert.False(deleted);
            Assert.Equal("preserve me", File.ReadAllText(originalMarker));
            Assert.Empty(Directory.EnumerateDirectories(envRoot, ".viberails-delete-*"));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            Directory.Delete(envRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_RestoreFailurePreservesRecreatedOriginalAndTombstone()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var envRoot = NewTempRoot();
        var originalPath = Path.Combine(envRoot, "review");
        var recreatedMarker = Path.Combine(originalPath, "new.txt");
        var service = CreateService(new FileService(Mock.Of<IConfiguration>()));
        var jobStore = new Mock<IJobStore>();
        var environment = NewEnvironment(originalPath);

        Directory.CreateDirectory(originalPath);
        File.WriteAllText(Path.Combine(originalPath, "old.txt"), "old credentials");
        ParserConfigs.SetEnvPath(envRoot);
        jobStore.Setup(x => x.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                It.IsAny<Action>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, Action stage, CancellationToken _) =>
            {
                stage();
                Directory.CreateDirectory(originalPath);
                File.WriteAllText(recreatedMarker, "new credentials");
            })
            .ThrowsAsync(new IOException("Simulated database failure after staging."));

        try
        {
            await Assert.ThrowsAsync<IOException>(() =>
                EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                    service,
                    jobStore.Object,
                    environment,
                    TestContext.Current.CancellationToken));

            Assert.Equal("new credentials", File.ReadAllText(recreatedMarker));
            Assert.Single(Directory.EnumerateDirectories(envRoot, ".viberails-delete-*"));
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            Directory.Delete(envRoot, recursive: true);
        }
    }

    [Fact]
    public async Task TryDeleteEnvironmentSafelyAsync_FinalizeFailureRetainsTombstoneAndReturnsSuccess()
    {
        var originalEnvRoot = ParserConfigs.GetEnvPath();
        var envRoot = NewTempRoot();
        var originalPath = Path.Combine(envRoot, "review");
        var fileService = new Mock<IFileService>();
        var service = CreateService(fileService.Object);
        var jobStore = new Mock<IJobStore>();
        var environment = NewEnvironment(originalPath);
        string? tombstonePath = null;
        var originalExists = true;
        var tombstoneExists = false;

        ParserConfigs.SetEnvPath(envRoot);
        fileService.Setup(x => x.DirectoryExists(It.IsAny<string>()))
            .Returns((string path) =>
                string.Equals(path, envRoot, StringComparison.OrdinalIgnoreCase)
                || (string.Equals(path, originalPath, StringComparison.OrdinalIgnoreCase) && originalExists)
                || (tombstonePath != null
                    && string.Equals(path, tombstonePath, StringComparison.OrdinalIgnoreCase)
                    && tombstoneExists));
        fileService.Setup(x => x.MoveDirectory(originalPath, It.IsAny<string>()))
            .Callback((string _, string destination) =>
            {
                tombstonePath = destination;
                originalExists = false;
                tombstoneExists = true;
            });
        fileService.Setup(x => x.DeleteDirectory(It.IsAny<string>(), true))
            .Throws(new IOException("Simulated cleanup failure."));
        jobStore.Setup(x => x.TryDeleteEnvironmentIfUnusedAsync(
                environment.Id,
                It.IsAny<Action>(),
                It.IsAny<CancellationToken>()))
            .Callback((int _, Action stage, CancellationToken _) =>
            {
                stage();
                originalExists = true;
            })
            .ReturnsAsync(true);

        try
        {
            var deleted = await EnvironmentRoutes.TryDeleteEnvironmentSafelyAsync(
                service,
                jobStore.Object,
                environment,
                TestContext.Current.CancellationToken);

            Assert.True(deleted);
            Assert.True(originalExists);
            Assert.True(tombstoneExists);
            Assert.NotNull(tombstonePath);
            fileService.Verify(
                x => x.MoveDirectory(It.IsAny<string>(), It.IsAny<string>()),
                Times.Once);
        }
        finally
        {
            ParserConfigs.SetEnvPath(originalEnvRoot);
            Directory.Delete(envRoot, recursive: true);
        }
    }

    private static string NewTempRoot()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"viberails-environment-route-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static LLM_Environment NewEnvironment(string path, int id = 91) => new()
    {
        Id = id,
        CustomName = "review",
        LLM = LLM.Codex,
        Path = path
    };

    private static LlmCliEnvironmentService CreateService(IFileService fileService) => new(
        new ClaudeLlmCliEnvironment(fileService),
        new CodexLlmCliEnvironment(fileService),
        new AntigravityLlmCliEnvironment(fileService),
        new CopilotLlmCliEnvironment(fileService),
        new OpencodeLlmCliEnvironment(fileService),
        fileService);

    private static void CreateEnvironmentTable(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Environments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomName TEXT NOT NULL,
                LLM INTEGER NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                CustomArgs TEXT NOT NULL DEFAULT '',
                CustomPrompt TEXT NOT NULL DEFAULT '',
                CreatedUTC TEXT NOT NULL,
                LastUsedUTC TEXT NOT NULL,
                UNIQUE(CustomName, LLM)
            );
            """;
        command.ExecuteNonQuery();
    }

    private static async Task<int> InsertEnvironmentAsync(
        string connectionString,
        string name,
        string path)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Environments
                (CustomName, LLM, Path, CustomArgs, CustomPrompt, CreatedUTC, LastUsedUTC)
            VALUES
                ($name, $llm, $path, '', '', $now, $now)
            RETURNING Id;
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$llm", (int)LLM.Codex);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<bool> EnvironmentExistsAsync(
        string connectionString,
        int environmentId)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM Environments WHERE Id = $id);";
        command.Parameters.AddWithValue("$id", environmentId);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) != 0;
    }
}

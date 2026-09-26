using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.DB;
using VibeRails.Services.Git;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.VcaRegression;

/// <summary>One run of the real hook host: the exit code Git would see and the console transcript.</summary>
internal sealed record HookRun(int ExitCode, string Transcript)
{
    private static readonly Regex TokenPattern = new(@"\[VCA:[^\]\r\n]+\]", RegexOptions.Compiled);

    /// <summary>Every <c>[VCA:source:slug]</c> acknowledgment token the transcript asked for.</summary>
    public IReadOnlyList<string> AcknowledgmentTokens => TokenPattern
        .Matches(Transcript)
        .Select(match => match.Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// A throwaway Git repository built from one fixture scenario under <c>Fixtures/</c>.
///
/// Scenario layout (every part optional):
/// <list type="bullet">
/// <item><c>base/</c> — copied in and committed as the baseline.</item>
/// <item><c>scenario.json</c> — <c>{"delete": [...], "rename": [{"from","to"}]}</c>, applied with
/// <c>git rm</c> / <c>git mv</c> after the baseline commit.</item>
/// <item><c>staged/</c> — copied in and <c>git add</c>ed on top.</item>
/// <item><c>unstaged/</c> — copied in last and left unstaged: working-tree noise the hook must ignore.</item>
/// </list>
/// Rules files are stored as <c>vc.rules.fixture.md</c> and materialized as <c>vc.rules.md</c>:
/// under their real name they would be live policy for the VibeRails repository itself (its Git
/// Guard reads every <c>vc.rules.md</c> in the index, scoped to its directory, and the Rules page
/// would list them). Any of them may carry the <c>{{LEVEL}}</c> token; it is replaced by the
/// requested enforcement level so WARN, COMMIT and STOP run against one fixture. Every other byte
/// is copied verbatim, which is why the fixture directory is <c>-text</c> in <c>.gitattributes</c>.
///
/// Validation always goes through the real path: <see cref="VcaHookProcessHost.RunCoreAsync"/>
/// (what <c>vb --vca-hook</c> runs, over the staged-index snapshot), with the automation store
/// pointed at a per-repository database so these repositories never touch the live
/// <c>state.db</c>. <see cref="ValidateWithMcpToolAsync"/> runs the MCP <c>validate_vca</c> path,
/// which reads the index through separate git commands, so the two readers can be compared.
/// </summary>
internal sealed class VcaRegressionRepository : IAsyncDisposable
{
    public const string LevelToken = "{{LEVEL}}";
    public const string FixtureRulesFileName = "vc.rules.fixture.md";
    private const string LiveRulesFileName = "vc.rules.md";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string FixturesRoot { get; } = ResolveFixturesRoot();

    public string Scenario { get; }
    public string Enforcement { get; }
    public string RepositoryPath { get; }

    public string ScenarioDirectory => Path.Combine(
        FixturesRoot,
        Scenario.Replace('/', Path.DirectorySeparatorChar));

    private VcaRegressionRepository(string scenario, string enforcement)
    {
        Scenario = scenario;
        Enforcement = enforcement;
        RepositoryPath = Path.Combine(Path.GetTempPath(), $"vca_regression_{Guid.NewGuid():N}");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<VcaRegressionRepository> CreateAsync(string scenario, string enforcement = "STOP")
    {
        var repository = new VcaRegressionRepository(scenario, enforcement);
        try
        {
            await repository.MaterializeAsync();
            return repository;
        }
        catch
        {
            await repository.DisposeAsync();
            throw;
        }
    }

    /// <summary>Runs the real pre-commit hook host against the staged index.</summary>
    public Task<HookRun> RunPreCommitAsync() =>
        RunHookAsync(["--vca-hook", "pre-commit", "--workdir", RepositoryPath]);

    /// <summary>
    /// Runs the pre-commit hook, then the MCP <c>validate_vca</c> reader over the same index, and
    /// asserts the two agree on the outcome that matters: whether the commit is blocked and which
    /// acknowledgments it requires. Both are "the real validation path"; they must not drift.
    /// </summary>
    public async Task<HookRun> RunPreCommitAndCrossCheckAsync()
    {
        var run = await RunPreCommitAsync();
        var report = await ValidateWithMcpToolAsync();

        Assert.False(report.HasError, $"validate_vca reported an error:\n{report.Output}\n\nhook transcript:\n{run.Transcript}");
        Assert.True(
            (run.ExitCode != 0) == report.HasStopViolation,
            $"Hook exit code {run.ExitCode} disagrees with validate_vca HasStopViolation={report.HasStopViolation}.\n"
                + $"hook transcript:\n{run.Transcript}\n\nvalidate_vca:\n{report.Output}");
        Assert.Equal(
            run.AcknowledgmentTokens.OrderBy(token => token, StringComparer.Ordinal),
            report.RequiredAcknowledgments.OrderBy(token => token, StringComparer.Ordinal));
        return run;
    }

    /// <summary>Runs the real commit-msg hook host with <paramref name="message"/> as the commit message.</summary>
    public async Task<HookRun> RunCommitMessageAsync(string message)
    {
        var messagePath = Path.Combine(RepositoryPath, ".git", "COMMIT_EDITMSG");
        await File.WriteAllTextAsync(messagePath, message, Utf8NoBom, Ct);
        return await RunHookAsync(
            ["--vca-hook", "commit-msg", "--workdir", RepositoryPath, "--commit-message", messagePath]);
    }

    /// <summary>The MCP <c>validate_vca</c> tool's reader: git commands over the live index, no snapshot.</summary>
    public Task<VcaToolValidationReport> ValidateWithMcpToolAsync(string? commitMessage = null) =>
        RulesTool.ValidateVcaReportAsync(
            RepositoryPath,
            commitMessage,
            validateCommitMessage: commitMessage is not null,
            Ct);

    public async Task<string> GitAsync(params string[] arguments)
    {
        var result = await GitCli.RunAsync(RepositoryPath, arguments, Ct, timeout: TimeSpan.FromSeconds(30));
        if (result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed ({(result.TimedOut ? "timed out" : result.ExitCode)}) in {RepositoryPath}.\n{result.StdOut}\n{result.StdErr}");
        }

        return result.StdOut;
    }

    public ValueTask DisposeAsync()
    {
        if (!Directory.Exists(RepositoryPath))
        {
            return ValueTask.CompletedTask;
        }

        var fullPath = Path.GetFullPath(RepositoryPath);
        var tempPath = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to clean a repository outside temp: {fullPath}");
        }

        foreach (var file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(fullPath, recursive: true);
        return ValueTask.CompletedTask;
    }

    private async Task MaterializeAsync()
    {
        if (!Directory.Exists(ScenarioDirectory))
        {
            throw new DirectoryNotFoundException($"No fixture scenario '{Scenario}' under {FixturesRoot}.");
        }

        Directory.CreateDirectory(RepositoryPath);
        await GitAsync("init", "-q");
        await GitAsync("config", "user.email", "vca-regression@example.invalid");
        await GitAsync("config", "user.name", "VCA Regression");
        await GitAsync("config", "core.hooksPath", ".git/hooks");
        // Fixture bytes must reach the index untouched; the developer's global autocrlf must not
        // decide what "N lines changed" means or whether a CRLF policy file is read.
        await GitAsync("config", "core.autocrlf", "false");
        await GitAsync("config", "core.safecrlf", "false");
        await GitAsync("config", "diff.renames", "true");
        await GitAsync("config", "commit.gpgsign", "false");
        await GitAsync("config", "core.commentChar", "#");

        var baseDirectory = Path.Combine(ScenarioDirectory, "base");
        if (Directory.Exists(baseDirectory))
        {
            CopyTree(baseDirectory);
            await GitAsync("add", "-A", "--", ".");
            await GitAsync("commit", "-q", "--no-verify", "-m", "baseline");
        }

        var manifestPath = Path.Combine(ScenarioDirectory, "scenario.json");
        if (File.Exists(manifestPath))
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, Ct));
            if (manifest.RootElement.TryGetProperty("delete", out var deletions))
            {
                foreach (var deletion in deletions.EnumerateArray())
                {
                    await GitAsync("rm", "-q", "--", deletion.GetString()!);
                }
            }

            if (manifest.RootElement.TryGetProperty("rename", out var renames))
            {
                foreach (var rename in renames.EnumerateArray())
                {
                    var from = rename.GetProperty("from").GetString()!;
                    var to = rename.GetProperty("to").GetString()!;
                    Directory.CreateDirectory(Path.GetDirectoryName(ToFullPath(to))!);
                    await GitAsync("mv", "--", from, to);
                }
            }
        }

        var stagedDirectory = Path.Combine(ScenarioDirectory, "staged");
        if (Directory.Exists(stagedDirectory))
        {
            var staged = CopyTree(stagedDirectory);
            await GitAsync(["add", "--", .. staged]);
        }

        var unstagedDirectory = Path.Combine(ScenarioDirectory, "unstaged");
        if (Directory.Exists(unstagedDirectory))
        {
            CopyTree(unstagedDirectory);
        }
    }

    private List<string> CopyTree(string sourceRoot)
    {
        var relativePaths = new List<string>();
        var sources = Directory
            .EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var relativePath = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            var bytes = File.ReadAllBytes(source);
            if (Path.GetFileName(source).Equals(FixtureRulesFileName, StringComparison.OrdinalIgnoreCase))
            {
                relativePath = relativePath[..^FixtureRulesFileName.Length] + LiveRulesFileName;
                bytes = SubstituteLevel(bytes, relativePath);
            }

            var target = ToFullPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            relativePaths.Add(relativePath);
        }

        return relativePaths;
    }

    private byte[] SubstituteLevel(byte[] bytes, string relativePath)
    {
        // Encoding.GetString keeps a leading U+FEFF, and the BOM-less encoder writes it back as
        // EF BB BF, so a fixture that deliberately starts with a BOM survives the substitution.
        var text = Encoding.UTF8.GetString(bytes);
        if (!text.Contains(LevelToken, StringComparison.Ordinal))
        {
            return bytes;
        }

        var replaced = text.Replace(LevelToken, Enforcement, StringComparison.Ordinal);
        if (replaced.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Fixture {Scenario}/{relativePath} still contains an unsubstituted template token.");
        }

        return Utf8NoBom.GetBytes(replaced);
    }

    private string ToFullPath(string relativePath) =>
        Path.GetFullPath(Path.Combine(RepositoryPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<HookRun> RunHookAsync(string[] args)
    {
        using var transcript = new StringWriter();
        // Same isolation as VcaHookEndToEndTests: the real JobStore, but under this repository's
        // .git so a run never opens or locks the developer's live state.db.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(RepositoryPath, ".git", "vca-regression-state.db"),
            Pooling = false
        }.ToString();

        var exitCode = await VcaHookProcessHost.RunCoreAsync(
            args,
            services => services.Replace(ServiceDescriptor.Singleton<IJobStore>(_ => new JobStore(connectionString))),
            transcript,
            transcript,
            input: null,
            Ct);
        return new HookRun(exitCode, transcript.ToString());
    }

    private static string ResolveFixturesRoot([CallerFilePath] string callerPath = "") =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures");
}

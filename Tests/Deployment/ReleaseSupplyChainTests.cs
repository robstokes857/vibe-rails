using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Xunit;

namespace Tests.Deployment;

/// <summary>
/// Static guards for the release supply chain (VIBE-195): SHA-pinned workflow actions, the
/// release token's write scope, the installer checks release.yml runs on its signed catalog,
/// the assets deploy.ps1 requires, the installers' release-tag stamp, PATH order on Unix and
/// the BERT download hashes. release.yml runs only on tags, so a normal test run catches
/// these regressions before a release does.
/// </summary>
public sealed partial class ReleaseSupplyChainTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void WorkflowActions_ArePinnedToCommitShasWithAVersionComment()
    {
        var workflows = Directory.GetFiles(Path.Combine(RepositoryRoot, ".github", "workflows"), "*.yml");
        Assert.NotEmpty(workflows);

        var unpinned = new List<string>();
        foreach (var workflow in workflows)
        {
            var lines = File.ReadAllLines(workflow);
            for (var index = 0; index < lines.Length; index++)
            {
                var uses = UsesLine().Match(lines[index]);
                if (!uses.Success || uses.Groups["action"].Value.StartsWith("./", StringComparison.Ordinal))
                    continue;
                if (!PinnedAction().IsMatch(uses.Groups["action"].Value + uses.Groups["comment"].Value))
                    unpinned.Add($"{Path.GetFileName(workflow)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        Assert.True(unpinned.Count == 0,
            "Pin each action to a full commit SHA followed by '# v<version>':\n" + string.Join('\n', unpinned));
    }

    [Fact]
    public void ReleaseWorkflow_GrantsWriteOnlyToTheUploadJob()
    {
        var workflow = ReadWithLf(".github/workflows/release.yml");

        Assert.Contains("\npermissions:\n  contents: read\n", workflow);
        var write = Assert.Single(WritePermission().Matches(workflow));
        var upload = workflow.IndexOf("\n  upload:\n", StringComparison.Ordinal);
        Assert.True(upload > 0, "release.yml has no upload job.");
        Assert.True(write.Index > upload, "The only write grant must belong to the upload job.");
        Assert.Equal("contents: write", write.Value);
    }

    [Fact]
    public void ReleaseWorkflow_RunsInstallerChecksThatExistOnTheCatalogItSigns()
    {
        var workflow = ReadWithLf(".github/workflows/release.yml");
        var installer = ReadWithLf("Scripts/install.ps1");

        // release.yml dot-sources these from install.ps1 by name, and only a tag run would
        // notice a rename.
        var extracted = InstallerFunctionList().Match(workflow);
        Assert.True(extracted.Success, "release.yml no longer runs install.ps1's checks on the packaged archive.");
        var names = QuotedName().Matches(extracted.Groups["names"].Value).Select(match => match.Groups["name"].Value).ToList();
        Assert.Contains("Assert-PayloadMatchesCatalog", names);
        foreach (var name in names)
            Assert.Contains($"\nfunction {name} {{", installer);

        Assert.Contains("-CatalogFilePath \"release-assets/vb-${{ matrix.rid }}.cat\"", workflow);
        Assert.Contains("$CatalogAssetName = \"vb-win-x64.cat\"", installer);
    }

    [Fact]
    public void DeployScript_RequiresTheAssetsReleaseAddedForInstallers()
    {
        var deploy = ReadWithLf("deploy/deploy.ps1");

        // deploy.ps1's asset check is the last gate after a tag run; without these names it
        // reports a release green that Windows installs refuse or that has no stamped installer.
        foreach (var asset in new[] { "vb-win-x64.cat", "install.ps1", "install.sh",
                     "vscode-viberails-win32-x64-$version.vsix.sha256",
                     "vscode-viberails-linux-x64-$version.vsix.sha256",
                     "vscode-viberails-darwin-arm64-$version.vsix.sha256" })
            Assert.Contains($"        \"{asset}\"", deploy);
    }

    [Theory]
    [InlineData("Scripts/install.ps1", "$ReleaseTag = \"\"", "s/^\\$ReleaseTag = \"\"$/")]
    [InlineData("Scripts/install.sh", "RELEASE_TAG=\"\"", "s/^RELEASE_TAG=\"\"$/")]
    public void Installers_CarryTheEmptyReleaseTagThatReleaseStamps(string script, string emptyTag, string stampPattern)
    {
        var lines = ReadWithLf(script).Split('\n');
        Assert.Single(lines, line => line == emptyTag);
        Assert.Contains(stampPattern, ReadWithLf(".github/workflows/release.yml"));
    }

    [Fact]
    public void InstallSh_AppendsTheInstallDirectoryToPath()
    {
        var script = ReadWithLf("Scripts/install.sh");

        Assert.Contains("local export_line='export PATH=\"$PATH:$HOME/.vibe_rails\"'", script);
        Assert.DoesNotContain(".vibe_rails:$PATH", script);
    }

    [Theory]
    [InlineData("Scripts/download-bert-model.ps1", "[string]$Ref = \"", "model.onnx.zip")]
    [InlineData("Scripts/download-bert-model.sh", "MODEL_REF=\"${MODEL_REF:-", "model.onnx.gz")]
    public void BertDownloadScripts_PinACommitAndTheCheckedInHashes(string script, string refPrefix, string modelArchive)
    {
        var text = ReadWithLf(script);

        var refLine = Assert.Single(text.Split('\n'), line => line.TrimStart().StartsWith(refPrefix, StringComparison.Ordinal));
        Assert.Matches(CommitSha(), refLine);

        var modelDirectory = Path.Combine(RepositoryRoot, "VibeRails", "Models", "BertV2");
        // The download is the git blob, which stores vocab.txt with LF even where a Windows
        // checkout has converted it to CRLF.
        var vocab = File.ReadAllBytes(Path.Combine(modelDirectory, "vocab.txt"));
        Assert.Contains(Sha256(WithLf(vocab)), text);
        Assert.Contains(Sha256(File.ReadAllBytes(Path.Combine(modelDirectory, modelArchive))), text);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] WithLf(byte[] bytes)
    {
        var result = new List<byte>(bytes.Length);
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != '\r' || index + 1 == bytes.Length || bytes[index + 1] != '\n')
                result.Add(bytes[index]);
        }
        return [.. result];
    }

    private static string ReadWithLf(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("VibeRails.slnx was not found above the test output directory.");
    }

    [GeneratedRegex(@"^\s*(?:-\s+)?uses:\s*(?<action>\S+)(?<comment>.*)$")]
    private static partial Regex UsesLine();

    [GeneratedRegex(@"^[\w.-]+/[\w./-]+@[0-9a-f]{40}\s+#\s*v\d[\w.-]*\s*$")]
    private static partial Regex PinnedAction();

    [GeneratedRegex(@"[\w-]+:\s*write\b")]
    private static partial Regex WritePermission();

    [GeneratedRegex(@"\b[0-9a-f]{40}\b")]
    private static partial Regex CommitSha();

    [GeneratedRegex(@"foreach \(\$functionName in @\((?<names>[^)]*)\)\)")]
    private static partial Regex InstallerFunctionList();

    [GeneratedRegex(@"'(?<name>[\w-]+)'")]
    private static partial Regex QuotedName();
}

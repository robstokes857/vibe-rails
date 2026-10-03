using System.Diagnostics;
using System.Text;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Services.CodeReports;

public sealed class WorkingTreeChangesTests
{
    // Long enough that git still pairs the rename after a one-line edit (similarity above 50%).
    private const string SameSource = "class Same {}\nline 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\n";
    private const string EditedSource = "class Same { int Edited; }\nline 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\n";

    [Fact]
    public void ParseStatus_ReadsPorcelainZ_IncludingRenamesUntrackedAndConflicts()
    {
        var output = string.Join('\0', [
            " M src/edited.cs", "M  src/staged.cs", "MM src/both.cs", "A  src/new.cs", " D src/gone.cs",
            "R  src/renamed.cs", "src/original.cs", "?? notes.md", "UU src/conflict.cs", "AD src/added-then-deleted.cs", ""]);
        var entries = WorkingTreeChanges.ParseStatus(output);
        Assert.Equal(["src/edited.cs", "src/staged.cs", "src/both.cs", "src/new.cs", "src/gone.cs", "src/renamed.cs",
            "notes.md", "src/conflict.cs", "src/added-then-deleted.cs"], entries.Select(entry => entry.Path));
        Assert.Equal(["modified", "modified", "modified", "added", "deleted", "renamed", "untracked", "conflicted", "deleted"],
            entries.Select(entry => entry.Status));
        Assert.Equal((false, true), (entries[0].Staged, entries[0].Unstaged));
        Assert.Equal((true, false), (entries[1].Staged, entries[1].Unstaged));
        Assert.Equal((true, true), (entries[2].Staged, entries[2].Unstaged));
        Assert.Equal("src/original.cs", entries[5].OriginalPath);
        Assert.Equal((false, true), (entries[6].Staged, entries[6].Unstaged));
    }

    [Fact]
    public void ParseNumstat_ReadsCountsBinaryMarkersAndRenames()
    {
        var output = "12\t3\tsrc/edited.cs\0-\t-\timage.png\0" + "4\t1\t\0src/original.cs\0src/renamed.cs\0";
        var counts = WorkingTreeChanges.ParseNumstat(output);
        Assert.Equal(new WorkingTreeChanges.LineCounts(12, 3, false), counts["src/edited.cs"]);
        Assert.Equal(new WorkingTreeChanges.LineCounts(null, null, true), counts["image.png"]);
        Assert.Equal(new WorkingTreeChanges.LineCounts(4, 1, false), counts["src/renamed.cs"]);
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public async Task ListAndDiff_DescribeEveryKindOfWorkingTreeChange()
    {
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "viberails-changes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Git(root, token, "init", "--quiet");
            await Git(root, token, "config", "user.email", "tests@example.com");
            await Git(root, token, "config", "user.name", "Tests");
            Directory.CreateDirectory(Path.Combine(root, "src"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "edited.cs"), "line one\nline two\nline three\n", token);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "gone.cs"), "class Gone {}\n", token);
            await File.WriteAllTextAsync(Path.Combine(root, "src", "same.cs"), SameSource, token);
            await File.WriteAllBytesAsync(Path.Combine(root, "logo.bin"), [1, 2, 0, 3], token);
            await Git(root, token, "add", "--all");
            await Git(root, token, "commit", "--quiet", "-m", "base");

            await File.WriteAllTextAsync(Path.Combine(root, "src", "edited.cs"), "line one\nline 2\nline three\nline four\n", token);
            File.Delete(Path.Combine(root, "src", "gone.cs"));
            await File.WriteAllTextAsync(Path.Combine(root, "src", "staged.cs"), "class Staged {}\n", token);
            await Git(root, token, "add", "src/staged.cs");
            await File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "# notes\nsecond line", token);
            await File.WriteAllBytesAsync(Path.Combine(root, "logo.bin"), [9, 8, 0, 7, 6], token);
            await File.WriteAllBytesAsync(Path.Combine(root, "new.bin"), [0, 1, 2], token);

            var changes = new WorkingTreeChanges();
            var list = await changes.ListAsync(root, token);
            Assert.False(list.Truncated);
            Assert.NotNull(list.Head);
            var byPath = list.Files.ToDictionary(file => file.Path);
            Assert.Equal(6, list.Count);
            Assert.Equal(["logo.bin", "new.bin", "notes.md", "src/edited.cs", "src/gone.cs", "src/staged.cs"], byPath.Keys.Order(StringComparer.Ordinal));

            var edited = byPath["src/edited.cs"];
            Assert.Equal(("modified", false, true, 2, 1, false), (edited.Status, edited.Staged, edited.Unstaged, edited.Additions, edited.Deletions, edited.Binary));
            var gone = byPath["src/gone.cs"];
            Assert.Equal(("deleted", 0, 1), (gone.Status, gone.Additions, gone.Deletions));
            var staged = byPath["src/staged.cs"];
            Assert.Equal(("added", true, false, 1), (staged.Status, staged.Staged, staged.Unstaged, staged.Additions));
            var notes = byPath["notes.md"];
            Assert.Equal(("untracked", false, true, 2, 0), (notes.Status, notes.Staged, notes.Unstaged, notes.Additions, notes.Deletions));
            Assert.True(byPath["logo.bin"].Binary);
            Assert.Null(byPath["logo.bin"].Additions);
            Assert.True(byPath["new.bin"].Binary);
            Assert.Equal(2 + 1 + 2, list.Additions);
            Assert.Equal(1 + 1, list.Deletions);

            var diff = await changes.ReadDiffAsync(root, "src/edited.cs", token);
            Assert.NotNull(diff);
            Assert.Equal(("src/edited.cs", "csharp", "modified", false, false), (diff.FileName, diff.Language, diff.Status, diff.Binary, diff.Truncated));
            Assert.Equal("line one\nline two\nline three\n", diff.OriginalContent);
            Assert.Equal("line one\nline 2\nline three\nline four\n", diff.ModifiedContent);

            var deleted = await changes.ReadDiffAsync(root, "src/gone.cs", token);
            Assert.Equal(("deleted", "class Gone {}\n", ""), (deleted!.Status, deleted.OriginalContent, deleted.ModifiedContent));
            var added = await changes.ReadDiffAsync(root, "notes.md", token);
            Assert.Equal(("added", "markdown", "", "# notes\nsecond line"), (added!.Status, added.Language, added.OriginalContent, added.ModifiedContent));
            var binary = await changes.ReadDiffAsync(root, "logo.bin", token);
            Assert.True(binary!.Binary);
            Assert.Equal(("", ""), (binary.OriginalContent, binary.ModifiedContent));
            Assert.Null(await changes.ReadDiffAsync(root, "src/never-existed.cs", token));

            // A staged rename keeps its "before" text at the original path, which the list reports and the diff uses.
            await Git(root, token, "mv", "src/same.cs", "src/moved.cs");
            var afterRename = await changes.ListAsync(root, token);
            var moved = Assert.Single(afterRename.Files, file => file.Path == "src/moved.cs");
            Assert.Equal(("renamed", true, false, "src/same.cs"), (moved.Status, moved.Staged, moved.Unstaged, moved.OriginalPath));
            var renamedDiff = await changes.ReadDiffAsync(root, "src/moved.cs", token, "src/same.cs");
            Assert.Equal(("renamed", SameSource, SameSource), (renamedDiff!.Status, renamedDiff.OriginalContent, renamedDiff.ModifiedContent));
            Assert.Equal("added", (await changes.ReadDiffAsync(root, "src/moved.cs", token))!.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() => changes.ReadDiffAsync(root, "src/moved.cs", token, "../outside.cs"));
            // Rename plus edit: still one renamed entry with the edit counted, and the diff shows the edit.
            await File.WriteAllTextAsync(Path.Combine(root, "src", "moved.cs"), EditedSource, token);
            var editedRename = Assert.Single((await changes.ListAsync(root, token)).Files, file => file.Path == "src/moved.cs");
            Assert.Equal(("renamed", true, true, "src/same.cs", 1, 1), (editedRename.Status, editedRename.Staged, editedRename.Unstaged, editedRename.OriginalPath, editedRename.Additions, editedRename.Deletions));
            var editedDiff = await changes.ReadDiffAsync(root, "src/moved.cs", token, "src/same.cs");
            Assert.Equal(("renamed", SameSource, EditedSource), (editedDiff!.Status, editedDiff.OriginalContent, editedDiff.ModifiedContent));

            // Unsafe paths are refused before git or the file system is consulted.
            foreach (var unsafePath in new[] { "../outside.cs", "/etc/passwd", "src/../../x", "C:/windows/win.ini", "" })
                await Assert.ThrowsAsync<InvalidOperationException>(() => changes.ReadDiffAsync(root, unsafePath, token));
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    private static async Task Git(string root, CancellationToken token, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await error}");
    }
}

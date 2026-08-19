using VibeRails.Services.FileSystem;
using Xunit;

namespace Tests.Services.FileSystem;

public sealed class FileSystemBrowserServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        $"viberails-filesystem-browser-{Guid.NewGuid():N}");
    private readonly string _browseRoot;
    private readonly List<string> _directoryLinks = [];
    private readonly FileSystemBrowserService _service = new();

    public FileSystemBrowserServiceTests()
    {
        _browseRoot = Path.Combine(_testRoot, "browse-root");
        Directory.CreateDirectory(_browseRoot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Browse_UsesDefaultDirectory_WhenRequestedPathIsMissing(string? requestedPath)
    {
        var response = _service.Browse(
            requestedPath,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var expected = Normalize(_browseRoot);
        Assert.Equal(expected, response.DefaultPath);
        Assert.Equal(expected, response.CurrentPath);
        Assert.Equal("browse-root", response.CurrentName);
        Assert.False(response.Truncated);
    }

    [Fact]
    public void Browse_ReturnsOneLevelOfMetadata_WithDirectoriesFirstAndDeterministicSorting()
    {
        var alphaDirectory = Path.Combine(_browseRoot, "alpha-dir");
        var zuluDirectory = Path.Combine(_browseRoot, "zulu-dir");
        Directory.CreateDirectory(alphaDirectory);
        Directory.CreateDirectory(zuluDirectory);
        File.WriteAllText(Path.Combine(alphaDirectory, "nested-only.txt"), "nested");
        File.WriteAllText(Path.Combine(_browseRoot, "bravo.txt"), "bravo");
        File.WriteAllText(Path.Combine(_browseRoot, "yankee.cs"), "yankee");

        var response = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            ["alpha-dir", "zulu-dir", "bravo.txt", "yankee.cs"],
            response.Entries.Select(entry => entry.Name));
        Assert.DoesNotContain(response.Entries, entry => entry.Name == "nested-only.txt");

        var directory = response.Entries[0];
        Assert.Equal("directory", directory.Kind);
        Assert.Equal(Normalize(alphaDirectory), directory.Path);
        Assert.Null(directory.Size);
        Assert.Null(directory.Extension);
        Assert.NotNull(directory.LastModifiedUtc);

        var file = response.Entries[2];
        Assert.Equal("file", file.Kind);
        Assert.Equal(Normalize(Path.Combine(_browseRoot, "bravo.txt")), file.Path);
        Assert.Equal(5, file.Size);
        Assert.Equal(".txt", file.Extension);
        Assert.NotNull(file.LastModifiedUtc);
    }

    [Fact]
    public void Browse_HiddenToggleControlsHiddenFilesAndDirectories()
    {
        var hiddenFile = Path.Combine(_browseRoot, ".hidden-file.txt");
        var hiddenDirectory = Path.Combine(_browseRoot, ".hidden-directory");
        File.WriteAllText(hiddenFile, "hidden");
        Directory.CreateDirectory(hiddenDirectory);
        File.WriteAllText(Path.Combine(_browseRoot, "visible.txt"), "visible");
        MarkHiddenOnWindows(hiddenFile);
        MarkHiddenOnWindows(hiddenDirectory);

        var withoutHidden = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);
        var withHidden = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["visible.txt"], withoutHidden.Entries.Select(entry => entry.Name));
        Assert.Contains(withHidden.Entries, entry => entry.Name == ".hidden-file.txt" && entry.IsHidden);
        Assert.Contains(withHidden.Entries, entry => entry.Name == ".hidden-directory" && entry.IsHidden);
    }

    [Fact]
    public void Browse_ReturnsParentAndBreadcrumbs_AndPreservesSpecialAndUnicodeNames()
    {
        const string specialDirectoryName = "space # + % 雪";
        const string childDirectoryName = "naïve-child";
        const string fileName = "résumé # + %.txt";
        var specialDirectory = Path.Combine(_browseRoot, specialDirectoryName);
        var childDirectory = Path.Combine(specialDirectory, childDirectoryName);
        Directory.CreateDirectory(childDirectory);
        File.WriteAllText(Path.Combine(childDirectory, fileName), "unicode");

        var response = _service.Browse(
            childDirectory,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Normalize(specialDirectory), response.ParentPath);
        Assert.Equal(childDirectoryName, response.CurrentName);
        Assert.Equal(
            [specialDirectoryName, childDirectoryName],
            response.Breadcrumbs.TakeLast(2).Select(item => item.Label));
        Assert.Equal(
            [Normalize(specialDirectory), Normalize(childDirectory)],
            response.Breadcrumbs.TakeLast(2).Select(item => item.Path));

        var file = Assert.Single(response.Entries);
        Assert.Equal(fileName, file.Name);
        Assert.Equal(Normalize(Path.Combine(childDirectory, fileName)), file.Path);
    }

    [Fact]
    public void Browse_AcceptsAFullyQualifiedLocalDirectory()
    {
        var response = _service.Browse(
            Path.GetFullPath(_browseRoot),
            Path.GetFullPath(_browseRoot),
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Normalize(_browseRoot), response.CurrentPath);
    }

    [Fact]
    public void Browse_ExplicitDirectoryRecoversFromAStaleDefaultDirectory()
    {
        var response = _service.Browse(
            _browseRoot,
            Path.Combine(_testRoot, "deleted-project-root"),
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Normalize(_browseRoot), response.CurrentPath);
        Assert.Equal(response.CurrentPath, response.DefaultPath);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("./relative")]
    [InlineData("../relative")]
    [InlineData("C:drive-relative")]
    [InlineData("//server/share")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\C:\")]
    public void Browse_RejectsRelativeNetworkAndDevicePaths(string requestedPath)
    {
        var exception = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                requestedPath,
                _browseRoot,
                includeHidden: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FileSystemBrowseError.InvalidPath, exception.Error);
    }

    [Fact]
    public void Browse_ReportsMissingDirectorySeparatelyFromAnInvalidPath()
    {
        var missing = Path.Combine(_testRoot, "does-not-exist");

        var exception = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                missing,
                _browseRoot,
                includeHidden: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FileSystemBrowseError.NotFound, exception.Error);
        Assert.Equal("Directory was not found.", exception.Message);
    }

    [Fact]
    public void Browse_RejectsAFileAsTheCurrentDirectory()
    {
        var filePath = Path.Combine(_browseRoot, "not-a-directory.txt");
        File.WriteAllText(filePath, "file");

        var exception = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                filePath,
                _browseRoot,
                includeHidden: false,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FileSystemBrowseError.InvalidPath, exception.Error);
        Assert.Equal("Path must refer to a directory.", exception.Message);
    }

    [Fact]
    public void Browse_CapsLargeDirectoriesAndReportsTruncation()
    {
        for (var index = 0; index <= FileSystemBrowserService.MaxEntries; index++)
        {
            using var stream = File.Create(Path.Combine(_browseRoot, $"entry-{index:D5}.tmp"));
        }

        var response = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(FileSystemBrowserService.MaxEntries, response.Entries.Count);
        Assert.True(response.Truncated);
        Assert.NotNull(response.NextCursor);
        Assert.Equal(FileSystemBrowserService.MaxEntries + 1, response.TotalCount);

        var finalPage = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cursor: response.NextCursor,
            cancellationToken: TestContext.Current.CancellationToken);

        var finalEntry = Assert.Single(finalPage.Entries);
        Assert.Equal($"entry-{FileSystemBrowserService.MaxEntries:D5}.tmp", finalEntry.Name);
        Assert.False(finalPage.Truncated);
        Assert.Null(finalPage.NextCursor);
    }

    [Fact]
    public void Browse_CursorPagesCoverTheGloballySortedListingExactlyOnce()
    {
        Directory.CreateDirectory(Path.Combine(_browseRoot, "zulu-dir"));
        Directory.CreateDirectory(Path.Combine(_browseRoot, "alpha-dir"));
        File.WriteAllText(Path.Combine(_browseRoot, "zulu.txt"), "z");
        File.WriteAllText(Path.Combine(_browseRoot, "bravo.txt"), "b");
        File.WriteAllText(Path.Combine(_browseRoot, "alpha.txt"), "a");

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var page = _service.Browse(
                _browseRoot,
                _browseRoot,
                includeHidden: false,
                cursor: cursor,
                pageSize: 2,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(5, page.TotalCount);
            Assert.Equal(page.NextCursor is not null, page.Truncated);
            names.AddRange(page.Entries.Select(entry => entry.Name));
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.Equal(
            ["alpha-dir", "zulu-dir", "alpha.txt", "bravo.txt", "zulu.txt"],
            names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Browse_SearchScansTheWholeDirectoryAndTreatsTheQueryLiterally()
    {
        File.WriteAllText(Path.Combine(_browseRoot, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(_browseRoot, "zulu-Needle.txt"), "z");
        File.WriteAllText(Path.Combine(_browseRoot, "[literal].txt"), "literal");

        var firstPage = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            pageSize: 1,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(firstPage.Entries, entry => entry.Name == "zulu-Needle.txt");

        var search = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            search: "  NEEDLE  ",
            pageSize: 1,
            cancellationToken: TestContext.Current.CancellationToken);
        var match = Assert.Single(search.Entries);
        Assert.Equal("zulu-Needle.txt", match.Name);
        Assert.Equal("NEEDLE", search.Search);
        Assert.Equal(1, search.TotalCount);
        Assert.Null(search.NextCursor);

        var literal = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            search: "[",
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("[literal].txt", Assert.Single(literal.Entries).Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(FileSystemBrowserService.MaxEntries + 1)]
    public void Browse_RejectsInvalidPageSizes(int pageSize)
    {
        var exception = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                _browseRoot,
                _browseRoot,
                includeHidden: false,
                pageSize: pageSize,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FileSystemBrowseError.InvalidPath, exception.Error);
        Assert.Contains("Page size", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Browse_RejectsMalformedOrMismatchedCursors()
    {
        File.WriteAllText(Path.Combine(_browseRoot, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(_browseRoot, "bravo.txt"), "b");
        var firstPage = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            search: "a",
            pageSize: 1,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(firstPage.NextCursor);

        var malformed = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                _browseRoot,
                _browseRoot,
                includeHidden: false,
                cursor: "not-a-valid-cursor!",
                cancellationToken: TestContext.Current.CancellationToken));
        var mismatched = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                _browseRoot,
                _browseRoot,
                includeHidden: false,
                search: "b",
                cursor: firstPage.NextCursor,
                pageSize: 1,
                cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(FileSystemBrowseError.InvalidPath, malformed.Error);
        Assert.Equal(FileSystemBrowseError.InvalidPath, mismatched.Error);
        Assert.Contains("cursor", mismatched.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Browse_MarksDirectorySymlinksWithoutRecursingIntoTheirTargets()
    {
        var targetDirectory = Path.Combine(_testRoot, "outside-target");
        var linkPath = Path.Combine(_browseRoot, "linked-directory");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(Path.Combine(targetDirectory, "nested"));
        File.WriteAllText(Path.Combine(targetDirectory, "target-only-secret.txt"), "secret");

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetDirectory);
            _directoryLinks.Add(linkPath);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or PlatformNotSupportedException
                                   or NotSupportedException)
        {
            Assert.Skip("This platform does not permit creating directory symbolic links.");
            return;
        }

        var response = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var link = Assert.Single(response.Entries, entry => entry.Name == "linked-directory");
        Assert.Contains(link.Kind, new[] { "directory", "file" });
        Assert.True(link.IsSymbolicLink);
        Assert.Null(link.LastModifiedUtc);
        Assert.DoesNotContain(response.Entries, entry => entry.Name == "target-only-secret.txt");

        var exception = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                linkPath,
                _browseRoot,
                includeHidden: false,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(FileSystemBrowseError.InvalidPath, exception.Error);
        Assert.Contains("symbolic links or junctions", exception.Message, StringComparison.OrdinalIgnoreCase);

        var ancestorException = Assert.Throws<FileSystemBrowseException>(() =>
            _service.Browse(
                Path.Combine(linkPath, "nested"),
                _browseRoot,
                includeHidden: false,
                cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(FileSystemBrowseError.InvalidPath, ancestorException.Error);
    }

    [Theory]
    [InlineData("C:", null, "Local Disk (C:)")]
    [InlineData("C:", "", "Local Disk (C:)")]
    [InlineData("C:", "   ", "Local Disk (C:)")]
    [InlineData("D:", " Data ", "Data (D:)")]
    public void FormatDriveLabel_UsesTheVolumeLabel_OrLocalDisk(string rootLabel, string? volumeLabel, string expected)
    {
        Assert.Equal(expected, FileSystemBrowserService.FormatDriveLabel(rootLabel, volumeLabel));
    }

    [Fact]
    public void Browse_LabelsRootsLikeExplorer_ButKeepsBreadcrumbRootsShort()
    {
        var response = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        var currentRoot = Normalize(Path.GetPathRoot(_browseRoot)!);
        var root = Assert.Single(response.Roots, item => string.Equals(item.Path, currentRoot, StringComparison.OrdinalIgnoreCase));
        var breadcrumbRoot = response.Breadcrumbs[0];
        Assert.Equal(currentRoot, breadcrumbRoot.Path, ignoreCase: true);

        // Roots list in drive-letter (path) order regardless of what the volumes are called.
        Assert.Equal(
            response.Roots.Select(item => item.Path).OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            response.Roots.Select(item => item.Path));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(root.Path, root.Label);
            Assert.Equal(root.Path, breadcrumbRoot.Label);
            return;
        }

        var shortLabel = currentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Assert.Equal(shortLabel, breadcrumbRoot.Label);
        var expected = FileSystemBrowserService.FormatDriveLabel(shortLabel, new DriveInfo(currentRoot).VolumeLabel);
        Assert.Equal(expected, root.Label);
        Assert.All(response.Roots, item => Assert.Matches(@"^.+ \([A-Za-z]:\)$", item.Label));
    }

    [Fact]
    public void Browse_ReturnsSidebarPlaces_ThatAreExistingLocalDirectoriesWithoutDuplicates()
    {
        var response = _service.Browse(
            _browseRoot,
            _browseRoot,
            includeHidden: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(response.Places);
        Assert.All(response.Places, place =>
        {
            Assert.False(string.IsNullOrWhiteSpace(place.Label));
            Assert.Contains(place.Kind, new[] { "home", "desktop", "documents", "downloads" });
            Assert.True(Path.IsPathFullyQualified(place.Path));
            Assert.True(Directory.Exists(place.Path));
            Assert.Equal(Normalize(place.Path), place.Path);
        });

        var comparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        Assert.Equal(
            response.Places.Count,
            response.Places.Select(place => place.Path).Distinct(comparer).Count());
        Assert.Equal(
            response.Places.Count,
            response.Places.Select(place => place.Kind).Distinct().Count());
    }

    [Fact]
    public void GetPlaces_ListsOnlyExistingFolders_InSidebarOrder_AndDeDuplicatesByPath()
    {
        var homeDirectory = Path.Combine(_testRoot, "home");
        Directory.CreateDirectory(Path.Combine(homeDirectory, "Downloads"));

        var places = FileSystemBrowserService.GetPlaces(
            folder => folder switch
            {
                Environment.SpecialFolder.UserProfile => homeDirectory,
                Environment.SpecialFolder.DesktopDirectory => Path.Combine(_testRoot, "missing-desktop"),
                // Documents resolves to the home folder itself (Linux does this): a duplicate path
                // spelled with a trailing separator must collapse onto Home.
                Environment.SpecialFolder.MyDocuments => homeDirectory + Path.DirectorySeparatorChar,
                _ => null
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["home", "downloads"], places.Select(place => place.Kind));
        Assert.Equal(["Home", "Downloads"], places.Select(place => place.Label));
        Assert.Equal(Normalize(homeDirectory), places[0].Path);
        Assert.Equal(Normalize(Path.Combine(homeDirectory, "Downloads")), places[1].Path);
    }

    [Fact]
    public void GetPlaces_SkipsUnsetRelativeNetworkAndFailingSpecialFolders_WithoutThrowing()
    {
        var documentsDirectory = Path.Combine(_testRoot, "documents");
        Directory.CreateDirectory(documentsDirectory);

        var places = FileSystemBrowserService.GetPlaces(
            folder => folder switch
            {
                Environment.SpecialFolder.UserProfile => throw new UnauthorizedAccessException("no profile"),
                Environment.SpecialFolder.DesktopDirectory => "relative/desktop",
                Environment.SpecialFolder.MyDocuments => documentsDirectory,
                _ => string.Empty
            },
            TestContext.Current.CancellationToken);
        var networkOnly = FileSystemBrowserService.GetPlaces(
            folder => folder == Environment.SpecialFolder.MyDocuments
                ? @"\\server\share\documents"
                : "   ",
            TestContext.Current.CancellationToken);
        var unsupported = FileSystemBrowserService.GetPlaces(
            _ => throw new PlatformNotSupportedException(),
            TestContext.Current.CancellationToken);

        var documents = Assert.Single(places);
        Assert.Equal("documents", documents.Kind);
        Assert.Equal(Normalize(documentsDirectory), documents.Path);
        Assert.Empty(networkOnly);
        Assert.Empty(unsupported);
    }

    [Fact]
    public void GetPlaces_SkipsFoldersReachedThroughSymbolicLinks()
    {
        var targetDirectory = Path.Combine(_testRoot, "linked-desktop-target");
        var linkPath = Path.Combine(_testRoot, "linked-desktop");
        var realDirectory = Path.Combine(_testRoot, "real-home");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(realDirectory);

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetDirectory);
            _directoryLinks.Add(linkPath);
        }
        catch (Exception ex) when (ex is IOException
                                   or UnauthorizedAccessException
                                   or PlatformNotSupportedException
                                   or NotSupportedException)
        {
            Assert.Skip("This platform does not permit creating directory symbolic links.");
            return;
        }

        var places = FileSystemBrowserService.GetPlaces(
            folder => folder switch
            {
                Environment.SpecialFolder.UserProfile => realDirectory,
                Environment.SpecialFolder.DesktopDirectory => linkPath,
                _ => null
            },
            TestContext.Current.CancellationToken);

        var home = Assert.Single(places);
        Assert.Equal("home", home.Kind);
        Assert.Equal(Normalize(realDirectory), home.Path);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static void MarkHiddenOnWindows(string path)
    {
        if (OperatingSystem.IsWindows())
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    public void Dispose()
    {
        foreach (var linkPath in _directoryLinks)
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
        }

        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }
}

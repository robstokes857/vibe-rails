using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PyBridge;

/// <summary>Configuration for <see cref="EmbeddedPython.EnsureAsync"/>.</summary>
public sealed class EmbeddedPythonOptions
{
    /// <summary>
    /// The CPython version to install, e.g. <c>3.13.5</c>. Must be a version python.org
    /// published Windows binaries for.
    /// </summary>
    public string Version { get; set; } = EmbeddedPython.DefaultVersion;

    /// <summary>
    /// Where to install. Defaults to
    /// <c>%LOCALAPPDATA%\PyBridge\runtimes\python-&lt;version&gt;-&lt;arch&gt;</c> — per-user,
    /// no admin rights needed, shared across your apps.
    /// </summary>
    public string? InstallDirectory { get; set; }

    /// <summary>
    /// Also bootstrap <c>pip</c> (downloads <c>get-pip.py</c> from bootstrap.pypa.io and enables
    /// <c>import site</c> in the embeddable distribution). Off by default: the bare runtime is
    /// enough to run dependency-free scripts.
    /// </summary>
    public bool EnsurePip { get; set; }

    /// <summary>
    /// Optional SHA-256 (hex) of the runtime zip, verified after download. Strongly recommended
    /// when you pin <see cref="Version"/> in production; find hashes on the python.org release page.
    /// </summary>
    public string? ExpectedZipSha256 { get; set; }

    /// <summary>Overrides the download URL (e.g. an internal mirror). Defaults to python.org.</summary>
    public Uri? DownloadUrl { get; set; }

    /// <summary>
    /// Overrides where <c>get-pip.py</c> is fetched from when <see cref="EnsurePip"/> is set
    /// (e.g. an internal mirror or a vendored, reviewed copy via a <c>file://</c> URI).
    /// Defaults to <c>https://bootstrap.pypa.io/get-pip.py</c>, which is maintained by PyPA;
    /// pin this in supply-chain-sensitive environments.
    /// </summary>
    public Uri? GetPipUrl { get; set; }
}

/// <summary>
/// Bootstraps a private, self-contained CPython runtime from the official python.org
/// <i>Windows embeddable package</i> (~11 MB) — the answer to "my users don't have Python
/// installed" without shipping an interpreter inside your app package:
///
/// <code>
/// var options = await EmbeddedPython.EnsureAsync();   // downloads once, reuses forever
/// var runner  = new PythonRunner(options);
/// await runner.RunCodeAsync("print('hello from embedded python')");
/// </code>
///
/// The first call downloads and unpacks the runtime; every later call (any process, any app
/// using the same directory) finds it already installed and returns immediately.
/// </summary>
/// <remarks>
/// Windows-only, because python.org only publishes embeddable builds for Windows. On
/// Linux/macOS, use the system interpreter, a venv, or conda —
/// <see cref="PythonRunnerOptions.Discover"/> finds them.
/// </remarks>
public static class EmbeddedPython
{
    /// <summary>The CPython version installed when <see cref="EmbeddedPythonOptions.Version"/> is not set.</summary>
    public const string DefaultVersion = "3.13.5";

    private const string CompletionMarkerFileName = ".pybridge-complete";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>The directory <see cref="EnsureAsync"/> installs to by default for a given version.</summary>
    public static string GetDefaultInstallDirectory(string version)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PyBridge", "runtimes", $"python-{version}-{GetArchitectureSuffix()}");

    /// <summary>
    /// Makes sure the embedded runtime is installed (downloading it on first use) and returns
    /// <see cref="PythonRunnerOptions"/> pointing at it, ready for <c>new PythonRunner(...)</c>.
    /// Safe to call every startup — when already installed it is just a couple of file checks.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    /// <exception cref="PythonExecutionException">Download, verification, or pip bootstrap failed.</exception>
    public static async Task<PythonRunnerOptions> EnsureAsync(
        EmbeddedPythonOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EmbeddedPythonOptions();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "python.org publishes the embeddable runtime for Windows only. On Linux/macOS use " +
                "the system Python, a venv, or conda — PythonRunnerOptions.Discover() finds them.");
        }

        var installDir = options.InstallDirectory ?? GetDefaultInstallDirectory(options.Version);
        var pythonExe = Path.Combine(installDir, "python.exe");
        var marker = Path.Combine(installDir, CompletionMarkerFileName);

        if (!(File.Exists(marker) && File.Exists(pythonExe)))
        {
            // Self-heal: a marker without python.exe is one of OUR installs that got mangled
            // (partially deleted, disk trouble) — safe to wipe and redo. A marker-less
            // directory is NOT ours; DownloadAndInstallAsync refuses to touch it if non-empty.
            if (File.Exists(marker))
                Directory.Delete(installDir, recursive: true);

            await DownloadAndInstallAsync(options, installDir, marker, cancellationToken).ConfigureAwait(false);
        }

        var runnerOptions = BuildRunnerOptions(installDir, pythonExe);

        if (options.EnsurePip)
            await EnsurePipAsync(options, runnerOptions, installDir, cancellationToken).ConfigureAwait(false);

        return runnerOptions;
    }

    private static async Task DownloadAndInstallAsync(
        EmbeddedPythonOptions options,
        string installDir,
        string markerPath,
        CancellationToken cancellationToken)
    {
        var url = options.DownloadUrl ?? new Uri(
            $"https://www.python.org/ftp/python/{options.Version}/" +
            $"python-{options.Version}-embed-{GetArchitectureSuffix()}.zip");

        var parentDir = Path.GetDirectoryName(Path.GetFullPath(installDir))
            ?? throw new PythonExecutionException($"Invalid install directory: {installDir}");
        Directory.CreateDirectory(parentDir);

        var stagingDir = Path.Combine(parentDir, $".staging-{Guid.NewGuid():N}");
        var zipPath = stagingDir + ".zip";

        try
        {
            await DownloadFileAsync(url, zipPath, cancellationToken).ConfigureAwait(false);

            if (options.ExpectedZipSha256 is { Length: > 0 } expected)
            {
                var actual = await ComputeSha256Async(zipPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    throw new PythonExecutionException(
                        $"SHA-256 mismatch for {url}: expected {expected}, got {actual}. " +
                        "The download may be corrupt or tampered with.");
                }
            }

            try
            {
                ZipFile.ExtractToDirectory(zipPath, stagingDir);
            }
            catch (InvalidDataException ex)
            {
                throw new PythonExecutionException(
                    $"The runtime archive downloaded from {url} is corrupt or not a zip file. " +
                    "Retry, or check the URL/mirror.", ex);
            }

            File.WriteAllText(Path.Combine(stagingDir, CompletionMarkerFileName), $"installed from {url}");

            // Publish by moving the staging directory into place (an atomic rename on the same
            // volume, so a visible install directory always already contains its marker).
            if (Directory.Exists(installDir) && !File.Exists(markerPath))
            {
                // The directory exists but was NOT created by us (our installs always carry the
                // marker). Never delete someone else's data — refuse unless it is empty.
                if (Directory.EnumerateFileSystemEntries(installDir).Any())
                {
                    throw new PythonExecutionException(
                        $"Install directory '{installDir}' already exists, is not empty, and does not " +
                        "contain a PyBridge-managed runtime. Refusing to overwrite it — delete it " +
                        "yourself or pick a different EmbeddedPythonOptions.InstallDirectory.");
                }

                Directory.Delete(installDir);
            }

            if (!Directory.Exists(installDir))
            {
                try
                {
                    Directory.Move(stagingDir, installDir);
                }
                catch (IOException) when (File.Exists(markerPath))
                {
                    // Another process completed the install between our checks — use theirs.
                }
            }

            if (!File.Exists(Path.Combine(installDir, "python.exe")))
            {
                throw new PythonExecutionException(
                    $"The embedded runtime at {installDir} is incomplete (python.exe missing). " +
                    "Delete the directory and retry.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(zipPath))
                    File.Delete(zipPath);
                if (Directory.Exists(stagingDir))
                    Directory.Delete(stagingDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; leftovers in the staging area are harmless.
            }
        }
    }

    private static async Task EnsurePipAsync(
        EmbeddedPythonOptions options,
        PythonRunnerOptions runnerOptions,
        string installDir,
        CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(installDir, "Scripts", "pip.exe")))
            return;

        // The embeddable distribution ships with `import site` disabled via python3XX._pth;
        // pip (and anything it installs) needs it enabled.
        foreach (var pthFile in Directory.EnumerateFiles(installDir, "python3*._pth"))
        {
            var content = await File.ReadAllTextAsync(pthFile, cancellationToken).ConfigureAwait(false);
            if (content.Contains("#import site"))
                await File.WriteAllTextAsync(pthFile, content.Replace("#import site", "import site"), cancellationToken)
                    .ConfigureAwait(false);
        }

        var getPipPath = Path.Combine(installDir, "get-pip.py");
        var getPipUrl = options.GetPipUrl ?? new Uri("https://bootstrap.pypa.io/get-pip.py");
        await DownloadFileAsync(getPipUrl, getPipPath, cancellationToken).ConfigureAwait(false);

        var result = await new PythonRunner(runnerOptions)
            .RunAsync([getPipPath, "--no-warn-script-location"], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        result.EnsureSuccess();
    }

    private static PythonRunnerOptions BuildRunnerOptions(string installDir, string pythonExe)
    {
        var options = new PythonRunnerOptions { PythonExecutable = pythonExe };
        options.AdditionalPathEntries.Add(installDir);
        options.AdditionalPathEntries.Add(Path.Combine(installDir, "Scripts"));
        return options;
    }

    private static async Task DownloadFileAsync(Uri url, string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var target = File.Create(destinationPath);
            await response.Content.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new PythonExecutionException(
                $"Failed to download {url}: {ex.Message}. Check connectivity, or point " +
                $"{nameof(EmbeddedPythonOptions)}.{nameof(EmbeddedPythonOptions.DownloadUrl)} at a mirror.",
                ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient signals ITS timeout as a cancellation; the caller didn't cancel.
            throw new PythonExecutionException(
                $"Downloading {url} timed out after {Http.Timeout}. Check connectivity or use a mirror.", ex);
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string GetArchitectureSuffix() => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "win32",
        var other => throw new PlatformNotSupportedException(
            $"No python.org embeddable build exists for architecture '{other}'."),
    };
}

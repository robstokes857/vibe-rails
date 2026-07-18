namespace PyBridge;

/// <summary>Where a discovered interpreter came from — also its priority order.</summary>
public enum PythonSourceKind
{
    /// <summary>The <c>PYBRIDGE_PYTHON</c> environment variable — an explicit user override.</summary>
    EnvironmentOverride,

    /// <summary>The virtual environment that is active in this process (<c>VIRTUAL_ENV</c>).</summary>
    ActiveVirtualEnv,

    /// <summary>A <c>.venv</c>/<c>venv</c> directory found near the working directory.</summary>
    LocalVirtualEnv,

    /// <summary>A conda environment (base or named).</summary>
    Conda,

    /// <summary>A plain interpreter found on PATH.</summary>
    PathCommand,

    /// <summary>The Windows <c>py</c> launcher.</summary>
    PyLauncher,
}

/// <summary>
/// One interpreter that <see cref="PythonLocator"/> found on this machine, and how to turn it
/// into ready-to-use <see cref="PythonRunnerOptions"/> (venv/conda activation included).
/// </summary>
/// <param name="Kind">Where it came from (doubles as priority).</param>
/// <param name="Executable">Full path (or command name) of the interpreter.</param>
/// <param name="Description">Human-readable summary for logs and pickers.</param>
/// <param name="RootDirectory">The venv directory or conda prefix, when applicable.</param>
public sealed record PythonCandidate(
    PythonSourceKind Kind,
    string Executable,
    string Description,
    string? RootDirectory = null)
{
    /// <summary>
    /// Builds runner options for this candidate, including the PATH/environment setup the
    /// interpreter needs (venv activation, conda activation) so native DLLs and scripts resolve.
    /// </summary>
    public PythonRunnerOptions ToOptions() => Kind switch
    {
        PythonSourceKind.ActiveVirtualEnv or PythonSourceKind.LocalVirtualEnv when RootDirectory is not null
            => PythonRunnerOptions.ForVenv(RootDirectory),
        PythonSourceKind.Conda when RootDirectory is not null
            => PythonRunnerOptions.ForCondaPrefix(RootDirectory),
        _ => PythonRunnerOptions.ForPython(Executable),
    };
}

/// <summary>
/// Finds Python interpreters on the machine so consumers don't have to think about it.
/// Priority order: explicit <c>PYBRIDGE_PYTHON</c> override → active venv → a local
/// <c>.venv</c>/<c>venv</c> (walking up from the working directory) → conda (base, then named
/// envs) → PATH → the Windows <c>py</c> launcher. Fake Microsoft Store aliases
/// (<c>WindowsApps</c> stubs that open the Store instead of running Python) are skipped.
/// </summary>
public static class PythonLocator
{
    /// <summary>
    /// Returns every interpreter found, best first. Purely filesystem/environment based —
    /// nothing is executed. Use <c>new PythonRunner(candidate.ToOptions()).ProbeAsync()</c>
    /// to validate one.
    /// </summary>
    /// <param name="startDirectory">Where the <c>.venv</c>/<c>venv</c> walk starts. Defaults to the current directory.</param>
    public static IReadOnlyList<PythonCandidate> Discover(string? startDirectory = null)
    {
        var candidates = new List<PythonCandidate>();
        var seenExecutables = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        void Add(PythonCandidate candidate)
        {
            var key = NormalizeKey(candidate.Executable);
            if (seenExecutables.Add(key))
                candidates.Add(candidate);
        }

        // 1. Explicit override — accepts an exe path, an install/venv directory, or a bare
        //    command name to resolve from PATH.
        var overrideValue = Environment.GetEnvironmentVariable("PYBRIDGE_PYTHON");
        if (!string.IsNullOrWhiteSpace(overrideValue) && TryResolveOverride(overrideValue, out var overrideExe))
        {
            Add(new PythonCandidate(
                PythonSourceKind.EnvironmentOverride, overrideExe,
                "set via the PYBRIDGE_PYTHON environment variable"));
        }

        // 2. The venv active in this process, if any.
        var activeVenv = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        if (!string.IsNullOrWhiteSpace(activeVenv) && TryGetVenvExecutable(activeVenv, out var activeExe))
        {
            Add(new PythonCandidate(
                PythonSourceKind.ActiveVirtualEnv, activeExe,
                $"active virtual environment ({activeVenv})", Path.GetFullPath(activeVenv)));
        }

        // 3. A .venv/venv directory near the working directory (walking up, like git does).
        for (var dir = new DirectoryInfo(Path.GetFullPath(startDirectory ?? Environment.CurrentDirectory));
             dir is not null;
             dir = dir.Parent)
        {
            var found = false;
            foreach (var name in (ReadOnlySpan<string>)[".venv", "venv"])
            {
                var venvDir = Path.Combine(dir.FullName, name);
                if (TryGetVenvExecutable(venvDir, out var venvExe))
                {
                    Add(new PythonCandidate(
                        PythonSourceKind.LocalVirtualEnv, venvExe,
                        $"local virtual environment ({venvDir})", venvDir));
                    found = true;
                    break;
                }
            }

            if (found)
                break;
        }

        // 4. Conda: base first, then every named environment.
        var condaRoot = CondaLocator.FindBaseRoot();
        if (condaRoot is not null)
        {
            var basePython = CondaLocator.GetPythonExecutable(condaRoot);
            if (File.Exists(basePython))
                Add(new PythonCandidate(PythonSourceKind.Conda, basePython, "conda base environment", condaRoot));

            var envsDir = Path.Combine(condaRoot, "envs");
            if (Directory.Exists(envsDir))
            {
                string[] envDirs;
                try
                {
                    envDirs = Directory.GetDirectories(envsDir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    envDirs = []; // discovery never throws for an unreadable envs directory
                }

                foreach (var envDir in envDirs)
                {
                    var envPython = CondaLocator.GetPythonExecutable(envDir);
                    if (File.Exists(envPython))
                    {
                        Add(new PythonCandidate(
                            PythonSourceKind.Conda, envPython,
                            $"conda environment '{Path.GetFileName(envDir)}'", envDir));
                    }
                }
            }
        }

        // 5. Interpreters on PATH (skipping the fake Microsoft Store aliases).
        foreach (var exe in FindOnPath(OperatingSystem.IsWindows()
                     ? ["python.exe", "python3.exe"]
                     : ["python3", "python"]))
        {
            Add(new PythonCandidate(PythonSourceKind.PathCommand, exe, "found on PATH"));
        }

        // 6. The Windows py launcher (all-users install lives in C:\Windows).
        if (OperatingSystem.IsWindows())
        {
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemLauncher = Path.Combine(windowsDir, "py.exe");
            if (File.Exists(systemLauncher))
                Add(new PythonCandidate(PythonSourceKind.PyLauncher, systemLauncher, "Windows py launcher"));
            else
                foreach (var exe in FindOnPath(["py.exe"]))
                    Add(new PythonCandidate(PythonSourceKind.PyLauncher, exe, "Windows py launcher (on PATH)"));
        }

        return candidates;
    }

    /// <summary>The single best interpreter on this machine, or null if none was found.</summary>
    /// <param name="startDirectory">Where the <c>.venv</c>/<c>venv</c> walk starts. Defaults to the current directory.</param>
    public static PythonCandidate? FindBest(string? startDirectory = null)
    {
        var discovered = Discover(startDirectory);
        return discovered.Count > 0 ? discovered[0] : null;
    }

    private static bool TryGetVenvExecutable(string venvDirectory, out string executable)
    {
        executable = OperatingSystem.IsWindows()
            ? Path.Combine(venvDirectory, "Scripts", "python.exe")
            : Path.Combine(venvDirectory, "bin", "python");

        // pyvenv.cfg is what makes a directory a venv — requiring it avoids false positives.
        return File.Exists(Path.Combine(venvDirectory, "pyvenv.cfg")) && File.Exists(executable);
    }

    private static IEnumerable<string> FindOnPath(string[] fileNames)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue))
            yield break;

        foreach (var rawDir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IsWindowsStoreAliasDirectory(rawDir))
                continue;

            foreach (var fileName in fileNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(rawDir, fileName);
                    if (!File.Exists(candidate))
                        continue;
                }
                catch (ArgumentException)
                {
                    break; // malformed PATH entry — skip the directory
                }

                yield return Path.GetFullPath(candidate);
            }
        }
    }

    /// <summary>
    /// The WindowsApps directory holds zero-byte "app execution alias" stubs for python.exe
    /// that open the Microsoft Store instead of running Python — never what a library wants.
    /// Matched on whole path segments so e.g. "...\MicrosoftX\WindowsAppsBackup" doesn't hit.
    /// </summary>
    private static bool IsWindowsStoreAliasDirectory(string directory)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var segments = directory.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < segments.Length; i++)
        {
            if (segments[i].Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                segments[i - 1].Equals("Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Resolves a PYBRIDGE_PYTHON value: exe path, directory, or bare command name.</summary>
    private static bool TryResolveOverride(string value, out string executable)
    {
        executable = string.Empty;

        if (File.Exists(value))
        {
            executable = Path.GetFullPath(value);
            return true;
        }

        if (Directory.Exists(value))
        {
            // Accept an interpreter directory (embedded/conda layout) or a venv root.
            foreach (var candidate in (ReadOnlySpan<string>)
                     [
                         OperatingSystem.IsWindows() ? "python.exe" : Path.Combine("bin", "python"),
                         OperatingSystem.IsWindows() ? Path.Combine("Scripts", "python.exe") : Path.Combine("bin", "python3"),
                     ])
            {
                var path = Path.Combine(value, candidate);
                if (File.Exists(path))
                {
                    executable = Path.GetFullPath(path);
                    return true;
                }
            }

            return false;
        }

        if (!value.Contains(Path.DirectorySeparatorChar) && !value.Contains(Path.AltDirectorySeparatorChar))
        {
            // Bare command name — resolve it from PATH ourselves.
            var fileName = OperatingSystem.IsWindows() && !value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? value + ".exe"
                : value;

            foreach (var found in FindOnPath([fileName]))
            {
                executable = found;
                return true;
            }
        }

        return false;
    }

    private static string NormalizeKey(string executable)
    {
        try
        {
            return Path.GetFullPath(executable);
        }
        catch (Exception)
        {
            return executable;
        }
    }
}

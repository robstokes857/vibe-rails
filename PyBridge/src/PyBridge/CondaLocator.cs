namespace PyBridge;

/// <summary>
/// Best-effort helpers for locating a conda installation and its environments, and for
/// reproducing the PATH changes that <c>conda activate</c> makes. This is the fiddly bit
/// that makes GPU / CUDA / native-DLL Python (PyTorch, etc.) actually work when launched
/// from another process on Windows: the environment's <c>Library\bin</c> and friends must
/// be on PATH or the native DLLs won't load.
/// </summary>
public static class CondaLocator
{
    /// <summary>
    /// Attempts to find the root of the base conda installation (e.g. <c>C:\Users\me\miniconda3</c>).
    /// Checks common environment variables first, then well-known install locations.
    /// </summary>
    /// <returns>The base directory, or <c>null</c> if it could not be found.</returns>
    public static string? FindBaseRoot()
    {
        // conda sets these when a shell is initialized / an env is active.
        foreach (var name in new[] { "CONDA_ROOT", "CONDA_PREFIX_1", "CONDA_EXE" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrEmpty(value))
                continue;

            // CONDA_EXE points at <root>\Scripts\conda.exe (or <root>/bin/conda).
            if (name == "CONDA_EXE")
            {
                var scriptsDir = Path.GetDirectoryName(value);
                var root = scriptsDir is null ? null : Path.GetDirectoryName(scriptsDir);
                if (root is not null && Directory.Exists(root))
                    return root;
                continue;
            }

            if (Directory.Exists(value))
                return value;
        }

        // If an env is currently active, CONDA_PREFIX points at that env. For the base env
        // that IS the root; for a named env it's <root>\envs\<name>.
        var prefix = Environment.GetEnvironmentVariable("CONDA_PREFIX");
        if (!string.IsNullOrEmpty(prefix) && Directory.Exists(prefix))
        {
            var parent = Path.GetDirectoryName(prefix);
            if (parent is not null && string.Equals(Path.GetFileName(parent), "envs", StringComparison.OrdinalIgnoreCase))
            {
                var root = Path.GetDirectoryName(parent);
                if (root is not null && Directory.Exists(root))
                    return root;
            }
            return prefix;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var candidate in new[] { "miniconda3", "anaconda3", "miniforge3", "mambaforge" })
        {
            var path = Path.Combine(home, candidate);
            if (Directory.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// Resolves the on-disk prefix directory for a named conda environment.
    /// The special name <c>base</c> (or an empty name) resolves to the base root itself.
    /// </summary>
    public static string GetEnvironmentPrefix(string environmentName, string? baseRoot = null)
    {
        baseRoot ??= FindBaseRoot()
            ?? throw new PythonExecutionException(
                "Could not locate a conda installation. Pass the conda root explicitly, " +
                "or set the CONDA_ROOT / CONDA_EXE environment variable.");

        if (string.IsNullOrEmpty(environmentName) ||
            string.Equals(environmentName, "base", StringComparison.OrdinalIgnoreCase))
        {
            return baseRoot;
        }

        return Path.Combine(baseRoot, "envs", environmentName);
    }

    /// <summary>
    /// Returns the directories that <c>conda activate</c> prepends to PATH for the given
    /// environment prefix, most-important first. Non-existent directories are skipped.
    /// </summary>
    public static IReadOnlyList<string> GetActivationPathEntries(string environmentPrefix)
    {
        // The canonical set conda prepends on Windows. On non-Windows it's just <prefix>/bin.
        string[] relative = OperatingSystem.IsWindows()
            ? new[]
            {
                "",
                Path.Combine("Library", "mingw-w64", "bin"),
                Path.Combine("Library", "usr", "bin"),
                Path.Combine("Library", "bin"),
                "Scripts",
                "bin",
            }
            : new[] { "bin" };

        var result = new List<string>(relative.Length);
        foreach (var rel in relative)
        {
            var full = rel.Length == 0 ? environmentPrefix : Path.Combine(environmentPrefix, rel);
            if (Directory.Exists(full))
                result.Add(full);
        }

        return result;
    }

    /// <summary>
    /// Returns the path to the Python executable inside the given environment prefix
    /// (<c>python.exe</c> on Windows, <c>bin/python</c> elsewhere).
    /// </summary>
    public static string GetPythonExecutable(string environmentPrefix)
        => OperatingSystem.IsWindows()
            ? Path.Combine(environmentPrefix, "python.exe")
            : Path.Combine(environmentPrefix, "bin", "python");
}

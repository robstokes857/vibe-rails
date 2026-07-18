namespace PyBridge;

/// <summary>
/// Configuration for a <see cref="PythonRunner"/>. Every field has a sensible default,
/// so <c>new PythonRunnerOptions()</c> is enough to run whatever <c>python</c> resolves to
/// on the current PATH. Use the <see cref="ForPython"/> / <see cref="ForConda"/> factories
/// for the common cases.
/// </summary>
public sealed class PythonRunnerOptions
{
    /// <summary>
    /// The Python executable to launch. Can be a bare command that lives on PATH
    /// (<c>python</c>, <c>python3</c>) or a full path to a specific interpreter.
    /// </summary>
    public string PythonExecutable { get; set; } = "python";

    /// <summary>Working directory for the process. When null, the current directory is used.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Extra environment variables layered on top of the ones this process inherits.
    /// Set a value to <c>null</c> to remove an inherited variable. Names are compared
    /// case-insensitively on Windows and case-sensitively elsewhere, matching each OS.
    /// </summary>
    public IDictionary<string, string?> EnvironmentVariables { get; } =
        new Dictionary<string, string?>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    /// <summary>
    /// Directories prepended to PATH for the child process, most-important first.
    /// Populated automatically by <see cref="ForConda"/> to reproduce conda activation.
    /// </summary>
    public IList<string> AdditionalPathEntries { get; } = new List<string>();

    /// <summary>Kill the process if it runs longer than this. Null means no timeout.</summary>
    public TimeSpan? Timeout { get; set; }

    /// <summary>
    /// When true, a non-zero exit code (or a timeout) throws a <see cref="PythonExecutionException"/>
    /// instead of returning a <see cref="PythonResult"/> you have to inspect. Default: false.
    /// </summary>
    public bool ThrowOnNonZeroExitCode { get; set; }

    /// <summary>
    /// When true (default), <c>PYTHONUNBUFFERED=1</c> is set so stdout/stderr arrive line-by-line
    /// in real time. Turn this off only if a script depends on buffered output.
    /// </summary>
    public bool UseUnbufferedOutput { get; set; } = true;

    /// <summary>
    /// When true (default), <c>PYTHONIOENCODING=utf-8</c> and <c>PYTHONUTF8=1</c> are set so the
    /// pipes speak UTF-8 end-to-end. Without this, Python on Windows encodes piped output with
    /// the legacy locale code page (cp1252 et al.) and non-ASCII text arrives mangled.
    /// </summary>
    public bool UseUtf8Io { get; set; } = true;

    /// <summary>Runs whatever <c>python</c> resolves to on PATH.</summary>
    public static PythonRunnerOptions Default() => new();

    /// <summary>
    /// The "don't make me think" factory: discovers the best interpreter on this machine via
    /// <see cref="PythonLocator"/> (override → active venv → local <c>.venv</c> → conda → PATH)
    /// and falls back to <c>python</c> on PATH if nothing is found.
    /// </summary>
    /// <param name="startDirectory">Where the local-venv search starts. Defaults to the current directory.</param>
    public static PythonRunnerOptions Discover(string? startDirectory = null)
        => PythonLocator.FindBest(startDirectory)?.ToOptions() ?? new PythonRunnerOptions();

    /// <summary>Runs a specific interpreter, given by command name or full path.</summary>
    public static PythonRunnerOptions ForPython(string pythonExecutable)
        => new() { PythonExecutable = pythonExecutable };

    /// <summary>
    /// Runs Python from a virtual environment directory (one containing <c>pyvenv.cfg</c>),
    /// reproducing activation: the venv's <c>Scripts</c>/<c>bin</c> directory is prepended to
    /// PATH and <c>VIRTUAL_ENV</c> is set, so console scripts and native wheels resolve.
    /// </summary>
    /// <param name="venvDirectory">The venv root (e.g. <c>./.venv</c>).</param>
    public static PythonRunnerOptions ForVenv(string venvDirectory)
    {
        var fullPath = Path.GetFullPath(venvDirectory);
        var binDir = OperatingSystem.IsWindows()
            ? Path.Combine(fullPath, "Scripts")
            : Path.Combine(fullPath, "bin");

        var options = new PythonRunnerOptions
        {
            PythonExecutable = OperatingSystem.IsWindows()
                ? Path.Combine(binDir, "python.exe")
                : Path.Combine(binDir, "python"),
        };

        options.AdditionalPathEntries.Add(binDir);
        options.EnvironmentVariables["VIRTUAL_ENV"] = fullPath;
        return options;
    }

    /// <summary>
    /// Runs Python from a named conda environment, reproducing what <c>conda activate</c>
    /// does to PATH so that native/GPU packages (PyTorch + CUDA, etc.) load correctly.
    /// The interpreter is invoked directly (no dependency on <c>conda run</c>), which keeps
    /// stdout/stderr capture clean.
    /// </summary>
    /// <param name="environmentName">The env name (e.g. <c>base</c>, <c>vision</c>).</param>
    /// <param name="condaRoot">Optional conda install root; auto-detected when null.</param>
    public static PythonRunnerOptions ForConda(string environmentName, string? condaRoot = null)
        => ForCondaPrefix(CondaLocator.GetEnvironmentPrefix(environmentName, condaRoot), environmentName);

    /// <summary>
    /// Runs Python from a conda environment given its on-disk prefix directory, reproducing
    /// the PATH changes <c>conda activate</c> makes. Prefer <see cref="ForConda"/> when you
    /// know the environment by name.
    /// </summary>
    /// <param name="environmentPrefix">The env directory (e.g. <c>...\miniconda3\envs\vision</c>).</param>
    /// <param name="environmentName">Optional name for <c>CONDA_DEFAULT_ENV</c>; inferred from the path when null.</param>
    public static PythonRunnerOptions ForCondaPrefix(string environmentPrefix, string? environmentName = null)
    {
        var options = new PythonRunnerOptions
        {
            PythonExecutable = CondaLocator.GetPythonExecutable(environmentPrefix),
        };

        foreach (var entry in CondaLocator.GetActivationPathEntries(environmentPrefix))
            options.AdditionalPathEntries.Add(entry);

        options.EnvironmentVariables["CONDA_PREFIX"] = environmentPrefix;
        options.EnvironmentVariables["CONDA_DEFAULT_ENV"] = environmentName ?? InferCondaEnvName(environmentPrefix);

        return options;
    }

    /// <summary>
    /// Named environments live at <c>&lt;root&gt;/envs/&lt;name&gt;</c>; any other prefix is the
    /// base installation itself, which conda calls <c>base</c> (not the install folder's name).
    /// </summary>
    private static string InferCondaEnvName(string environmentPrefix)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(environmentPrefix);
        var parent = Path.GetDirectoryName(trimmed);
        return parent is not null &&
               string.Equals(Path.GetFileName(parent), "envs", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(trimmed)
            : "base";
    }
}

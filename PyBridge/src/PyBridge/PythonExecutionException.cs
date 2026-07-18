namespace PyBridge;

/// <summary>
/// Thrown when a Python process fails to start, or when it exits with a non-zero
/// exit code and the caller asked for failures to be surfaced as exceptions
/// (see <see cref="PythonRunnerOptions.ThrowOnNonZeroExitCode"/> or
/// <see cref="PythonResult.EnsureSuccess"/>).
/// </summary>
public sealed class PythonExecutionException : Exception
{
    /// <summary>The process exit code, or <c>-1</c> if the process never started / timed out.</summary>
    public int ExitCode { get; }

    /// <summary>Whatever the process wrote to standard error (may be empty).</summary>
    public string StandardError { get; }

    /// <summary>Whatever the process wrote to standard output (may be empty).</summary>
    public string StandardOutput { get; }

    /// <summary>Creates an exception for a failure to start / a general error, with no captured output.</summary>
    public PythonExecutionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = -1;
        StandardError = string.Empty;
        StandardOutput = string.Empty;
    }

    /// <summary>Creates an exception for a completed-but-failed run, carrying the captured exit code and output.</summary>
    public PythonExecutionException(string message, int exitCode, string standardOutput, string standardError)
        : base(message)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }
}

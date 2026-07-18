using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PyBridge;

/// <summary>
/// The captured result of running a Python process: everything the caller needs to
/// know about what happened, and nothing they don't. This is the "output passed back
/// to the caller" that the whole wrapper exists to deliver.
/// </summary>
public sealed class PythonResult
{
    /// <summary>The process exit code. <c>0</c> conventionally means success. <c>-1</c> if it timed out.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Everything the script wrote to <c>stdout</c>, verbatim.</summary>
    public required string StandardOutput { get; init; }

    /// <summary>Everything the script wrote to <c>stderr</c>, verbatim.</summary>
    public required string StandardError { get; init; }

    /// <summary>How long the process ran.</summary>
    public required TimeSpan RunTime { get; init; }

    /// <summary>The executable that was launched (e.g. <c>python</c> or a full path to a conda env's python.exe).</summary>
    public required string Executable { get; init; }

    /// <summary>The full command line that was run, handy for logging and debugging.</summary>
    public required string CommandLine { get; init; }

    /// <summary>True if the process was killed because it exceeded the configured timeout.</summary>
    public bool TimedOut { get; init; }

    /// <summary>True if the process was killed on request (<see cref="PythonRun.KillAsync"/> or disposal).</summary>
    public bool WasKilled { get; init; }

    /// <summary>True when the process ran to completion with a zero exit code.</summary>
    public bool IsSuccess => !TimedOut && !WasKilled && ExitCode == 0;

    /// <summary><see cref="StandardOutput"/> with leading/trailing whitespace removed. Convenient for single-value scripts.</summary>
    public string StandardOutputTrimmed => StandardOutput.Trim();

    /// <summary>
    /// Throws a <see cref="PythonExecutionException"/> if the run was not successful,
    /// otherwise returns this same result so calls can be chained.
    /// </summary>
    public PythonResult EnsureSuccess()
    {
        if (IsSuccess)
            return this;

        var reason = TimedOut
            ? $"Python process timed out after {RunTime}."
            : WasKilled
                ? $"Python process was killed after {RunTime}."
                : $"Python process exited with code {ExitCode}.";

        throw new PythonExecutionException(
            $"{reason}\nCommand: {CommandLine}\n--- stderr ---\n{StandardError}",
            ExitCode,
            StandardOutput,
            StandardError);
    }

    /// <summary>
    /// Deserializes <see cref="StandardOutput"/> as JSON into <typeparamref name="T"/>.
    /// AOT/trim safe: you supply a source-generated <see cref="JsonTypeInfo{T}"/>, so no
    /// runtime reflection is used. Ideal for scripts that print a JSON document.
    /// </summary>
    public T? ReadJsonOutput<T>(JsonTypeInfo<T> typeInfo)
        => JsonSerializer.Deserialize(StandardOutput, typeInfo);

    /// <summary>A short, log-friendly summary of the result (no full output dumped).</summary>
    public override string ToString()
        => $"PythonResult(ExitCode={ExitCode}, TimedOut={TimedOut}, WasKilled={WasKilled}, RunTime={RunTime}, " +
           $"StdOut={StandardOutput.Length} chars, StdErr={StandardError.Length} chars)";
}

namespace PyBridge;

/// <summary>
/// The abstraction consumers depend on (and mock in tests) for running Python from .NET.
/// It deliberately has only four core operations — everything else (<c>RunFileAsync</c>,
/// <c>RunModuleAsync</c>, <c>RunCodeAsync</c>, <c>StreamFileAsync</c>, sessions, probing, …)
/// is provided for every implementation as extension members in
/// <see cref="PythonRunnerExtensions"/>.
/// </summary>
/// <remarks>
/// Register it once and forget about it:
/// <code>
/// services.AddSingleton&lt;IPythonRunner&gt;(new PythonRunner(PythonRunnerOptions.Discover()));
/// </code>
/// The four shapes, from simplest to most control:
/// <list type="bullet">
///   <item><see cref="RunAsync"/> — run to completion, get everything back in one <see cref="PythonResult"/>.</item>
///   <item><see cref="StreamAsync"/> — <c>await foreach</c> live output lines; throws if the run fails.</item>
///   <item><see cref="Start"/> — a <see cref="PythonRun"/> handle: live lines <i>and</i> the final result <i>and</i> kill.</item>
///   <item><see cref="StartInteractive"/> — same handle, but stdin stays open so you can stream input in.</item>
/// </list>
/// </remarks>
public interface IPythonRunner
{
    /// <summary>The interpreter/environment configuration this runner launches processes with.</summary>
    PythonRunnerOptions Options { get; }

    /// <summary>
    /// Runs <c>python &lt;arguments...&gt;</c> to completion and returns the fully captured result
    /// (stdout, stderr, exit code, timing). Optional callbacks additionally receive each output
    /// line live as it is produced.
    /// </summary>
    /// <param name="arguments">The exact argument list passed to the interpreter. Escaping is handled for you.</param>
    /// <param name="standardInput">Text piped to stdin, or null for an immediately-closed (empty) stdin.</param>
    /// <param name="onStandardOutputLine">Optional live callback per stdout line.</param>
    /// <param name="onStandardErrorLine">Optional live callback per stderr line.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process.</param>
    Task<PythonResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        Action<string>? onStandardOutputLine = null,
        Action<string>? onStandardErrorLine = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <c>python &lt;arguments...&gt;</c> and yields stdout/stderr lines live, in arrival
    /// order, as an <see cref="IAsyncEnumerable{T}"/>. The sequence ends when the process exits.
    /// </summary>
    /// <remarks>
    /// Because there is no result object to inspect, failure is surfaced by throwing: a non-zero
    /// exit code or a timeout raises <see cref="PythonExecutionException"/> after the last line.
    /// Need the exit code without an exception? Use <see cref="Start"/> and await
    /// <see cref="PythonRun.Completion"/>. Abandoning enumeration early kills the process.
    /// </remarks>
    /// <param name="arguments">The exact argument list passed to the interpreter.</param>
    /// <param name="standardInput">Text piped to stdin, or null for an immediately-closed (empty) stdin.</param>
    /// <param name="cancellationToken">Cancels the stream and kills the process.</param>
    IAsyncEnumerable<PythonOutputLine> StreamAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches <c>python &lt;arguments...&gt;</c> and immediately returns a <see cref="PythonRun"/>
    /// handle: enumerate <see cref="PythonRun.Lines"/> live, await <see cref="PythonRun.Completion"/>
    /// for the full result, or <see cref="PythonRun.KillAsync"/> it. Stdin is fixed up-front
    /// (the given text, or empty) — for live input use <see cref="StartInteractive"/>.
    /// </summary>
    /// <param name="arguments">The exact argument list passed to the interpreter.</param>
    /// <param name="standardInput">Text piped to stdin, or null for an immediately-closed (empty) stdin.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process.</param>
    PythonRun Start(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Launches <c>python &lt;arguments...&gt;</c> with stdin held open: stream input lines in with
    /// <see cref="PythonRun.WriteInputLineAsync"/> / <see cref="PythonRun.PipeInputAsync"/> while
    /// reading output lines out — a two-way live conversation with the process. Call
    /// <see cref="PythonRun.CompleteInput"/> to send EOF.
    /// </summary>
    /// <param name="arguments">The exact argument list passed to the interpreter.</param>
    /// <param name="cancellationToken">Cancels the run and kills the process.</param>
    PythonRun StartInteractive(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default);
}

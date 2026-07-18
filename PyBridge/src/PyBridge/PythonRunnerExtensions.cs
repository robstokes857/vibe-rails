namespace PyBridge;

/// <summary>
/// The convenience surface for every <see cref="IPythonRunner"/> — C# 14 extension members,
/// so the interface itself stays tiny (four methods) while consumers get the full menu:
/// <list type="bullet">
///   <item><c>RunFileAsync</c> / <c>RunModuleAsync</c> / <c>RunCodeAsync</c> — buffered results.</item>
///   <item><c>StreamFileAsync</c> / <c>StreamModuleAsync</c> / <c>StreamCodeAsync</c> — <c>await foreach</c> live lines.</item>
///   <item><c>StartFile</c> / <c>StartInteractiveFile</c> — live <see cref="PythonRun"/> handles.</item>
///   <item><c>StartSession</c> — a warm, long-lived worker (<see cref="PythonSession"/>).</item>
///   <item><c>ProbeAsync</c> — ask the interpreter what it actually is.</item>
/// </list>
/// The <c>params ReadOnlySpan&lt;string&gt;</c> overloads make the common call allocation-light
/// and ceremony-free: <c>runner.RunFileAsync("train.py", "--epochs", "10")</c>.
/// </summary>
public static class PythonRunnerExtensions
{
    /// <summary>
    /// Python source that prints a one-line JSON description of the interpreter; kept in sync
    /// with <see cref="PythonInfo"/>.
    /// </summary>
    private const string ProbeCode =
        "import sys, json, platform; " +
        "print(json.dumps({'version': platform.python_version(), 'executable': sys.executable, " +
        "'prefix': sys.prefix, 'implementation': platform.python_implementation()}))";

    extension(IPythonRunner runner)
    {
        // -------------------------------------------------------------------------------
        //  Run — buffered results
        // -------------------------------------------------------------------------------

        /// <summary>Runs a <c>.py</c> file: <c>python &lt;scriptPath&gt; [arguments...]</c>.</summary>
        public Task<PythonResult> RunFileAsync(string scriptPath, params ReadOnlySpan<string> arguments)
            => runner.RunAsync(Compose([scriptPath], arguments));

        /// <summary>
        /// Runs a <c>.py</c> file with full control over stdin, live output callbacks and cancellation.
        /// </summary>
        /// <param name="scriptPath">Path to the script (relative to <see cref="PythonRunnerOptions.WorkingDirectory"/> or absolute).</param>
        /// <param name="arguments">Arguments passed to the script. Escaping is handled for you — pass raw values.</param>
        /// <param name="standardInput">Text piped to the script's stdin, or null for none.</param>
        /// <param name="onStandardOutputLine">Optional callback invoked for each line of stdout as it arrives.</param>
        /// <param name="onStandardErrorLine">Optional callback invoked for each line of stderr as it arrives.</param>
        /// <param name="cancellationToken">Cancels the run and kills the process.</param>
        public Task<PythonResult> RunFileAsync(
            string scriptPath,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            Action<string>? onStandardOutputLine = null,
            Action<string>? onStandardErrorLine = null,
            CancellationToken cancellationToken = default)
            => runner.RunAsync(
                Compose([scriptPath], arguments), standardInput,
                onStandardOutputLine, onStandardErrorLine, cancellationToken);

        /// <summary>Runs a module: <c>python -m &lt;moduleName&gt; [arguments...]</c>.</summary>
        public Task<PythonResult> RunModuleAsync(string moduleName, params ReadOnlySpan<string> arguments)
            => runner.RunAsync(Compose(["-m", moduleName], arguments));

        /// <summary>Runs a module (<c>python -m ...</c>) with full control over stdin, callbacks and cancellation.</summary>
        /// <param name="moduleName">The module to run (e.g. <c>pip</c>, <c>http.server</c>).</param>
        /// <param name="arguments">Arguments passed to the module.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="onStandardOutputLine">Optional callback per stdout line, live.</param>
        /// <param name="onStandardErrorLine">Optional callback per stderr line, live.</param>
        /// <param name="cancellationToken">Cancels the run and kills the process.</param>
        public Task<PythonResult> RunModuleAsync(
            string moduleName,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            Action<string>? onStandardOutputLine = null,
            Action<string>? onStandardErrorLine = null,
            CancellationToken cancellationToken = default)
            => runner.RunAsync(
                Compose(["-m", moduleName], arguments), standardInput,
                onStandardOutputLine, onStandardErrorLine, cancellationToken);

        /// <summary>Runs an inline snippet: <c>python -c "&lt;code&gt;"</c>. Great for quick checks.</summary>
        public Task<PythonResult> RunCodeAsync(string code, params ReadOnlySpan<string> arguments)
            => runner.RunAsync(Compose(["-c", code], arguments));

        /// <summary>Runs an inline snippet (<c>python -c ...</c>) with full control over stdin, callbacks and cancellation.</summary>
        /// <param name="code">The Python source to execute.</param>
        /// <param name="arguments">Arguments visible to the snippet via <c>sys.argv[1:]</c>.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="onStandardOutputLine">Optional callback per stdout line, live.</param>
        /// <param name="onStandardErrorLine">Optional callback per stderr line, live.</param>
        /// <param name="cancellationToken">Cancels the run and kills the process.</param>
        public Task<PythonResult> RunCodeAsync(
            string code,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            Action<string>? onStandardOutputLine = null,
            Action<string>? onStandardErrorLine = null,
            CancellationToken cancellationToken = default)
            => runner.RunAsync(
                Compose(["-c", code], arguments), standardInput,
                onStandardOutputLine, onStandardErrorLine, cancellationToken);

        // -------------------------------------------------------------------------------
        //  Stream — await foreach over live output
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Runs a <c>.py</c> file and yields its stdout/stderr lines live:
        /// <code>await foreach (var line in runner.StreamFileAsync("train.py", "--epochs", "10")) ...</code>
        /// Throws <see cref="PythonExecutionException"/> after the last line if the run failed.
        /// </summary>
        public IAsyncEnumerable<PythonOutputLine> StreamFileAsync(string scriptPath, params ReadOnlySpan<string> arguments)
            => runner.StreamAsync(Compose([scriptPath], arguments));

        /// <summary>Runs a <c>.py</c> file and yields its output lines live, with stdin text and cancellation.</summary>
        /// <param name="scriptPath">Path to the script.</param>
        /// <param name="arguments">Arguments passed to the script.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="cancellationToken">Cancels the stream and kills the process.</param>
        public IAsyncEnumerable<PythonOutputLine> StreamFileAsync(
            string scriptPath,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            CancellationToken cancellationToken = default)
            => runner.StreamAsync(Compose([scriptPath], arguments), standardInput, cancellationToken);

        /// <summary>Runs a module (<c>python -m ...</c>) and yields its output lines live.</summary>
        public IAsyncEnumerable<PythonOutputLine> StreamModuleAsync(string moduleName, params ReadOnlySpan<string> arguments)
            => runner.StreamAsync(Compose(["-m", moduleName], arguments));

        /// <summary>Runs a module (<c>python -m ...</c>) and yields its output lines live, with stdin text and cancellation.</summary>
        /// <param name="moduleName">The module to run.</param>
        /// <param name="arguments">Arguments passed to the module.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="cancellationToken">Cancels the stream and kills the process.</param>
        public IAsyncEnumerable<PythonOutputLine> StreamModuleAsync(
            string moduleName,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            CancellationToken cancellationToken = default)
            => runner.StreamAsync(Compose(["-m", moduleName], arguments), standardInput, cancellationToken);

        /// <summary>Runs an inline snippet (<c>python -c ...</c>) and yields its output lines live.</summary>
        public IAsyncEnumerable<PythonOutputLine> StreamCodeAsync(string code, params ReadOnlySpan<string> arguments)
            => runner.StreamAsync(Compose(["-c", code], arguments));

        /// <summary>Runs an inline snippet (<c>python -c ...</c>) and yields its output lines live, with stdin text and cancellation.</summary>
        /// <param name="code">The Python source to execute.</param>
        /// <param name="arguments">Arguments visible via <c>sys.argv[1:]</c>.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="cancellationToken">Cancels the stream and kills the process.</param>
        public IAsyncEnumerable<PythonOutputLine> StreamCodeAsync(
            string code,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            CancellationToken cancellationToken = default)
            => runner.StreamAsync(Compose(["-c", code], arguments), standardInput, cancellationToken);

        // -------------------------------------------------------------------------------
        //  Start — live handles
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Launches a <c>.py</c> file and returns the live <see cref="PythonRun"/> handle
        /// (lines + completion + kill). Stdin is the given text, or empty.
        /// </summary>
        /// <param name="scriptPath">Path to the script.</param>
        /// <param name="arguments">Arguments passed to the script.</param>
        /// <param name="standardInput">Text piped to stdin, or null for none.</param>
        /// <param name="cancellationToken">Cancels the run and kills the process.</param>
        public PythonRun StartFile(
            string scriptPath,
            IEnumerable<string>? arguments = null,
            string? standardInput = null,
            CancellationToken cancellationToken = default)
            => runner.Start(Compose([scriptPath], arguments), standardInput, cancellationToken);

        /// <summary>
        /// Launches a <c>.py</c> file with stdin held open for live input — write lines with
        /// <see cref="PythonRun.WriteInputLineAsync"/>, send EOF with <see cref="PythonRun.CompleteInput"/>.
        /// </summary>
        /// <param name="scriptPath">Path to the script.</param>
        /// <param name="arguments">Arguments passed to the script.</param>
        /// <param name="cancellationToken">Cancels the run and kills the process.</param>
        public PythonRun StartInteractiveFile(
            string scriptPath,
            IEnumerable<string>? arguments = null,
            CancellationToken cancellationToken = default)
            => runner.StartInteractive(Compose([scriptPath], arguments), cancellationToken);

        // -------------------------------------------------------------------------------
        //  Sessions — warm, long-lived workers
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Starts a <c>.py</c> worker as a long-lived <see cref="PythonSession"/>: one line of
        /// stdin per request, one line of stdout per reply. Pay the interpreter/import cost
        /// once, then make many cheap calls — ideal for keeping an ML model loaded.
        /// See <c>python/worker.py</c> in the repo for a reference worker.
        /// </summary>
        /// <param name="scriptPath">Path to the worker script.</param>
        /// <param name="arguments">Arguments passed to the worker.</param>
        /// <param name="cancellationToken">Cancels the underlying process.</param>
        public PythonSession StartSession(
            string scriptPath,
            IEnumerable<string>? arguments = null,
            CancellationToken cancellationToken = default)
            => new(runner.StartInteractive(Compose([scriptPath], arguments), cancellationToken));

        /// <summary>Starts a module (<c>python -m ...</c>) as a long-lived <see cref="PythonSession"/>.</summary>
        /// <param name="moduleName">The worker module.</param>
        /// <param name="arguments">Arguments passed to the worker.</param>
        /// <param name="cancellationToken">Cancels the underlying process.</param>
        public PythonSession StartSessionForModule(
            string moduleName,
            IEnumerable<string>? arguments = null,
            CancellationToken cancellationToken = default)
            => new(runner.StartInteractive(Compose(["-m", moduleName], arguments), cancellationToken));

        // -------------------------------------------------------------------------------
        //  Probing
        // -------------------------------------------------------------------------------

        /// <summary>
        /// Asks the configured interpreter what it actually is (version, executable path,
        /// prefix, implementation). Cheap and dependency-free — the standard way to health-check
        /// an environment before relying on it.
        /// </summary>
        public async Task<PythonInfo> ProbeAsync(CancellationToken cancellationToken = default)
        {
            var result = await runner.RunAsync(["-c", ProbeCode], cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            result.EnsureSuccess();

            return result.ReadJsonOutput(PyBridgeJsonContext.Default.PythonInfo)
                ?? throw new PythonExecutionException(
                    $"The interpreter probe printed unexpected output: {result.StandardOutputTrimmed}");
        }
    }

    /// <summary>Concatenates a fixed prefix (e.g. <c>["-m", module]</c>) with span arguments.</summary>
    private static string[] Compose(ReadOnlySpan<string> prefix, ReadOnlySpan<string> arguments)
    {
        var args = new string[prefix.Length + arguments.Length];
        prefix.CopyTo(args);
        arguments.CopyTo(args.AsSpan(prefix.Length));
        return args;
    }

    /// <summary>Concatenates a fixed prefix with an optional argument sequence.</summary>
    private static string[] Compose(ReadOnlySpan<string> prefix, IEnumerable<string>? arguments)
    {
        if (arguments is null)
            return prefix.ToArray();

        var list = new List<string>(prefix.Length + 8);
        foreach (var item in prefix)
            list.Add(item);
        list.AddRange(arguments);
        return [.. list];
    }
}

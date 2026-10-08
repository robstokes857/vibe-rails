using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PyBridge;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Utils;

namespace VibeRails.Services.PythonScripts;

public interface IPythonScriptService
{
    Task<PythonScriptListResponse> GetStatusAsync(CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> UpdateSettingsAsync(PythonScriptSettingsRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> SetPinAsync(
        SetPythonScriptPinRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> SetRunPinRequirementAsync(
        PythonScriptRunPinRequirementRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> ApproveAsync(
        PythonScriptApprovalRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> RevokeAsync(
        PythonScriptApprovalRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptRunResponse> RunAsync(string? name, CancellationToken cancellationToken = default);
    Task<PythonScriptRunResponse> RunAsync(
        string? name,
        IReadOnlyList<string>? arguments,
        CancellationToken cancellationToken = default);
    Task<PythonScriptRunResponse> RunAsync(
        string? name,
        IReadOnlyList<string>? arguments,
        string? standardInput,
        string? pin,
        CancellationToken cancellationToken = default);
    Task<string> ValidateRunnableAsync(string? name, CancellationToken cancellationToken = default);
    Task ValidateRunAuthorizationAsync(string? name, string? pin, CancellationToken cancellationToken = default);
    PythonScriptRunHistoryResponse GetRunHistory();
    string GetScriptsDirectory();

    // Authoring. These never take a PIN — see the class remarks.
    Task<PythonScriptContentResponse> GetContentAsync(
        string? name, CancellationToken cancellationToken = default);
    Task<PythonScriptSaveResponse> SaveContentAsync(
        PythonScriptSaveRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> CreateAsync(
        PythonScriptSaveRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> ImportAsync(
        PythonScriptImportRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> RenameAsync(
        PythonScriptRenameRequest request, CancellationToken cancellationToken = default);
    Task<PythonScriptListResponse> DeleteAsync(
        string? name, CancellationToken cancellationToken = default);
}

public sealed class PythonScriptValidationException(string message) : Exception(message);

/// <summary>
/// Explicitly registered user scripts (default <c>~/.vibe_rails/scripts/UserScripts</c>) — Python (<c>.py</c>), PowerShell
/// (<c>.ps1</c>, run by pwsh) or Bash (<c>.sh</c>), chosen by the file extension — gated by
/// hash pinning:
/// the user approves ("signs") a script by entering their PIN, which records the
/// script's canonical SHA-256; a script only runs while its current content still
/// hashes to an approved value, and the run executes the exact verified bytes (via a
/// temp copy) so the file cannot be swapped between check and launch.
///
/// The PIN is a PBKDF2-SHA256 verifier stored next to the approvals in
/// <c>~/.vibe_rails/script_signing.json</c>. It is required for every approval — there
/// is no unlocked session to ride — and this class never logs it.
///
/// Threat model — read this before treating the PIN as a trust boundary. It raises the
/// bar for anything limited to the HTTP API or the CLI to bless a script, but it is NOT
/// a hard boundary against a managed agent running as the user. Such an agent is handed
/// the same tool-API tokens as the human dashboard (see <c>LocalToolApiContext</c>) and
/// routes to the same backend, so at the HTTP layer it is indistinguishable from the
/// user and can call approve/run; it can also rewrite this very document on disk. A real
/// boundary would need out-of-process privilege separation or a genuine user-presence
/// signal (an OS secure prompt or an external protected key) — a PIN checked in-process
/// cannot provide that while the agent shares the user's identity. Treat signing as
/// defense-in-depth against accidental or unattended execution, not as containment of a
/// hostile same-user agent.
///
/// Hashes are computed over LF-normalized, BOM-stripped, strictly-decoded UTF-8 bytes
/// with the registration ID and absolute path mixed in, so CRLF churn (git autocrlf, editors) cannot
/// invalidate an approval, while renaming a file or changing its canonical content does.
///
/// Authoring (create / save / register / rename / remove) deliberately takes no PIN and can
/// never create an approval. Create/import/rename always land unsigned; a changed save
/// becomes modified, while a byte-for-byte or canonical line-ending-only save may retain
/// the approval for that same version. New registrations use fresh IDs; remove and rename
/// revoke existing approvals. Removal retains the file on disk, and restoring a removed
/// registration cannot silently recover trust. Approve/revoke keep requiring the PIN; only Approve can
/// make a previously unapproved or modified script runnable.
/// </summary>
public sealed partial class PythonScriptService : IPythonScriptService
{
    public static readonly string ScriptsSubdirectory = Path.Combine("scripts", "UserScripts");
    public const string SigningFileName = "script_signing.json";

    public const string StatusApproved = "approved";
    public const string StatusModified = "modified";
    public const string StatusUnapproved = "unapproved";

    internal const int DocumentVersion = 1;
    internal const int PinIterations = 210_000;
    private const int PinMinLength = 4;
    private const int PinMaxLength = 128;
    private const int PinFailureDelayMs = 300;
    private const int MaxScriptBytes = 5 * 1024 * 1024;
    private const int MaxCapturedOutputChars = 200_000;
    // Argv and stdin arrive from the local, authenticated UI and never touch a shell, but
    // they are still request data: bound them so a malformed caller cannot push megabytes
    // through the process start path.
    private const int MaxRunArguments = 64;
    private const int MaxRunArgumentChars = 8_000;
    private const int MaxStandardInputChars = 256_000;
    private const int RunHistoryCap = 50;
    private const int WriteLockTimeoutMs = 10_000;
    private const int ReadRetryAttempts = 4;
    private const int ReadRetryDelayMs = 40;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The extension picks the interpreter (see RuntimeFor), so it is part of the contract:
    // lower-case only, matching what the dashboard writes and what the old .py rule accepted.
    private static readonly Regex ScriptNamePattern = new(
        @"^[A-Za-z0-9][A-Za-z0-9._ -]{0,120}\.(?:py|ps1|sh)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string ScriptNameRule =
        "Script names must be a plain .py, .ps1 or .sh file name (letters, digits, dots, dashes, spaces).";

    private readonly string _installDirectory;
    private readonly Lazy<IPythonRunner?> _pythonRunner;
    private readonly Func<PythonRunnerOptions, IPythonRunner> _runnerFactory;
    private readonly IJobExecutableResolver _executableResolver;
    private readonly SemaphoreSlim _documentLock = new(1, 1);
    private readonly object _historyLock = new();
    private readonly List<PythonScriptRunRecord> _runHistory = [];

    public PythonScriptService(
        IPythonRunner? pythonRunner = null,
        string? installDirectory = null,
        Func<PythonRunnerOptions, IPythonRunner>? runnerFactory = null,
        Func<IPythonRunner?>? pythonRunnerProvider = null,
        IJobExecutableResolver? executableResolver = null,
        Func<string?>? projectRootProvider = null,
        bool allProjects = false)
    {
        _pythonRunner = new Lazy<IPythonRunner?>(
            () => pythonRunner ?? pythonRunnerProvider?.Invoke(),
            LazyThreadSafetyMode.ExecutionAndPublication);
        _installDirectory = installDirectory ?? PathConstants.GetInstallDirPath();
        _runnerFactory = runnerFactory ?? (options => new PythonRunner(options));
        // pwsh and Bash resolve exactly like repository Automation scripts (Git Bash on Windows,
        // never the System32 WSL bridge). Resolution is per run, so listing never probes PATH.
        _executableResolver = executableResolver ?? new JobExecutableResolver();
        _projectRoot = projectRootProvider ?? (() => ParserConfigs.GetRootPath());
        _allProjects = allProjects;
    }

    /// <summary>The interpreter a script runs under, from its (validated) file extension.</summary>
    public static JobScriptRuntime RuntimeFor(string scriptName) =>
        Path.GetExtension(scriptName).ToLowerInvariant() switch
        {
            ".ps1" => JobScriptRuntime.PowerShell,
            ".sh" => JobScriptRuntime.Bash,
            _ => JobScriptRuntime.Python
        };

    /// <summary>The word the UI and terminal titles use for a script's runtime.</summary>
    public static string RuntimeDisplayName(string scriptName) =>
        RuntimeFor(scriptName) switch
        {
            JobScriptRuntime.PowerShell => "PowerShell",
            JobScriptRuntime.Bash => "Bash",
            _ => "Python"
        };

    public string GetScriptsDirectory() => Path.Combine(_installDirectory, ScriptsSubdirectory);

    private string SigningFilePath => Path.Combine(_installDirectory, SigningFileName);

    public async Task<PythonScriptListResponse> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await _documentLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadDocument();
            return BuildStatus(document);
        }
        finally
        {
            _documentLock.Release();
        }
    }

    public async Task<PythonScriptListResponse> SetRunPinRequirementAsync(
        PythonScriptRunPinRequirementRequest request, CancellationToken cancellationToken = default)
    {
        using var writeLock = await AcquireCrossProcessWriteLockAsync(cancellationToken);
        await _documentLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadDocument();
            if (document.Pin == null || !VerifyPin(document, request.Pin))
                throw new PythonScriptValidationException("Incorrect PIN.");
            var names = (document.RequirePinEachRunNames ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (string.IsNullOrWhiteSpace(request.Name))
                document = document with { RequirePinEachRun = request.Enabled };
            else
            {
                var entry = Registration(request.Name);
                var name = entry.Id;
                var library = ReadLibrary();
                library.Scripts[library.Scripts.FindIndex(item => item.Id == name)] = entry with { RequirePinEachRun = request.Enabled };
                WriteLibrary(library);
                names.RemoveAll(n => NameEquals(n, name));
                document = document with { RequirePinEachRunNames = names };
            }
            WriteDocument(document);
            return BuildStatus(document);
        }
        finally { _documentLock.Release(); }
    }

    public async Task<PythonScriptListResponse> SetPinAsync(
        SetPythonScriptPinRequest request,
        CancellationToken cancellationToken = default)
    {
        var newPin = request.NewPin ?? string.Empty;
        if (newPin.Length is < PinMinLength or > PinMaxLength)
        {
            throw new PythonScriptValidationException(
                $"The PIN must be between {PinMinLength} and {PinMaxLength} characters.");
        }

        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                var document = ReadDocument();
                if (document.Pin == null || VerifyPin(document, request.CurrentPin))
                {
                    var salt = RandomNumberGenerator.GetBytes(16);
                    var hash = DerivePinHash(newPin, salt, PinIterations);
                    var updated = document with
                    {
                        Pin = new PythonScriptPinRecord(
                            Convert.ToBase64String(salt),
                            PinIterations,
                            Convert.ToBase64String(hash))
                    };
                    WriteDocument(updated);
                    return BuildStatus(updated);
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        // Wrong current PIN: throttle outside every lock so a typo never serializes
        // concurrent readers behind a 300 ms sleep.
        await Task.Delay(TimeSpan.FromMilliseconds(PinFailureDelayMs), cancellationToken);
        throw new PythonScriptValidationException("Incorrect PIN.");
    }

    public async Task<PythonScriptListResponse> ApproveAsync(
        PythonScriptApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = RegisteredName(request.Name);
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                var document = ReadDocument();
                if (document.Pin == null)
                {
                    throw new PythonScriptValidationException(
                        "No signing PIN is configured yet. Set a PIN first, then approve the script.");
                }

                if (VerifyPin(document, request.Pin))
                {
                    var scriptPath = ResolveScriptPath(name);
                    if (!File.Exists(scriptPath))
                    {
                        throw new PythonScriptValidationException($"Script '{name}' was not found.");
                    }

                    // Key approval to the registration ID; the hash also binds its absolute path.
                    // Equal filenames in different repositories never share approval.
                    var canonicalName = name;
                    var content = ReadScriptBytes(scriptPath);
                    var hash = RegisteredHash(canonicalName, content);
                    var approvals = document.Approvals
                        .Where(approval => !NameEquals(approval.Name, canonicalName))
                        .Append(new PythonScriptApprovalRecord(
                            canonicalName,
                            hash,
                            DateTime.UtcNow.ToString("O")))
                        .ToList();
                    var updated = document with { Approvals = approvals };
                    WriteDocument(updated);
                    Log.Information("[PythonScripts] Approved script {Name} ({Hash})", canonicalName, hash);
                    return BuildStatus(updated);
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(PinFailureDelayMs), cancellationToken);
        throw new PythonScriptValidationException("Incorrect PIN.");
    }

    public async Task<PythonScriptListResponse> RevokeAsync(
        PythonScriptApprovalRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = RegisteredName(request.Name);
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            await _documentLock.WaitAsync(cancellationToken);
            try
            {
                var document = ReadDocument();
                if (document.Pin == null)
                {
                    throw new PythonScriptValidationException("No signing PIN is configured yet.");
                }

                if (VerifyPin(document, request.Pin))
                {
                    // Resolve the registration before clearing its approval, even if its file is gone.
                    var canonicalName = name;
                    var updated = document with
                    {
                        Approvals = document.Approvals
                            .Where(approval => !NameEquals(approval.Name, canonicalName)
                                && !NameEquals(approval.Name, name))
                            .ToList()
                    };
                    WriteDocument(updated);
                    Log.Information("[PythonScripts] Revoked approval for script {Name}", canonicalName);
                    return BuildStatus(updated);
                }
            }
            finally
            {
                _documentLock.Release();
            }
        }

        await Task.Delay(TimeSpan.FromMilliseconds(PinFailureDelayMs), cancellationToken);
        throw new PythonScriptValidationException("Incorrect PIN.");
    }

    public async Task<PythonScriptRunResponse> RunAsync(
        string? requestedName,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(requestedName, arguments: null, standardInput: null, pin: null, cancellationToken);
    }

    public async Task<PythonScriptRunResponse> RunAsync(
        string? requestedName,
        IReadOnlyList<string>? arguments,
        CancellationToken cancellationToken = default)
    {
        return await RunAsync(requestedName, arguments, standardInput: null, pin: null, cancellationToken);
    }

    public async Task<PythonScriptRunResponse> RunAsync(
        string? requestedName,
        IReadOnlyList<string>? arguments,
        string? standardInput,
        CancellationToken cancellationToken = default) =>
        await RunAsync(requestedName, arguments, standardInput, pin: null, cancellationToken);

    public async Task<PythonScriptRunResponse> RunAsync(
        string? requestedName,
        IReadOnlyList<string>? arguments,
        string? standardInput,
        string? pin,
        CancellationToken cancellationToken = default)
    {
        await VerifyRunPinAsync(requestedName, pin, cancellationToken);
        ValidateRunInputs(arguments, standardInput);
        var runtime = RuntimeFor(ResolveScriptPath(RegisteredName(requestedName)));
        var interpreter = ResolveInterpreterOrThrow(runtime);

        var verified = await ReadVerifiedScriptAsync(requestedName, cancellationToken);
        var name = verified.Name;

        var startedUtc = DateTime.UtcNow;
        var verifiedCopy = VerifiedCopyPath(name);
        try
        {
            await File.WriteAllBytesAsync(
                verifiedCopy, ExecutableBytes(runtime, verified.Content), cancellationToken);

            // The PyBridge runner is a plain CliWrap process runner: pointed at pwsh or bash it
            // gives those scripts the same argv, stdin, timeout and output capture as Python.
            var options = interpreter.Options;
            options.WorkingDirectory = Path.GetDirectoryName(ResolveScriptPath(name))!;
            options.Timeout = RunTimeout;
            var runner = _runnerFactory(options);

            PythonResult result;
            try
            {
                var interpreterArguments = new List<string>(interpreter.PrefixArguments);
                interpreterArguments.AddRange(
                    ScriptLaunchArguments(runtime, verifiedCopy, options.WorkingDirectory, interactive: false));
                if (arguments is { Count: > 0 })
                {
                    interpreterArguments.AddRange(arguments);
                }
                result = await runner.RunAsync(
                    interpreterArguments,
                    standardInput: string.IsNullOrEmpty(standardInput) ? null : standardInput,
                    cancellationToken: cancellationToken);
            }
            catch (PythonExecutionException ex)
            {
                // The interpreter itself could not be launched (missing or broken install).
                // Surface an actionable message so the route returns 400, not a raw 500.
                Log.Warning(ex, "[PythonScripts] {Runtime} interpreter failed to launch for {Name}", runtime, name);
                throw new PythonScriptValidationException(CouldNotStartMessage(runtime));
            }

            RecordRun(name, startedUtc, result.ExitCode, result.TimedOut, result.RunTime.TotalMilliseconds);
            Log.Information(
                "[PythonScripts] Ran script {Name}: exit={ExitCode} timedOut={TimedOut} durationMs={Duration}",
                name, result.ExitCode, result.TimedOut, Math.Round(result.RunTime.TotalMilliseconds));

            return new PythonScriptRunResponse(
                name,
                result.ExitCode,
                result.TimedOut,
                Truncate(result.StandardOutput),
                Truncate(result.StandardError),
                result.RunTime.TotalMilliseconds,
                startedUtc.ToString("O"),
                ExtractReturnJson(result.StandardOutput));
        }
        finally
        {
            try { File.Delete(verifiedCopy); }
            catch (Exception ex) { Log.Debug(ex, "[PythonScripts] Temp cleanup failed for {Path}", verifiedCopy); }
        }
    }

    private async Task VerifyRunPinAsync(string? requestedName, string? pin, CancellationToken cancellationToken)
    {
        await _documentLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadDocument();
            var name = RegisteredName(requestedName);
            var required = Registration(name).RequirePinEachRun || document.RequirePinEachRun || (document.RequirePinEachRunNames ?? [])
                .Any(n => NameEquals(n, name));
            if (required && (document.Pin == null || !VerifyPin(document, pin)))
                throw new PythonScriptValidationException("A correct signing PIN is required for this run.");
        }
        finally { _documentLock.Release(); }
    }

    public Task ValidateRunAuthorizationAsync(string? name, string? pin, CancellationToken cancellationToken = default) =>
        VerifyRunPinAsync(name, pin, cancellationToken);

    /// <summary>
    /// Checks the same name/hash approval contract as a real run without launching anything. The
    /// interactive terminal route uses this before reserving a tab; the helper process verifies
    /// again immediately before it executes, so a file change in between still fails closed.
    /// </summary>
    public async Task<string> ValidateRunnableAsync(
        string? requestedName,
        CancellationToken cancellationToken = default)
    {
        var verified = await ReadVerifiedScriptAsync(requestedName, cancellationToken);
        return verified.Name;
    }

    /// <summary>
    /// Runs approved bytes with the script's interpreter attached to this process's inherited
    /// console/PTY. This is intentionally concrete-service-only: the dashboard reaches it through
    /// the narrowly scoped <c>--run-python-script</c> helper process, never through an API that
    /// accepts a command.
    /// </summary>
    public async Task<int> RunInteractiveAsync(
        string? requestedName,
        CancellationToken cancellationToken = default)
    {
        var runtime = RuntimeFor(ResolveScriptPath(RegisteredName(requestedName)));
        var interpreter = ResolveInterpreterOrThrow(runtime);

        var verified = await ReadVerifiedScriptAsync(requestedName, cancellationToken);
        var verifiedCopy = VerifiedCopyPath(verified.Name);
        var startedUtc = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await File.WriteAllBytesAsync(
                verifiedCopy, ExecutableBytes(runtime, verified.Content), cancellationToken);

            var options = interpreter.Options;
            var startInfo = new ProcessStartInfo
            {
                FileName = options.PythonExecutable,
                WorkingDirectory = Path.GetDirectoryName(ResolveScriptPath(verified.Name))!,
                UseShellExecute = false,
                RedirectStandardInput = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                CreateNoWindow = false
            };
            foreach (var argument in interpreter.PrefixArguments)
                startInfo.ArgumentList.Add(argument);
            foreach (var argument in ScriptLaunchArguments(
                         runtime, verifiedCopy, startInfo.WorkingDirectory, interactive: true))
                startInfo.ArgumentList.Add(argument);
            ApplyPythonEnvironment(startInfo, options);

            using var process = new Process { StartInfo = startInfo };
            try
            {
                if (!process.Start())
                    throw new InvalidOperationException($"{runtime} did not start.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Warning(ex, "[PythonScripts] Interactive {Runtime} failed to launch for {Name}", runtime, verified.Name);
                throw new PythonScriptValidationException(CouldNotStartMessage(runtime));
            }

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "[PythonScripts] Interactive {Runtime} cleanup failed for {Name}", runtime, verified.Name);
                }
                throw;
            }

            stopwatch.Stop();
            RecordRun(
                verified.Name,
                startedUtc,
                process.ExitCode,
                timedOut: false,
                durationMs: stopwatch.Elapsed.TotalMilliseconds);
            Log.Information(
                "[PythonScripts] Interactive run finished for {Name}: exit={ExitCode} durationMs={Duration}",
                verified.Name, process.ExitCode, Math.Round(stopwatch.Elapsed.TotalMilliseconds));
            return process.ExitCode;
        }
        finally
        {
            stopwatch.Stop();
            try { File.Delete(verifiedCopy); }
            catch (Exception ex) { Log.Debug(ex, "[PythonScripts] Temp cleanup failed for {Path}", verifiedCopy); }
        }
    }

    public async Task<PythonScriptContentResponse> GetContentAsync(
        string? requestedName,
        CancellationToken cancellationToken = default)
    {
        var name = RegisteredName(requestedName);
        var scriptPath = ResolveScriptPath(name);
        if (!File.Exists(scriptPath))
        {
            throw new PythonScriptValidationException($"Script '{name}' was not found.");
        }

        byte[] content;
        try
        {
            content = ReadScriptBytes(scriptPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PythonScriptValidationException($"Could not read '{name}': {ex.Message}");
        }

        var text = DecodeUtf8OrThrow(content);
        // Hand the editor BOM-free text. Canonicalization already ignores a BOM, so saving
        // the round-tripped text back does not by itself invalidate an approval.
        if (text.StartsWith('﻿')) text = text[1..];
        var fileInfo = new FileInfo(scriptPath);

        await _documentLock.WaitAsync(cancellationToken);
        PythonScriptApprovalRecord? approval;
        try
        {
            approval = ReadDocument().Approvals.FirstOrDefault(entry => NameEquals(entry.Name, name));
        }
        finally
        {
            _documentLock.Release();
        }

        return new PythonScriptContentResponse(
            name,
            text,
            ResolveStatus(approval, name, () => content),
            fileInfo.LastWriteTimeUtc.ToString("O"),
            fileInfo.Length,
            ComputeContentVersion(content));
    }

    public async Task<PythonScriptSaveResponse> SaveContentAsync(
        PythonScriptSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var name = RegisteredName(request.Name);
        var scriptPath = ResolveScriptPath(name);
        var bytes = EncodeScriptContent(request.Content);
        var expectedVersion = ValidateExpectedVersion(request.ExpectedVersion);
        Directory.CreateDirectory(GetScriptsDirectory());

        // Serialize with signing and the other file mutations across every vb process.
        // This makes the optimistic version check meaningful for concurrent API callers and
        // prevents an approval from being recorded in the middle of a replacement.
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            var current = ReadScriptBytes(scriptPath);
            if (!string.Equals(
                    ComputeContentVersion(current), expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonScriptValidationException(
                    $"'{name}' changed after it was opened. Reopen it before saving so newer edits are not overwritten.");
            }

            await ReplaceFileAtomicallyAsync(
                scriptPath, bytes, name, expectedVersion, cancellationToken);
        }

        // No approval bookkeeping: the entry (if any) stays, and the new bytes simply stop
        // matching its hash, so the script reads as "modified" until the user re-signs.
        Log.Information("[PythonScripts] Saved script {Name} ({Bytes} bytes)", name, bytes.Length);
        return new PythonScriptSaveResponse(
            await GetStatusAsync(CancellationToken.None),
            ComputeContentVersion(bytes));
    }

    public PythonScriptRunHistoryResponse GetRunHistory()
    {
        lock (_historyLock)
        {
            return new PythonScriptRunHistoryResponse([.. _runHistory]);
        }
    }

    private async Task<(string Name, byte[] Content)> ReadVerifiedScriptAsync(
        string? requestedName,
        CancellationToken cancellationToken)
    {
        var name = RegisteredName(requestedName);
        var scriptPath = ResolveScriptPath(name);
        if (!File.Exists(scriptPath))
        {
            throw new PythonScriptValidationException($"Script '{name}' was not found.");
        }

        // Match the approval keyed to the registration ID, then read once. Every caller
        // executes or copies this byte array; none re-read the writable original after approval.
        var content = ReadScriptBytes(scriptPath);
        var hash = RegisteredHash(name, content);

        await _documentLock.WaitAsync(cancellationToken);
        try
        {
            var document = ReadDocument();
            var approval = document.Approvals.FirstOrDefault(entry => NameEquals(entry.Name, name));
            if (approval == null)
            {
                throw new PythonScriptValidationException(
                    $"Script '{name}' is not signed. Approve it with your PIN before running it.");
            }

            if (!string.Equals(approval.Hash, hash, StringComparison.Ordinal))
            {
                throw new PythonScriptValidationException(
                    $"Script '{name}' has changed since it was signed. Re-approve it with your PIN to run the new version.");
            }
        }
        finally
        {
            _documentLock.Release();
        }

        return (name, content);
    }

    // --- internals ---

    /// <summary>
    /// The executable to launch and the arguments that precede the script path. The options are
    /// always a fresh object, so the per-run WorkingDirectory/Timeout never mutate the injected
    /// runner's shared configuration.
    /// </summary>
    private sealed record ScriptInterpreter(PythonRunnerOptions Options, IReadOnlyList<string> PrefixArguments);

    private ScriptInterpreter ResolveInterpreterOrThrow(JobScriptRuntime runtime)
    {
        if (runtime == JobScriptRuntime.Python)
            return new ScriptInterpreter(ClonedRunnerOptions(GetPythonRunnerOrThrow()), []);

        var executable = _executableResolver.Resolve(runtime)
            ?? throw new PythonScriptValidationException(AutomationScriptService.MissingRuntimeMessage(runtime));
        // No PYTHON* variables: they mean nothing to pwsh or bash.
        return new ScriptInterpreter(
            new PythonRunnerOptions
            {
                PythonExecutable = executable.Path,
                UseUnbufferedOutput = false,
                UseUtf8Io = false,
                ThrowOnNonZeroExitCode = false
            },
            executable.PrefixArguments);
    }

    /// <summary>
    /// The arguments that name the verified copy for its interpreter. PowerShell gets the same
    /// switches as a repository Automation script: the hash pin is the trust decision, so the
    /// machine's execution policy must not veto an approved script. A captured run also passes
    /// -NonInteractive, turning a stray Read-Host into an error rather than a hung run; the
    /// terminal path leaves it off because answering prompts is what that path is for.
    /// </summary>
    private static IEnumerable<string> ScriptLaunchArguments(
        JobScriptRuntime runtime, string verifiedCopy, string workingDirectory, bool interactive)
    {
        switch (runtime)
        {
            case JobScriptRuntime.PowerShell:
                yield return "-NoLogo";
                yield return "-NoProfile";
                if (!interactive) yield return "-NonInteractive";
                yield return "-ExecutionPolicy";
                yield return "Bypass";
                yield return "-File";
                yield return verifiedCopy;
                break;
            case JobScriptRuntime.Bash:
                // Git Bash and POSIX bash both read a relative slash path; a raw Windows drive
                // path is not portable between them.
                yield return AutomationScriptService.ToBashPath(workingDirectory, verifiedCopy);
                break;
            default:
                yield return verifiedCopy;
                break;
        }
    }

    /// <summary>
    /// The verified copy keeps the script's extension: pwsh refuses -File on anything that is
    /// not a .ps1, and a traceback naming a .py file reads as expected.
    /// </summary>
    private string VerifiedCopyPath(string scriptName) =>
        Path.Combine(
            Path.GetDirectoryName(ResolveScriptPath(scriptName))!,
            $".viberails-script-{Guid.NewGuid():N}{Path.GetExtension(ResolveScriptPath(scriptName)).ToLowerInvariant()}");

    /// <summary>
    /// The bytes written to the verified copy. Bash gets the canonical text the approval was
    /// computed over (BOM removed, LF line endings): a CRLF file is the same signed script, but
    /// bash would read every '\r' as part of a command. Other interpreters take the bytes as-is.
    /// </summary>
    private static byte[] ExecutableBytes(JobScriptRuntime runtime, byte[] content) =>
        runtime == JobScriptRuntime.Bash
            ? StrictUtf8.GetBytes(CanonicalText(content))
            : content;

    private static string CouldNotStartMessage(JobScriptRuntime runtime) =>
        runtime switch
        {
            JobScriptRuntime.PowerShell =>
                "PowerShell (pwsh) could not be started. Make sure PowerShell 7 is installed and on your PATH.",
            JobScriptRuntime.Bash when OperatingSystem.IsWindows() =>
                "Bash could not be started. Make sure Git for Windows (Git Bash) is installed.",
            JobScriptRuntime.Bash => "Bash could not be started. Make sure bash is installed and on your PATH.",
            _ => "Python could not be started. Make sure a Python interpreter is installed and on your PATH."
        };

    private IPythonRunner GetPythonRunnerOrThrow()
    {
        try
        {
            return _pythonRunner.Value
                ?? throw new PythonScriptValidationException(
                    "Python execution is not available in this process.");
        }
        catch (PythonScriptValidationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[PythonScripts] Python interpreter discovery failed");
            throw new PythonScriptValidationException(
                "Python could not be started. Make sure a Python interpreter is installed and on your PATH.");
        }
    }

    private static PythonRunnerOptions ClonedRunnerOptions(IPythonRunner pythonRunner)
    {
        var source = pythonRunner.Options;
        var clone = new PythonRunnerOptions
        {
            PythonExecutable = source.PythonExecutable,
            UseUnbufferedOutput = source.UseUnbufferedOutput,
            UseUtf8Io = source.UseUtf8Io,
            ThrowOnNonZeroExitCode = false
        };
        foreach (var (key, value) in source.EnvironmentVariables)
        {
            clone.EnvironmentVariables[key] = value;
        }

        foreach (var entry in source.AdditionalPathEntries)
        {
            clone.AdditionalPathEntries.Add(entry);
        }

        return clone;
    }

    private static void ApplyPythonEnvironment(
        ProcessStartInfo startInfo,
        PythonRunnerOptions options)
    {
        if (options.UseUnbufferedOutput)
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";

        if (options.UseUtf8Io)
        {
            startInfo.Environment["PYTHONIOENCODING"] = "utf-8";
            startInfo.Environment["PYTHONUTF8"] = "1";
        }

        foreach (var (key, value) in options.EnvironmentVariables)
        {
            if (value is null)
                startInfo.Environment.Remove(key);
            else
                startInfo.Environment[key] = value;
        }

        if (options.AdditionalPathEntries.Count > 0)
        {
            startInfo.Environment.TryGetValue("PATH", out var basePath);
            var prepend = string.Join(Path.PathSeparator, options.AdditionalPathEntries);
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(basePath)
                ? prepend
                : prepend + Path.PathSeparator + basePath;
        }
    }

    private void RecordRun(string name, DateTime startedUtc, int exitCode, bool timedOut, double durationMs)
    {
        lock (_historyLock)
        {
            _runHistory.Insert(0, new PythonScriptRunRecord(
                name, startedUtc.ToString("O"), exitCode, timedOut, durationMs));
            if (_runHistory.Count > RunHistoryCap)
            {
                _runHistory.RemoveRange(RunHistoryCap, _runHistory.Count - RunHistoryCap);
            }
        }
    }

    private PythonScriptListResponse BuildStatus(PythonScriptSigningDocument document)
    {
        var scripts = new List<PythonScriptInfo>();
        foreach (var entry in ReadLibrary().Scripts.Where(IsVisible).OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var approval = document.Approvals.FirstOrDefault(item => item.Name == entry.Id);
            string status = StatusUnapproved;
            FileInfo? info = null;
            try
            {
                ValidateLibraryPath(entry.Path);
                info = GetRegularScriptFileInfo(entry.Path);
                status = ResolveStatus(approval, entry.Id, () => ReadScriptBytes(entry.Path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PythonScriptValidationException)
            {
                // Retain missing/unsafe registrations so the owner can remove them, but
                // never show them as runnable or silently substitute another file.
            }
            scripts.Add(new PythonScriptInfo(Path.GetFileName(entry.Path), status, approval?.ApprovedUtc,
                info?.LastWriteTimeUtc.ToString("O"), info?.Length ?? 0, entry.Path,
                entry.RequirePinEachRun || (document.RequirePinEachRunNames ?? []).Contains(entry.Id),
                entry.Id, entry.DisplayName, entry.ProjectPath == null ? "global" : "repo", entry.ProjectPath));
        }
        return new PythonScriptListResponse(document.Pin != null, GetScriptsDirectory(), scripts, document.RequirePinEachRun);
    }

    private static string ValidateScriptName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (!ScriptNamePattern.IsMatch(trimmed)
            || trimmed.Contains("..", StringComparison.Ordinal))
        {
            throw new PythonScriptValidationException(ScriptNameRule);
        }

        return trimmed;
    }

    private string ResolveScriptPath(string identifier) => ValidateLibraryPath(Registration(identifier).Path);

    private static byte[] ReadScriptBytes(string path)
    {
        var info = GetRegularScriptFileInfo(path);
        if (info.Length > MaxScriptBytes)
        {
            throw new PythonScriptValidationException(
                $"Script is larger than the {MaxScriptBytes / (1024 * 1024)} MB limit.");
        }

        return File.ReadAllBytes(path);
    }

    private static FileInfo GetRegularScriptFileInfo(string path)
    {
        try
        {
            ValidatePathComponents(path);
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new PythonScriptValidationException(
                    $"Script '{Path.GetFileName(path)}' was not found.");
            }

            if (IsLinkOrReparsePoint(info))
            {
                throw new PythonScriptValidationException(
                    $"Script '{Path.GetFileName(path)}' is a symbolic link or reparse point. "
                    + "Only registered regular files can be opened, signed, or run.");
            }

            return info;
        }
        catch (PythonScriptValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PythonScriptValidationException(
                $"Could not inspect '{Path.GetFileName(path)}': {ex.Message}");
        }
    }


    /// <summary>
    /// Encodes editor text for disk: UTF-8, no BOM, byte-exact (line endings included — the
    /// canonical hash normalizes them, so there is nothing to gain by rewriting the file).
    /// </summary>
    private static byte[] EncodeScriptContent(string? content)
    {
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content ?? string.Empty);
        }
        catch (EncoderFallbackException)
        {
            throw new PythonScriptValidationException(
                "The script text contains characters that cannot be saved as UTF-8.");
        }

        if (bytes.Length > MaxScriptBytes)
        {
            throw new PythonScriptValidationException(
                $"Script is larger than the {MaxScriptBytes / (1024 * 1024)} MB limit.");
        }

        return bytes;
    }

    private static string DecodeUtf8OrThrow(byte[] content)
    {
        try
        {
            return StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            throw new PythonScriptValidationException(
                "Script is not valid UTF-8. Save it as UTF-8 before signing or running it.");
        }
    }

    private static async Task WriteNewFileAtomicallyAsync(
        string path, byte[] bytes, string name, CancellationToken cancellationToken)
    {
        var tempPath = TemporarySiblingPath(path);
        try
        {
            await WriteTemporaryFileAsync(tempPath, bytes, cancellationToken);
            File.Move(tempPath, path);
        }
        catch (IOException) when (PathEntryExists(path))
        {
            throw new PythonScriptValidationException(
                $"A script named '{name}' already exists. Pick another name.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PythonScriptValidationException($"Could not create '{name}': {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static async Task ReplaceFileAtomicallyAsync(
        string path,
        byte[] bytes,
        string name,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var tempPath = TemporarySiblingPath(path);
        try
        {
            await WriteTemporaryFileAsync(tempPath, bytes, cancellationToken);

            // Recheck immediately before the atomic rename. This catches an external editor
            // changing or deleting the file while the replacement bytes were being written.
            var current = ReadScriptBytes(path);
            if (!string.Equals(
                    ComputeContentVersion(current), expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new PythonScriptValidationException(
                    $"'{name}' changed while it was being saved. Reopen it and try again.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        catch (PythonScriptValidationException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PythonScriptValidationException($"Could not save '{name}': {ex.Message}");
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private static async Task WriteTemporaryFileAsync(
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string TemporarySiblingPath(string path) =>
        Path.Combine(
            Path.GetDirectoryName(path)!,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[PythonScripts] Temp cleanup failed for {Path}", path);
        }
    }

    /// <summary>
    /// A plain file name turned into a candidate script name: a .py, .ps1 or .sh extension is
    /// kept (lower-cased, since it selects the interpreter); anything else is treated as Python.
    /// </summary>
    private static string SuggestScriptName(string fileName)
    {
        var trimmed = (fileName ?? string.Empty).Trim();
        var extension = Path.GetExtension(trimmed).ToLowerInvariant();
        return extension is ".py" or ".ps1" or ".sh"
            ? trimmed[..^extension.Length] + extension
            : trimmed + ".py";
    }

    private string ResolveStatus(
        PythonScriptApprovalRecord? approval, string name, Func<byte[]> contentFactory)
    {
        if (approval == null)
        {
            return StatusUnapproved;
        }

        string currentHash;
        try
        {
            currentHash = RegisteredHash(name, contentFactory());
        }
        catch (PythonScriptValidationException)
        {
            currentHash = string.Empty;
        }

        return string.Equals(approval.Hash, currentHash, StringComparison.Ordinal)
            ? StatusApproved
            : StatusModified;
    }

    private static PythonScriptSigningDocument WithoutApprovals(
        PythonScriptSigningDocument document, params string[] names) =>
        document with
        {
            Approvals = document.Approvals
                .Where(approval => !names.Any(name => NameEquals(approval.Name, name)))
                .ToList()
        };

    private static string ValidateExpectedVersion(string? version)
    {
        var trimmed = (version ?? string.Empty).Trim();
        if (trimmed.Length != 64 || !trimmed.All(Uri.IsHexDigit))
        {
            throw new PythonScriptValidationException(
                "The script version is missing or invalid. Reopen the script before saving.");
        }

        return trimmed;
    }

    internal static string ComputeContentVersion(byte[] content) =>
        Convert.ToHexStringLower(SHA256.HashData(content));

    private static bool PathEntryExists(string path)
    {
        if (File.Exists(path) || Directory.Exists(path)) return true;
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An entry that cannot be inspected is not safe to overwrite as a new script.
            return true;
        }
    }

    private static bool IsLinkOrReparsePoint(FileInfo info) =>
        info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsNetworkOrDevicePath(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
        || path.StartsWith("//", StringComparison.Ordinal);

    private static bool IsUnsupportedWindowsDrive(string path)
    {
        var rootPath = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(rootPath)) return true;

        try
        {
            return new DriveInfo(rootPath).DriveType is
                DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory;
        }
        catch (Exception ex) when (ex is
            ArgumentException or
            IOException or
            UnauthorizedAccessException or
            System.Security.SecurityException or
            NotSupportedException)
        {
            return true;
        }
    }

    /// <summary>
    /// SHA-256 over a canonical form: version tag + script name + UTF-8 content with a
    /// leading BOM stripped and CRLF/CR normalized to LF. Content-identical scripts hash
    /// the same on every machine and after any line-ending churn.
    ///
    /// The bytes are decoded with a strict UTF-8 decoder: lenient decoding maps every
    /// invalid byte to U+FFFD, which would let two different files (e.g. latin-1 0xE8 vs
    /// 0xE9) collide on one hash and defeat exact-content signing. The name is mixed in
    /// case-sensitively so a rename — including a case-only rename — always invalidates.
    /// </summary>
    internal static string ComputeCanonicalHash(string name, byte[] content)
    {
        var preimage = $"viberails-script-v1\n{name}\n{CanonicalText(content)}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(preimage)));
    }

    /// <summary>Strict UTF-8 text with a leading BOM removed and CRLF/CR folded to LF.</summary>
    private static string CanonicalText(byte[] content)
    {
        var text = DecodeUtf8OrThrow(content);
        if (text.StartsWith('﻿')) text = text[1..];
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    private static bool NameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    /// <summary>
    /// The script's real on-disk file name, so approvals key off the exact casing the
    /// filesystem uses. On a case-insensitive volume this folds a differently-cased request
    /// (JOB.PY) back to the stored entry (job.py); on a case-sensitive one it is an exact
    /// match that keeps same-named siblings distinct. Returns null when the file is absent.
    /// </summary>
    private static string? CanonicalOnDiskName(string fullPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            // fileName is a validated script name (no glob metacharacters), so this matches
            // only the one real entry and returns its canonical casing.
            var matches = Directory.GetFiles(directory, fileName, SearchOption.TopDirectoryOnly);
            return matches.Length == 1 ? Path.GetFileName(matches[0]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] DerivePinHash(string pin, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            32);

    /// <summary>
    /// Constant-time PIN check with no side effects. Callers apply the anti-guessing delay
    /// after releasing every lock, so a wrong PIN never stalls a concurrent reader.
    /// </summary>
    private static bool VerifyPin(PythonScriptSigningDocument document, string? pin)
    {
        var record = document.Pin;
        if (record == null || string.IsNullOrEmpty(pin))
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(record.Salt);
            var expected = Convert.FromBase64String(record.Hash);
            var actual = DerivePinHash(pin, salt, record.Iterations);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// An advisory cross-process lock spanning the signing document's read-modify-write.
    /// The dashboard, terminal children, and <c>vb --sign-script</c> are separate processes
    /// with independent in-process locks; without this, two concurrent approvals could
    /// overwrite each other. Only the mutators take it; the read path never does.
    /// </summary>
    private async Task<FileStream> AcquireCrossProcessWriteLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_installDirectory);
        var lockPath = SigningFilePath + ".lock";
        var deadline = Environment.TickCount64 + WriteLockTimeoutMs;
        var delayMs = 20;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Held by another process (or another instance in this one); back off.
            }
            catch (UnauthorizedAccessException)
            {
                // Transient sharing/AV lock on Windows; back off.
            }

            if (Environment.TickCount64 >= deadline)
            {
                throw new PythonScriptValidationException(
                    "The signing file is in use by another process. Try again in a moment.");
            }

            await Task.Delay(delayMs, cancellationToken);
            delayMs = Math.Min(delayMs * 2, 250);
        }
    }

    private PythonScriptSigningDocument ReadDocument()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(SigningFilePath))
                {
                    return new PythonScriptSigningDocument(DocumentVersion, null, []);
                }

                var document = JsonSerializer.Deserialize(
                    File.ReadAllText(SigningFilePath),
                    AppJsonSerializerContext.Default.PythonScriptSigningDocument);
                return document is { Version: DocumentVersion, Approvals: not null }
                    ? document
                    : new PythonScriptSigningDocument(DocumentVersion, null, []);
            }
            catch (JsonException ex)
            {
                // Genuinely unparseable content (corrupt or hand-edited): treat as empty
                // state so the feature still works, exactly like a fresh install.
                Log.Warning(ex, "[PythonScripts] {Path} is not valid JSON; treating as empty.", SigningFilePath);
                return new PythonScriptSigningDocument(DocumentVersion, null, []);
            }
            catch (IOException ex)
            {
                // A concurrent writer's rename, or an AV scan, can briefly lock the file.
                // Retry — never fall through to "empty", which would silently drop every
                // approval and disable the PIN check. Fail closed once retries are exhausted.
                if (attempt >= ReadRetryAttempts)
                {
                    Log.Warning(ex, "[PythonScripts] {Path} stayed locked after {Attempts} attempts.",
                        SigningFilePath, attempt + 1);
                    throw new PythonScriptValidationException(
                        "The signing file is temporarily unavailable. Try again in a moment.");
                }

                Log.Debug(ex, "[PythonScripts] Retrying read of {Path} (attempt {Attempt}).",
                    SigningFilePath, attempt + 1);
                Thread.Sleep(ReadRetryDelayMs);
            }
        }
    }

    private void WriteDocument(PythonScriptSigningDocument document)
    {
        Directory.CreateDirectory(_installDirectory);
        Directory.CreateDirectory(GetScriptsDirectory());
        var json = JsonSerializer.Serialize(
            document,
            AppJsonSerializerContext.Default.PythonScriptSigningDocument);
        // Write-then-rename so a crash mid-write can never leave a truncated signing file.
        // The temp name is unique per write so two processes renaming at once cannot collide
        // on a shared scratch file.
        var temp = $"{SigningFilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, json);
            File.Move(temp, SigningFilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) { Log.Debug(ex, "[PythonScripts] Temp cleanup failed for {Path}", temp); }
            throw;
        }
    }

    private static string Truncate(string value) =>
        value.Length <= MaxCapturedOutputChars
            ? value
            : value[..MaxCapturedOutputChars] + $"\n[... output truncated at {MaxCapturedOutputChars} characters ...]";

    /// <summary>
    /// Bounds what a caller may push into a run. Argv never reaches a shell — CliWrap hands
    /// the tokens straight to the interpreter — so the risk here is size, not injection.
    /// </summary>
    private static void ValidateRunInputs(IReadOnlyList<string>? arguments, string? standardInput)
    {
        if (arguments is { Count: > MaxRunArguments })
        {
            throw new PythonScriptValidationException(
                $"A run takes at most {MaxRunArguments} arguments.");
        }

        if (arguments is not null)
        {
            foreach (var argument in arguments)
            {
                if (argument is null)
                {
                    throw new PythonScriptValidationException("An argument was empty.");
                }

                if (argument.Length > MaxRunArgumentChars)
                {
                    throw new PythonScriptValidationException(
                        $"An argument is longer than {MaxRunArgumentChars} characters.");
                }
            }
        }

        if (standardInput is { Length: > MaxStandardInputChars })
        {
            throw new PythonScriptValidationException(
                $"Standard input is longer than {MaxStandardInputChars} characters.");
        }
    }

    /// <summary>
    /// The script's return value, by the convention the run window documents: the JSON object
    /// or array a script printed as the whole of stdout, or on stdout's last line (so a script
    /// can log freely and still return something). A bare number, string or boolean does not
    /// count — those are output, and treating them as a return value would make every script
    /// that prints "42" look like it returned one. Returns the JSON text, or null; the run
    /// window is what pretty-prints it.
    /// </summary>
    internal static string? ExtractReturnJson(string? standardOutput)
    {
        if (string.IsNullOrWhiteSpace(standardOutput)) return null;

        var whole = standardOutput.Trim();
        if (TryReadJsonValue(whole, out var fromWhole)) return fromWhole;

        var lastBreak = whole.LastIndexOfAny(['\n', '\r']);
        if (lastBreak < 0) return null;

        var lastLine = whole[(lastBreak + 1)..].Trim();
        return TryReadJsonValue(lastLine, out var fromLastLine) ? fromLastLine : null;
    }

    private static bool TryReadJsonValue(string candidate, out string? formatted)
    {
        formatted = null;
        if (candidate.Length < 2) return false;
        if (candidate[0] is not ('{' or '[')) return false;
        if (candidate.Length > MaxCapturedOutputChars) return false;

        try
        {
            using var document = JsonDocument.Parse(candidate);
            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return false;
            }

            formatted = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

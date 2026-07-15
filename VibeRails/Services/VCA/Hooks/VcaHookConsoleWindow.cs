using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Mirrors hook output into a real Windows console when Git was launched by a GUI
/// client. The original redirected writers stay connected too, so the Git client
/// still receives the same deterministic transcript.
/// </summary>
internal sealed class VcaHookConsoleWindow : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly TimeSpan MinimumVisibleDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan FailurePauseTimeout = TimeSpan.FromSeconds(15);

    private readonly TextWriter _consoleWriter;
    private readonly TextReader _consoleReader;
    private readonly DateTimeOffset _openedAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    private VcaHookConsoleWindow(
        TextWriter output,
        TextWriter error,
        TextReader input,
        TextWriter consoleWriter,
        TextReader consoleReader)
    {
        Output = output;
        Error = error;
        Input = input;
        _consoleWriter = consoleWriter;
        _consoleReader = consoleReader;
    }

    public TextWriter Output { get; }
    public TextWriter Error { get; }
    public TextReader Input { get; }

    public static VcaHookConsoleWindow? TryOpen(
        VcaHookInvocation invocation,
        TextWriter originalOutput,
        TextWriter originalError)
    {
        if (!invocation.ShowConsoleWindow ||
            !OperatingSystem.IsWindows() ||
            !Environment.UserInteractive ||
            IsAutomatedEnvironment())
        {
            return null;
        }

        try
        {
            // A redirected command launched from a real terminal still has a console.
            // Keep that console (and its redirected handles) intact. Git GUI launches have
            // no console, which is the only case where we allocate a dedicated window.
            if (GetConsoleWindow() != IntPtr.Zero)
            {
                return null;
            }

            if (!AllocConsole())
            {
                return null;
            }

            var outputHandle = CreateFile(
                "CONOUT$",
                GenericRead | GenericWrite,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            var inputHandle = CreateFile(
                "CONIN$",
                GenericRead | GenericWrite,
                ShareRead | ShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);

            if (outputHandle.IsInvalid || inputHandle.IsInvalid)
            {
                outputHandle.Dispose();
                inputHandle.Dispose();
                FreeConsole();
                return null;
            }

            var consoleWriter = new StreamWriter(
                new FileStream(outputHandle, FileAccess.Write),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true
            };
            var consoleReader = new StreamReader(
                new FileStream(inputHandle, FileAccess.Read),
                System.Text.Encoding.UTF8);

            SetStdHandle(StdInputHandle, inputHandle.DangerousGetHandle());
            SetStdHandle(StdOutputHandle, outputHandle.DangerousGetHandle());
            SetStdHandle(StdErrorHandle, outputHandle.DangerousGetHandle());
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.SetOut(consoleWriter);
            Console.SetError(consoleWriter);
            Console.SetIn(consoleReader);
            Console.Title = $"VibeRails VCA - {FormatTitle(invocation.Kind)}";

            return new VcaHookConsoleWindow(
                new TeeTextWriter(originalOutput, consoleWriter),
                new TeeTextWriter(originalError, consoleWriter),
                consoleReader,
                consoleWriter,
                consoleReader);
        }
        catch
        {
            try
            {
                FreeConsole();
            }
            catch
            {
                // Best effort only. Hook validation continues through redirected output.
            }

            return null;
        }
    }

    public async Task CompleteAsync(int exitCode, bool pauseOnFailure)
    {
        var remaining = MinimumVisibleDuration - (DateTimeOffset.UtcNow - _openedAt);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        if (exitCode != 0 && pauseOnFailure)
        {
            await Output.WriteLineAsync();
            await Output.WriteAsync("Press Enter to close the VCA console (auto-closes in 15 seconds)...");
            await Output.FlushAsync();
            var readTask = Input.ReadLineAsync();
            var completed = await Task.WhenAny(
                readTask,
                Task.Delay(FailurePauseTimeout));
            if (completed == readTask)
            {
                await readTask;
            }
            else
            {
                // Auto-close keeps a missed GUI prompt from holding Git indefinitely.
                // Disposing the console reader immediately after this method unblocks the
                // outstanding read; observe a resulting fault so it cannot go unobserved.
                _ = readTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _consoleWriter.Dispose();
        _consoleReader.Dispose();
        FreeConsole();
    }

    private static string FormatTitle(VcaHookKind kind) => kind switch
    {
        VcaHookKind.CommitMessage => "Commit Message",
        VcaHookKind.AcknowledgeCommitMessage => "Commit Acknowledgment",
        VcaHookKind.Preview => "Preview",
        _ => "Pre-Commit"
    };

    private static bool IsAutomatedEnvironment()
    {
        // CI providers consistently expose at least one of these variables. A hook must
        // never allocate a desktop console (or wait for input) in a build agent/service.
        string[] variables =
        [
            "CI",
            "TF_BUILD",
            "GITHUB_ACTIONS",
            "GITLAB_CI",
            "JENKINS_URL",
            "TEAMCITY_VERSION",
            "BUILDKITE",
            "APPVEYOR"
        ];

        return variables.Any(variable =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int stdHandle, IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}

internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeTextWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override System.Text.Encoding Encoding => _second.Encoding;

    public override void Write(char value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void Write(string? value)
    {
        _first.Write(value);
        _second.Write(value);
    }

    public override void WriteLine(string? value)
    {
        _first.WriteLine(value);
        _second.WriteLine(value);
    }

    public override async Task WriteAsync(char value)
    {
        await _first.WriteAsync(value);
        await _second.WriteAsync(value);
    }

    public override async Task WriteAsync(string? value)
    {
        await _first.WriteAsync(value);
        await _second.WriteAsync(value);
    }

    public override async Task WriteLineAsync(string? value)
    {
        await _first.WriteLineAsync(value);
        await _second.WriteLineAsync(value);
    }

    public override async Task FlushAsync()
    {
        await _first.FlushAsync();
        await _second.FlushAsync();
    }
}

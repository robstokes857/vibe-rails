using System.Runtime.InteropServices;
using Pty.Net.Windows;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// The ConPTY launch builds one command line from the program and argv. Quoting used to double
/// quotes and ignore backslashes, which CommandLineToArgvW does not undo, and a program missing
/// from PATH fell back to <c>cwd\app</c>, so a file planted in a repository could run in its
/// place. VIBE-194 (scan finding L-25).
/// </summary>
public sealed class PtyNetWindowsLaunchTests : IDisposable
{
    private static readonly string[] Vectors =
    [
        "plain",
        "",
        "two words",
        "tab\there",
        "say \"hi\"",
        @"C:\Program Files\",
        @"C:\dir with space\\",
        @"trailing\",
        @"a\\b",
        @"a\""b",
        @"\\server\share\x y",
        "\"",
        @"\""",
        "-Command",
        "$x = 'single'; Write-Host \"$x\"",
    ];

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"vb_pty_lookup_{Guid.NewGuid():N}");

    public static TheoryData<string> QuotingVectors() => new(Vectors);

    [Theory]
    [MemberData(nameof(QuotingVectors))]
    public void QuoteArgument_MatchesTheHookLauncherRecipe(string value)
    {
        Assert.Equal(VcaHookProcessLaunch.QuoteArgument(value), WindowsArguments.QuoteArgument(value));
    }

    [Fact]
    public void Format_RoundTripsThroughCommandLineToArgvW()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "CommandLineToArgvW is a Windows API.");

        var parsed = SplitCommandLine("prog.exe " + WindowsArguments.Format(Vectors));

        Assert.Equal(["prog.exe", .. Vectors], parsed);
    }

    [Fact]
    public void GetAppOnPath_NeverResolvesABareNameAgainstTheWorkingDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows provider resolves Windows PATH entries.");
        var cwd = CreateDirectory("repo");
        Touch(Path.Combine(cwd, "planted.exe"));
        Touch(Path.Combine(cwd, "bin", "planted.exe"));

        Assert.Throws<FileNotFoundException>(() =>
            PtyProvider.GetAppOnPath("planted", cwd, Env(CreateDirectory("empty"))));
        Assert.Throws<FileNotFoundException>(() =>
            PtyProvider.GetAppOnPath("planted.exe", cwd, Env(".", "bin", @"\" + Path.GetFileName(cwd))));
    }

    [Fact]
    public void GetAppOnPath_FindsProgramsOnAbsolutePathEntriesOnly()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows provider resolves Windows PATH entries.");
        var cwd = CreateDirectory("repo");
        Touch(Path.Combine(cwd, "tool.exe"));
        var tools = CreateDirectory("tools dir");
        var installed = Path.Combine(tools, "tool.exe");
        Touch(installed);

        Assert.Equal(installed, PtyProvider.GetAppOnPath("tool", cwd, Env(".", tools)));
        Assert.Equal(installed, PtyProvider.GetAppOnPath("tool.exe", cwd, Env("\"" + tools + "\"")));
    }

    [Fact]
    public void GetAppOnPath_EmptyPathThrowsInsteadOfUsingTheWorkingDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The Windows provider resolves Windows PATH entries.");
        var cwd = CreateDirectory("repo");
        Touch(Path.Combine(cwd, "tool.exe"));

        Assert.Throws<FileNotFoundException>(() =>
            PtyProvider.GetAppOnPath("tool.exe", cwd, new Dictionary<string, string> { ["PATH"] = "" }));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateDirectory(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
    }

    private static Dictionary<string, string> Env(params string[] pathEntries) =>
        new() { ["PATH"] = string.Join(';', pathEntries) };

    private static string[] SplitCommandLine(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        Assert.NotEqual(IntPtr.Zero, argv);
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!;
            return result;
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCmdLine,
        out int pNumArgs);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}

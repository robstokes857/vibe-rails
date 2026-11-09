using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pty.Net.Windows;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

[CollectionDefinition("Windows process containment", DisableParallelization = true)]
public sealed class WindowsProcessContainmentCollection;

/// <summary>Runs only disposable trees, isolated from real terminals and the application database.</summary>
[Collection("Windows process containment")]
public sealed class WindowsProcessJobTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vb-job-" + Guid.NewGuid().ToString("N"));
    private static string? helper;

    public WindowsProcessJobTests() => Directory.CreateDirectory(directory);

    [Theory]
    [InlineData(2, 3, "tree", 15)]
    [InlineData(12, 1, "tree", 13)]
    [InlineData(2, 2, "exit", 7)]
    [InlineData(1, 0, "spawning", 6)]
    public async Task JobStopsDeepWideOrphanedAndSpawningTrees(int width, int depth, string mode, int expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job API");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, width, depth, mode);
        using var siblingJob = new WindowsProcessJob();
        using var sibling = await Start(siblingJob, 0, 0, "tree", Path.Combine(directory, "sibling"));
        await WaitUntil(() => Pids().Length >= expected);
        var processes = Pids().Where(p => p != root.Id).Select(Process.GetProcessById).ToArray();
        try
        {
            if (mode == "exit") await root.WaitForExitAsync(TestContext.Current.CancellationToken);
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            {
                if (i % 3 == 0) job.Dispose();
                return job.TryTerminate();
            })));
            Assert.All(results, error => Assert.Equal(0, error));
            await root.WaitForExitAsync(TestContext.Current.CancellationToken);
            await WaitUntil(() => processes.All(p => p.HasExited));
            Assert.False(sibling.HasExited);
            foreach (int pid in Pids()) AssertExited(pid);
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [Fact]
    public async Task ClosingLastOwnerHandleStopsDescendantsAfterRootExit()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job API");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, 2, 1, "exit");
        await WaitUntil(() => Pids().Length == 3);
        using var child = Process.GetProcessById(Pids().First(id => id != root.Id));
        await root.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.False(child.HasExited);
        job.Dispose();
        await WaitUntil(() => child.HasExited);
        job.Dispose();
        Assert.Equal(0, job.TryTerminate());
    }

    [Fact]
    public void RepeatedJobCreationAndDisposalDoesNotLeakHandles()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows handle ownership");
        using var self = Process.GetCurrentProcess();
        using (var warmup = new WindowsProcessJob()) Assert.Equal(0, warmup.TryTerminate());
        Assert.True(GetProcessHandleCount(self.SafeHandle, out uint before));
        for (int i = 0; i < 100; i++)
        {
            using var job = new WindowsProcessJob();
            Assert.Equal(0, job.TryTerminate());
        }
        Assert.True(GetProcessHandleCount(self.SafeHandle, out uint after));
        Assert.True(after <= before + 2, $"Handle count grew from {before} to {after}");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(SafeProcessHandle process, out uint count);

    [Fact]
    public async Task NestedPtyStopPreservesReusableHost()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows ConPTY");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, 0, 0, "nested");
        await WaitUntil(() => File.Exists(Path.Combine(directory, "stopped")));
        var children = Pids().Where(id => id != root.Id).ToArray();
        Assert.NotEmpty(children);
        foreach (int pid in children) AssertExited(pid);
        Assert.False(root.HasExited);
        Assert.Equal(0, job.TryTerminate());
        await root.WaitForExitAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("12345")]
    public async Task FailedBootstrapDoesNotRunWorkload(string handle)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job API");
        using var process = CreateProcess(0, 0, "tree", directory);
        process.Start();
        await process.StandardInput.WriteLineAsync(handle);
        process.StandardInput.Close();
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, process.ExitCode);
        Assert.Empty(Pids());
        using var job = new WindowsProcessJob();
        using var invalid = new SafeProcessHandle(IntPtr.Zero, false);
        Assert.Throws<Win32Exception>(() => job.Assign(invalid));
    }

    [Fact]
    public async Task OwnerProcessDeathClosesItsJobWithoutStoppingSibling()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job API");
        using var protection = new WindowsProcessJob();
        using var owner = await Start(protection, 0, 0, "owner");
        await WaitUntil(() => Pids().Length == 4);
        var children = Pids().Where(id => id != owner.Id).Select(Process.GetProcessById).ToArray();
        using var siblingJob = new WindowsProcessJob();
        using var sibling = await Start(siblingJob, 0, 0, "tree", Path.Combine(directory, "sibling"));
        try
        {
            owner.Kill(); // Simulates an abrupt root backend exit, not a tree scan.
            await WaitUntil(() => owner.HasExited && children.All(p => p.HasExited));
            Assert.False(sibling.HasExited);
        }
        finally { foreach (var child in children) child.Dispose(); }
    }

    [Fact]
    public async Task SnapshotFallbackUsesPinnedIdentityAndLeavesSiblingAlive()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows Toolhelp");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, 2, 2, "tree");
        await WaitUntil(() => Pids().Length == 7);
        var children = Pids().Where(id => id != root.Id).Select(Process.GetProcessById).ToArray();
        using var siblingJob = new WindowsProcessJob();
        using var sibling = await Start(siblingJob, 0, 0, "tree", Path.Combine(directory, "sibling"));
        try
        {
            var result = WindowsProcessCleanup.TerminateTree(root.SafeHandle, root.Id);
            Assert.Equal(0, result.NativeErrors);
            Assert.Equal(3, result.Snapshots);
            await WaitUntil(() => root.HasExited && children.All(p => p.HasExited));
            Assert.False(sibling.HasExited);
        }
        finally { foreach (var child in children) child.Dispose(); }
        Assert.True(WindowsProcessCleanup.IsChildLifetime(10, 20, 15));
        Assert.False(WindowsProcessCleanup.IsChildLifetime(10, 20, 21));
        Assert.False(WindowsProcessCleanup.IsChildLifetime(10, 0, 9));
    }

    [Fact]
    public async Task ContainedTerminationCreatesNoInspectionExceptions()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows job API");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, 2, 2, "tree");
        await WaitUntil(() => Pids().Length == 7);
        int denied = 0;
        int measuringThread = Environment.CurrentManagedThreadId;
        void Count(object? sender, FirstChanceExceptionEventArgs args)
        { if (Environment.CurrentManagedThreadId == measuringThread && args.Exception is Win32Exception { NativeErrorCode: 5 }) denied++; }
        AppDomain.CurrentDomain.FirstChanceException += Count;
        try { Assert.Equal(0, job.TryTerminate()); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }
        Assert.Equal(0, denied);
    }

    [Fact]
    public async Task MeasureLegacyAndContainedCleanupWithLogging()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process cleanup measurements");
        for (int sample = 0; sample < 3; sample++)
        {
            foreach (bool contained in new[] { false, true })
            {
                string dir = Path.Combine(directory, $"measure-{sample}-{contained}");
                using var job = new WindowsProcessJob();
                using var root = await Start(job, 2, 2, "tree", dir);
                await WaitUntil(() => Directory.GetFiles(dir, "*.pid").Length == 7);
                int denied = 0;
                int measuringThread = Environment.CurrentManagedThreadId;
                var sink = new CountingSink();
                using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
                void Count(object? sender, FirstChanceExceptionEventArgs args)
                {
                    if (Environment.CurrentManagedThreadId != measuringThread || args.Exception is not Win32Exception { NativeErrorCode: 5 }) return;
                    denied++;
                    if (!contained || !FirstChanceExceptionPolicy.IsExpectedProcessInspection(args.Exception))
                        logger.Warning(args.Exception, "[FirstChance:NATIVE] {Type}", args.Exception.GetType().FullName);
                }
                AppDomain.CurrentDomain.FirstChanceException += Count;
                long before = GC.GetAllocatedBytesForCurrentThread();
                long allocated;
                try
                {
                    if (contained) Assert.Equal(0, job.TryTerminate());
                    else root.Kill(entireProcessTree: true); // baseline, ONLY this disposable fixture
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                }
                finally { AppDomain.CurrentDomain.FirstChanceException -= Count; }
                TestContext.Current.TestOutputHelper!.WriteLine(
                    $"sample={sample} contained={contained} denied={denied} allocated={allocated} logBytes={sink.Bytes}");
                if (contained) { Assert.Equal(0, denied); Assert.Equal(0, sink.Bytes); }
            }
        }
    }

    private sealed class CountingSink : ILogEventSink
    {
        public long Bytes;
        public void Emit(LogEvent logEvent) => Bytes += Encoding.UTF8.GetByteCount(
            logEvent.RenderMessage() + Environment.NewLine + logEvent.Exception);
    }

    [Fact]
    public async Task LoggingFilterRecognizesRuntimeInspectionButNotArbitraryAccessDenial()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows process inspection");
        using var job = new WindowsProcessJob();
        using var root = await Start(job, 0, 0, "tree");
        bool? firstInspectionFiltered = null;
        string? firstStack = null;
        int measuringThread = Environment.CurrentManagedThreadId;
        void Inspect(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (Environment.CurrentManagedThreadId == measuringThread && firstInspectionFiltered is null && args.Exception is Win32Exception { NativeErrorCode: 5 })
            {
                firstInspectionFiltered = FirstChanceExceptionPolicy.IsExpectedProcessInspection(args.Exception);
                firstStack = new StackTrace(false).ToString();
            }
        }
        AppDomain.CurrentDomain.FirstChanceException += Inspect;
        try { root.Kill(entireProcessTree: true); } // Legacy runtime probe on this fixture only.
        finally { AppDomain.CurrentDomain.FirstChanceException -= Inspect; }
        // Administrators may be able to inspect every process, producing no denied probes.
        if (firstInspectionFiltered is not null) Assert.True(firstInspectionFiltered, firstStack);
        Assert.False(FirstChanceExceptionPolicy.IsExpectedProcessInspection(new Win32Exception(5)));
    }

    private int[] Pids() => Directory.GetFiles(directory, "*.pid").Select(path => int.Parse(Path.GetFileNameWithoutExtension(path))).ToArray();
    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { } // Process object already released by Windows.
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }
    private async Task<Process> Start(WindowsProcessJob job, int width, int depth, string mode, string? dir = null)
    {
        var process = CreateProcess(width, depth, mode, dir ?? directory);
        process.Start();
        await process.StandardInput.WriteLineAsync(job.DuplicateInto(process.SafeHandle).ToString(CultureInfo.InvariantCulture));
        process.StandardInput.Close();
        Assert.Equal("terminal-job-enrolled", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        return process;
    }
    private static Process CreateProcess(int width, int depth, string mode, string dir)
    {
        Directory.CreateDirectory(dir);
        var info = new ProcessStartInfo(GetHelper())
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { dir, width.ToString(), depth.ToString(), mode }) info.ArgumentList.Add(arg);
        info.Environment[WindowsJobBootstrap.EnvironmentVariable] = "1";
        return new Process { StartInfo = info };
    }
    private static string GetHelper()
    {
        if (helper is not null) return helper;
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Pty.Net", "Pty.Net.csproj"))) root = root.Parent;
        var project = Path.Combine(root!.FullName, "Tests", "headless", "WindowsProcessTree", "WindowsProcessTree.csproj");
        var output = Path.Combine(root.FullName, "artifacts", "windows-process-tree");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "build", project, "-o", output, "-v:q" }) info.ArgumentList.Add(arg);
        using var build = Process.Start(info)!;
        var stdout = build.StandardOutput.ReadToEndAsync();
        var stderr = build.StandardError.ReadToEndAsync();
        Assert.True(build.WaitForExit(60000), "helper build timed out");
        Assert.True(build.ExitCode == 0, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
        return helper = Path.Combine(output, "WindowsProcessTree.exe");
    }
    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }
}

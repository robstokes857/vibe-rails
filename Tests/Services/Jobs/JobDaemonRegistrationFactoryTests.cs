using System.Security.AccessControl;
using System.Security.Principal;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobDaemonRegistrationFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"vb-daemon-registration-{Guid.NewGuid():N}");
    private readonly string _stableRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".viberails-registration-tests",
        Guid.NewGuid().ToString("N"));

    public JobDaemonRegistrationFactoryTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_stableRoot);
    }

    [Fact]
    public void ResolveStableCommand_PrefersTheStableNativeExecutable()
    {
        var nativePath = Path.Combine(_root, "vb.exe");
        File.WriteAllText(nativePath, string.Empty);
        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var temporaryProcessPath = Path.Combine(_root, "installer-stage", "vb.exe");

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: true,
            temporaryProcessPath);

        var registration = Assert.IsType<VibeRails.Daemon.DaemonRegistration>(result.Registration);
        Assert.Equal(Path.GetFullPath(nativePath), registration.ExecutablePath);
        Assert.Equal([JobDaemonRegistrationFactory.ProcessArgument], registration.Arguments);
        Assert.Equal(Path.GetFullPath(_root), registration.WorkingDirectory);
        Assert.Equal(Path.GetFullPath(_root), registration.DataDirectory);
        Assert.Equal(Path.GetFullPath(nativePath), result.LaunchTargetPath);
        Assert.True(result.LaunchTargetExists);
        Assert.Null(result.Error);
        Assert.NotEqual(Path.GetFullPath(temporaryProcessPath), registration.ExecutablePath);
    }

    [Fact]
    public void ResolveStableCommand_UsesAnAbsoluteDotnetHostForAStableManagedInstall()
    {
        var managedPath = Path.Combine(_root, "vb.dll");
        File.WriteAllText(managedPath, string.Empty);
        var dotnetHostPath = CreateStableDotnetHost();

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: OperatingSystem.IsWindows(),
            currentProcessPath: Path.Combine(_root, "installer-stage", "vb.dll"),
            configuredDotnetHostPath: dotnetHostPath);

        var registration = Assert.IsType<VibeRails.Daemon.DaemonRegistration>(result.Registration);
        Assert.Equal(Path.GetFullPath(dotnetHostPath), registration.ExecutablePath);
        Assert.Equal(
            [Path.GetFullPath(managedPath), JobDaemonRegistrationFactory.ProcessArgument],
            registration.Arguments);
        Assert.Equal(Path.GetFullPath(managedPath), result.LaunchTargetPath);
        Assert.True(result.LaunchTargetExists);
        Assert.Null(result.Error);
    }

    [Fact]
    public void ResolveStableCommand_RejectsADotnetHostInTheTemporaryDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var dotnetHostPath = Path.Combine(
            _root,
            "runtime",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Directory.CreateDirectory(Path.GetDirectoryName(dotnetHostPath)!);
        File.WriteAllText(dotnetHostPath, string.Empty);

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: OperatingSystem.IsWindows(),
            currentProcessPath: null,
            configuredDotnetHostPath: dotnetHostPath);

        Assert.Null(result.Registration);
        Assert.Contains("temporary or staging", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_RejectsADotnetHostInAStagingDirectory()
    {
        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var dotnetHostPath = CreateStableDotnetHost("installer-stage");

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: OperatingSystem.IsWindows(),
            currentProcessPath: null,
            configuredDotnetHostPath: dotnetHostPath);

        Assert.Null(result.Registration);
        Assert.Contains("temporary or staging", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_RejectsAMissingDotnetHost()
    {
        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var dotnetHostPath = Path.Combine(
            _stableRoot,
            "missing",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: OperatingSystem.IsWindows(),
            currentProcessPath: null,
            configuredDotnetHostPath: dotnetHostPath);

        Assert.Null(result.Registration);
        Assert.Contains("does not exist", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_RejectsASymbolicLinkDotnetHost()
    {
        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var targetPath = CreateStableDotnetHost("link-target");
        var linkPath = Path.Combine(
            _stableRoot,
            "linked-runtime",
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or PlatformNotSupportedException ||
            (OperatingSystem.IsWindows() && ex is IOException))
        {
            return;
        }

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: OperatingSystem.IsWindows(),
            currentProcessPath: null,
            configuredDotnetHostPath: linkPath);

        Assert.Null(result.Registration);
        Assert.Contains("symbolic link or reparse point", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_RejectsAUnixDotnetHostWritableByOtherUsers()
    {
        if (OperatingSystem.IsWindows())
            return;

        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var dotnetHostPath = CreateStableDotnetHost();
        File.SetUnixFileMode(
            dotnetHostPath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupWrite);

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: false,
            currentProcessPath: null,
            configuredDotnetHostPath: dotnetHostPath);

        Assert.Null(result.Registration);
        Assert.Contains("writable by other users", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_RejectsAWindowsDotnetHostWritableByEveryone()
    {
        if (!OperatingSystem.IsWindows())
            return;

        File.WriteAllText(Path.Combine(_root, "vb.dll"), string.Empty);
        var dotnetHostPath = CreateStableDotnetHost();
        var fileInfo = new FileInfo(dotnetHostPath);
        var security = FileSystemAclExtensions.GetAccessControl(fileInfo);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.WriteData,
            AccessControlType.Allow));
        FileSystemAclExtensions.SetAccessControl(fileInfo, security);

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: true,
            currentProcessPath: null,
            configuredDotnetHostPath: dotnetHostPath);

        Assert.Null(result.Registration);
        Assert.Contains("write access to an untrusted principal", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveStableCommand_DoesNotPersistATemporaryOrVersionedProcessPath()
    {
        var temporaryProcessPath = Path.Combine(
            _root,
            "vscode-extension",
            "versions",
            "1.9.23",
            "vb.exe");

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: true,
            temporaryProcessPath);

        var registration = Assert.IsType<VibeRails.Daemon.DaemonRegistration>(result.Registration);
        var stablePath = Path.Combine(_root, "vb.exe");
        Assert.Equal(Path.GetFullPath(stablePath), registration.ExecutablePath);
        Assert.Equal(Path.GetFullPath(stablePath), result.LaunchTargetPath);
        Assert.False(result.LaunchTargetExists);
        Assert.NotEqual(Path.GetFullPath(temporaryProcessPath), registration.ExecutablePath);
    }

    [Fact]
    public void ResolveStableCommand_RejectsManagedSelfExecutionWithoutAnAbsoluteDotnetHost()
    {
        var managedPath = Path.Combine(_root, "vb.dll");
        File.WriteAllText(managedPath, string.Empty);

        var result = JobDaemonRegistrationFactory.ResolveStableCommand(
            _root,
            windows: true,
            currentProcessPath: Path.Combine(_root, "installer-stage", "vb.dll"));

        Assert.Null(result.Registration);
        Assert.Equal(Path.GetFullPath(managedPath), result.LaunchTargetPath);
        Assert.False(result.LaunchTargetExists);
        Assert.Contains("absolute dotnet host", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a leftover temporary fixture is not worth failing the test run */ }
        try { Directory.Delete(_stableRoot, recursive: true); }
        catch { /* a leftover user-local fixture is not worth failing the test run */ }
    }

    private string CreateStableDotnetHost(string directoryName = "runtime")
    {
        var dotnetHostPath = Path.Combine(
            _stableRoot,
            directoryName,
            OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        Directory.CreateDirectory(Path.GetDirectoryName(dotnetHostPath)!);
        File.WriteAllText(dotnetHostPath, string.Empty);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                dotnetHostPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return dotnetHostPath;
    }
}

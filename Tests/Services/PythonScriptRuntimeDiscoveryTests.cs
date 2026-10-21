using VibeRails.Services.PythonScripts;
using Xunit;

namespace Tests.Services;

[Collection("ProcessEnvIsolation")]
public sealed class PythonScriptRuntimeDiscoveryTests
{
    [Fact]
    public void ApprovedScriptsIgnoreRepositoryInterpreterOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibe182-python-" + Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "installed", "scripts");
        var trusted = Path.Combine(scripts, ".venv");
        var repository = Path.Combine(root, "repository", ".venv");
        string Prepare(string directory)
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "pyvenv.cfg"), "home = fixture");
            var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "Scripts" : "bin", OperatingSystem.IsWindows() ? "python.exe" : "python");
            Directory.CreateDirectory(Path.GetDirectoryName(executable)!);
            File.WriteAllText(executable, "fixture, never executed");
            return executable;
        }
        var originalOverride = Environment.GetEnvironmentVariable("PYBRIDGE_PYTHON");
        var originalVenv = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
        try
        {
            var expected = Prepare(trusted);
            Environment.SetEnvironmentVariable("PYBRIDGE_PYTHON", Prepare(repository));
            Environment.SetEnvironmentVariable("VIRTUAL_ENV", repository);
            var options = PythonScriptService.DiscoverPythonRunnerOptions(scripts);
            Assert.Equal(expected, options.PythonExecutable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYBRIDGE_PYTHON", originalOverride);
            Environment.SetEnvironmentVariable("VIRTUAL_ENV", originalVenv);
            Directory.Delete(root, true);
        }
    }
}

using System.Globalization;

namespace Pty.Net.Windows;

/// <summary>A tab host must cross this barrier before it initializes any application services.</summary>
public static class WindowsJobBootstrap
{
    /// <summary>Private parent/child protocol marker; removed before descendants can inherit it.</summary>
    public const string EnvironmentVariable = "VIBERAILS_WINDOWS_JOB_BOOTSTRAP";

    /// <summary>Joins the job passed on redirected stdin, then closes the duplicate before acknowledging.</summary>
    public static async Task EnrollAsync()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable(EnvironmentVariable) != "1") return;
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        // Console's synchronized TextReader can implement ReadLineAsync synchronously. Keep the
        // deadline effective even when a parent is alive but never completes its side of launch.
        var line = await Task.Run(() => Console.In.ReadLine()).WaitAsync(TimeSpan.FromSeconds(15));
        if (!long.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new InvalidOperationException("Missing terminal job enrollment handle");
        WindowsProcessJob.JoinBootstrapJob(value);
        Console.WriteLine("terminal-job-enrolled");
        await Console.Out.FlushAsync();
    }
}

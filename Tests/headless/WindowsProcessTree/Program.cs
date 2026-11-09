using System.Diagnostics;
using Pty.Net;
using Pty.Net.Windows;

// Disposable fixture: no application host, database, network, or provider CLI.
try { await WindowsJobBootstrap.EnrollAsync(); }
catch { return 2; }
string directory = args[0];
int width = int.Parse(args[1]), depth = int.Parse(args[2]);
string mode = args[3];
File.WriteAllText(Path.Combine(directory, $"{Environment.ProcessId}.pid"), mode);

Process StartChild(int childDepth, string childMode)
{
    var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var arg in new[] { directory, width.ToString(), childDepth.ToString(), childMode }) info.ArgumentList.Add(arg);
    return Process.Start(info)!;
}

if (mode == "owner")
{
    using var job = new WindowsProcessJob();
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true
    };
    foreach (var arg in new[] { directory, "2", "1", "tree" }) info.ArgumentList.Add(arg);
    info.Environment[WindowsJobBootstrap.EnvironmentVariable] = "1";
    using var child = Process.Start(info)!;
    await child.StandardInput.WriteLineAsync(job.DuplicateInto(child.SafeHandle).ToString());
    child.StandardInput.Close();
    await child.StandardOutput.ReadLineAsync();
    await Task.Delay(TimeSpan.FromSeconds(90));
    return 0;
}
if (mode == "nested")
{
    using var pty = await PtyProvider.SpawnAsync(new PtyOptions
    {
        App = Environment.ProcessPath!, CommandLine = [directory, "2", "1", "tree"],
        Cwd = directory, Cols = 80, Rows = 24
    }, CancellationToken.None);
    _ = Task.Run(async () => { try { await pty.ReaderStream.CopyToAsync(Stream.Null); } catch { } });
    File.WriteAllText(Path.Combine(directory, "inner"), pty.Pid.ToString());
    await Task.Delay(2000);
    pty.KillProcessTree();
    pty.WaitForExit(5000);
    File.WriteAllText(Path.Combine(directory, "stopped"), "yes");
}
else if (depth > 0)
{
    for (int i = 0; i < width; i++) StartChild(depth - 1, "tree").Dispose();
}
if (mode == "exit") { await Task.Delay(1000); return 7; }
if (mode == "spawning")
{
    for (int i = 0; i < 200; i++) { StartChild(0, "tree").Dispose(); await Task.Delay(30); }
}
await Task.Delay(TimeSpan.FromSeconds(90));
return 0;

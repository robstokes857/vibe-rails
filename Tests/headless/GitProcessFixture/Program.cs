using System.Diagnostics;
using System.Reflection;
using VibeRails.Services.Git;

// Real child processes for GitCommandRunnerTests. No shell, global environment changes or
// user database. PID files let tests verify cleanup even when output is intentionally blocked.
if (args.Length > 1) File.WriteAllText(args[1], Environment.ProcessId.ToString());
switch (args[0])
{
    case "pending-stdin":
        var pending = Console.OpenStandardInput().ReadAsync(new byte[1]).AsTask();
        if (pending.IsCompleted) throw new Exception("The parent's stdin must remain open.");
        var hash = await GitCli.RunAsync("", ["hash-object", "--stdin"], CancellationToken.None);
        Console.Write(hash.StdOut);
        return hash.Succeeded && !pending.IsCompleted ? 0 : 1;
    case "tree":
    case "hold-output":
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("sleep");
        start.ArgumentList.Add(args[1] + ".child");
        using (var child = Process.Start(start)!)
        {
            while (!File.Exists(args[1] + ".child")) await Task.Delay(10);
            Console.Write("ready");
            Console.Error.Write("error-ready");
            if (args[0] == "hold-output") return 0;
            await child.WaitForExitAsync();
        }
        return 0;
    case "flood":
        var bytes = new byte[8192];
        Array.Fill(bytes, (byte)'x');
        var output = Console.OpenStandardOutput();
        while (true) await output.WriteAsync(bytes);
    case "sleep":
        await Task.Delay(TimeSpan.FromMinutes(1));
        return 0;
    case "text":
        await Console.OpenStandardOutput().WriteAsync("é漢🙂\0end\r\n"u8.ToArray());
        await Console.OpenStandardError().WriteAsync("  error\r\n"u8.ToArray());
        return 7;
    case "stderr-only":
        await Console.OpenStandardError().WriteAsync("diagnostics"u8.ToArray());
        return 7;
    default:
        throw new ArgumentException("Unknown fixture mode.");
}

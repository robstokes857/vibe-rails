using System.Diagnostics;

namespace VibeRails.Utils;

public static class LaunchBrowser
{
    public static void Launch(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.WriteLine("No URL provided.");
            return;
        }

        var uri = ParseWebUrl(url);
        if (uri is null)
        {
            Console.WriteLine($"Invalid URL: {url}");
            return;
        }

        var finalUrl = uri.AbsoluteUri;
        var startInfo = CreateStartInfo(uri, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), OperatingSystem.IsLinux());
        if (startInfo is null)
        {
            Console.WriteLine($"Unsupported OS. Please open: {finalUrl}");
            return;
        }

        try
        {
            Process.Start(startInfo);
            Console.WriteLine($"Opening browser at {finalUrl}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not automatically open browser: {ex.Message}");
            Console.WriteLine($"Please open your browser manually and navigate to: {finalUrl}");
        }
    }

    /// <summary>An absolute http or https URL (https assumed when the scheme is missing), or null.</summary>
    internal static Uri? ParseWebUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && !Uri.TryCreate("https://" + url, UriKind.Absolute, out uri))
            return null;
        // Only a web page is ever opened: on Windows the value is handed to the OS shell.
        return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    }

    /// <summary>
    /// How the default browser is opened on each OS, or null for an unsupported one. The URL
    /// travels in its escaped form as one value: no command line is built from it, and no program
    /// is named without its directory (a bare name is looked up in the current directory, the
    /// project, before PATH). VIBE-310.
    /// </summary>
    internal static ProcessStartInfo? CreateStartInfo(Uri uri, bool isWindows, bool isMacOS, bool isLinux)
    {
        var url = uri.AbsoluteUri;
        if (isWindows)
        {
            // The shell opens the default browser itself. cmd.exe's "start" would re-parse the URL.
            return new ProcessStartInfo { FileName = url, UseShellExecute = true };
        }

        string? opener = isMacOS ? "/usr/bin/open" : isLinux ? PathExecutable.Resolve("xdg-open") : null;
        if (opener is null)
            return null;
        var startInfo = new ProcessStartInfo { FileName = opener, UseShellExecute = false };
        startInfo.ArgumentList.Add(url);
        return startInfo;
    }
}

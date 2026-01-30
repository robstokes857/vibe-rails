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

        // Normalize and validate
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // Try assuming https
            if (!Uri.TryCreate("https://" + url, UriKind.Absolute, out uri))
            {
                Console.WriteLine($"Invalid URL: {url}");
                return;
            }
        }

        var finalUrl = uri.ToString();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // start "" "url"  (empty title is important)
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd",
                    Arguments = $"/c start \"\" \"{finalUrl}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = finalUrl,
                    UseShellExecute = false
                });
            }
            else if (OperatingSystem.IsLinux())
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "xdg-open",
                    Arguments = finalUrl,
                    UseShellExecute = false
                });
            }
            else
            {
                Console.WriteLine($"Unsupported OS. Please open: {finalUrl}");
                return;
            }

            Console.WriteLine($"Opening browser at {finalUrl}...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not automatically open browser: {ex.Message}");
            Console.WriteLine($"Please open your browser manually and navigate to: {finalUrl}");
        }
    }
}

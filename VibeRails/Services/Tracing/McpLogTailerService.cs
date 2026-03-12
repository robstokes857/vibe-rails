using VibeRails.Utils;

namespace VibeRails.Services.Tracing;

/// <summary>
/// Background service that tails the MCP server log file and forwards new lines
/// to the trace buffer as LogEntry events with source "MCP-Server".
/// MCP_Server.exe is a separate stdio process that cannot write to the main
/// app's Serilog buffer directly, so we tail its log file instead.
/// </summary>
public sealed class McpLogTailerService : BackgroundService
{
    private const int InitialTailBytes = 32 * 1024;

    private readonly TraceEventBuffer _buffer;
    private readonly string _logDir;

    private static readonly System.Text.RegularExpressions.Regex s_linePattern =
        new(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(\w+)\] (.+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    public McpLogTailerService(TraceEventBuffer buffer)
    {
        _buffer = buffer;
        _logDir = Path.Combine(
            PathConstants.GetInstallDirPath(),
            PathConstants.LOG_SUBDIR,
            PathConstants.MCP_LOG_SUBDIR);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var logFile = GetLatestLogFile();
            if (logFile == null)
            {
                await Task.Delay(5000, stoppingToken).ConfigureAwait(false);
                continue;
            }

            await TailFile(logFile, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task TailFile(string path, CancellationToken stoppingToken)
    {
        // Start near the end so newly opened trace sessions see recent MCP startup/activity.
        long position;
        try
        {
            var length = new FileInfo(path).Length;
            position = Math.Max(0, length - InitialTailBytes);
        }
        catch { position = 0; }

        string? pendingLine = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            // If a newer file appeared (daily rotation), let the outer loop pick it up
            var latest = GetLatestLogFile();
            if (latest != null && latest != path)
                return;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(position, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);

                string? line;
                while ((line = await reader.ReadLineAsync(stoppingToken)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        FlushPending(ref pendingLine);
                        continue;
                    }

                    if (s_linePattern.IsMatch(line))
                    {
                        // New timestamped entry — flush any accumulated continuation first
                        FlushPending(ref pendingLine);
                        pendingLine = line;
                    }
                    else if (pendingLine != null)
                    {
                        // Exception/stack-trace continuation — append to current entry
                        pendingLine += "\n" + line;
                    }
                    // else: orphaned continuation with no leading timestamp, skip
                }

                FlushPending(ref pendingLine);
                position = fs.Position;
            }
            catch (FileNotFoundException) { return; }
            catch (OperationCanceledException) { return; }
            catch { /* file briefly locked — retry on next iteration */ }

            try { await Task.Delay(500, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void FlushPending(ref string? pendingLine)
    {
        if (pendingLine == null) return;
        Emit(pendingLine);
        pendingLine = null;
    }

    private void Emit(string raw)
    {
        var m = s_linePattern.Match(raw);
        string level, message;

        if (m.Success)
        {
            level = m.Groups[1].Value.ToUpperInvariant();
            // First line's message + any continuation lines after the first newline
            var firstLine = m.Groups[2].Value;
            var nl = raw.IndexOf('\n');
            message = nl >= 0 ? firstLine + raw[nl..] : firstLine;
        }
        else
        {
            level = "INF";
            message = raw;
        }

        var tag = level switch
        {
            "ERR" or "FTL" => "ERR",
            "WRN"          => "WRN",
            "DBG" or "VRB" => "DBG",
            _              => "INF"
        };

        _buffer.Add(TraceEvent.Create(
            TraceEventType.LogEntry,
            "MCP-Server",
            $"[{tag}] MCP-Server: {message}"));
    }

    private string? GetLatestLogFile()
    {
        if (!Directory.Exists(_logDir)) return null;
        return Directory.EnumerateFiles(_logDir, "mcp-server*.log")
            .OrderByDescending(f => f)
            .FirstOrDefault();
    }
}

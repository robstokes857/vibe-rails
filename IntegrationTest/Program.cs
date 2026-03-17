// Integration test tool for the TerminalEmulator.
//
// Commands:
//   (default)  replay  — replay a session live to the console with timing
//   export             — export session BLOBs from state.db to .bin fixture files
//   snapshot           — render every session's final screen to a .txt golden file
//   render             — render a session's final screen instantly (no timing)
//
// Replay:
//   dotnet run --project IntegrationTest
//   dotnet run --project IntegrationTest -- --session 901f5c53
//   dotnet run --project IntegrationTest -- --speed 20
//   dotnet run --project IntegrationTest -- --speed 0
//   dotnet run --project IntegrationTest -- --chunks 500
//   dotnet run --project IntegrationTest -- --db path/to/state.db
//
// Export:
//   dotnet run --project IntegrationTest -- export              (top 10)
//   dotnet run --project IntegrationTest -- export --max 0      (all sessions)
//   dotnet run --project IntegrationTest -- export --max 50
//
// Snapshot (generates golden files for SnapshotTests):
//   dotnet run --project IntegrationTest -- snapshot
//   dotnet run --project IntegrationTest -- snapshot --max 0    (all sessions)
//
// Render (quick visual check, no timing):
//   dotnet run --project IntegrationTest -- render
//   dotnet run --project IntegrationTest -- render --session 901f5c53

using Microsoft.Data.Sqlite;
using System.Text;
using TerminalEmulator;

string solutionRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
string defaultDb    = Path.Combine(solutionRoot, "TerminalEmulator.Tests", "state.db");

var rawArgs = Environment.GetCommandLineArgs();
string command = rawArgs.Skip(1).FirstOrDefault(a => !a.StartsWith('-')) ?? "replay";

return command switch
{
    "export"   => RunExport(),
    "snapshot" => RunSnapshot(),
    "render"   => RunRender(),
    _          => await RunReplay(),
};

// ── Export ────────────────────────────────────────────────────────────────────

int RunExport()
{
    string dbPath    = GetArg("--db",  defaultDb);
    string outputDir = GetArg("--out", Path.Combine(solutionRoot, "fixtures"));
    int maxSessions  = int.Parse(GetArg("--max", "10"));

    Directory.CreateDirectory(outputDir);
    Console.WriteLine($"Opening   {dbPath}");
    Console.WriteLine($"Output    {outputDir}");
    string limitStr = maxSessions == 0 ? "all" : $"top {maxSessions}";
    Console.WriteLine($"Exporting {limitStr} sessions by total bytes\n");

    foreach (var (sessionId, totalBytes, chunkCount) in GetSessions(dbPath, maxSessions))
    {
        Console.Write($"  {sessionId[..8]}... {chunkCount,6} chunks, {totalBytes / 1024,6} KB  ");
        long written = WriteFixture(dbPath, sessionId, Path.Combine(outputDir, $"{sessionId}.bin"));
        Console.WriteLine($"-> {written / 1024} KB written");
    }

    Console.WriteLine($"\nDone. Fixtures in {outputDir}");
    return 0;
}

// ── Snapshot ──────────────────────────────────────────────────────────────────

int RunSnapshot()
{
    string dbPath      = GetArg("--db",  defaultDb);
    string snapshotDir = GetArg("--out", Path.Combine(solutionRoot, "TerminalEmulator.Tests", "snapshots"));
    int maxSessions    = int.Parse(GetArg("--max", "0")); // default: all

    Directory.CreateDirectory(snapshotDir);
    Console.WriteLine($"Opening   {dbPath}");
    Console.WriteLine($"Snapshots {snapshotDir}\n");

    int count = 0;
    foreach (var (sessionId, totalBytes, chunkCount) in GetSessions(dbPath, maxSessions))
    {
        Console.Write($"  {sessionId[..8]}... {chunkCount,6} chunks  ");
        var terminal = ReplayFromDb(dbPath, sessionId);
        string outFile = Path.Combine(snapshotDir, $"{sessionId}.txt");
        WriteSnapshot(terminal, outFile);
        Console.WriteLine($"-> {terminal.Cols}x{terminal.Rows}  cursor ({terminal.CursorCol},{terminal.CursorRow})");
        count++;
    }

    Console.WriteLine($"\nDone. {count} snapshots written to {snapshotDir}");
    return 0;
}

// ── Render ────────────────────────────────────────────────────────────────────

int RunRender()
{
    string dbPath      = GetArg("--db",      defaultDb);
    string sessionHint = GetArg("--session", "");

    string sessionId = ResolveSession(dbPath, sessionHint);

    int cols = Console.WindowWidth;
    int rows = Console.WindowHeight - 2;
    var terminal = ReplayFromDb(dbPath, sessionId, cols, rows);

    Console.OutputEncoding = Encoding.UTF8;
    Console.Clear();

    var snap = terminal.GetSnapshot();
    for (int r = 0; r < terminal.Rows; r++)
        RenderRow(terminal, 0, snap, r);

    Console.SetCursorPosition(0, terminal.Rows);
    Console.WriteLine($"\n[{sessionId[..8]}]  {terminal.Cols}x{terminal.Rows}  cursor ({terminal.CursorCol},{terminal.CursorRow})  {(terminal.IsAlternateScreen ? "ALT" : "NRM")}  — press any key");
    Console.ReadKey(intercept: true);
    return 0;
}

// ── Replay ────────────────────────────────────────────────────────────────────

async Task<int> RunReplay()
{
    string dbPath      = GetArg("--db",      defaultDb);
    string sessionHint = GetArg("--session", "");
    int    maxSessions = int.Parse(GetArg("--max", "0")); // 0 = all recent sessions

    double[] speedLevels = { 1.0, 3.0, 5.0, 10.0, 20.0 };
    var state = new ReplayState(); // SpeedIdx=0 → 1x (normal speed)

    int cols = Console.WindowWidth;
    int rows = Console.WindowHeight - 2;

    Console.OutputEncoding = Encoding.UTF8;
    Console.CursorVisible = false;
    Console.Clear();

    var appCts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; appCts.Cancel(); };

    // Background key reader — intercepts keys so they don't bleed into the fake terminal
    _ = Task.Run(async () =>
    {
        while (!appCts.Token.IsCancellationRequested)
        {
            if (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Enter:
                        state.Skip = true;
                        break;
                    case ConsoleKey.OemPlus:
                    case ConsoleKey.Add:
                        state.SpeedIdx = Math.Min(state.SpeedIdx + 1, speedLevels.Length - 1);
                        break;
                    case ConsoleKey.OemMinus:
                    case ConsoleKey.Subtract:
                        state.SpeedIdx = Math.Max(state.SpeedIdx - 1, 0);
                        break;
                }
            }
            try { await Task.Delay(30, appCts.Token); }
            catch (OperationCanceledException) { }
        }
    });

    const int STATUS_ROW      = 0;
    const int terminalOriginRow = 1;

    var sessions = GetRecentSessions(dbPath, maxSessions, sessionHint).ToList();
    if (sessions.Count == 0)
    {
        Console.WriteLine("No sessions found.");
        return 1;
    }

    foreach (var sessionId in sessions)
    {
        if (appCts.Token.IsCancellationRequested) break;
        state.Skip = false;

        var terminal = new Terminal(cols: cols, rows: rows);

        int chunkCount = 0, renderCount = 0;
        long totalBytes = 0;
        DateTime? sessionStart = null, lastChunkTime = null;

        terminal.OnRender += dirtyRows =>
        {
            renderCount++;
            var snap = terminal.GetSnapshot();
            foreach (var r in dirtyRows)
                RenderRow(terminal, terminalOriginRow, snap, r);

            int cr = terminalOriginRow + terminal.CursorRow;
            int cc = terminal.CursorCol;
            if (cr < Console.WindowHeight && cc < Console.WindowWidth)
                Console.SetCursorPosition(cc, cr);

            UpdateStatusBar(terminal, sessionId, chunkCount, renderCount, totalBytes,
                sessionStart, lastChunkTime, speedLevels[state.SpeedIdx]);
        };

        using var liveConn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        liveConn.Open();
        using var liveCmd = liveConn.CreateCommand();
        liveCmd.CommandText = "SELECT Timestamp, Content FROM SessionLogs WHERE SessionId = $sid ORDER BY Id";
        liveCmd.Parameters.AddWithValue("$sid", sessionId);

        using var reader = liveCmd.ExecuteReader();
        while (reader.Read() && !state.Skip && !appCts.Token.IsCancellationRequested)
        {
            var chunkTime = DateTime.Parse(reader.GetString(0), null, System.Globalization.DateTimeStyles.RoundtripKind);
            var bytes     = (byte[])reader.GetValue(1);

            if (lastChunkTime.HasValue)
            {
                double speed = speedLevels[state.SpeedIdx];
                var scaledMs = (int)((chunkTime - lastChunkTime.Value).TotalMilliseconds / speed);
                scaledMs = Math.Min(scaledMs, 500);
                if (scaledMs > 0)
                    await Task.Delay(scaledMs, appCts.Token).ContinueWith(_ => { });
            }

            sessionStart  ??= chunkTime;
            lastChunkTime   = chunkTime;
            totalBytes     += bytes.Length;
            chunkCount++;
            terminal.Write(bytes.AsSpan());
        }

        // Render the final state of this session
        var finalSnap = terminal.GetSnapshot();
        for (int r = 0; r < terminal.Rows; r++)
            RenderRow(terminal, terminalOriginRow, finalSnap, r);

        // Green status bar: wait for Enter before advancing
        var elapsed = lastChunkTime.HasValue && sessionStart.HasValue
            ? lastChunkTime.Value - sessionStart.Value : TimeSpan.Zero;

        state.Skip = false;
        while (!state.Skip && !appCts.Token.IsCancellationRequested)
        {
            Console.SetCursorPosition(0, STATUS_ROW);
            Console.BackgroundColor = ConsoleColor.DarkGreen;
            Console.ForegroundColor = ConsoleColor.White;
            double spd = speedLevels[state.SpeedIdx];
            string msg = $" Done [{sessionId[..8]}]  {chunkCount} chunks  {totalBytes / 1024} KB  {elapsed:hh\\:mm\\:ss}  +/-=speed({spd}x)  Enter=next";
            if (msg.Length > Console.WindowWidth - 1) msg = msg[..(Console.WindowWidth - 1)];
            Console.Write(msg.PadRight(Console.WindowWidth - 1));
            Console.ResetColor();
            try { await Task.Delay(100, appCts.Token); }
            catch (OperationCanceledException) { }
        }
    }

    appCts.Cancel();
    Console.CursorVisible = true;
    Console.SetCursorPosition(0, terminalOriginRow + rows);
    Console.WriteLine("\nAll sessions done. Press any key.");
    Console.ReadKey(intercept: true);
    return 0;
}

// ── Shared helpers ────────────────────────────────────────────────────────────

string GetArg(string name, string def)
{
    for (int i = 0; i < rawArgs.Length - 1; i++)
        if (rawArgs[i] == name) return rawArgs[i + 1];
    return def;
}

string ResolveSession(string dbPath, string hint)
{
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = string.IsNullOrEmpty(hint)
        ? "SELECT SessionId FROM SessionLogs GROUP BY SessionId ORDER BY SUM(length(Content)) DESC LIMIT 1"
        : "SELECT SessionId FROM SessionLogs WHERE SessionId LIKE $hint || '%' GROUP BY SessionId ORDER BY SUM(length(Content)) DESC LIMIT 1";
    if (!string.IsNullOrEmpty(hint))
        cmd.Parameters.AddWithValue("$hint", hint);
    var result = cmd.ExecuteScalar() as string;
    if (result is null) { Console.Error.WriteLine($"No session matching '{hint}'"); Environment.Exit(1); }
    return result!;
}

IEnumerable<string> GetRecentSessions(string dbPath, int max, string hint)
{
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    if (!string.IsNullOrEmpty(hint))
    {
        cmd.CommandText = "SELECT SessionId FROM SessionLogs WHERE SessionId LIKE $hint || '%' GROUP BY SessionId ORDER BY MAX(Timestamp) DESC LIMIT 1";
        cmd.Parameters.AddWithValue("$hint", hint);
    }
    else
    {
        cmd.CommandText = max > 0
            ? "SELECT SessionId FROM SessionLogs GROUP BY SessionId ORDER BY MAX(Timestamp) DESC LIMIT $lim"
            : "SELECT SessionId FROM SessionLogs GROUP BY SessionId ORDER BY MAX(Timestamp) DESC";
        if (max > 0) cmd.Parameters.AddWithValue("$lim", max);
    }
    using var reader = cmd.ExecuteReader();
    var results = new List<string>();
    while (reader.Read())
        results.Add(reader.GetString(0));
    return results;
}

IEnumerable<(string Id, long TotalBytes, int ChunkCount)> GetSessions(string dbPath, int max)
{
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = max == 0
        ? "SELECT SessionId, SUM(length(Content)) AS tb, COUNT(*) AS c FROM SessionLogs GROUP BY SessionId ORDER BY tb DESC"
        : "SELECT SessionId, SUM(length(Content)) AS tb, COUNT(*) AS c FROM SessionLogs GROUP BY SessionId ORDER BY tb DESC LIMIT $lim";
    if (max > 0) cmd.Parameters.AddWithValue("$lim", max);
    using var reader = cmd.ExecuteReader();
    var results = new List<(string, long, int)>();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt32(2)));
    return results;
}

long WriteFixture(string dbPath, string sessionId, string outFile)
{
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Content FROM SessionLogs WHERE SessionId = $sid ORDER BY Id";
    cmd.Parameters.AddWithValue("$sid", sessionId);
    using var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
    long written = 0;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var bytes = (byte[])reader.GetValue(0);
        fs.Write(bytes);
        written += bytes.Length;
    }
    return written;
}

Terminal ReplayFromDb(string dbPath, string sessionId, int cols = 150, int rows = 28)
{
    var terminal = new Terminal(cols: cols, rows: rows);
    using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Content FROM SessionLogs WHERE SessionId = $sid ORDER BY Id";
    cmd.Parameters.AddWithValue("$sid", sessionId);
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        terminal.Write(((byte[])reader.GetValue(0)).AsSpan());
    return terminal;
}

void WriteSnapshot(Terminal terminal, string outFile)
{
    var sb = new StringBuilder();
    sb.AppendLine($"COLS={terminal.Cols}");
    sb.AppendLine($"ROWS={terminal.Rows}");
    sb.AppendLine($"CURSOR={terminal.CursorCol},{terminal.CursorRow}");
    sb.AppendLine("---");
    foreach (var line in terminal.GetScreenText())
        sb.AppendLine(NormalizeSurrogates(line));
    File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
}

// Unpaired surrogates (from emoji stored as a single cell char) can't round-trip
// through UTF-8. Normalize them to the replacement character before file I/O.
static string NormalizeSurrogates(string s)
{
    if (!s.Any(char.IsSurrogate)) return s;
    var sb = new StringBuilder(s.Length);
    foreach (char c in s)
        sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
    return sb.ToString();
}

void RenderRow(Terminal terminal, int originRow, TerminalCell[,] snap, int row)
{
    int consoleRow = originRow + row;
    if (consoleRow >= Console.WindowHeight) return;

    Console.SetCursorPosition(0, consoleRow);
    var sb = new StringBuilder(terminal.Cols * 6);

    CellColor lastFg = default;
    CellColor lastBg = default;
    CellAttributes lastAttrs = CellAttributes.None;
    bool firstCell = true;

    for (int c = 0; c < terminal.Cols && c < Console.WindowWidth - 1; c++)
    {
        var cell = snap[row, c];
        if (cell.IsWideContinuation) continue;

        if (firstCell || cell.Fg != lastFg || cell.Bg != lastBg || cell.Attributes != lastAttrs)
        {
            sb.Append(BuildSgr(cell.Fg, cell.Bg, cell.Attributes, lastFg, lastBg, lastAttrs, firstCell));
            lastFg    = cell.Fg;
            lastBg    = cell.Bg;
            lastAttrs = cell.Attributes;
            firstCell = false;
        }

        char ch = cell.Char;
        if (ch == '\0' || ch < ' ') ch = ' ';
        sb.Append(ch);
    }

    sb.Append("\x1b[0m\x1b[K");
    Console.Write(sb.ToString());
}

void UpdateStatusBar(Terminal terminal, string sessionId, int chunks, int renders, long bytes, DateTime? start, DateTime? last, double speed)
{
    if (Console.WindowHeight < 2) return;
    Console.SetCursorPosition(0, 0);
    string t = start.HasValue && last.HasValue ? $"t={last.Value - start.Value:mm\\:ss}" : "t=--:--";
    string status = $" [{sessionId[..8]}]  chunk {chunks,6}  render {renders,6}  {bytes / 1024,6} KB  {t}  {speed}x  {terminal.Cols}x{terminal.Rows}  {(terminal.IsAlternateScreen ? "ALT" : "NRM")}";
    if (status.Length > Console.WindowWidth - 1) status = status[..(Console.WindowWidth - 1)];
    Console.BackgroundColor = ConsoleColor.DarkBlue;
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write(status.PadRight(Console.WindowWidth - 1));
    Console.ResetColor();
}

static string BuildSgr(CellColor fg, CellColor bg, CellAttributes attrs,
    CellColor prevFg, CellColor prevBg, CellAttributes prevAttrs, bool reset)
{
    var parts = new List<string>(8);
    if (reset) parts.Add("0");

    if (reset || attrs != prevAttrs)
    {
        if (attrs.HasFlag(CellAttributes.Bold))      parts.Add("1");
        if (attrs.HasFlag(CellAttributes.Dim))       parts.Add("2");
        if (attrs.HasFlag(CellAttributes.Italic))    parts.Add("3");
        if (attrs.HasFlag(CellAttributes.Underline)) parts.Add("4");
        if (attrs.HasFlag(CellAttributes.Inverse))   parts.Add("7");
    }

    if (reset || fg != prevFg)
    {
        if      (fg.IsDefault)                          parts.Add("39");
        else if (fg.IsPalette && fg.PaletteIndex < 8)   parts.Add((30 + fg.PaletteIndex).ToString());
        else if (fg.IsPalette && fg.PaletteIndex < 16)  parts.Add((90 + fg.PaletteIndex - 8).ToString());
        else if (fg.IsPalette)                          { parts.Add("38"); parts.Add("5"); parts.Add(fg.PaletteIndex.ToString()); }
        else if (fg.IsRgb)                              { parts.Add("38"); parts.Add("2"); parts.Add(fg.R.ToString()); parts.Add(fg.G.ToString()); parts.Add(fg.B.ToString()); }
    }

    if (reset || bg != prevBg)
    {
        if      (bg.IsDefault)                          parts.Add("49");
        else if (bg.IsPalette && bg.PaletteIndex < 8)   parts.Add((40 + bg.PaletteIndex).ToString());
        else if (bg.IsPalette && bg.PaletteIndex < 16)  parts.Add((100 + bg.PaletteIndex - 8).ToString());
        else if (bg.IsPalette)                          { parts.Add("48"); parts.Add("5"); parts.Add(bg.PaletteIndex.ToString()); }
        else if (bg.IsRgb)                              { parts.Add("48"); parts.Add("2"); parts.Add(bg.R.ToString()); parts.Add(bg.G.ToString()); parts.Add(bg.B.ToString()); }
    }

    if (parts.Count == 0) return string.Empty;
    return $"\x1b[{string.Join(";", parts)}m";
}

class ReplayState
{
    public volatile bool Skip;
    public volatile int SpeedIdx;
}

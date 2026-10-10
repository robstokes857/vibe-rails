namespace VibeRails.Data.Replay;

public record SessionInfo(string Id, string Cli, string Environment, string Directory, string Project, string Title, long Started, long? Ended, int? ExitCode, bool HasTerminal, int Changes);
public record SessionPage(List<SessionInfo> Items, int NextOffset);
public record BoardLink(string Key, string Title, string Selection);
public record Prompt(long Id, int Sequence, long At, string Text);
public record Change(long Id, long InputId, string Path, string Type, int? Added, int? Deleted, bool HasDiff, long At, string Timing);
public record Geometry(long Id, int Sequence, long At, int Cols, int Rows, long Bytes);
// TokensSaved estimates the net captured request reduction at four characters per token.
// Null means there are no attributed captures; zero means captured requests had no net savings.
public record Manifest(SessionInfo Session, List<BoardLink> Cards, List<Prompt> Prompts, List<Change> Changes, List<Geometry> Geometry, string FrameSource, long FrameMaxId, long ProxyMaxId, long FrameCount, long FrameBytes, long End, List<string> Notes, long? TokensSaved = null);
public record Frame(long Id, long At, byte[] Data, int Cols, int Rows);
public record FramePage(List<Frame> Items, long Next, bool Done);
public record ToolCall(string Id, string Name, string Arguments);
public record Exchange(long Cursor, string Id, long At, string Provider, string Method, string Path, int Status, int ElapsedMs, bool Truncated, string Model, string Effort, List<ToolCall> Tools, string? ParseNote);
public record ExchangePage(List<Exchange> Items, long Next, bool Done);
public record ExchangeDetail(string Id, string Before, string After, string Response, bool DisplayTruncated, bool CaptureTruncated);
public record DiffDetail(long Id, string Path, string? Diff);
public record SourceStatus(string Name, bool Available);
public record AppStatus(List<SourceStatus> Sources);
public record ApiError(string Error);

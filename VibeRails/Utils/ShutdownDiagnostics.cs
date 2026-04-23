namespace VibeRails.Utils;

public sealed record ShutdownReasonRecord(
    string Source,
    string Detail,
    DateTimeOffset RecordedUtc,
    int ProcessId);

public sealed record ShutdownDiagnosticsSnapshot(
    ShutdownReasonRecord? First,
    ShutdownReasonRecord? Last,
    int RecordCount);

public static class ShutdownDiagnostics
{
    private const int MaxFieldLength = 512;
    private static readonly Lock s_lock = new();
    private static ShutdownReasonRecord? s_first;
    private static ShutdownReasonRecord? s_last;
    private static int s_recordCount;

    public static void RecordStopRequest(string source, string detail)
    {
        var record = new ShutdownReasonRecord(
            Sanitize(source),
            Sanitize(detail),
            DateTimeOffset.UtcNow,
            Environment.ProcessId);

        lock (s_lock)
        {
            s_first ??= record;
            s_last = record;
            s_recordCount++;
        }
    }

    public static ShutdownDiagnosticsSnapshot GetSnapshot()
    {
        lock (s_lock)
        {
            return new ShutdownDiagnosticsSnapshot(s_first, s_last, s_recordCount);
        }
    }

    public static string FormatSnapshot(ShutdownDiagnosticsSnapshot snapshot)
    {
        static string FormatRecord(string label, ShutdownReasonRecord? record)
        {
            if (record == null)
            {
                return $"{label}=none";
            }

            return
                $"{label}={{source={record.Source}, detail={record.Detail}, recordedUtc={record.RecordedUtc:O}, pid={record.ProcessId}}}";
        }

        return $"{FormatRecord("first", snapshot.First)}; {FormatRecord("last", snapshot.Last)}; count={snapshot.RecordCount}";
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "n/a";
        }

        var trimmed = value.Trim();
        var chars = trimmed
            .Where(ch => !char.IsControl(ch))
            .Take(MaxFieldLength)
            .ToArray();

        return chars.Length == 0 ? "n/a" : new string(chars);
    }
}

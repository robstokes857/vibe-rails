# Internal tools and feature logs

Click the VibeRails logo **three times within 900 ms** to open **Internal tools**.
The modal has an extensible tab bar:

- **About** shows the running application's version.
- **Data uploads** shows retained upload attempts, their latest outcome, and the session ID
  or database snapshot involved. Open a row's details or **View logs** to follow that attempt.
  Create, Edit, and Delete are visibly disabled placeholders for future data management.
- **Logs** opens with existing **Application logs**. Use **Source** to switch to **VibeRails
  Demon logs** or the new **Feature journal**. Filter by category/feature, level, or text,
  then refresh or page through the results. Feature events also support status and operation
  ID filters; an upload's **View logs** opens the journal for that specific attempt.

Both session sharing and the legacy full-database export record new attempts automatically.
The full-database export uploads `state.db` and, when it exists, the `board.db` beside it as
two separate compressed snapshots; the reported SHA-256 is the `state.db` upload's.
`succeeded` means the upload completed; `failed`, `cancelled`, and `skipped` explain other
outcomes. `uploaded` with a warning means the server accepted a session but its local
confirmation could not be saved. An attempt left at `started` has no recorded final outcome
(for example, the process stopped); it is not proof that the server rejected the data.

Upload history starts with this version and only covers retained attempts. It does not backfill old
uploads or list every session waiting to be uploaded. An absent record does not establish
whether data was sent. Completed-session uploading is always on when an API key and export
URL are configured; opening this modal or recording a local event does not start an upload.

## Logging a new feature

Inject `IFeatureLog` from `VibeRails.Services.Diagnostics` and explicitly write small,
structured events. Use a stable feature name and reuse one operation ID through the action:

```csharp
public sealed class FeatureXService(IFeatureLog featureLog)
{
    public void Run()
    {
        var operationId = Guid.NewGuid().ToString("N");
        featureLog.Write("feature-x", "started", "Feature X started.",
            operationId: operationId, subject: "Item 42", status: "started");

        // Perform the action; record failures/cancellation at the appropriate boundary too.

        featureLog.Write("feature-x", "completed", "Feature X completed.",
            operationId: operationId, subject: "Item 42", status: "succeeded");
    }
}
```

`level` defaults to `LogLevel.Information`; pass `LogLevel.Warning` or `LogLevel.Error` for
problems. New feature names automatically appear in the Logs filter once an event is available.
Use safe, concise messages and identifiers: never include API keys, authentication headers,
session content, remote response bodies, or raw exception messages. The upload integration
records only fixed event messages and identifiers. Select **Feature journal** to see these
events. Existing diagnostic files are also readable through the other Sources below; terminal
transcripts remain in Chat History.

## Viewing existing diagnostic logs

The application and Demon sources read the existing Serilog files directly from
`~/.vibe_rails/logs/vb-*.log` and `vbd-*.log`, including entries written before this UI existed.
They do not copy, rewrite, or migrate the files, and do not change the existing log writers.
The file writer records Information and above, so a scheduler cycle that did work is visible on
disk. The leading category tag, such as `[Jobs]`, `[Startup]`, or `[DataExport]`, populates the feature
filter; messages without a tag appear under `general`. Severity filters recognize the existing
three-letter log levels. Details show the source filename and multiline exception text.
Existing timestamps are local wall-clock time; the reader converts them to UTC for ordering
and the UI displays them in your browser's local time.

Each source reads at most the newest seven matching files, the last 2 MiB of each file, and
10,000 events. Directory enumeration is limited to 1,024 entries, and individual messages are
capped at 16,384 characters. The viewer indicates when these limits omit older data or part
of a message. Source snapshots are cached for two seconds and loaded only when requested;
there are no file watchers or background scans. Malformed or unreadable files produce a visible
read-error count. Partial lines are skipped until the writer completes them. Only the fixed log
sources are accepted, with links/reparse points excluded.

Historical diagnostic messages do not reliably identify an upload attempt or its final outcome,
so they remain searchable logs rather than being converted into upload-history rows.

## Storage and performance

Events are local JSON Lines files under `~/.vibe_rails/logs/features/` (beside the configured
`state.db` directory). The writer enqueues without waiting for disk, uses a bounded queue of
1,024 events, and writes up to 128 events per background batch, coalesced over 100 ms. Files
rotate at 2 MiB with a target of eight retained segments; active files belonging to other
running backends are preserved.
Each process has its own filenames so multiple dashboards can write concurrently.

Feature-journal reads happen on demand and are bounded to the newest eight segments and 10,000 events, with
a two-second cache and paged responses, so a refresh can briefly show the previous state. The
modal loads its module and tab data only when needed and does not poll in the background.
Terminal child processes use a no-op logger and have no new
logging worker. The internal read APIs are authenticated and available only on root backends:
`GET /api/v1/internal/logs` and `GET /api/v1/internal/uploads`.

Logging is best effort: queue overflow or disk errors never block an upload. The viewer reports
how many events the current process dropped or could not write (counted per event, for the
process lifetime) and how many files or records could not be read in the snapshot being shown
(counted per read, so refreshing over the same damaged file does not inflate it). Normal shutdown
drains the queue within the host's shutdown budget; an interrupted shutdown reports the events it
abandons as dropped, while a crash loses queued events silently. Rotation removes old history, so
this is a troubleshooting journal rather than a permanent audit archive.

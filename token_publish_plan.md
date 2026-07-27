# Token Savings Publish Job — Plan

> Status: Draft for review (2026-07-27). Implementation TBD tomorrow.

## 1. Goal

Add a recurring background job that POSTs the user's cumulative lifetime
tokens-saved total to `https://viberails.ai/api/v1/token-savings` so VibeRails
can report aggregate savings across machines.

- **Trigger:** timer (the simple in-process `JobBase` system, not the durable
  `Services/Jobs/` Automations queue).
- **Payload:** absolute cumulative total (Int64), not a delta. Reposting
  upserts the existing record keyed by API key + computer name.
- **Auth:** `X-Api-Key` header from existing app config. Never in body or logs.
- **Failure isolation:** a failed post must never crash or slow the main app.

## 2. Where it fits

This uses the **`VibeRails/Jobs/`** system (`JobBase`), not the durable
`Services/Jobs/` queue. `JobBase` already gives us:

- `PeriodicTimer` with `Interval`
- `JobPriority` (defers `Low`/`Lowest`/`Med` when `ISystemResourceService.IsUnderPressure`)
- Per-tick exception isolation (unhandled exceptions are logged, not fatal)
- Serilog lifecycle logging (`[Job:{Name}] Tick begin/end`, deferred, etc.)

Reference jobs:
- `VibeRails/Jobs/UpdateCheckJob.cs` — closest template (Low priority, 30 min)
- `VibeRails/Jobs/JobBase.cs` — base class

## 3. Source of truth for the total

`ITokenSavingsStore.GetTotals()` returns a `TokenSavingsTotals` record
(`VibeRails/DB/TokenSavingsStore.cs:10`). Use `TokensSaved` (all-time,
`BytesSaved / 4`).

```csharp
var total = _store.GetTotals().TokensSaved;   // Int64
```

The store is a singleton registered at `MapRegisterServices.cs:95`. Its history
load is asynchronous and may not be complete on the first tick
(`TokenSavingsStore.cs:69` `_loaded` flag). See §9 for the early-tick caveat.

## 4. API key

Read via `ParserConfigs.GetApiKey()` (`VibeRails/Utils/ParserConfigs.cs:156`).
This is the same accessor every other VibeCodeRemote integration uses
(`SummaryService.cs:31`, `RemoteStateService.cs:50`, etc.).

- If the key is missing/whitespace → log a Debug line and **skip the tick**
  (mirror `SummaryService.cs:32-36`). Do not throw.
- Never log the key. Never put it in the JSON body. Only the `X-Api-Key` header.

## 5. Endpoint URL

- Default: `https://viberails.ai/api/v1/token-savings`
- Local testing override: `https://localhost:5164/api/v1/token-savings`

Add a new appsettings key (deployment-configurable, not user-facing):

```json
{
  "VibeRails": {
    "TokenSavingsPublish": {
      "Enabled": true,
      "EndpointUrl": "https://viberails.ai/api/v1/token-savings",
      "IntervalMinutes": 15
    }
  }
}
```

Read via `IConfiguration` in DI. If absent, fall back to the production URL and
`15`-minute interval. `Enabled=false` → the hosted service returns immediately
(see §8).

> Note: do **not** add this to `Settings` (`Utils/Config.cs`). That class is the
> user-mutable `settings.json` surface; this knob is operator/deployment-only
> and belongs in `appsettings.json` next to `VibeRails:FrontendUrl`.

## 6. New files

### 6.1 `VibeRails/Jobs/TokenSavingsPublishJob.cs`

A sealed class extending `JobBase`. Primary constructor injects:

- `ILogger<TokenSavingsPublishJob>`
- `ISystemResourceService` (required by `JobBase`)
- `ITokenSavingsStore` (for the total)
- `HttpClient` (typed client, see §7)
- `IConfiguration` (for `Enabled`, `EndpointUrl`, `IntervalMinutes`)

```csharp
public sealed class TokenSavingsPublishJob(
    ILogger<TokenSavingsPublishJob> logger,
    ISystemResourceService resources,
    ITokenSavingsStore store,
    HttpClient httpClient,
    IConfiguration configuration) : JobBase(logger, resources)
{
    private const string DefaultEndpoint = "https://viberails.ai/api/v1/token-savings";
    private const int DefaultIntervalMinutes = 15;

    private static readonly TimeSpan IntervalConfig =
        TimeSpan.FromMinutes(/* read from config, default 15 */);

    protected override TimeSpan Interval => /* from config */;
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        // 1. Read API key; if missing, log Debug and return.
        // 2. Read totals.TokensSaved.
        // 3. POST (see §7). Catch + log non-success; never throw out of ExecuteJob.
    }
}
```

### 6.2 DTO + serializer context (AOT-friendly)

The project supports Native AOT (per `AGENTS.md`). `JsonContent.Create(new { … })`
with an anonymous type is **not** AOT-safe. Define an explicit DTO and register
it in an existing `JsonSerializerContext` (or a small new one), mirroring
`SummaryService.cs:41-44` which uses `AppJsonSerializerContext.Default.SummaryPostDto`.

```csharp
// In DTOs/ (or co-located) — match existing DTO naming style.
public sealed class TokenSavingsPostDto
{
    public string ComputerName { get; init; } = "";
    public long TotalTokensSaved { get; init; }
}
```

Wire it into whatever `JsonSerializerContext` the rest of the
`VibeCodeRemote` DTOs use (look for `AppJsonSerializerContext` /
`SummaryPostDto`'s home and add `TokenSavingsPostDto` there).

## 7. The HTTP call

Follow `SummaryService.cs:39-49` verbatim in style:

```csharp
using System.Net.Http.Json;

var apiKey = ParserConfigs.GetApiKey();
if (string.IsNullOrWhiteSpace(apiKey))
{
    _logger.LogDebug("[TokenSavingsPublishJob] No API key configured; skipping tick.");
    return;
}

var total = _store.GetTotals().TokensSaved;

using var request = new HttpRequestMessage(
    HttpMethod.Post,
    _endpointUrl);  // from config; HttpClient.BaseAddress may be null → full URL here

request.Headers.Add("X-Api-Key", apiKey);
request.Content = JsonContent.Create(
    new TokenSavingsPostDto
    {
        ComputerName = Environment.MachineName,
        TotalTokensSaved = total
    },
    AppJsonSerializerContext.Default.TokenSavingsPostDto);

try
{
    using var response = await _httpClient.SendAsync(
        request,
        HttpCompletionOption.ResponseHeadersRead,
        cancellationToken);

    if (!response.IsSuccessStatusCode)
    {
        _logger.LogWarning(
            "[TokenSavingsPublishJob] Non-success status {Status} from {Endpoint}",
            (int)response.StatusCode,
            _endpointUrl);
        return;
    }

    _logger.LogInformation(
        "[TokenSavingsPublishJob] Published total={Total} computer={Computer}",
        total,
        Environment.MachineName);
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    throw;  // honor shutdown
}
catch (HttpRequestException ex)
{
    _logger.LogWarning(ex,
        "[TokenSavingsPublishJob] Request failed to {Endpoint}; will retry next tick",
        _endpointUrl);
}
```

Key choices:
- **No `EnsureSuccessStatusCode()`** at the boundary — `JobBase` would catch
  the throw, but a targeted warning with the status code is far more useful
  than an "Unhandled exception" stack.
- **`ResponseHeadersRead`** — we don't read the body; don't wait for it.
- **`OperationCanceledException`** re-thrown so shutdown is prompt.
- **API key never logged.** The `LogInformation` line omits it deliberately.

## 8. DI registration (`MapRegisterServices.cs`)

Two additions, both in the **root-backend branch** (the `else` at line 255
that already hosts `UpdateCheckJob` etc.). Terminal-tab children must NOT run
this job — they'd double-publish and waste the user's network.

```csharp
// Next to the other AddHttpClient calls (~line 285):
serviceCollection.AddHttpClient<TokenSavingsPublishJob>(client =>
{
    client.BaseAddress = null;                       // we send full URLs
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Inside the root-backend else-branch (~line 257):
serviceCollection.AddHostedService<TokenSavingsPublishJob>();
```

The `AddHttpClient<TJob>` overload makes a typed `HttpClient` injectable into
the job's primary constructor, identical to how `UpdateService` is wired
(`MapRegisterServices.cs:285`).

> **Enabled gate:** read `VibeRails:TokenSavingsPublish:Enabled` from
> `IConfiguration` at the top of `ExecuteAsync`. If false, log once and
> `return` (don't tick). Simplest implementation: override `ExecuteAsync` in
> the job to check the flag before delegating to `base.ExecuteAsync`, OR check
> the flag inside `ExecuteJob` and no-op. The latter is simpler and still
> honors the timer (one wasted tick per interval is negligible). Prefer the
> `ExecuteJob` no-op for simplicity unless we want zero-tick behavior.

## 9. Early-tick caveat (history not yet loaded)

`ITokenSavingsStore` loads persisted history asynchronously after construction
(`TokenSavingsStore.cs:96`, `EnsureHistoryLoadScheduled`). The very first tick
of this job may observe a session-only total that's lower than the true
cumulative number.

This is **acceptable** because the endpoint treats the value as an absolute
total and reposts upsert — the next tick will correct it within 15 minutes.

If we want to be precise, expose `TokenSavingsStore.HistoryLoaded`
(`TokenSavingsStore.cs:87`, currently `internal`) via the interface and skip
the tick until it's true. **Recommendation: don't bother** — the 15-min
self-correction is fine and the extra surface area isn't worth it. Flag as a
future refinement if telemetry shows the first-post dip matters.

## 10. Interval & priority

- **Interval:** 15 minutes default (configurable). The value is cumulative and
  repost upserts; sub-minute cadence would be noise. 15 min balances
  freshness vs. request volume.
- **Priority:** `JobPriority.Low` — same as `UpdateCheckJob`. Telemetry must
  never preempt real work; the `JobBase` pressure-deferral
  (`JobBase.cs:40`) will skip ticks when CPU/memory is high.

## 11. Security checklist

- [x] API key only in `X-Api-Key` header, never body
- [x] API key never logged (review the `LogInformation` line carefully)
- [x] `HttpClient` from DI (no manual `new HttpClient()` — socket exhaustion)
- [x] `CancellationToken` threaded through `SendAsync`
- [x] Endpoint overridable only via `appsettings.json` (operator trust level),
      not user-editable `settings.json`
- [ ] Confirm the local-test URL override is stripped from release builds or
      gated behind `#if DEBUG` if we don't want operators pointing at
      `localhost`. (Probably fine to leave configurable, but worth a decision.)

## 12. Tests to add

Mirror the style in `Tests/Services/SummaryServiceTests.cs` (which already
tests API-key-missing behavior for an analogous service).

Add `Tests/Jobs/TokenSavingsPublishJobTests.cs` covering:

1. **Skips tick when API key is missing** — assert no HTTP request sent.
2. **Posts correct payload** — fake `HttpMessageHandler` captures the request;
   assert `X-Api-Key` header, JSON body `computerName` and `totalTokensSaved`
   match `Environment.MachineName` and the store's `TokensSaved`.
3. **Non-success status does not throw** — handler returns 500; job logs
   warning and returns normally.
4. **`HttpRequestException` is swallowed** — handler throws; job logs and
   returns normally.
5. **`OperationCanceledException` propagates** — cancellation token signaled
   mid-send; job throws (so `JobBase` treats it as shutdown, not a failure).
6. **Reads total from `ITokenSavingsStore`** — use a fake store returning a
   known `TokenSavingsTotals`.
7. **Endpoint URL from config** — assert the request URI honors
   `VibeRails:TokenSavingsPublish:EndpointUrl`.
8. **`Enabled=false` no-ops** — assert no HTTP request sent.

`TokenSavingsStore` is already testable without DB by injecting a fake
`ITokenSavingsStore`. For the `HttpClient`, use `IHttpClientFactory` with a
capturing `HttpMessageHandler` in tests (standard .NET pattern).

## 13. Out of scope (explicitly)

- No retry-with-backoff within a tick. A failed post waits for the next tick.
  (The endpoint is append/upsert, so eventual consistency is fine.)
- No persistent "last successfully posted total" — every tick posts the
  current absolute total.
- No batching/multi-machine aggregation — this is per-machine.
- No UI surface. No `Settings` changes. No new API routes on our side.
- No change to `ITokenSavingsStore` interface (the `HistoryLoaded` exposure
  in §9 is explicitly deferred).

## 14. Implementation order (suggested)

1. Add `TokenSavingsPostDto` + register in serializer context.
2. Add `appsettings.json` section (`TokenSavingsPublish:Enabled/EndpointUrl/IntervalMinutes`).
3. Write `TokenSavingsPublishJob.cs` with the §7 HTTP call.
4. Wire DI: `AddHttpClient<TokenSavingsPublishJob>` + `AddHostedService<…>`
   in the root-backend branch.
5. Write tests (§12); run `dotnet test` from `Tests/`.
6. Manual local test: set `EndpointUrl` to `https://localhost:5164/...`,
   confirm a post lands; check Serilog output for the
   `[TokenSavingsPublishJob]` lifecycle lines.
7. Verify API key is absent from logs by grepping the log file after a tick.
8. Run `dotnet build` and the lint/typecheck step the project uses
   (`AGENTS.md` doesn't name one beyond `dotnet test` — confirm with user).

## 15. Open questions for tomorrow

- **Interval:** 15 min default OK, or do we want longer (30 min) to match
  `UpdateCheckJob`?
- **`Enabled` default:** ship on by default, or off until we're confident?
  (Recommendation: on, since the endpoint is idempotent and failures are
  silent.)
- **Should we expose `HistoryLoaded` and skip the first tick?** §9
  recommendation is no; confirm.
- **Local-test URL:** OK to leave `EndpointUrl` operator-configurable in
  `appsettings.json`, or gate `localhost` behind `#if DEBUG`? §11.
- **Is there a lint/typecheck command beyond `dotnet test`?** `AGENTS.md`
  doesn't specify; ask the user.

---

## Appendix A — Reference files

| What | Where |
|---|---|
| Job base (timer, priority, pressure-deferral) | `VibeRails/Jobs/JobBase.cs` |
| Closest job template | `VibeRails/Jobs/UpdateCheckJob.cs` |
| Token savings store (totals) | `VibeRails/DB/TokenSavingsStore.cs:10,49` |
| API key accessor | `VibeRails/Utils/ParserConfigs.cs:156` |
| HttpClient+X-Api-Key template | `VibeRails/Services/Integrations/VibeCodeRemote/SummaryService.cs:31-49` |
| DI: AddHttpClient pattern | `VibeRails/MapRegisterServices.cs:44,285` |
| DI: AddHostedService root-backend branch | `VibeRails/MapRegisterServices.cs:255-262` |
| User settings class (do NOT modify) | `VibeRails/Utils/Config.cs` |
| Test template for similar service | `Tests/Services/SummaryServiceTests.cs` |

## Appendix B — Endpoint contract (from Codex)

```
POST https://viberails.ai/api/v1/token-savings
Header: X-Api-Key: <api key>
Body:   { "computerName": "<Environment.MachineName>",
          "totalTokensSaved": <Int64 cumulative absolute total> }

The value is an absolute cumulative total, not a delta. Reposting updates the
existing record for this API key and computer.

Local testing URL: https://localhost:5164/api/v1/token-savings

Example success response:
{ "id": 1, "computerName": "WORKSTATION-01", "totalTokensSaved": 3000000000000 }
```

using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Services;


namespace VibeRails.DTOs
{
    // Response records for API
    public record OK(params string[] data);
    public record HealthResponse(string Status);
    public record FileResponse(string FileName, string Content);
    public record ErrorResponse(string Error);

    // CLI Launch DTOs
    public record LaunchCliRequest(
        string? WorkingDirectory = null,
        string? EnvironmentName = null,
        string[]? Args = null
    );

    public record LaunchCliResponse(
        bool Success,
        int ExitCode,
        string Message,
        string StandardOutput,
        string StandardError
    );

    // Session DTOs
    public record SessionResponse(
        string Id,
        string Cli,
        string? EnvironmentName,
        string WorkingDirectory,
        DateTime StartedUTC,
        DateTime? EndedUTC,
        int? ExitCode
    );

    public record OpenSessionCleanupCandidate(
        string SessionId,
        int? OwnerPid
    );

    public record SessionLogResponse(
        long Id,
        string SessionId,
        DateTime Timestamp,
        string Content,
        bool IsError
    );

    public record SessionWithLogsResponse(
        SessionResponse Session,
        List<SessionLogResponse> Logs
    );

    // User Input tracking DTOs
    public record UserInputRecord(
        long Id,
        string SessionId,
        int Sequence,
        string InputText,
        string? GitCommitHash,
        DateTime TimestampUTC
    );

    public record UnembeddedUserInputRow(
        long Id,
        string SessionId,
        string InputText
    );

    public record FileChangeInfo(
        string FilePath,
        string ChangeType,
        int? LinesAdded,
        int? LinesDeleted,
        string? DiffContent
    );

    //
    public record ContextResponse(
        bool IsInGit,
        string? LaunchDirectory = null,
        string? RootPath = null,
        string? GitBranch = null,
        string? GitRemoteUrl = null,
        bool IsSandbox = false
    );

    public record GitOpenDirectoryRequest(string Directory);

    public record GitOpenDirectoryResponse(string Url);

    // Agent File DTOs
    public record RuleWithEnforcementResponse(
        string Text,
        string Enforcement
    );

    public record AgentFileResponse(
        string Path,
        string Name,
        string? CustomName,
        int RuleCount,
        List<RuleWithEnforcementResponse> Rules
    );

    public record AgentFileListResponse(
        List<AgentFileResponse> Agents
    );

    public record AgentFileContentResponse(
        string Content
    );

    public record AgentDocumentedFilesResponse(
        List<string> Files,
        int TotalCount
    );

    public record AgentRulesRequest(
        string Path,
        string[] Rules
    );

    public record AddRuleWithEnforcementRequest(
        string Path,
        string RuleText,
        string Enforcement
    );

    public record UpdateEnforcementRequest(
        string Path,
        string RuleText,
        string Enforcement
    );

    public record CreateAgentRequest(
        string Path,
        string[]? Rules = null
    );

    public record AvailableRulesResponse(
        List<string> Rules
    );

    public record RuleWithDescription(
        string Name,
        string Description
    );

    public record AvailableRulesWithDescriptionsResponse(
        List<RuleWithDescription> Rules
    );

    // Custom Name DTOs
    public record UpdateAgentNameRequest(
        string Path,
        string CustomName
    );

    public record UpdateAgentNameResponse(
        string Path,
        string CustomName
    );

    // Sandbox DTOs
    public record CreateSandboxRequest(
        string Name
    );

    public record SandboxResponse(
        int Id,
        string Name,
        string Path,
        string Branch,
        string? SourceBranch,
        string? CommitHash,
        string? RemoteUrl,
        DateTime CreatedUTC
    );

    public record SandboxDiffFileResponse(
        string FileName,
        string Language,
        string OriginalContent,
        string ModifiedContent
    );

    public record SandboxDiffResponse(
        List<SandboxDiffFileResponse> Files,
        int TotalChanges
    );

    public record MergeBackResponse(
        bool Success,
        string Message
    );

    public record SandboxListResponse(
        List<SandboxResponse> Sandboxes
    );

    // Environment DTOs
    public record CreateEnvironmentRequest(
        string Name,
        string Cli,
        string? CustomArgs = null,
        string? CustomPrompt = null
    );

    public record UpdateEnvironmentRequest(
        string Name,
        string? CustomArgs = null,
        string? CustomPrompt = null
    );

    public record EnvironmentResponse(
        int Id,
        string Name,
        string Cli,
        string Path,
        string CustomArgs,
        string CustomPrompt,
        string DefaultPrompt,
        DateTime LastUsedUTC
    );

    public record EnvironmentListResponse(
        List<EnvironmentResponse> Environments
    );

    // Hook Management DTOs
    public record HookFileStatusResponse(
        string Name,
        string State,
        bool Installed,
        bool Current,
        string Message
    );

    public record HookStatusResponse(
        bool InGitRepo,
        bool IsInstalled,
        bool NeedsRepair,
        string State,
        string? Message,
        string? RepositoryPath,
        string? HooksPath,
        bool AutoInstallEnabled,
        HookFileStatusResponse? PreCommit,
        HookFileStatusResponse? CommitMessage,
        HookFileStatusResponse? PostCommit
    );

    public record HookActionResponse(
        bool Success,
        string Message
    );

    public record VcaRuleFindingResponse(
        string Status,
        string Enforcement,
        string Rule,
        string Reason,
        string SourcePath,
        string Guidance,
        string? Acknowledgment
    );

    public record VcaValidationOverviewResponse(
        string Outcome,
        int StagedFileCount,
        int ApplicableRuleCount,
        int FindingCount,
        int StopCount,
        int CommitCount,
        int WarningCount,
        int DeferredCount,
        List<VcaRuleFindingResponse> Findings
    );

    public record HookPreviewResponse(
        bool Success,
        int ExitCode,
        string Status,
        string Title,
        string Output,
        DateTime StartedUtc,
        long DurationMs,
        double? HealthScore = null,
        string? Rating = null,
        int? AnalyzedFileCount = null,
        int? SkippedFileCount = null,
        VcaValidationOverviewResponse? Validation = null,
        MintLintReportResponse? Report = null,
        int? IgnoredFileCount = null
    );

    // MintLint code-analyzer report: every metric measured per file and how each score
    // was assembled (metric → category worst-signal → weighted overall roll-up).
    // Direction describes the RAW measured value: "LB" = lower is better, "HB" = higher is
    // better, "NA" = unknown/mixed. Concern scores are always higher-is-worse.
    public record MintLintMetricResponse(
        string Name,
        double Value,
        double Score,
        double Warn,
        double Critical,
        bool HigherIsBetter,
        string? Source = null,
        int? Line = null,
        string? Snippet = null,
        string Direction = "NA"
    );

    public record MintLintCategoryResponse(
        string Name,
        double Score,
        double Weight,
        double WeightedScore,
        List<MintLintMetricResponse> Metrics,
        string Direction = "NA"
    );

    // ReferencedByCount = distinct other repo files referencing this file's declared
    // classes/functions; Priority = concern attributable to this change scaled up by that
    // reach, used to rank which bad code matters most. BaselineScore is the committed
    // (pre-change) version's concern; IntroducedScore = Score - BaselineScore. Concern the
    // file already had counts half toward priority.
    public record MintLintFileReportResponse(
        string File,
        double Score,
        string Rating,
        List<MintLintCategoryResponse> Categories,
        int ReferencedByCount = 0,
        double Priority = 0,
        double? BaselineScore = null,
        double? IntroducedScore = null
    );

    // The single worst measurement of one metric across the whole scan, with the code
    // that caused it.
    public record MintLintWorstMetricResponse(
        string Name,
        string File,
        double Value,
        double Score,
        double Warn,
        double Critical,
        bool HigherIsBetter,
        string? Source = null,
        int? Line = null,
        string? Snippet = null,
        string Direction = "NA"
    );

    // One card of the Overview strip, aggregated across the whole scan so the frontend
    // renders what the backend decided instead of re-deriving it. Concern is the AVERAGE
    // category risk across the files that have the category (the changeset picture);
    // WorstConcern keeps the single worst file's number, and the worst measurement
    // behind it stays attributed.
    public record MintLintOverviewCardResponse(
        string Category,
        double Concern,
        string Direction,
        double? WorstConcern = null,
        string? WorstMetricName = null,
        double? WorstMetricValue = null,
        string? WorstMetricDirection = null,
        string? WorstMetricFile = null
    );

    // One fixed row of the score card's always-present metric roster, describing the
    // WHOLE changeset: the average value/risk across every file where the metric was
    // measured, with the single worst measurement kept alongside for context. Combined
    // rows (Coupling, Testability) carry the member metric with the higher average risk;
    // MetricName says which member that was. Measured=false rows render as
    // "not measured", never disappear.
    public record MintLintScorecardEntryResponse(
        string Label,
        bool Measured,
        int FileCount = 0,
        double? AverageValue = null,
        double? AverageConcern = null,
        double? WorstValue = null,
        double? WorstConcern = null,
        string Direction = "NA",
        string? MetricName = null,
        string? File = null,
        string? Source = null,
        int? Line = null
    );

    public record MintLintReportResponse(
        double Score,
        string Rating,
        int AnalyzedFileCount,
        int SkippedFileCount,
        List<MintLintFileReportResponse> Files,
        List<MintLintWorstMetricResponse>? WorstMetrics = null,
        List<MintLintOverviewCardResponse>? Overview = null,
        List<MintLintScorecardEntryResponse>? Scorecard = null
    );

    // Full working-tree file content for the Code quality source pane. Content is null
    // when the file is missing, unreadable, binary, or over the size cap.
    public record CodeAnalyzerSourceResponse(
        string Path,
        string? Content,
        bool Exists = true,
        bool IsBinary = false,
        bool Truncated = false
    );

    // Code quality ignore list: files the user removed from scan results, with an
    // optional reason ("test" / "config" / "other" plus free text). MatchKind is
    // "file" (default, exact path) or "directory" (the path itself plus everything
    // underneath it — used to drop a whole Tests/ or node_modules/ folder at once).
    public record CodeAnalyzerIgnoreEntryResponse(
        string Path,
        string MatchKind,
        string? ReasonKind,
        string? ReasonText,
        DateTime CreatedUtc
    );

    public record CodeAnalyzerIgnoreListResponse(
        List<CodeAnalyzerIgnoreEntryResponse> Files
    );

    public record CodeAnalyzerIgnoreRequest(
        string Path,
        string? MatchKind = null,
        string? ReasonKind = null,
        string? ReasonText = null
    );

    /// <summary>
    /// Body for POST /api/v1/code-analyzer/ignores/bulk. Applies one MatchKind + reason
    /// to many paths in a single request so the UI can multi-select files without
    /// firing N round-trips. Returns the count actually applied.
    /// </summary>
    public record CodeAnalyzerIgnoreBulkRequest(
        List<string> Paths,
        string? MatchKind = null,
        string? ReasonKind = null,
        string? ReasonText = null
    );

    public record CodeAnalyzerIgnoreBulkResponse(
        int Applied,
        int Skipped,
        string Message
    );

    public record GitPreflightEventResponse(
        string RunId,
        long Sequence,
        DateTimeOffset TimestampUtc,
        string Type,
        string? StepId,
        string Status,
        string Message,
        string? Output,
        Dictionary<string, string>? Details,
        long? DurationMs,
        bool Blocking,
        bool? CommitAllowed,
        int? StepNumber,
        int? StepCount
    );

    // Validation DTOs
    public record ValidationResultResponse(
        string RuleName,
        string Enforcement,
        bool Passed,
        string? Message,
        List<string>? AffectedFiles
    );

    public record ValidationResponse(
        bool Passed,
        string Message,
        List<ValidationResultResponse> Results
    );

    // Terminal Session DTOs
    public record TerminalStatusResponse(
        bool HasActiveSession,
        string? SessionId = null,
        string? Cli = null,
        string? WorkingDirectory = null
    );

    public record TerminalTabStatusResponse(
        string TabId,
        DateTime CreatedUTC,
        bool HasActiveSession,
        string? SessionId = null,
        string? Cli = null,
        string? WorkingDirectory = null
    );

    public record TerminalTabListResponse(
        List<TerminalTabStatusResponse> Tabs,
        int MaxTabs
    );

    public record StartTerminalRequest(
        string? WorkingDirectory = null,
        string? Cli = null,
        string? EnvironmentName = null,
        string? Title = null,
        bool MakeRemote = false,
        string? InitialPrompt = null,
        string? ResumeSessionId = null,
        string? ResumeSummary = null
    );

    public record TerminalInputRequest(
        string Text,
        bool Submit = false
    );

    public record TerminalInputResponse(
        bool Success,
        string Message,
        string? TabId = null,
        string? SessionId = null
    );

    public record TerminalSnapshotResponse(
        string? TabId,
        string SessionId,
        DateTimeOffset CapturedUtc,
        int Cols,
        int Rows,
        string[] ScreenText,
        [property: JsonPropertyName("xterm_ui_bytes")] TerminalXtermUiBytes? XtermUiBytes = null,
        [property: JsonPropertyName("xterm_png_string")] string? XtermPngString = null
    );

    public record TerminalXtermUiBytes(
        [property: JsonPropertyName("content_type")] string ContentType,
        string Encoding,
        string Format,
        string Base64,
        [property: JsonPropertyName("byte_length")] int ByteLength,
        int Cols,
        int Rows,
        [property: JsonPropertyName("includes_scrollback")] bool IncludesScrollback,
        [property: JsonPropertyName("renderer_hint")] string RendererHint
    );

    public record AgentToolOpenTerminalRequest(
        string? WorkingDirectory = null,
        string? Cli = "Shell",
        string? EnvironmentName = null,
        string? Title = null,
        string? InitialPrompt = null
    );

    public record AgentToolSendInputRequest(
        string? TabId,
        string Text,
        bool Submit = false
    );

    public record AgentToolSnapshotRequest(
        string? TabId = null
    );

    public record AgentToolTerminalListResponse(
        List<TerminalTabStatusResponse> Terminals,
        int MaxTerminals
    );

    public record AgentToolControlRequest(
        string? Id,
        string Action,
        JsonElement? Payload = null
    );

    public record AgentToolControlResponse(
        string? Id,
        bool Success,
        string? Error = null,
        JsonElement? Data = null
    );

    public record BootstrapCommandResponse(
        string Command
    );

    // Version/Update DTOs
    public record VersionResponse(
        string Version
    );

    public record ApiVersionResponse(
        string ApiVersion,
        string AppVersion
    );

    public record MessageResponse(
        string Message
    );

    // Push Notification DTOs (per-tab opt-in push, proxied to VibeRails-Front).
    // The client sends the raw pieces — tab label, the user's (possibly blank)
    // computer name, and the status key — and the server composes the title/body
    // and fills the blank computer name with Environment.MachineName before
    // forwarding. Keeping that fallback server-side makes it authoritative even
    // for clients (remote viewers, freshly-loaded tabs) that never learned the
    // machine name.
    public record PushSendRequest(
        string? Label = null,
        string? ComputerName = null,
        string? StatusKey = null,
        string? Tag = null,
        string? ImageBase64 = null
    );

    // Debug Bundle DTOs (remote debug-log export)
    public record DebugBundleSendRequest(
        string? Message
    );

    public record DebugBundleSendResponse(
        bool Success,
        string Status,
        string Message
    );

    public record AppSettingsDto(
        bool RemoteAccess,
        string ApiKey,
        bool UseVsCodeTheme,
        bool McpEnabled,
        string? ComputerName,
        // Nullable so a stale client that omits these keys leaves the stored values untouched
        // (see AppSettingsRoutes) instead of resetting an enabled proxy to false/subscription.
        bool? CodexLlmProxyEnabled,
        string? CodexLlmProxyMode,
        bool? ClaudeLlmProxyEnabled,
        // OpenCode (zai/Z.AI GLM) proxy toggle. Nullable for the same stale-client guard as the
        // other proxy fields — a client that omits it leaves the stored value untouched.
        bool? OpenCodeLlmProxyEnabled,
        // Per-LLM token saver on/off — the whole saver surface since 2026-07-18 (the stage
        // selection is fixed to the catalog's curated set; the old TokenSaverStages field is
        // gone). Nullable for the same stale-client guard as the proxy fields.
        bool? ClaudeTokenSaverEnabled,
        bool? CodexTokenSaverEnabled,
        bool? OpenCodeTokenSaverEnabled,
        bool? TokenSaverCaptureEnabled,
        // Read-only: the live machine name, used by the client as the placeholder
        // and as the push-notification fallback when ComputerName is blank. Never
        // persisted (see AppSettingsRoutes), so renaming the machine reflects live.
        string? MachineName = null
    );

    // Narrow update for just the notification computer name. Lets the terminal
    // settings panel change ComputerName without resending (and risking clobbering)
    // the full settings object from a possibly-stale client cache.
    public record UpdateComputerNameDto(string? ComputerName);

    // Display-only ping for the proxy/token-saver meter (see terminal-token-compression.js).
    // Only the authenticated Claude/Codex proxy routes publish this event; ordinary terminal or
    // app activity must not light it. Never include secrets, request bodies, or query strings.
    //   Source: stable proxy name the UI groups by, e.g. "Claude proxy".
    //   Label:  short verb/context, e.g. an HTTP method.
    //   Target: where it went, e.g. an upstream URL (no query string).
    //   Status: outcome, e.g. an HTTP status code.
    //   BytesSaved: this request's minification win, when it was rewritten.
    //   TokensSavedTotal: all-time running tally (~4 bytes/token estimate) for the light's label.
    //   TokensSavedSession / TokensSavedMonth: the same estimate scoped to this server process
    //   and to the current UTC month, for the light's popover breakdown.
    public record ProxyActivityPingPayload(
        string Source,
        string? Label,
        string? Target,
        string? Status,
        long? BytesSaved = null,
        long? TokensSavedTotal = null,
        long? TokensSavedSession = null,
        long? TokensSavedMonth = null
    );

    // Served by GET /api/v1/token-savings for the UI's initial tally (live updates ride the
    // proxy_activity pings). Bytes are the measured truth; the token fields are the ~4 bytes/token
    // display estimate — all-time, this server process, and the current UTC month — derived
    // server-side so the heuristic lives in one place.
    public record TokenSavingsDto(
        long BytesBefore,
        long BytesAfter,
        long BytesSaved,
        long TokensSaved,
        long TokensSavedSession,
        long TokensSavedMonth
    );

    // Compression capture DTOs — served by /api/v1/compression/*.
    //
    // Outcome and Kind cross the wire as strings, not enum ordinals: the capture view renders
    // these directly, and an ordinal would silently change meaning if a value is ever inserted
    // into StageOutcome/StageKind. Same wire-format discipline CompressionCatalog applies to
    // stage ids.
    public record CompressionStageTraceResponse(
        string StageId,
        string Outcome,
        int CharsRemoved
    );

    // The list projection. No RawText: the capture table is uncapped and one row can hold
    // megabytes, so the big strings only ever ship from the by-id endpoint.
    public record CompressionCaptureSummaryResponse(
        Guid Id,
        DateTime CreatedUtc,
        string Provider,
        string ToolName,
        string? Command,
        int CharsBefore,
        int CharsAfter,
        bool Changed,
        bool RewriteAccepted
    );

    public record CompressionCaptureDetailResponse(
        Guid Id,
        DateTime CreatedUtc,
        string Provider,
        string ToolName,
        string? Command,
        string RawText,
        string CompressedText,
        int CharsBefore,
        int CharsAfter,
        bool Changed,
        bool RewriteAccepted,
        List<CompressionStageTraceResponse> Trace,
        List<string> EnabledIds
    );

    public record CompressionClearResponse(int Deleted);

    public record CompressionStageResponse(
        string Id,
        string Name,
        string Summary,
        string Kind,
        bool OnByDefault,
        int Order
    );

    public record CompressionScopeResponse(
        string Id,
        string Name,
        string Summary,
        bool OnByDefault,
        string? Warning
    );

    // Projected live from CompressionCatalog on every request. The catalog is the single source of
    // truth for what the token saver can do; a hand-copied list here would be a fourth place to
    // forget when a stage is added, which is the exact failure the catalog exists to prevent.
    public record CompressionCatalogResponse(
        List<CompressionStageResponse> Stages,
        List<CompressionScopeResponse> Scopes,
        List<string> DefaultSelection
    );

    // EnabledIds is nullable because null and empty are different answers: null means "not
    // specified" and resolves to the catalog defaults, empty means "every stage off". See
    // CompressionCatalog.Resolve, which draws that line.
    public record CompressionPreviewRequest(Guid CaptureId, string[]? EnabledIds);

    public record CompressionPreviewResponse(
        string Output,
        List<CompressionStageTraceResponse> Trace,
        int CharsBefore,
        int CharsAfter,
        bool ScopeAllowed
    );

    // Remote PIN DTOs
    public record SetPinRequest(string Pin);
    public record PinStatusResponse(bool IsSet);

    // BERT Explorer DTOs
    public record BertStatusResponse(
        bool DatabaseExists,
        bool StateDatabaseExists,
        bool ModelAvailable,
        bool SemanticSearchAvailable,
        string DataDirectory,
        string DatabasePath,
        string StateDatabasePath,
        string ModelDirectory,
        string ModelPath,
        string VocabPath,
        int DocumentCount,
        int SessionCount,
        DateTime? LatestCaptureUTC,
        string ModelName,
        int EmbeddingDimension,
        int MaxSequenceLength,
        int VectorCount,
        long VectorDatabaseSizeBytes,
        long StateDatabaseSizeBytes,
        long ModelFileSizeBytes,
        long VocabFileSizeBytes,
        int SessionDocumentCount,
        int SessionVectorCount
    );

    public record BertFileChangeResponse(
        string FilePath,
        string ChangeType,
        int? LinesAdded,
        int? LinesDeleted
    );

    public record BertCaptureSummaryResponse(
        string DocumentId,
        string SessionId,
        long? UserInputId,
        int? Sequence,
        DateTime? TimestampUTC,
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory,
        string? GitCommitHash,
        string UserTextPreview,
        int FileChangeCount,
        string Kind = "input",
        int? ChunkIndex = null
    );

    public record BertCaptureDetailResponse(
        string DocumentId,
        string SessionId,
        long? UserInputId,
        int? Sequence,
        DateTime? TimestampUTC,
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory,
        string? GitCommitHash,
        string UserText,
        List<BertFileChangeResponse> FileChanges,
        string RawText,
        string Kind = "input",
        int? ChunkIndex = null
    );

    public record BertCaptureListResponse(
        List<BertCaptureSummaryResponse> Captures,
        int TotalCount,
        int Skip,
        int Take
    );

    public record BertSearchRequest(
        string Query,
        string Mode = "semantic",
        int TopK = 10,
        string Scope = "inputs"
    );

    public record BertSearchHitResponse(
        string DocumentId,
        string SessionId,
        long? UserInputId,
        int? Sequence,
        DateTime? TimestampUTC,
        string? Cli,
        string? EnvironmentName,
        string? WorkingDirectory,
        string? GitCommitHash,
        string UserTextPreview,
        int FileChangeCount,
        double? Score,
        string Kind = "input",
        int? ChunkIndex = null
    );

    public record BertSearchResponse(
        string Query,
        string Mode,
        string Scope,
        int TopK,
        int DocumentCount,
        long SearchTimeMs,
        List<BertSearchHitResponse> Results
    );

    public record UnifiedSearchRequest(
        string Query,
        int TopK = 10
    );

    public record UnifiedSearchHitGroup(
        string Key,
        string Label,
        string Description,
        long SearchTimeMs,
        int CorpusSize,
        List<BertSearchHitResponse> Hits
    );

    public record UnifiedSearchResponse(
        string Query,
        int TopK,
        long TotalSearchTimeMs,
        List<UnifiedSearchHitGroup> Groups
    );

    public record UpdateChatHistorySessionRequest(
        string? SessionDisplayName
    );

    // Chat History DTOs
    public record ChatSummaryResponse(
        string Summary,
        string Transcript
    );

    public record ChatHistoryTranscriptResponse(
        string SessionId,
        string Text
    );

    public record ChatHistoryReplayResponse(
        string SessionId,
        string Content
    );

    public record ChatHistoryRawSessionArchiveResponse(
        string SessionId,
        List<SessionLogResponse> SessionLogs,
        List<UserInputRecord> UserInputs
    );

    public record TerminalReplayChunk(
        int Sequence,
        bool IsAlternateScreen,
        string Data,
        int Cols,
        int Rows
    );

    public record TerminalReplayFrame(
        string Data,
        long DelayMs
    );

    public record TerminalReplayResponse(
        string SessionId,
        int InitialCols,
        int InitialRows,
        List<TerminalReplayChunk> Chunks,
        List<TerminalReplayFrame> Frames
    );

    public record ChatHistoryItem(
        string Id,
        string Cli,
        string? EnvironmentName,
        string WorkingDirectory,
        string? ProjectDisplayName,
        DateTime StartedUTC,
        DateTime? EndedUTC,
        int? ExitCode,
        string? ParentSessionId,
        string? ParentCli,
        string? SessionDisplayName,
        int? Sequence,
        string? InputText,
        int UserInputCount,
        long? DurationSeconds
    );

    public record ChatHistoryResponse(
        List<ChatHistoryItem> Items,
        int Page,
        int PageSize
    );

    // Summary DTOs (remote summary service)
    public class SummaryPostDto
    {
        public string SessionText { get; set; } = string.Empty;
    }

    public class SummaryResponseDto
    {
        public string Summary { get; set; } = string.Empty;
    }

    public record AppToastNotification(
        string Title,
        string Message,
        string Type = "info",
        bool RequireDismiss = false,
        string? Icon = null,
        string? IconBackground = null,
        string? IconColor = null
    );

    public record EventMessage(string Type, string Text);

    // Generic typed event pushed to browser over /api/v1/events/ws
    public record AppEvent(string Type, JsonElement Payload);

    // Session state event payloads
    public record SessionStartedPayload(string SessionId, string Cli);
    public record SessionIdlePayload(string SessionId, string Cli, double IdleForSeconds);
    public record SessionBusyPayload(string SessionId, string Cli);
    public record SessionInputPayload(string SessionId, string Kind, string Source);
    public record SessionWaitingForUserPayload(string SessionId);
    public record SessionCompletedPayload(string SessionId, string Cli, int? ExitCode);

    [JsonSerializable(typeof(AppToastNotification))]
    [JsonSerializable(typeof(AppEvent))]
    [JsonSerializable(typeof(SessionStartedPayload))]
    [JsonSerializable(typeof(SessionIdlePayload))]
    [JsonSerializable(typeof(SessionBusyPayload))]
    [JsonSerializable(typeof(SessionInputPayload))]
    [JsonSerializable(typeof(SessionWaitingForUserPayload))]
    [JsonSerializable(typeof(SessionCompletedPayload))]
    [JsonSerializable(typeof(EventMessage))]
    [JsonSerializable(typeof(HealthResponse))]
    [JsonSerializable(typeof(FileResponse))]
    [JsonSerializable(typeof(ErrorResponse))]
    [JsonSerializable(typeof(OK))]
    [JsonSerializable(typeof(StateFileObject))]
    [JsonSerializable(typeof(LLM_Environment))]
    [JsonSerializable(typeof(List<LLM_Environment>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(LaunchCliRequest))]
    [JsonSerializable(typeof(LaunchCliResponse))]
    [JsonSerializable(typeof(SessionResponse))]
    [JsonSerializable(typeof(SessionLogResponse))]
    [JsonSerializable(typeof(SessionWithLogsResponse))]
    [JsonSerializable(typeof(List<SessionResponse>))]
    [JsonSerializable(typeof(List<SessionLogResponse>))]
    [JsonSerializable(typeof(SessionOutputDetailResponse))]
    // Chat History DTOs
    [JsonSerializable(typeof(ChatSummaryResponse))]
    [JsonSerializable(typeof(ChatHistoryTranscriptResponse))]
    [JsonSerializable(typeof(ChatHistoryReplayResponse))]
    [JsonSerializable(typeof(ChatHistoryRawSessionArchiveResponse))]
    [JsonSerializable(typeof(TerminalReplayChunk))]
    [JsonSerializable(typeof(TerminalReplayFrame))]
    [JsonSerializable(typeof(TerminalReplayResponse))]
    [JsonSerializable(typeof(List<TerminalReplayChunk>))]
    [JsonSerializable(typeof(List<TerminalReplayFrame>))]
    [JsonSerializable(typeof(ChatHistoryItem))]
    [JsonSerializable(typeof(List<ChatHistoryItem>))]
    [JsonSerializable(typeof(ChatHistoryResponse))]
    [JsonSerializable(typeof(UpdateChatHistorySessionRequest))]
    // User Input tracking DTOs
    [JsonSerializable(typeof(UserInputRecord))]
    [JsonSerializable(typeof(List<UserInputRecord>))]
    [JsonSerializable(typeof(FileChangeInfo))]
    [JsonSerializable(typeof(List<FileChangeInfo>))]
    // MCP DTOs
    [JsonSerializable(typeof(McpToolInfo))]
    [JsonSerializable(typeof(List<McpToolInfo>))]
    [JsonSerializable(typeof(McpServerTargetRequest))]
    [JsonSerializable(typeof(McpInspectResponse))]
    [JsonSerializable(typeof(McpToolCallRequest))]
    [JsonSerializable(typeof(McpToolCallResponse))]
    [JsonSerializable(typeof(McpStatusResponse))]
    [JsonSerializable(typeof(Dictionary<string, object?>))]
    [JsonSerializable(typeof(ContextResponse))]
    [JsonSerializable(typeof(GitOpenDirectoryRequest))]
    [JsonSerializable(typeof(GitOpenDirectoryResponse))]
    // Agent File DTOs
    [JsonSerializable(typeof(RuleWithEnforcementResponse))]
    [JsonSerializable(typeof(List<RuleWithEnforcementResponse>))]
    [JsonSerializable(typeof(AgentFileResponse))]
    [JsonSerializable(typeof(AgentFileListResponse))]
    [JsonSerializable(typeof(AgentFileContentResponse))]
    [JsonSerializable(typeof(AgentDocumentedFilesResponse))]
    [JsonSerializable(typeof(List<AgentFileResponse>))]
    [JsonSerializable(typeof(AgentRulesRequest))]
    [JsonSerializable(typeof(AddRuleWithEnforcementRequest))]
    [JsonSerializable(typeof(UpdateEnforcementRequest))]
    [JsonSerializable(typeof(CreateAgentRequest))]
    [JsonSerializable(typeof(AvailableRulesResponse))]
    [JsonSerializable(typeof(RuleWithDescription))]
    [JsonSerializable(typeof(List<RuleWithDescription>))]
    [JsonSerializable(typeof(AvailableRulesWithDescriptionsResponse))]
    [JsonSerializable(typeof(UpdateAgentNameRequest))]
    [JsonSerializable(typeof(UpdateAgentNameResponse))]
    // Hook Management DTOs
    [JsonSerializable(typeof(HookFileStatusResponse))]
    [JsonSerializable(typeof(HookStatusResponse))]
    [JsonSerializable(typeof(HookActionResponse))]
    [JsonSerializable(typeof(VcaRuleFindingResponse))]
    [JsonSerializable(typeof(List<VcaRuleFindingResponse>))]
    [JsonSerializable(typeof(VcaValidationOverviewResponse))]
    [JsonSerializable(typeof(HookPreviewResponse))]
    [JsonSerializable(typeof(MintLintMetricResponse))]
    [JsonSerializable(typeof(List<MintLintMetricResponse>))]
    [JsonSerializable(typeof(MintLintCategoryResponse))]
    [JsonSerializable(typeof(List<MintLintCategoryResponse>))]
    [JsonSerializable(typeof(MintLintFileReportResponse))]
    [JsonSerializable(typeof(List<MintLintFileReportResponse>))]
    [JsonSerializable(typeof(MintLintWorstMetricResponse))]
    [JsonSerializable(typeof(List<MintLintWorstMetricResponse>))]
    [JsonSerializable(typeof(MintLintOverviewCardResponse))]
    [JsonSerializable(typeof(List<MintLintOverviewCardResponse>))]
    [JsonSerializable(typeof(MintLintScorecardEntryResponse))]
    [JsonSerializable(typeof(List<MintLintScorecardEntryResponse>))]
    [JsonSerializable(typeof(MintLintReportResponse))]
    [JsonSerializable(typeof(CodeAnalyzerSourceResponse))]
    [JsonSerializable(typeof(CodeAnalyzerIgnoreEntryResponse))]
    [JsonSerializable(typeof(List<CodeAnalyzerIgnoreEntryResponse>))]
    [JsonSerializable(typeof(CodeAnalyzerIgnoreListResponse))]
    [JsonSerializable(typeof(CodeAnalyzerIgnoreRequest))]
    [JsonSerializable(typeof(CodeAnalyzerIgnoreBulkRequest))]
    [JsonSerializable(typeof(CodeAnalyzerIgnoreBulkResponse))]
    [JsonSerializable(typeof(GitPreflightEventResponse))]
    [JsonSerializable(typeof(ValidationResultResponse))]
    [JsonSerializable(typeof(List<ValidationResultResponse>))]
    [JsonSerializable(typeof(ValidationResponse))]
    // Sandbox DTOs
    [JsonSerializable(typeof(CreateSandboxRequest))]
    [JsonSerializable(typeof(SandboxResponse))]
    [JsonSerializable(typeof(SandboxListResponse))]
    [JsonSerializable(typeof(List<SandboxResponse>))]
    [JsonSerializable(typeof(SandboxDiffFileResponse))]
    [JsonSerializable(typeof(SandboxDiffResponse))]
    [JsonSerializable(typeof(List<SandboxDiffFileResponse>))]
    [JsonSerializable(typeof(MergeBackResponse))]
    // Environment DTOs
    [JsonSerializable(typeof(CreateEnvironmentRequest))]
    [JsonSerializable(typeof(UpdateEnvironmentRequest))]
    [JsonSerializable(typeof(EnvironmentResponse))]
    [JsonSerializable(typeof(EnvironmentListResponse))]
    [JsonSerializable(typeof(List<EnvironmentResponse>))]
    // Jobs DTOs
    [JsonSerializable(typeof(JobTriggerDto))]
    [JsonSerializable(typeof(List<JobTriggerDto>))]
    [JsonSerializable(typeof(JobResponse))]
    [JsonSerializable(typeof(List<JobResponse>))]
    [JsonSerializable(typeof(JobListResponse))]
    [JsonSerializable(typeof(JobTriggerRequest))]
    [JsonSerializable(typeof(List<JobTriggerRequest>))]
    [JsonSerializable(typeof(CreateJobRequest))]
    [JsonSerializable(typeof(UpdateJobRequest))]
    [JsonSerializable(typeof(JobRunResponse))]
    [JsonSerializable(typeof(List<JobRunResponse>))]
    [JsonSerializable(typeof(JobRunListResponse))]
    [JsonSerializable(typeof(JobActionResponse))]
    // Codex Settings DTOs
    [JsonSerializable(typeof(CodexSettingsDto))]
    // Claude Settings DTOs
    [JsonSerializable(typeof(ClaudeSettingsDto))]
    // Terminal Session DTOs
    [JsonSerializable(typeof(TerminalStatusResponse))]
    [JsonSerializable(typeof(TerminalTabStatusResponse))]
    [JsonSerializable(typeof(List<TerminalTabStatusResponse>))]
    [JsonSerializable(typeof(TerminalTabListResponse))]
    [JsonSerializable(typeof(StartTerminalRequest))]
    [JsonSerializable(typeof(TerminalInputRequest))]
    [JsonSerializable(typeof(TerminalInputResponse))]
    [JsonSerializable(typeof(TerminalSnapshotResponse))]
    [JsonSerializable(typeof(TerminalXtermUiBytes))]
    [JsonSerializable(typeof(AgentToolOpenTerminalRequest))]
    [JsonSerializable(typeof(AgentToolSendInputRequest))]
    [JsonSerializable(typeof(AgentToolSnapshotRequest))]
    [JsonSerializable(typeof(AgentToolTerminalListResponse))]
    [JsonSerializable(typeof(AgentToolControlRequest))]
    [JsonSerializable(typeof(AgentToolControlResponse))]
    [JsonSerializable(typeof(BootstrapCommandResponse))]
    // Summary DTOs
    [JsonSerializable(typeof(SummaryPostDto))]
    [JsonSerializable(typeof(SummaryResponseDto))]
    // Version/Update DTOs
    [JsonSerializable(typeof(VersionResponse))]
    [JsonSerializable(typeof(ApiVersionResponse))]
    [JsonSerializable(typeof(MessageResponse))]
    // Push Notification DTOs
    [JsonSerializable(typeof(PushSendRequest))]
    // Debug Bundle DTOs
    [JsonSerializable(typeof(DebugBundleSendRequest))]
    [JsonSerializable(typeof(DebugBundleSendResponse))]
    [JsonSerializable(typeof(UpdateInfo))]
    [JsonSerializable(typeof(AppSettingsDto))]
    [JsonSerializable(typeof(UpdateComputerNameDto))]
    [JsonSerializable(typeof(ProxyActivityPingPayload))]
    [JsonSerializable(typeof(TokenSavingsDto))]
    // Compression capture DTOs
    [JsonSerializable(typeof(CompressionStageTraceResponse))]
    [JsonSerializable(typeof(List<CompressionStageTraceResponse>))]
    [JsonSerializable(typeof(CompressionCaptureSummaryResponse))]
    [JsonSerializable(typeof(List<CompressionCaptureSummaryResponse>))]
    [JsonSerializable(typeof(CompressionCaptureDetailResponse))]
    [JsonSerializable(typeof(CompressionClearResponse))]
    [JsonSerializable(typeof(CompressionStageResponse))]
    [JsonSerializable(typeof(List<CompressionStageResponse>))]
    [JsonSerializable(typeof(CompressionScopeResponse))]
    [JsonSerializable(typeof(List<CompressionScopeResponse>))]
    [JsonSerializable(typeof(CompressionCatalogResponse))]
    [JsonSerializable(typeof(CompressionPreviewRequest))]
    [JsonSerializable(typeof(CompressionPreviewResponse))]
    // Remote PIN DTOs
    [JsonSerializable(typeof(SetPinRequest))]
    [JsonSerializable(typeof(PinStatusResponse))]
    // BERT explorer DTOs
    [JsonSerializable(typeof(BertStatusResponse))]
    [JsonSerializable(typeof(BertFileChangeResponse))]
    [JsonSerializable(typeof(List<BertFileChangeResponse>))]
    [JsonSerializable(typeof(BertCaptureSummaryResponse))]
    [JsonSerializable(typeof(List<BertCaptureSummaryResponse>))]
    [JsonSerializable(typeof(BertCaptureDetailResponse))]
    [JsonSerializable(typeof(BertCaptureListResponse))]
    [JsonSerializable(typeof(BertSearchRequest))]
    [JsonSerializable(typeof(BertSearchHitResponse))]
    [JsonSerializable(typeof(List<BertSearchHitResponse>))]
    [JsonSerializable(typeof(BertSearchResponse))]
    [JsonSerializable(typeof(UnifiedSearchRequest))]
    [JsonSerializable(typeof(UnifiedSearchHitGroup))]
    [JsonSerializable(typeof(List<UnifiedSearchHitGroup>))]
    [JsonSerializable(typeof(UnifiedSearchResponse))]
    // App Configuration (for appsettings.json VibeRails section)
    [JsonSerializable(typeof(Services.VibeRailsConfiguration))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}



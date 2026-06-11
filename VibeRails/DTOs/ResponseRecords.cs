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
    public record HookStatusResponse(
        bool InGitRepo,
        bool IsInstalled,
        string? Message
    );

    public record HookActionResponse(
        bool Success,
        string Message
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
        string? Cli = null
    );

    public record TerminalTabStatusResponse(
        string TabId,
        DateTime CreatedUTC,
        bool HasActiveSession,
        string? SessionId = null,
        string? Cli = null
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
        bool EnablePrerelease,
        bool DeveloperOptions,
        bool UseVsCodeTheme
    );

    // Remote PIN DTOs
    public record SetPinRequest(string Pin);
    public record PinStatusResponse(bool IsSet);

    // Signed Message DTOs (matches VibeRails-Front TerminalSignedMessage shape)
    public record SignedMessage(string Message, string Signature);
    public record SignatureVerificationResponse(bool Verified, string Message);

    // Proxy relay DTOs (sent from proxy WS to browser)
    public record ProxyRelayMessage(string Type, string Message, string? Signature = null, bool? Verified = null);

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
    public record SessionWaitingForUserPayload(string SessionId);
    public record SessionCompletedPayload(string SessionId, string Cli, int? ExitCode);

    [JsonSerializable(typeof(AppToastNotification))]
    [JsonSerializable(typeof(AppEvent))]
    [JsonSerializable(typeof(SessionStartedPayload))]
    [JsonSerializable(typeof(SessionIdlePayload))]
    [JsonSerializable(typeof(SessionBusyPayload))]
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
    [JsonSerializable(typeof(McpSettings))]
    [JsonSerializable(typeof(McpToolInfo))]
    [JsonSerializable(typeof(List<McpToolInfo>))]
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
    [JsonSerializable(typeof(HookStatusResponse))]
    [JsonSerializable(typeof(HookActionResponse))]
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
    // Gemini Settings DTOs
    [JsonSerializable(typeof(GeminiSettingsDto))]
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
    [JsonSerializable(typeof(BootstrapCommandResponse))]
    // Summary DTOs
    [JsonSerializable(typeof(SummaryPostDto))]
    [JsonSerializable(typeof(SummaryResponseDto))]
    // Version/Update DTOs
    [JsonSerializable(typeof(VersionResponse))]
    [JsonSerializable(typeof(ApiVersionResponse))]
    [JsonSerializable(typeof(MessageResponse))]
    // Debug Bundle DTOs
    [JsonSerializable(typeof(DebugBundleSendRequest))]
    [JsonSerializable(typeof(DebugBundleSendResponse))]
    [JsonSerializable(typeof(UpdateInfo))]
    [JsonSerializable(typeof(AppSettingsDto))]
    // Remote PIN DTOs
    [JsonSerializable(typeof(SetPinRequest))]
    [JsonSerializable(typeof(PinStatusResponse))]
    // Signed Message DTOs
    [JsonSerializable(typeof(SignedMessage))]
    [JsonSerializable(typeof(SignatureVerificationResponse))]
    [JsonSerializable(typeof(ProxyRelayMessage))]
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



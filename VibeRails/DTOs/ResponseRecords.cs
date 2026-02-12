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

    public record FileChangeInfo(
        string FilePath,
        string ChangeType,
        int? LinesAdded,
        int? LinesDeleted,
        string? DiffContent
    );

    //
    public record IsLocalResponse(
        bool IsLocalContext,
        string? LaunchDirectory = null,
        string? RootPath = null
    );

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
        string? CommitHash,
        DateTime CreatedUTC
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
        string? SessionId = null
    );

    public record StartTerminalRequest(
        string? WorkingDirectory = null,
        string? Cli = null,
        string? EnvironmentName = null,
        string? Title = null
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

    // App Settings DTOs
    public record AppSettingsDto(
        bool RemoteAccess,
        string ApiKey
    );

    // Signed Message DTOs (matches VibeRails-Front TerminalSignedMessage shape)
    public record SignedMessage(string Message, string Signature);
    public record SignatureVerificationResponse(bool Verified, string Message);

    // Proxy relay DTOs (sent from proxy WS to browser)
    public record ProxyRelayMessage(string Type, string Message, string? Signature = null, bool? Verified = null);

    // Claude Plan DTOs
    public record ClaudePlanRecord(
        long Id,
        string SessionId,
        long? UserInputId,
        string? PlanFilePath,
        string PlanContent,
        string? PlanSummary,
        string Status,
        DateTime CreatedUTC,
        DateTime? CompletedUTC
    );

    public record CreateClaudePlanRequest(
        string SessionId,
        long? UserInputId,
        string? PlanFilePath,
        string PlanContent,
        string? PlanSummary
    );

    public record UpdateClaudePlanStatusRequest(
        string Status
    );

    public record ClaudePlanListResponse(
        List<ClaudePlanRecord> Plans,
        int TotalCount
    );

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
    // User Input tracking DTOs
    [JsonSerializable(typeof(UserInputRecord))]
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
    [JsonSerializable(typeof(IsLocalResponse))]
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
    [JsonSerializable(typeof(StartTerminalRequest))]
    [JsonSerializable(typeof(BootstrapCommandResponse))]
    // Claude Plan DTOs
    [JsonSerializable(typeof(ClaudePlanRecord))]
    [JsonSerializable(typeof(List<ClaudePlanRecord>))]
    [JsonSerializable(typeof(CreateClaudePlanRequest))]
    [JsonSerializable(typeof(UpdateClaudePlanStatusRequest))]
    [JsonSerializable(typeof(ClaudePlanListResponse))]
    // Version/Update DTOs
    [JsonSerializable(typeof(VersionResponse))]
    [JsonSerializable(typeof(ApiVersionResponse))]
    [JsonSerializable(typeof(MessageResponse))]
    [JsonSerializable(typeof(UpdateInfo))]
    // App Settings DTOs
    [JsonSerializable(typeof(AppSettingsDto))]
    // Signed Message DTOs
    [JsonSerializable(typeof(SignedMessage))]
    [JsonSerializable(typeof(SignatureVerificationResponse))]
    [JsonSerializable(typeof(ProxyRelayMessage))]
    // App Configuration (for app_config.json)
    [JsonSerializable(typeof(Services.AppConfiguration))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    internal partial class AppJsonSerializerContext : JsonSerializerContext
    {
    }
}

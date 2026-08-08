using VibeRails.Auth;
using VibeRails.Jobs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.BertBaseClasses;
using VibeRails.Services.BertV2;
using VibeRails.Services.UserInOut;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.LlmClis;
using VibeRails.Services.LlmClis.Launchers;
using TokenSaver;
using VibeRails.Services.AgentTools;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Mcp.WebResearch;
using VibeRails.Services.Messaging;
using VibeRails.Services.Terminal;
using VibeRails.Services.Workspaces;
using VibeRails.Services.Terminal.Consumers;

using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Validators;
using VibeRails.Utils;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using VibeRails.Services.HttpRelay;

namespace VibeRails
{
    public static class MapRegisterServices
    {
        public static void Register(IServiceCollection serviceCollection, string[]? args = null, string? localApiBaseUrl = null)
        {
            var processArgs = args ?? Environment.GetCommandLineArgs();
            var isTerminalTabChildProcess = IsTerminalTabChildProcess(processArgs);
            var isActiveRootBackendProcess = IsActiveRootBackendProcess(processArgs);
            var isFakeCliTestProcess = string.Equals(
                Environment.GetEnvironmentVariable("VIBERAILS_TEST_FAKE_CLI"),
                "1",
                StringComparison.Ordinal);
            localApiBaseUrl ??= "http://127.0.0.1:0";

            serviceCollection.AddHttpClient<ISummaryService, SummaryService>(
                x =>
                {
                    x.BaseAddress = new Uri("https://viberails.ai");
                });

            serviceCollection.AddScoped<IFileService, FileService>();

            // BERT (V2) — write path, read path, and search strategies are registered separately.
            serviceCollection.AddSingleton<IBertSettings, BertV2BgeSmallEnSettings>();
            serviceCollection.AddSingleton<IBertV2BgeEmbedder>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2BgeEmbedder(settings.ModelPath, settings.VocabPath);
            });
            serviceCollection.AddSingleton<IBertV2VectorStore>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2VectorStore(Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName));
            });
            serviceCollection.AddSingleton<IBertV2SessionVectorStore>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2SessionVectorStore(Path.Combine(settings.DataDirectory, BertSearchSchema.DatabaseFileName));
            });
            serviceCollection.AddSingleton<IBertV2InputService, BertV2InputService>();
            serviceCollection.AddSingleton<IBertV2SessionEmbeddingService, BertV2SessionEmbeddingService>();
            serviceCollection.AddScoped<IBertV2InputDBService, BertV2InputDBService>();
            serviceCollection.AddSingleton<IBertSearchDbService, BertSearchDbService>();
            serviceCollection.AddSingleton<IBertDocumentResponseMapper, BertDocumentResponseMapper>();
            serviceCollection.AddSingleton<IBertCaptureQueryService, BertCaptureQueryService>();
            serviceCollection.AddSingleton<IBertSearchStrategy, SemanticBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, TextBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, SemanticSessionBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy, TextSessionBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchServiceV2, BertSearchServiceV2>();
            serviceCollection.AddSingleton<IUnifiedSearchService, UnifiedSearchService>();

            serviceCollection.AddSingleton<IGitDiffCaptureService, GitDiffCaptureService>();
            serviceCollection.AddSingleton<ILlmParser, LlmParser>();
            serviceCollection.AddSingleton<ILocalToolApiContext>(sp =>
                new LocalToolApiContext(localApiBaseUrl, sp.GetRequiredService<IAuthService>()));
            serviceCollection.AddSingleton<ILocalLlmProxyContext>(sp =>
                new LocalLlmProxyContext(localApiBaseUrl, sp.GetRequiredService<IAuthService>()));
            serviceCollection.AddSingleton<ILlmProxySessionState, LlmProxySessionState>();
            // Singleton, and ungated: the pause must outlive request scopes, and it is the terminal
            // tab child — not the root — that hosts the proxy an agent is asking to pause. In the
            // root it simply stays un-paused.
            serviceCollection.AddSingleton<ITokenSaverPauseState, TokenSaverPauseState>();
            // One instance behind two interfaces: the effective snapshot the proxy routes read, and
            // the pause-blind "is it configured on" question the control endpoint asks. Registering
            // them separately would give the control endpoint its own settings reader.
            serviceCollection.AddSingleton<LlmProxySettingsService>();
            serviceCollection.AddSingleton<ILlmProxySettingsService>(
                sp => sp.GetRequiredService<LlmProxySettingsService>());
            serviceCollection.AddSingleton<ITokenSaverConfiguration>(
                sp => sp.GetRequiredService<LlmProxySettingsService>());
            // Host adapters behind the TokenSaver library's seams: auth-gate → IAuthService,
            // event sink → app event bus + Serilog + the state.db savings tally.
            serviceCollection.AddSingleton<ILlmProxyAuthGate, LlmProxyAuthGateAdapter>();
            serviceCollection.AddSingleton<ILlmProxyEventSink, LlmProxyEventSinkAdapter>();
            serviceCollection.AddSingleton<ICompressionCaptureSink, CompressionCaptureSinkAdapter>();
            // Required by every proxy route: exchange logging has no settings/UI gate.
            serviceCollection.AddSingleton<ILlmProxyExchangeSink, LlmProxyExchangeSinkAdapter>();
            // Its own database file, never state.db: one exchange is the whole conversation plus
            // the response, and the CLI resends that history every turn. See LlmExchangeLogStore.
            serviceCollection.AddSingleton<ILlmExchangeLogStore>(_ => new LlmExchangeLogStore(
                $"Data Source={Path.Combine(Path.GetDirectoryName(ParserConfigs.GetStatePath()) ?? ".", "proxy_exchanges.db")};Mode=ReadWriteCreate"));
            serviceCollection.AddSingleton<ITokenSavingsStore>(_ => new TokenSavingsStore(
                $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
            // Files the user excluded from Code quality scans (Rules page). Singleton for the
            // same reason as the other state.db stores: one writer, connection-per-operation.
            serviceCollection.AddSingleton<ICodeAnalyzerIgnoreStore>(_ => new CodeAnalyzerIgnoreStore(
                $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
            serviceCollection.AddSingleton<IJobStore>(_ => new JobStore(
                $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
            serviceCollection.AddSingleton<IJobExecutableResolver, JobExecutableResolver>();
            // Spawns a real OS terminal per queued run through the same scoped Environment launch
            // pipeline as the CLI launch API. JobRunner itself lives in the spawned process.
            serviceCollection.AddScoped<IJobLaunchService, JobLaunchService>();
            // Keep the scheduler signal resolvable everywhere JobService can be resolved, but only
            // an active root backend hosts the polling loop. Terminal-tab children and `vb --env`
            // processes share state.db but must never compete for scheduled work.
            serviceCollection.AddSingleton<JobSchedulerHostedService>();
            serviceCollection.AddSingleton<IJobScheduler>(sp => sp.GetRequiredService<JobSchedulerHostedService>());
            // Playwright's real backend uses a fake CLI but shares the developer account's state
            // directory. Never let that test host dispatch persisted Automation work.
            if (isActiveRootBackendProcess && !isFakeCliTestProcess)
            {
                serviceCollection.AddHostedService(sp => sp.GetRequiredService<JobSchedulerHostedService>());
            }
            else if (isActiveRootBackendProcess)
            {
                // An env var silently disabling the scheduler in a real environment would look
                // exactly like "Automations never fire". Make the suppression loud and findable.
                Serilog.Log.Warning(
                    "[Jobs] Automation scheduler NOT registered: VIBERAILS_TEST_FAKE_CLI=1 marks this as a UI-test host");
            }
            serviceCollection.AddScoped<IJobService, JobService>();
            // Singleton for the same reason as the savings tally: one ordered writer, so concurrent
            // relays serialize capture inserts, re-sight counts, and clears before reaching SQLite.
            serviceCollection.AddSingleton<ICompressionCaptureStore>(_ => new CompressionCaptureStore(
                $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared"));
            serviceCollection.AddScoped<IAgentTerminalToolService, AgentTerminalToolService>();
            serviceCollection.AddScoped<IRepository>(sp =>
            {
                var connectionString = $"Data Source={ParserConfigs.GetStatePath()};Mode=ReadWriteCreate;Cache=Shared";
                var gitDiff = sp.GetService<IGitDiffCaptureService>();
                var logger = sp.GetService<ILogger<Repository>>();
                return new Repository(connectionString, gitDiff, logger);
            });
            serviceCollection.AddScoped<ILlmPickerPreferenceService, LlmPickerPreferenceService>();
            serviceCollection.AddScoped<IGetUserText, GetUserText>();
            serviceCollection.AddScoped<IProjectCache, ProjectCache>();
            serviceCollection.AddScoped<IGlobalCache, GlobalCache>();
            serviceCollection.AddScoped<IChatHistoryService, ChatHistoryService>();
            serviceCollection.AddSingleton<ISessionOutputParser, SessionParseV4>();
            serviceCollection.AddScoped<IGitService, GitService>();

            // Rules and Agent File services
            serviceCollection.AddScoped<IRulesService, RulesService>();
            serviceCollection.AddScoped<IAgentFileService, AgentFileService>();

            // VCA Validation services (for git hooks)
            serviceCollection.AddScoped<IRuleValidationService, RuleValidationService>();
            serviceCollection.AddScoped<IHookInstallationService, HookInstallationService>();
            serviceCollection.AddGitPreflight();

            // NEW VCA Validation Architecture (modular)
            // Infrastructure services
            serviceCollection.AddScoped<IFileClassifier, FileClassifier>();
            serviceCollection.AddScoped<IFileReader, FileReader>();
            serviceCollection.AddScoped<IPathNormalizer, PathNormalizer>();

            // Core VCA services
            serviceCollection.AddScoped<IFileAndRuleParser, FileAndRuleParser>();
            serviceCollection.AddScoped<IValidatorList, ValidatorList>();
            serviceCollection.AddScoped<VibeRails.Services.VCA.ValidationService>();

            // Individual validators
            serviceCollection.AddScoped<LogAllFileChangesValidator>();
            serviceCollection.AddScoped<PackageChangeValidator>();

            // Shell service (cross-platform process runner)
            serviceCollection.AddScoped<IShellService, ShellService>();

            // LLM CLI Environment services
            serviceCollection.AddScoped<IClaudeLlmCliEnvironment, ClaudeLlmCliEnvironment>();
            serviceCollection.AddScoped<ICodexLlmCliEnvironment, CodexLlmCliEnvironment>();
            serviceCollection.AddScoped<IAntigravityLlmCliEnvironment, AntigravityLlmCliEnvironment>();
            serviceCollection.AddScoped<ICopilotLlmCliEnvironment, CopilotLlmCliEnvironment>();
            serviceCollection.AddScoped<IOpencodeLlmCliEnvironment, OpencodeLlmCliEnvironment>();
            serviceCollection.AddScoped<LlmCliEnvironmentService>();

            // Sandbox service, and the workspace resolution that reuses it to back an
            // environment's Persistent / PerRun clone modes.
            serviceCollection.AddScoped<ISandboxService, SandboxService>();
            serviceCollection.AddScoped<IRunWorkspaceService, RunWorkspaceService>();

            // LLM CLI Launcher services
            serviceCollection.AddScoped<IClaudeLlmCliLauncher, ClaudeLlmCliLauncher>();
            serviceCollection.AddScoped<ICodexLlmCliLauncher, CodexLlmCliLauncher>();
            serviceCollection.AddScoped<IAntigravityLlmCliLauncher, AntigravityLlmCliLauncher>();
            serviceCollection.AddScoped<ICopilotLlmCliLauncher, CopilotLlmCliLauncher>();
            serviceCollection.AddScoped<IOpencodeLlmCliLauncher, OpencodeLlmCliLauncher>();
            serviceCollection.AddScoped<ILaunchLLMService, LaunchLLMService>();
            serviceCollection.AddScoped<IEnvironmentLaunchService, EnvironmentLaunchService>();

            // In-process MCP server, exposed over HTTP at /mcp (see app.MapMcp in Program.cs).
            // Only the root backend hosts it — terminal-tab child processes skip it so we don't
            // stand up a redundant MCP endpoint per tab. The MapMcp call is gated the same way,
            // so resolving these services and mapping the endpoint always happen together.
            if (!isTerminalTabChildProcess)
            {
                // SessionSearchTool is an instance tool with constructor injection; register it
                // so the MCP server can resolve it (and its IUnifiedSearchService dependency)
                // from the per-request scope.
                serviceCollection.AddScoped<SessionSearchTool>();
                // Same story for TokenSaverTool (ctor-injected IHttpClientFactory). Its named client
                // is short-timeout because every call is a loopback hop to this machine's proxy.
                // Registered here AND in McpStdioHost.ConfigureServices — the two transports must
                // expose the same tools.
                serviceCollection.AddHttpClient(TokenSaverTool.HttpClientName, client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                });
                serviceCollection.AddScoped<TokenSaverTool>();
                // run_shell_command (HostShellTools) and web_search/web_fetch (WebResearchTools) are
                // intentionally not exposed for now (security review 2026-07-02). Classes kept in-tree;
                // to restore, re-add AddScoped<...>() here, WithTools<...>() in the chain below, and the
                // backing DI (AddSingleton<IHostShellCommandService,...>() / AddWebResearchClient()).

                serviceCollection
                    .AddMcpServer(options =>
                    {
                        options.ServerInfo = new() { Name = "viberails-mcp", Version = "1.0.0" };
                    })
                    .WithHttpTransport()
                    .WithTools<RulesTool>()
                    .WithTools<SessionSearchTool>()
                    .WithTools<TokenSaverTool>();
            }

            // Claude Agent Sync Service (syncs CLAUDE.md to AGENTS.md on session lifecycle)
            serviceCollection.AddSingleton<IClaudeAgentSyncService, ClaudeAgentSyncService>();

            // Terminal Session Service (scoped to work with other scoped services)
            serviceCollection.AddScoped<ITerminalIoObserver, MyTerminalObserver>();
            serviceCollection.AddScoped<ITerminalIoObserver, SessionStateEventObserver>();
            serviceCollection.AddScoped<ITerminalIoObserver, GitDiffIdleCaptureObserver>();
            serviceCollection.AddSingleton<ITerminalIoObserver, WaitingForUserInputObserver>();

            serviceCollection.AddScoped<ITerminalIoObserverService, TerminalIoObserverService>();
            serviceCollection.AddScoped<ITerminalStateService, TerminalStateService>();
            serviceCollection.AddScoped<ICommandService, CommandService>();
            // One lazy scanner for raw PTY activity in this process. Only --job-run terminals call
            // RegisterSession, so normal Web/CLI hosts never start its timer.
            serviceCollection.AddSingleton<IAutomationConsumer, AutomationConsumer>();
            serviceCollection.AddScoped<TerminalRunner>();
            serviceCollection.AddScoped<ITerminalSessionService, TerminalSessionService>();
            serviceCollection.AddSingleton<ITerminalTabHostService, TerminalTabHostService>();
            serviceCollection.AddSingleton<ILocalClientTracker, LocalClientTracker>();
            serviceCollection.AddHostedService<LocalClientLifecycleWatchdogService>();

            // System resource monitor — injectable as ISystemResourceService, also runs as a hosted service.
            serviceCollection.AddSingleton<SystemResourceService>();
            serviceCollection.AddSingleton<ISystemResourceService>(sp => sp.GetRequiredService<SystemResourceService>());
            serviceCollection.AddHostedService(sp => sp.GetRequiredService<SystemResourceService>());

            if (isTerminalTabChildProcess)
            {
                Serilog.Log.Information(
                    "[Startup] Terminal tab child detected; global maintenance jobs disabled. processId={ProcessId}",
                    Environment.ProcessId);
                // Child-only: exit when the root backend is gone so an ungraceful root
                // crash doesn't leave orphan tab processes running.
                serviceCollection.AddHostedService<ChildParentWatchdogService>();
            }
            else
            {
                serviceCollection.AddHostedService<UpdateCheckJob>();
                serviceCollection.AddHostedService<StaleSessionCleanupJob>();
                serviceCollection.AddHostedService<ProjectCacheRefreshJob>();
                serviceCollection.AddHostedService<BertEmbeddingBackfillJob>();
                serviceCollection.AddHostedService<SessionAggregateEmbeddingBackfillJob>();
                // Token-savings publish job — active root backends only. Multiple supported roots
                // can coexist (for example browser + VS Code), so the job also holds an OS-backed
                // lock across its refresh and absolute upsert. This branch still excludes
                // LMBootstrap (--env) processes, which have no global background-job role.
                if (isActiveRootBackendProcess)
                {
                    // A named client, not AddHttpClient<TokenSavingsPublishJob>: that registers a
                    // transient typed client nothing resolves, while AddHostedService<T> activates
                    // its own singleton from the container — which would silently get the default
                    // unnamed HttpClient (100s timeout, redirects on) instead of this one.
                    // Full URLs are sent per request, so BaseAddress is deliberately left null.
                    serviceCollection
                        .AddHttpClient(TokenSavingsPublishJob.HttpClientName, client =>
                        {
                            // 30s so a stalled endpoint can't starve the timer thread.
                            client.Timeout = TimeSpan.FromSeconds(30);
                        })
                        .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);
                    serviceCollection.AddHostedService<TokenSavingsPublishJob>();
                }
            }

            serviceCollection.AddScoped<ISessionTranscriptService, SessionTranscriptService>();
            serviceCollection.AddScoped<ISessionResumeService, SessionResumeService>();

            serviceCollection.AddSingleton<IAppEventBus, AppEventBus>();
            serviceCollection.AddSingleton<AppEventWebSocketHandler>();

            // Remote State Service (for terminal session remote registration)
            serviceCollection.AddHttpClient<IRemoteStateService, RemoteStateService>();

            // Debug Bundle Service (builds + encrypts + uploads a session bundle for remote debugging)
            serviceCollection.AddHttpClient<IDebugBundleService, DebugBundleService>();

            // Progress for the running export, polled by the settings modal. Singleton because a
            // process-wide gate means only one export can ever be in flight here.
            serviceCollection.AddSingleton<IDataExportProgress, DataExportProgress>();

            // Complete state.db export. Large databases are streamed, so allow a deliberate
            // long-running upload window while still propagating request cancellation.
            serviceCollection
                .AddHttpClient<IDataExportService, DataExportService>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(30);
                })
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            // Push Notification Service (forwards per-tab "ready/waiting" pushes to VibeRails-Front)
            serviceCollection.AddHttpClient<IPushNotificationService, PushNotificationService>();

            // Update Service (singleton with HttpClient)
            serviceCollection.AddHttpClient<UpdateService>();

            // Dedicated persistent HTTP-over-WSS relay. It connects on first use and is reset by
            // settings changes so API-key rotation is reflected by the next handshake.
            serviceCollection.AddSingleton<IRemoteHttpRelayClient, RemoteHttpRelayClient>();

            // WebSocket Messaging Client (singleton - auto-reconnects, URL from appsettings.json)
            serviceCollection.AddSingleton<MessagingClient>(sp =>
            {
                var configuration = sp.GetRequiredService<IConfiguration>();
                var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
                return new MessagingClient(frontendUrl);
            });

            // Authentication service (singleton - one token per instance)
            serviceCollection.AddSingleton<IAuthService, AuthService>();
            serviceCollection.AddSingleton<IAuthBootstrapService, AuthBootstrapService>();
            serviceCollection.AddSingleton<IUnconsumedBootstrapCodeShutdownWatchdog, UnconsumedBootstrapCodeShutdownWatchdog>();
        }

        internal static bool IsTerminalTabChildProcess(IEnumerable<string> args)
        {
            return args.Any(static arg =>
                arg.Equals("--parent-pid", StringComparison.OrdinalIgnoreCase) ||
                arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Shared by every client that sends the user's API key in an <c>X-Api-Key</c> header.
        /// Unlike <c>Authorization</c>, custom headers are NOT stripped when a redirect crosses to
        /// another host, so following one would replay the secret (and, for the export, the whole
        /// database body) to wherever the response pointed. 3xx is treated as a plain failure.
        /// </summary>
        internal static HttpMessageHandler CreateNoRedirectHttpMessageHandler() =>
            new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

        /// <summary>
        /// True only for a long-lived root backend that may coordinate global Automation work.
        /// Kept pure so process-role behavior can be pinned without starting DI or Kestrel.
        /// </summary>
        internal static bool IsActiveRootBackendProcess(IEnumerable<string> args)
        {
            var arguments = args as string[] ?? args.ToArray();
            return !IsTerminalTabChildProcess(arguments)
                   && !ArgumentParser.Parse(arguments).IsLMBootstrap;
        }

    }
}



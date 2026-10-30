using VibeRails.Auth;
using VibeRails.Data.Sqlite;
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
using VibeRails.Services.Cli;
using VibeRails.Services.Environments;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.FileSystem;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Mcp;
using VibeRails.Services.AzureDevOps;
using VibeRails.Services.Jira;
using VibeRails.Services.Mcp.HostShell;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Mcp.WebResearch;
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
using VibeRails.Services.Diagnostics;

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

            // Published so route mapping gates on the answer resolved here rather than deriving its
            // own from a different argv array. See <see cref="ProcessRole"/>.
            serviceCollection.AddSingleton(
                new ProcessRole(isActiveRootBackendProcess, isTerminalTabChildProcess));

            // Feature logging is explicitly opted into by callers. Only root dashboards own
            // its bounded background writer; terminal children do not add a logging worker.
            if (isActiveRootBackendProcess)
            {
                serviceCollection.AddSingleton<FeatureLogService>();
                serviceCollection.AddSingleton<IFeatureLog>(sp => sp.GetRequiredService<FeatureLogService>());
                serviceCollection.AddSingleton<IFeatureLogReader>(sp => sp.GetRequiredService<FeatureLogService>());
                serviceCollection.AddSingleton<IDiagnosticLogReader, DiagnosticLogReader>();
                serviceCollection.AddHostedService(sp => sp.GetRequiredService<FeatureLogService>());
            }
            else
            {
                serviceCollection.AddSingleton<IFeatureLog>(NullFeatureLog.Instance);
            }

            // Summary, terminal registration and push send X-Api-Key, so like every credential
            // client they follow no redirects: an automatic redirect would replay the key and the
            // request body to wherever the response pointed (VIBE-109 review R1).
            serviceCollection.AddHttpClient<ISummaryService, SummaryService>(
                x =>
                {
                    x.BaseAddress = new Uri("https://viberails.ai");
                })
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            serviceCollection.AddScoped<IFileService, FileService>();

            if (isActiveRootBackendProcess)
            {
                serviceCollection.AddHttpClient("remote-account-link", client =>
                    client.Timeout = TimeSpan.FromSeconds(20))
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                    {
                        AllowAutoRedirect = false,
                        UseCookies = false
                    });
                serviceCollection.AddSingleton<IRemoteAccountKeyStore, ApiKeyStore>();
                serviceCollection.AddSingleton(sp => new RemoteAccountLinkService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("remote-account-link"),
                    new Uri(sp.GetRequiredService<IConfiguration>()["VibeRails:FrontendUrl"]
                        ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json")),
                    sp.GetRequiredService<IRemoteAccountKeyStore>(),
                    sp.GetRequiredService<IAppEventBus>()));
                serviceCollection.AddHttpClient("signing-key-registration")
                    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
                serviceCollection.AddSingleton<Services.SigningKeys.SigningKeyStore>();
                serviceCollection.AddSingleton(sp => new Services.SigningKeys.SigningKeyService(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("signing-key-registration"),
                    sp.GetRequiredService<Services.SigningKeys.SigningKeyStore>(), () => Config.LoadFresh().ApiKey));
                // Review requests before a commit (VB-50): the staged-snapshot reader and the commit
                // signature verifier come from AddGitPreflight below; the store from AddSqliteStorage.
                serviceCollection.AddSingleton(sp => new Services.ReviewRequests.ReviewRequestService(
                    sp.GetRequiredService<DB.IReviewRequestStore>(),
                    sp.GetRequiredService<Services.GitPreflight.IGitStagedSnapshotProvider>(),
                    sp.GetRequiredService<Services.VCA.ICommitSignatureVerifier>()));
                // Slice C: the fixed-origin transport (no redirects, no cookies), the sender behind the
                // disclosure/send/cancel routes, and the decision poller the Board sync scheduler paces.
                // The sender and poller are scoped because the session archive queue is the scoped repository.
                serviceCollection.AddHttpClient<Services.ReviewRequests.ReviewRequestTransport>(client =>
                        client.Timeout = Services.ReviewRequests.ReviewRequestTransport.SendDeadline)
                    .ConfigurePrimaryHttpMessageHandler(Services.ReviewRequests.ReviewRequestTransport.CreateHandler);
                serviceCollection.AddSingleton(_ => Services.ReviewRequests.ReviewRequestPollState.BesideStateDatabase());
                serviceCollection.AddScoped(sp => new Services.ReviewRequests.ReviewRequestSender(
                    sp.GetRequiredService<DB.IReviewRequestStore>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestService>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestTransport>(),
                    sp.GetRequiredService<DB.ISessionArchiveReader>(),
                    sp.GetRequiredService<IAppEventBus>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestPollState>(),
                    ParserConfigs.GetApiKey));
                serviceCollection.AddScoped(sp => new Services.ReviewRequests.ReviewRequestPoller(
                    sp.GetRequiredService<DB.IReviewRequestStore>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestService>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestTransport>(),
                    sp.GetRequiredService<DB.ISessionArchiveReader>(),
                    sp.GetRequiredService<IAppEventBus>(),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewRequestPollState>(),
                    ParserConfigs.GetApiKey));
                // Slice E: the Rules page reads the ledger the commit hook reads, through the same quiet,
                // bounded source (resolved only when a signature finding is deferred). Read-only.
                serviceCollection.AddSingleton<Services.VCA.Hooks.IStoredCommitApprovalSource>(sp =>
                    new Services.VCA.Hooks.ReviewRequestApprovalSource(() => sp.GetService<DB.IReviewRequestStore>()));
                // Slice D: the requests addressed to this account, refreshed by the scheduler on the same
                // cadence and answered from the "Reviews requested of you" page. The singleton holds the
                // popup states, so it builds a fresh transport per call from the typed client's factory
                // registration; the popup launcher is the only path to an approval and this never signs.
                serviceCollection.AddSingleton(_ => Services.ReviewRequests.ReviewInboxState.BesideStateDatabase());
                serviceCollection.AddSingleton<Services.ReviewRequests.IReviewSignaturePopupLauncher,
                    Services.ReviewRequests.ConsoleReviewSignaturePopupLauncher>();
                serviceCollection.AddSingleton(sp => new Services.ReviewRequests.ReviewInboxService(
                    () => new Services.ReviewRequests.ReviewRequestTransport(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(Services.ReviewRequests.ReviewRequestTransport))),
                    sp.GetRequiredService<Services.ReviewRequests.ReviewInboxState>(),
                    ParserConfigs.GetApiKey,
                    ApiKeyStore.CachedAccountEmail(ParserConfigs.GetApiKey, Config.LoadFresh),
                    sp.GetRequiredService<IAppEventBus>(),
                    sp.GetRequiredService<Services.ReviewRequests.IReviewSignaturePopupLauncher>()));
            }

            // Host filesystem metadata is exposed only by an active root backend. Terminal-tab
            // children and `vb --env` hosts never map the corresponding route.
            if (isActiveRootBackendProcess)
                serviceCollection.AddSingleton<IFileSystemBrowserService, FileSystemBrowserService>();

            // BERT (V2) — write path, read path, and search strategies are registered separately.
            serviceCollection.AddSingleton<IBertSettings, BertV2BgeSmallEnSettings>();
            serviceCollection.AddSingleton<IBertV2BgeEmbedder>(sp =>
            {
                var settings = sp.GetRequiredService<IBertSettings>();
                return new BertV2BgeEmbedder(settings.ModelPath, settings.VocabPath);
            });
            serviceCollection.AddSqliteStorage(_ => new SqliteStoragePaths(ParserConfigs.GetStatePath()));
            serviceCollection.AddSingleton<SearchTextChunker>();
            serviceCollection.AddSingleton<SearchIndexingService>(SearchIndexingService.Create);
            serviceCollection.AddSingleton<IBertDocumentResponseMapper, BertDocumentResponseMapper>();
            serviceCollection.AddSingleton<IBertCaptureQueryService, BertCaptureQueryService>();
            serviceCollection.AddSingleton<IBertSearchStrategy>(sp => new SemanticBertSearchStrategy(
                () => sp.GetRequiredService<IBertV2BgeEmbedder>(), sp.GetRequiredService<IBertSearchDbService>()));
            serviceCollection.AddSingleton<IBertSearchStrategy, TextBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchStrategy>(sp => new SemanticSessionBertSearchStrategy(
                () => sp.GetRequiredService<IBertV2BgeEmbedder>(), sp.GetRequiredService<IBertSearchDbService>()));
            serviceCollection.AddSingleton<IBertSearchStrategy, TextSessionBertSearchStrategy>();
            serviceCollection.AddSingleton<IBertSearchServiceV2, BertSearchServiceV2>();
            serviceCollection.AddSingleton<IUnifiedSearchService>(UnifiedSearchService.Create);

            serviceCollection.AddSingleton<IGitDiffCaptureService, GitDiffCaptureService>();
            serviceCollection.AddSingleton<ILlmParser, LlmParser>();
            serviceCollection.AddSingleton<AgentCredentialService>();
            serviceCollection.AddSingleton<SettingsPresenceService>();
            serviceCollection.AddSingleton<ILocalToolApiContext>(sp =>
                new LocalToolApiContext(localApiBaseUrl, sp.GetRequiredService<IAuthService>(), sp.GetRequiredService<AgentCredentialService>()));
            serviceCollection.AddSingleton<ILocalLlmProxyContext>(sp =>
                new LocalLlmProxyContext(localApiBaseUrl, sp.GetRequiredService<IAuthService>(), sp.GetRequiredService<AgentCredentialService>()));
            serviceCollection.AddHttpClient("llm-proxy").ConfigurePrimaryHttpMessageHandler(CreateLlmProxyHttpMessageHandler);
            // The Settings gateway Test button; it shares the relay's transport on purpose, so a
            // proxy that needs Windows sign-in answers the Test the way it answers the relay.
            serviceCollection.AddSingleton<EnterpriseNetworkProxyProbe>();
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
            // ICompressionCaptureSink is deliberately NOT registered: the per-tool_result capture
            // table was retired on 2026-09-17 (its re-sight UPDATE scanned CompressionCaptures under
            // state.db's writer lock, hundreds of times per request). The exchange log below holds
            // the same bytes.
            // Required by every proxy route: exchange logging has no settings/UI gate.
            serviceCollection.AddSingleton<ILlmProxyExchangeSink, LlmProxyExchangeSinkAdapter>();
            // SQLite stores are registered together above; board orchestration stays in the host.
            serviceCollection.AddSingleton<Services.Board.IBoardProjectResolver, Services.Board.BoardProjectResolver>();
            serviceCollection.AddSingleton<Services.Board.IBoardCommitService, Services.Board.BoardCommitService>();
            serviceCollection.AddScoped<Services.Board.IBoardService, Services.Board.BoardService>();
            serviceCollection.AddScoped<Services.Board.BoardReviewService>();
            serviceCollection.AddScoped<Services.Board.BoardWorkflowService>();
            // Jira Cloud pull (VB-40). The client follows redirects off and never logs the token.
            // The scheduler is ticked by the leased root job scheduler, so only one process pulls.
            serviceCollection.AddJiraClients();
            serviceCollection.AddScoped<Services.Jira.IJiraPullService, Services.Jira.JiraPullService>();
            serviceCollection.AddSingleton<Services.Jira.IJiraPullScheduler, Services.Jira.JiraPullScheduler>();
            serviceCollection.AddScoped<Services.Jira.IJiraSessionSharing, Services.Jira.JiraSessionSharing>();
            serviceCollection.AddScoped<Services.Jira.JiraDeliveryService>();
            // Azure DevOps pull, mirroring Jira: same client rules, its own lock and token file,
            // ticked beside the Jira pull by the leased root job scheduler.
            serviceCollection.AddAdoClients();
            serviceCollection.AddScoped<Services.AzureDevOps.IAdoPullService, Services.AzureDevOps.AdoPullService>();
            serviceCollection.AddSingleton<Services.AzureDevOps.IAdoPullScheduler, Services.AzureDevOps.AdoPullScheduler>();
            serviceCollection.AddScoped<Services.AzureDevOps.IAdoSessionSharing, Services.AzureDevOps.AdoSessionSharing>();
            serviceCollection.AddScoped<Services.AzureDevOps.AdoDeliveryService>();
            // Board sync with viberails.ai (VB-51). Outbound only, HTTPS, X-Api-Key, redirects off;
            // the API key is re-read from settings on every call. Ticked every 60 s by the leased
            // root job scheduler beside the Jira pull, and one OS lock across root backends.
            serviceCollection.AddHttpClient(Services.Board.Sync.BoardSyncHttpClient.HttpClientName, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            }).ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);
            serviceCollection.AddSingleton<Services.Board.Sync.IBoardSyncClient>(sp =>
                Services.Board.Sync.BoardSyncHttpClient.FromConfiguration(
                    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<IConfiguration>()));
            serviceCollection.AddSingleton(_ => Services.Board.Sync.BoardSyncLock.BesideStateDatabase());
            serviceCollection.AddSingleton<Services.Board.Sync.BoardSyncActivityCache>();
            serviceCollection.AddScoped<Services.Board.Sharing.CardShareCapture>();
            serviceCollection.AddScoped<Services.Board.Sharing.CardSharePublisher>();
            serviceCollection.AddSingleton(_ => Services.Board.Sharing.CardShareRefreshState.BesideStateDatabase());
            serviceCollection.AddHttpClient<Services.Board.Sharing.CardShareClient>(client => client.Timeout = TimeSpan.FromSeconds(90))
                .ConfigurePrimaryHttpMessageHandler(Services.Board.Sharing.CardShareClient.CreateHandler);
            serviceCollection.AddScoped<Services.Board.Sync.IBoardSyncService, Services.Board.Sync.BoardSyncService>();
            serviceCollection.AddScoped<Services.Board.Sync.BoardSharingService>();
            serviceCollection.AddScoped<Services.Board.Sync.RemoteBoardsService>();
            serviceCollection.AddSingleton<Services.Board.Sync.IBoardSyncScheduler, Services.Board.Sync.BoardSyncScheduler>();
            serviceCollection.AddScoped<Services.Board.BoardAutomationService>();
            serviceCollection.AddScoped<Services.Board.BoardCardAutomationService>();
            serviceCollection.AddScoped<Services.Board.BoardChecksReader>();
            // Run-once card post-processing (VIBE-185). Each IBoardCardPostProcessor registration is
            // one rule; the job below runs them on active roots only.
            serviceCollection.AddSingleton<Services.Board.PostProcessing.IBoardDefaultBranchCheck, Services.Board.PostProcessing.GitDefaultBranchCheck>();
            serviceCollection.AddSingleton<Services.Board.PostProcessing.IBoardCodeLinkLandingCheck, Services.Board.PostProcessing.GitHubCodeLinkLandingCheck>();
            serviceCollection.AddSingleton<Services.Board.PostProcessing.GhostCardPostProcessor>();
            serviceCollection.AddSingleton<Services.Board.PostProcessing.IBoardCardPostProcessor>(sp => sp.GetRequiredService<Services.Board.PostProcessing.GhostCardPostProcessor>());
            serviceCollection.AddSingleton<Services.Board.PostProcessing.BoardGhostService>();
            serviceCollection.AddSingleton<Services.Board.PostProcessing.BoardCardPostProcessingService>();
            // Singleton: it holds the ten-second repo file-list cache behind the composer's `@` typeahead.
            serviceCollection.AddSingleton<Services.Board.IBoardFileIndexService, Services.Board.BoardFileIndexService>();
            serviceCollection.AddSingleton<Services.Jobs.AutomationScriptCatalogService>();
            if (isActiveRootBackendProcess)
            {
                serviceCollection.AddSingleton<Services.Board.IBoardLiveSessionProbe, Services.Board.TerminalTabLiveSessionProbe>();
                serviceCollection.AddScoped<Services.Board.IBoardLaunchService, Services.Board.BoardLaunchService>();
                serviceCollection.AddScoped<Services.Board.Sync.BoardRemoteLaunchService>();
                if (!isFakeCliTestProcess)
                    serviceCollection.AddHostedService<Services.Board.Sync.BoardRemoteLaunchHostedService>();
                // Agent-context measurement (VB-63): the card editor's estimate and the sample each
                // launch records. Scoped like the launch service; it reads the scoped IRepository.
                serviceCollection.AddScoped<Services.Board.IBoardContextEstimator, Services.Board.BoardContextEstimator>();
            }
            else
            {
                serviceCollection.AddSingleton<Services.Board.IBoardLiveSessionProbe, Services.Board.NullBoardLiveSessionProbe>();
            }
            serviceCollection.AddAutomationRuntime(
                hostScheduler: isActiveRootBackendProcess && !isFakeCliTestProcess);
            // Scoped because it reads steps through the scoped IRepository, matching TerminalRunner
            // (its only launch-path consumer) so both live in the same scope.
            serviceCollection.AddScoped<IEnvironmentStepRunner, EnvironmentStepRunner>();
            // Resolves {{datetime}}-style built-ins and {{step:<id>}} output injection in an
            // environment's Initial Message. Scoped for the same IRepository reason as above.
            serviceCollection.AddScoped<IPromptPlaceholderService, PromptPlaceholderService>();
            serviceCollection.AddSingleton<Services.Workspaces.IManagedWorktreeService, Services.Workspaces.ManagedWorktreeService>();
            // Playwright's real backend uses a fake CLI but shares the developer account's state
            // directory. Never let that test host dispatch persisted Automation work.
            if (isActiveRootBackendProcess && isFakeCliTestProcess)
            {
                // An env var silently disabling the scheduler in a real environment would look
                // exactly like "Automations never fire". Make the suppression loud and findable.
                Serilog.Log.Warning(
                    "[Jobs] Automation scheduler NOT registered: VIBERAILS_TEST_FAKE_CLI=1 marks this as a UI-test host");
            }
            serviceCollection.AddScoped<IJobService, JobService>();
            // Cross-repository Automation import (root dashboard only; the routes are gated, the
            // registration is harmless elsewhere). Scoped for the same IRepository reason as above.
            serviceCollection.AddScoped<IAutomationImportService, AutomationImportService>();
            serviceCollection.AddScoped<IAgentTerminalToolService, AgentTerminalToolService>();
            serviceCollection.AddScoped<ILlmPickerPreferenceService, LlmPickerPreferenceService>();
            serviceCollection.AddScoped<IAutomationNavPreferenceService, AutomationNavPreferenceService>();
            // Python script signing: file-based state + a lazily-probed interpreter. Singleton so
            // the run-history and the signing-file lock live in one place. Factory form on
            // purpose: Discover() probes the filesystem/PATH for an interpreter, and that must
            // happen on the first actual run (not list/sign), nor during startup of
            // every vb process (terminal tab children register services too).
            serviceCollection.AddSingleton<Services.PythonScripts.IPythonScriptService>(sp =>
                new Services.PythonScripts.PythonScriptService(
                    pythonRunnerProvider: () => new PyBridge.PythonRunner(
                        Services.PythonScripts.PythonScriptService.DiscoverPythonRunnerOptions(
                            new Services.PythonScripts.PythonScriptService().GetScriptsDirectory())),
                    firstPinPresenceVerifier: proof => sp.GetRequiredService<SettingsPresenceService>()
                        .Verify(proof, Config.LoadFresh())));
            serviceCollection.AddScoped<IGetUserText, GetUserText>();
            serviceCollection.AddScoped<IProjectCache, ProjectCache>();
            serviceCollection.AddScoped<IGlobalCache, GlobalCache>();
            serviceCollection.AddScoped<Services.Setup.ISetupStatusService, Services.Setup.SetupStatusService>();
            // PR review tracker: routes are root-only, the registration is harmless elsewhere.
            // Storage comes from AddSqliteStorage above.
            Services.PrReviews.PrReviewServiceCollectionExtensions.AddPrReviews(serviceCollection);
            serviceCollection.AddScoped<IChatHistoryService, ChatHistoryService>();
            serviceCollection.AddSingleton<ISessionOutputParser, SessionParseV4>();
            serviceCollection.AddScoped<IGitService, GitService>();
            serviceCollection.AddSingleton<Services.CodeReports.RepositoryCodeGraph>();
            serviceCollection.AddSingleton<Services.CodeReports.WorkingTreeChanges>();

            // Rules and rule-file services
            serviceCollection.AddScoped<IRulesService, RulesService>();
            serviceCollection.AddScoped<IAgentFileService, AgentFileService>();

            // VCA Validation services (for git hooks)
            serviceCollection.AddScoped<IRuleValidationService, RuleValidationService>();
            serviceCollection.AddScoped<IHookInstallationService, HookInstallationService>();
            serviceCollection.AddGitPreflight();
            serviceCollection.AddScoped<Services.Mcp.Tools.RulesTool>();

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
            serviceCollection.AddScoped<IGrokLlmCliEnvironment, GrokLlmCliEnvironment>();
            serviceCollection.AddScoped<LlmCliEnvironmentService>();

            // In-process MCP server, exposed over HTTP at /mcp (see app.MapMcp in Program.cs).
            // Only the root backend hosts it — terminal-tab child processes skip it so we don't
            // stand up a redundant MCP endpoint per tab. The MapMcp call is gated the same way,
            // so resolving these services and mapping the endpoint always happen together.
            if (!isTerminalTabChildProcess)
            {
                // SessionSearchTool is an instance tool with constructor injection; register it
                // so the MCP server can resolve it (and its IUnifiedSearchService dependency)
                // from the per-request scope.
                serviceCollection.AddScoped<SessionSearchTool>(SessionSearchTool.Create);
                serviceCollection.AddScoped<Services.Board.BoardRecallService>(Services.Board.BoardRecallService.Create);
                serviceCollection.AddScoped<Services.Board.BoardSearchService>(Services.Board.BoardSearchService.Create);
                // Same story for TokenSaverTool (ctor-injected IHttpClientFactory). Its named client
                // is short-timeout because every call is a loopback hop to this machine's proxy.
                // Registered here AND in McpStdioHost.ConfigureServices — the two transports must
                // expose the same tools.
                serviceCollection.AddTokenSaverMcp();
                serviceCollection.AddAgentSessionMcp();
                serviceCollection.AddSessionSharingMcp();
                serviceCollection.AddReviewRequestMcp();
                serviceCollection.AddBoardAgentLaunchMcp();
                serviceCollection.AddDesktopMcpActivity(stdio: false);
                // Kanban board tools (ctor-injected board services). Registered here AND in
                // McpStdioHost.ConfigureServices — the two transports must expose the same tools.
                serviceCollection.AddScoped<BoardTool>();
                // PR review tools. Registered here AND in McpStdioHost.ConfigureServices.
                serviceCollection.AddScoped<PrReviewTool>();
                // run_shell_command (HostShellTools) and web_search/web_fetch (WebResearchTools) are
                // intentionally not exposed for now (security review 2026-07-02). Classes kept in-tree;
                // to restore, re-add AddScoped<...>() here, WithTools<...>() in the chain below, and the
                // backing DI (AddSingleton<IHostShellCommandService,...>() / AddWebResearchClient()).

                serviceCollection
                    .AddMcpServer(options =>
                    {
                        options.ServerInfo = new() { Name = "viberails-mcp", Version = "1.0.0" };
                    })
                    .WithVibeRailsHttpTransport()
                    .WithVibeRailsTools();
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
            serviceCollection.AddSingleton<AgentSessionEndScheduler>();
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
                // Token-savings publish job — active root backends only. Multiple supported roots
                // can coexist (for example browser + VS Code), so the job also holds an OS-backed
                // lock across its refresh and absolute upsert. This branch still excludes
                // LMBootstrap (--env) processes, which have no global background-job role.
                if (isActiveRootBackendProcess)
                {
                    if (!isFakeCliTestProcess) serviceCollection.AddHostedService<WorktreeCleanupJob>();
                    // A UI-test host shares the developer's board.db; it must not process real cards.
                    if (!isFakeCliTestProcess) serviceCollection.AddHostedService<BoardCardPostProcessingJob>();
                    serviceCollection.AddHttpClient(ConversationTitleClient.HttpClientName,
                            client => client.Timeout = TimeSpan.FromSeconds(20))
                        .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
                        {
                            AllowAutoRedirect = false,
                            UseCookies = false
                        })
                        .RemoveAllLoggers();
                    serviceCollection.AddSingleton(sp => new ConversationTitleClient(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient(ConversationTitleClient.HttpClientName)));
                    serviceCollection.AddScoped<ConversationTitleService>();
                    serviceCollection.AddHostedService<ConversationTitleJob>();
                    serviceCollection.AddHostedService<BertEmbeddingBackfillJob>();
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
                    serviceCollection.AddHostedService<SessionDataDrainJob>();
                    serviceCollection.AddHostedService<BoardDataBackupJob>();
                    // Full snapshot scheduling retired; session drain and Board content sync remain active.
                    serviceCollection.AddHostedService<DataRetentionJob>();
                    serviceCollection.AddHostedService<SearchIndexMaintenanceJob>();
                }
            }

            serviceCollection.AddScoped<ISessionTranscriptService, SessionTranscriptService>();
            serviceCollection.AddScoped<ISessionResumeService, SessionResumeService>();

            serviceCollection.AddSingleton<IAppEventBus, AppEventBus>();
            serviceCollection.AddSingleton<AppEventWebSocketHandler>();

            // Remote State Service (for terminal session remote registration); sends X-Api-Key.
            serviceCollection.AddHttpClient<IRemoteStateService, RemoteStateService>()
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            serviceCollection.AddHttpClient<SessionSharingService>(client => client.Timeout = TimeSpan.FromSeconds(20))
                .ConfigurePrimaryHttpMessageHandler(SessionSharingService.CreateHandler);

            // Incremental session export. The service creates its own repository scopes and owns
            // both process/cross-process gates, so it is safe for the singleton drain job.
            serviceCollection
                .AddHttpClient<ISessionDataExportService, SessionDataExportService>(client =>
                {
                    client.Timeout = TimeSpan.FromMinutes(30);
                })
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            serviceCollection.AddSingleton<Services.Backups.BackupFiles>();
            serviceCollection.AddSingleton<Services.Backups.CompleteBackupService>();
            serviceCollection.AddSingleton<Services.Board.Backup.BoardBackupTransport>();
            serviceCollection.AddSingleton<Services.Board.Backup.BoardDataBackupService>();
            serviceCollection.AddHttpClient(Services.Board.Backup.BoardBackupTransport.ClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(2))
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);
            // A named client, not AddHttpClient<BackupTransport>: the transient typed client would be captured
            // once by the singleton CompleteBackupService and its handler never rotated (see TokenSavingsPublishJob).
            serviceCollection.AddSingleton<Services.Backups.BackupTransport>();
            serviceCollection.AddHttpClient(Services.Backups.BackupTransport.HttpClientName, client =>
                client.Timeout = TimeSpan.FromMinutes(2))
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            // Push Notification Service (forwards per-tab "ready/waiting" pushes to VibeRails-Front); sends X-Api-Key.
            serviceCollection.AddHttpClient<IPushNotificationService, PushNotificationService>()
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            // Update Service (singleton with HttpClient)
            serviceCollection.AddHttpClient<UpdateService>()
                .ConfigurePrimaryHttpMessageHandler(CreateNoRedirectHttpMessageHandler);

            // Dedicated persistent HTTP-over-WSS relay. It connects on first use and is reset by
            // settings changes so API-key rotation is reflected by the next handshake.
            serviceCollection.AddSingleton<IRemoteHttpRelayClient, RemoteHttpRelayClient>();

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
        /// another host, so following one would replay the secret and request body to wherever the
        /// response pointed. 3xx is treated as a plain failure.
        /// </summary>
        internal static HttpMessageHandler CreateNoRedirectHttpMessageHandler() => CreateCredentialSafeHandler();

        private static HttpClientHandler CreateCredentialSafeHandler() =>
            new()
            {
                AllowAutoRedirect = false,
                UseCookies = false
            };

        /// <summary>
        /// The LLM relay's transport (<c>llm-proxy</c>): no redirects, no cookies, like every
        /// credential client, and the signed-in Windows account for a system or environment proxy
        /// that answers 407. .NET offers them only to the default (system or environment) proxy and
        /// only after it challenges; they are usable for NTLM, Negotiate or Kerberos, which are
        /// challenge-response, and carry no password a Basic or Digest challenge could obtain. On
        /// Linux it works only with Kerberos (GSSAPI) configured and is otherwise inert. Scoped to
        /// this client, not the shared X-Api-Key handler above; see API_SEC.md.
        /// </summary>
        internal static HttpMessageHandler CreateLlmProxyHttpMessageHandler()
        {
            var handler = CreateCredentialSafeHandler();
            handler.DefaultProxyCredentials = System.Net.CredentialCache.DefaultCredentials;
            return handler;
        }

        /// <summary>
        /// True only for a long-lived root backend that may coordinate global Automation work.
        /// Kept pure so process-role behavior can be pinned without starting DI or Kestrel.
        /// </summary>
        internal static bool IsActiveRootBackendProcess(IEnumerable<string> args)
        {
            var arguments = args as string[] ?? args.ToArray();
            var parsed = ArgumentParser.Parse(arguments);
            return !IsTerminalTabChildProcess(arguments)
                   && !parsed.IsLMBootstrap
                   && parsed.JobRunId is null;
        }

    }
}

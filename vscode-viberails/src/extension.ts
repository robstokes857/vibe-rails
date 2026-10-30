import * as vscode from 'vscode';
import * as http from 'http';
import * as path from 'path';
import * as fs from 'fs';
import { BackendManager } from './backend-manager';
import { WebviewPanelManager } from './webview-panel';
import { resolveScriptImportTarget } from './script-import';
import { BackendConnection, requestJson } from './backend-api';
import { LauncherItem, LauncherNode, LauncherTreeDataProvider, runLauncherItem } from './launcher-view';
import {
    ReviewInboxActionDependencies, ReviewInboxRow, ReviewInboxTreeDataProvider,
    approveReviewRequest, readReviewRequest, rejectReviewRequest
} from './review-inbox-view';
import { ReviewRequestRow, ReviewRequestsTreeDataProvider } from './review-requests-view';
import { bindSidebarTreeView } from './sidebar-tree-view';
import {
    AUTOMATION_NAV_PREFERENCES_PATH,
    BOOTSTRAP_REQUEST_TIMEOUT_MS,
    BOOTSTRAP_RETRY_ATTEMPTS,
    BOOTSTRAP_RETRY_DELAY_MS,
    COMMAND_OPEN,
    COMMAND_ADD_SCRIPT,
    COMMAND_LAUNCHER_CUSTOMIZE,
    COMMAND_LAUNCHER_MANAGE,
    COMMAND_LAUNCHER_REFRESH,
    COMMAND_LAUNCHER_RUN,
    COMMAND_REVIEW_INBOX_APPROVE,
    COMMAND_REVIEW_INBOX_OPEN,
    COMMAND_REVIEW_INBOX_READ,
    COMMAND_REVIEW_INBOX_REFRESH,
    COMMAND_REVIEW_INBOX_REJECT,
    COMMAND_REVIEW_REQUESTS_OPEN,
    COMMAND_REVIEW_REQUESTS_REFRESH,
    COMMAND_STOP,
    COMMAND_TEST_CONNECTION_INFO,
    COMMAND_TEST_LAUNCHER_ITEMS,
    CONFIG_SECTION,
    CONFIG_AUTO_START,
    CONFIG_STARTUP_TIMEOUT_MS,
    CONTEXT_BACKEND_READY,
    DEFAULT_STARTUP_TIMEOUT_MS,
    HEALTH_CHECK_ATTEMPTS,
    HEALTH_CHECK_DELAY_MS,
    HEALTH_CHECK_TIMEOUT_MS,
    HEALTH_PATH,
    LAUNCHER_STATUS_MESSAGE_MS,
    LAUNCHER_VIEW_ID,
    MAX_STARTUP_TIMEOUT_MS,
    MIN_STARTUP_TIMEOUT_MS,
    REVIEW_INBOX_PATH,
    REVIEW_INBOX_VIEW_ID,
    REVIEW_REQUESTS_PATH,
    REVIEW_REQUESTS_VIEW_ID,
    SESSION_COOKIE_NAME,
    SMOKE_WORKSPACE_ENV,
    TAB_TOKEN_HEADER,
} from './constants';

interface BundledAssets {
    target: string;
    exePath: string;
    wwwrootPath: string;
}

interface TestConnectionInfo {
    port: number | null;
    sessionToken: string | null;
    tabToken: string | null;
    webviewVisible: boolean;
    webviewOpen: boolean;
    backendReady: boolean;
}

/** Marks a bootstrap failure that came back as an HTTP response — never worth retrying. */
const BOOTSTRAP_STATUS_ERROR = 'BootstrapStatusError';
/** A timeout may occur after the server consumed the one-time code, so it is unsafe to retry. */
const BOOTSTRAP_AMBIGUOUS_ERROR = 'BootstrapAmbiguousError';
const SESSION_COOKIE_PATTERN = new RegExp(
    `(?:^|;\\s*)${escapeRegExp(SESSION_COOKIE_NAME)}=([^;]+)`
);

let backendManager: BackendManager | null = null;
let webviewManager: WebviewPanelManager | null = null;
let statusBarItem: vscode.StatusBarItem | null = null;
let stopBarItem: vscode.StatusBarItem | null = null;
let launcherProvider: LauncherTreeDataProvider | null = null;
let reviewInboxProvider: ReviewInboxTreeDataProvider | null = null;
let reviewRequestsProvider: ReviewRequestsTreeDataProvider | null = null;
let closingPromise: Promise<void> | null = null;
let openingPromise: Promise<void> | null = null;
let startingPromise: Promise<BackendConnection> | null = null;
let startupAbort: AbortController | null = null;
let backendReady = false;
let deactivating = false;
/** Launcher rows with a run in flight (see runLauncherItem). */
const runningLauncherKeys = new Set<string>();

export async function activate(context: vscode.ExtensionContext): Promise<void> {
    deactivating = false;
    const registeredCommands = new Set(await vscode.commands.getCommands(true));
    if (deactivating) return;
    statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 1000);
    statusBarItem.text = "$(terminal) VibeRails";
    statusBarItem.tooltip = "Open VibeRails Dashboard";
    statusBarItem.command = COMMAND_OPEN;
    statusBarItem.color = '#c084fc';
    statusBarItem.show();
    context.subscriptions.push(statusBarItem);

    stopBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 999);
    stopBarItem.text = "$(close)";
    stopBarItem.tooltip = "Stop VibeRails";
    stopBarItem.command = COMMAND_STOP;
    stopBarItem.color = '#c084fc';
    context.subscriptions.push(stopBarItem);

    registerLauncherView(context, registeredCommands);
    registerReviewInboxView(context, registeredCommands);
    registerReviewRequestsView(context, registeredCommands);

    const openCommand = vscode.commands.registerCommand(COMMAND_OPEN, async () => {
        try {
            await openDashboard(context);
        } catch (error) {
            if (closingPromise || deactivating) return;
            const message = error instanceof Error ? error.message : String(error);
            vscode.window.showErrorMessage(`Failed to open VibeRails Dashboard: ${message}`);
        }
    });

    const stopCommand = vscode.commands.registerCommand(COMMAND_STOP, async () => {
        await closeDashboard(true, false);
    });

    context.subscriptions.push(openCommand);
    context.subscriptions.push(stopCommand);
    context.subscriptions.push(vscode.commands.registerCommand(COMMAND_ADD_SCRIPT, async (uri?: vscode.Uri) => {
        try {
            const target = await resolveScriptImportTarget(uri);
            await openDashboard(context, target.projectFolder);
            if (!webviewManager) throw new Error('The VibeRails dashboard was closed. Try again.');
            await webviewManager.importScript(target.filePath, getStartupTimeoutMs());
        } catch (error) {
            const message = error instanceof Error ? error.message : String(error);
            void vscode.window.showErrorMessage(`Could not add VibeRails script: ${message}`);
        }
    }));
    registerTestCommands(context);
    context.subscriptions.push({
        dispose: () => {
            deactivating = true;
            void closeDashboard(false, true);
        }
    });
    context.subscriptions.push(
        vscode.workspace.onDidGrantWorkspaceTrust(() => autoStartBackend(context)),
        vscode.workspace.onDidChangeWorkspaceFolders(() => autoStartBackend(context)),
        vscode.workspace.onDidChangeConfiguration(event => {
            if (event.affectsConfiguration(`${CONFIG_SECTION}.${CONFIG_AUTO_START}`)) autoStartBackend(context);
        })
    );
    autoStartBackend(context);
}

/** Warm the API without creating a webview or delaying extension activation. */
function autoStartBackend(context: vscode.ExtensionContext): void {
    if (deactivating || closingPromise || !vscode.workspace.isTrusted || !getCurrentWorkspaceFolder(context)
        || !vscode.workspace.getConfiguration(CONFIG_SECTION).get<boolean>(CONFIG_AUTO_START, true)) return;

    void startBackend(context).catch(error => {
        if (deactivating || closingPromise) return;
        const message = error instanceof Error ? error.message : String(error);
        void vscode.window.showWarningMessage(`VibeRails could not start automatically: ${message}. Open the dashboard to retry.`);
    });
}

async function closeDashboard(showMessage: boolean, shutdownBackend: boolean): Promise<void> {
    if (closingPromise) {
        await closingPromise;
        return;
    }

    // Publish the stop first, then cancel startup and begin shutdown immediately.
    // VS Code allows only five seconds for deactivation; startup may never finish.
    closingPromise = Promise.resolve().then(async () => {
        startupAbort?.abort();
        disposeDashboardPanel();
        backendReady = false;
        refreshBackendViews();

        const manager = backendManager;
        let stopping: Promise<void> | undefined;
        if (manager) {
            if (shutdownBackend) {
                if (backendManager === manager) { backendManager = null; }
                stopping = manager.shutdown();
            } else {
                stopping = manager.stop();
            }
        }
        // Startup is abortable even if an underlying launch/HTTP continuation is
        // delayed. Those continuations also check the signal before mutating state.
        await Promise.allSettled([startingPromise, openingPromise]);
        await stopping;

        if (showMessage) {
            vscode.window.showInformationMessage('VibeRails closed');
        }
    });

    try {
        await closingPromise;
    } finally {
        closingPromise = null;
    }
}

async function openDashboard(context: vscode.ExtensionContext, projectFolder?: string): Promise<void> {
    if (closingPromise) await closingPromise;
    if (deactivating) return;
    if (openingPromise) return openingPromise;
    openingPromise = createDashboard(context, projectFolder);
    try {
        await openingPromise;
    } finally {
        openingPromise = null;
    }
}

async function createDashboard(context: vscode.ExtensionContext, projectFolder?: string): Promise<void> {
    if (webviewManager?.hasPanel()) {
        webviewManager.reveal();
        return;
    }

    const bundledAssets = resolveBundledAssets(context);
    await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: 'Starting VibeRails...',
        cancellable: false
    }, async (progress) => {
        progress.report({ message: 'Preparing backend...' });
        const { port, sessionToken, tabToken } = await startBackend(context, projectFolder);
        if (closingPromise || deactivating) return;

        progress.report({ message: 'Creating dashboard...' });
        const panel = new WebviewPanelManager(bundledAssets.wwwrootPath);
        webviewManager = panel;

        panel.onCloseRequested(() => { void closeDashboard(true, false); });
        panel.onDidClose(() => { if (webviewManager === panel) disposeDashboardPanel(); });
        panel.onLauncherChanged(() => { void launcherProvider?.refresh(); });
        panel.onReviewRequestChanged((change) => { void reviewRequestsProvider?.handleChange(change); });

        await panel.create(port, sessionToken, tabToken);
    });
}

function disposeDashboardPanel(): void {
    const panel = webviewManager;
    webviewManager = null;
    panel?.dispose();
}

/** Share the complete start/bootstrap/health handshake with every caller. */
function startBackend(context: vscode.ExtensionContext, projectFolder?: string): Promise<BackendConnection> {
    if (closingPromise || deactivating) return Promise.reject(new Error('VibeRails is stopping.'));
    if (startingPromise) return startingPromise;
    if (isBackendReady()) return Promise.resolve(getBackendConnection());

    const controller = new AbortController();
    startupAbort = controller;
    const { signal } = controller;
    const startup = (async () => {
        if (!vscode.workspace.isTrusted) throw new Error('Trust this workspace before starting VibeRails.');
        const assets = resolveBundledAssets(context);
        const manager = await ensureBackendManager(assets.exePath);
        try {
            signal.throwIfAborted();
            await manager.start(projectFolder ?? getCurrentWorkspaceFolder(context), getStartupTimeoutMs(), signal);
            signal.throwIfAborted();
            const port = manager.getPort();
            const host = manager.getHost();
            const bootstrapUrl = manager.getBootstrapUrl();
            if (!port || !host || !bootstrapUrl) throw new Error('Backend did not provide a loopback connection.');

            // Consume the expiring, single-use code immediately, even if no panel will
            // be opened for hours. All later callers reuse these process-lifetime tokens.
            let { sessionToken, tabToken } = manager.getAuthTokens();
            if (!sessionToken || !tabToken) {
                const fetched = await fetchTokensWithRetry(bootstrapUrl, signal);
                signal.throwIfAborted();
                sessionToken = fetched.sessionToken;
                tabToken = fetched.tabToken;
                manager.setAuthTokens(sessionToken, tabToken);
            }
            await waitForHealthy(host, port, signal);
            signal.throwIfAborted();
            if (!manager.isRunning()) throw new Error('VibeRails stopped during startup.');
            backendReady = true;
            if (!closingPromise && !deactivating) refreshBackendViews();
            return { host, port, sessionToken, tabToken };
        } catch (error) {
            // A failed/ambiguous bootstrap cannot reuse its one-time code on retry.
            // Close owns cleanup after cancellation. A late continuation must not
            // stop a newer launch that reused the manager after an explicit Stop.
            if (!signal.aborted) {
                backendReady = false;
                await manager.stop();
                refreshBackendViews();
            }
            throw error;
        }
    })();
    startingPromise = abortableStartup(startup, signal).finally(() => {
        if (startupAbort === controller) startupAbort = null;
        startingPromise = null;
    });
    return startingPromise;
}

/** Stop waiting immediately while still observing any late launch rejection. */
function abortableStartup<T>(work: Promise<T>, signal: AbortSignal): Promise<T> {
    return new Promise((resolve, reject) => {
        const aborted = () => reject(signal.reason);
        signal.addEventListener('abort', aborted, { once: true });
        work.then(resolve, reject).finally(() => signal.removeEventListener('abort', aborted));
        if (signal.aborted) aborted();
    });
}

function refreshBackendViews(): void {
    const ready = isBackendReady();
    if (ready) stopBarItem?.show();
    else stopBarItem?.hide();
    void vscode.commands.executeCommand('setContext', CONTEXT_BACKEND_READY, ready);
    void launcherProvider?.refresh();
    void reviewInboxProvider?.refresh();
    void reviewRequestsProvider?.refresh();
}

// --- Activity Bar launcher view -------------------------------------------

/**
 * The side-bar twin of the dashboard's nav Play button. The tree lists the same catalog;
 * running, customizing and managing behave like the flyout's rows and footer.
 */
function registerLauncherView(context: vscode.ExtensionContext, registeredCommands: ReadonlySet<string>): void {
    void vscode.commands.executeCommand('setContext', CONTEXT_BACKEND_READY, false);

    const provider = new LauncherTreeDataProvider({
        isBackendReady,
        fetchItems: () => requestJson(getBackendConnection(), 'GET', AUTOMATION_NAV_PREFERENCES_PATH)
    });
    launcherProvider = provider;

    context.subscriptions.push(
        provider,
        // The flyout re-reads the catalog on every open; the view does so whenever it comes
        // back on screen, which also catches automations edited while it was hidden.
        ...bindSidebarTreeView<LauncherNode>(LAUNCHER_VIEW_ID, provider, registeredCommands),
        vscode.commands.registerCommand(COMMAND_LAUNCHER_REFRESH, () => provider.refresh()),
        vscode.commands.registerCommand(COMMAND_LAUNCHER_RUN, (item?: LauncherItem) => runLauncherItem(item, {
            request: (method, route) => requestJson(getBackendConnection(), method, route),
            openDashboard: () => openDashboard(context),
            reveal: () => webviewManager?.reveal(),
            post: (message) => webviewManager?.postWhenReady(message) ?? Promise.resolve(false),
            notifyQueued: (label, message) => {
                vscode.window.setStatusBarMessage(`$(check) ${label}: ${message}`, LAUNCHER_STATUS_MESSAGE_MS);
            },
            notifyError: (message) => { void vscode.window.showErrorMessage(message); },
            refresh: () => { void provider.refresh(); },
            running: runningLauncherKeys
        })),
        vscode.commands.registerCommand(COMMAND_LAUNCHER_CUSTOMIZE, () =>
            sendToDashboard(context, { command: 'openLauncherCustomize' }, 'Could not open the launcher customization')),
        vscode.commands.registerCommand(COMMAND_LAUNCHER_MANAGE, () =>
            sendToDashboard(context, { command: 'manageAutomations' }, 'Could not open the Automation page'))
    );
}

/**
 * "Reviews requested of you" (VB-50 slice D): the review requests addressed to the signed-in
 * account, from the backend's cache. A row opens the dashboard page; Approve asks the backend to
 * open the signing popup on its machine (the popup shows the request, rebuilds the tree from the
 * repository and signs it there); Reject asks for a comment here and posts it through the backend.
 */
function registerReviewInboxView(context: vscode.ExtensionContext, registeredCommands: ReadonlySet<string>): void {
    const provider = new ReviewInboxTreeDataProvider({
        isBackendReady,
        fetchInbox: () => requestJson(getBackendConnection(), 'GET', REVIEW_INBOX_PATH)
    });
    reviewInboxProvider = provider;

    const dependencies: ReviewInboxActionDependencies = {
        getProjectPath: () => provider.getState()?.project ?? null,
        confirmRepositoryMismatch: async (message) =>
            await vscode.window.showWarningMessage(message, { modal: true }, 'Approve in this repository') === 'Approve in this repository',
        request: (method, route, body) => requestJson(getBackendConnection(), method, route, body),
        askComment: (row) => vscode.window.showInputBox({
            title: `Reject the review request from ${row.requestedBy}`,
            prompt: 'Why are you rejecting it? The requester sees this comment.',
            placeHolder: 'What has to change before you can approve it',
            ignoreFocusOut: true,
            validateInput: (value) => value.trim() ? undefined : 'Say why in a comment when you reject a review.'
        }),
        notify: (message) => { vscode.window.setStatusBarMessage(`$(check) ${message}`, LAUNCHER_STATUS_MESSAGE_MS); },
        notifyError: (message) => { void vscode.window.showErrorMessage(message); },
        refresh: () => { void provider.refresh(); },
        openExternal: (url) => vscode.env.openExternal(vscode.Uri.parse(url))
    };

    context.subscriptions.push(
        provider,
        ...bindSidebarTreeView<ReviewInboxRow>(REVIEW_INBOX_VIEW_ID, provider, registeredCommands),
        vscode.commands.registerCommand(COMMAND_REVIEW_INBOX_REFRESH, () => provider.refresh()),
        vscode.commands.registerCommand(COMMAND_REVIEW_INBOX_OPEN, () =>
            sendToDashboard(context, { command: 'openReviewInbox' }, 'Could not open Reviews requested of you')),
        vscode.commands.registerCommand(COMMAND_REVIEW_INBOX_READ, (row?: ReviewInboxRow) => readReviewRequest(row, dependencies)),
        vscode.commands.registerCommand(COMMAND_REVIEW_INBOX_APPROVE, (row?: ReviewInboxRow) => approveReviewRequest(row, dependencies)),
        vscode.commands.registerCommand(COMMAND_REVIEW_INBOX_REJECT, (row?: ReviewInboxRow) => rejectReviewRequest(row, dependencies))
    );
}

/**
 * "Review requests" (VB-50 slice F): this project's own requests as a Board card's Request review
 * section lists them, from the root backend. A row opens its card on the dashboard Board. The
 * dashboard relays review_request_updated over the webview bridge; the view reloads and shows a
 * notification when a decision arrived. Nothing here creates, sends, decides or signs.
 */
function registerReviewRequestsView(context: vscode.ExtensionContext, registeredCommands: ReadonlySet<string>): void {
    const openCard = 'Open card';
    const provider = new ReviewRequestsTreeDataProvider({
        isBackendReady,
        fetchRequests: () => requestJson(getBackendConnection(), 'GET', REVIEW_REQUESTS_PATH),
        notify: (message, change) => {
            void vscode.window.showInformationMessage(message, ...(change.cardKey ? [openCard] : [])).then((choice) => {
                if (choice === openCard) void vscode.commands.executeCommand(COMMAND_REVIEW_REQUESTS_OPEN, change);
            });
        }
    });
    reviewRequestsProvider = provider;

    context.subscriptions.push(
        provider,
        ...bindSidebarTreeView<ReviewRequestRow>(REVIEW_REQUESTS_VIEW_ID, provider, registeredCommands),
        vscode.commands.registerCommand(COMMAND_REVIEW_REQUESTS_REFRESH, () => provider.refresh()),
        vscode.commands.registerCommand(COMMAND_REVIEW_REQUESTS_OPEN, (target?: { id?: string; cardKey?: string | null }) =>
            sendToDashboard(context, {
                command: 'openReviewRequest',
                id: typeof target?.id === 'string' ? target.id : null,
                cardKey: typeof target?.cardKey === 'string' && target.cardKey ? target.cardKey : null
            }, 'Could not open the review request'))
    );
}

/** Readiness belongs to the backend; the dashboard panel is optional. */
function isBackendReady(): boolean {
    if (!backendReady || !backendManager?.isRunning()) {
        return false;
    }
    const { sessionToken, tabToken } = backendManager.getAuthTokens();
    return Boolean(sessionToken && tabToken);
}

function getBackendConnection(): BackendConnection {
    const manager = backendManager;
    const host = manager?.getHost();
    const port = manager?.getPort();
    const tokens = manager?.getAuthTokens();
    if (!manager?.isRunning() || !host || !port || !tokens?.sessionToken || !tokens.tabToken) {
        throw new Error('VibeRails is stopped. Open the dashboard to start it.');
    }
    return { host, port, sessionToken: tokens.sessionToken, tabToken: tokens.tabToken };
}

/** Opens the dashboard when needed, brings it forward and hands it one bridge command. */
async function sendToDashboard(context: vscode.ExtensionContext, message: object, failure: string): Promise<void> {
    try {
        await openDashboard(context);
        const manager = webviewManager;
        if (!manager) throw new Error('The VibeRails dashboard was closed. Try again.');
        manager.reveal();
        if (!await manager.postWhenReady(message)) {
            throw new Error('The dashboard did not respond. Try again.');
        }
    } catch (error) {
        const text = error instanceof Error ? error.message : String(error);
        void vscode.window.showErrorMessage(`${failure}: ${text}`);
    }
}

/**
 * Reuse the existing manager only when it points at the same executable. A reinstalled
 * or repackaged extension changes the bundled path, and a stale manager would keep
 * launching the old binary (or a path that no longer exists).
 */
async function ensureBackendManager(exePath: string): Promise<BackendManager> {
    if (backendManager && backendManager.getExePath() !== exePath) {
        const stale = backendManager;
        backendManager = null;
        await stale.shutdown();
    }

    if (!backendManager) {
        const manager = new BackendManager(exePath);
        backendManager = manager;
        manager.onDidStop(() => {
            if (backendManager !== manager) return;
            backendReady = false;
            disposeDashboardPanel();
            refreshBackendViews();
        });
    }
    return backendManager;
}

function getStartupTimeoutMs(): number {
    const configured = vscode.workspace
        .getConfiguration(CONFIG_SECTION)
        .get<number>(CONFIG_STARTUP_TIMEOUT_MS);

    return clampStartupTimeoutMs(configured);
}

export function clampStartupTimeoutMs(configured: unknown): number {
    if (typeof configured !== 'number' || !Number.isFinite(configured)) {
        return DEFAULT_STARTUP_TIMEOUT_MS;
    }

    return Math.min(
        MAX_STARTUP_TIMEOUT_MS,
        Math.max(MIN_STARTUP_TIMEOUT_MS, Math.trunc(configured))
    );
}

function getCurrentWorkspaceFolder(context: vscode.ExtensionContext): string | null {
    const activeUri = vscode.window.activeTextEditor?.document?.uri;
    if (activeUri) {
        const activeFolder = vscode.workspace.getWorkspaceFolder(activeUri);
        if (activeFolder) {
            return activeFolder.uri.fsPath;
        }
    }

    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
        return workspaceFolders[0].uri.fsPath;
    }

    // Test-only fallback: the smoke test launches VS Code with no folder open, so this
    // env var is the only way it can point the backend at a project. Never honored in
    // a real install.
    if (context.extensionMode !== vscode.ExtensionMode.Production) {
        const testWorkspace = process.env[SMOKE_WORKSPACE_ENV]?.trim();
        if (testWorkspace && fs.existsSync(testWorkspace)) {
            return testWorkspace;
        }
    }

    return null;
}

function resolveBundledAssets(context: vscode.ExtensionContext): BundledAssets {
    const target = getSupportedExtensionTarget();
    const exeName = process.platform === 'win32' ? 'vb.exe' : 'vb';
    const basePath = path.join(context.extensionPath, 'bin', target);
    const exePath = path.join(basePath, exeName);
    const wwwrootPath = path.join(basePath, 'wwwroot');
    const indexPath = path.join(wwwrootPath, 'index.html');

    if (!fs.existsSync(exePath) || !fs.existsSync(indexPath)) {
        throw new Error(
            `Bundled VibeRails backend is missing for ${target}. Reinstall the extension or rebuild the packaged assets.`
        );
    }

    return {
        target,
        exePath,
        wwwrootPath
    };
}

function getSupportedExtensionTarget(): string {
    if (process.platform === 'win32' && process.arch === 'x64') {
        return 'win32-x64';
    }

    if (process.platform === 'linux' && process.arch === 'x64') {
        return 'linux-x64';
    }

    if (process.platform === 'darwin' && process.arch === 'x64') {
        return 'darwin-x64';
    }

    if (process.platform === 'darwin' && process.arch === 'arm64') {
        return 'darwin-arm64';
    }

    throw new Error(`Unsupported platform for bundled VibeRails backend: ${process.platform}-${process.arch}`);
}

/**
 * Retry only connection-refused failures, which prove the request was not accepted.
 * Timeouts, resets, and HTTP responses are all potentially post-consumption: the bootstrap
 * code is single-use, so retrying any of those can only turn an ambiguous success into a 403.
 */
async function fetchTokensWithRetry(bootstrapUrl: string, signal: AbortSignal): Promise<{ sessionToken: string, tabToken: string }> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= BOOTSTRAP_RETRY_ATTEMPTS; attempt++) {
        signal.throwIfAborted();
        try {
            return await fetchTokens(bootstrapUrl, signal);
        } catch (error) {
            lastError = error;

            if (!isRetryableBootstrapError(error)) {
                throw error;
            }

            if (attempt < BOOTSTRAP_RETRY_ATTEMPTS) {
                await delay(BOOTSTRAP_RETRY_DELAY_MS);
            }
        }
    }

    throw lastError instanceof Error ? lastError : new Error(String(lastError));
}

export function isRetryableBootstrapError(error: unknown): boolean {
    return error instanceof Error
        && (error as NodeJS.ErrnoException).code === 'ECONNREFUSED';
}

function fetchTokens(bootstrapUrl: string, signal: AbortSignal): Promise<{ sessionToken: string, tabToken: string }> {
    return new Promise((resolve, reject) => {
        const rejectWithStatus = (message: string) => {
            const error = new Error(message);
            error.name = BOOTSTRAP_STATUS_ERROR;
            reject(error);
        };

        const req = http.get(bootstrapUrl, { signal }, (res) => {
            res.resume();

            const statusCode = res.statusCode ?? 0;
            if (statusCode === 403) {
                rejectWithStatus(
                    'Bootstrap was rejected (403). The one-time code may have expired or already been used — run "VibeRails: Open Dashboard" again.'
                );
                return;
            }
            if (statusCode < 200 || statusCode >= 300) {
                rejectWithStatus(`Bootstrap request failed with status ${statusCode}`);
                return;
            }

            const sessionToken = parseSessionTokenFromSetCookie(res.headers['set-cookie']);

            const tabHeader = res.headers[TAB_TOKEN_HEADER];
            const tabToken = Array.isArray(tabHeader) ? (tabHeader[0] ?? null) : (tabHeader ?? null);

            if (!sessionToken) {
                rejectWithStatus('Bootstrap did not return a session token');
                return;
            }

            if (!tabToken) {
                rejectWithStatus('Bootstrap did not return a tab token');
                return;
            }

            resolve({ sessionToken, tabToken });
        });
        req.on('error', (error) => reject(error));
        req.setTimeout(BOOTSTRAP_REQUEST_TIMEOUT_MS, () => {
            const error = new Error('Bootstrap request timed out after the request may have been processed');
            error.name = BOOTSTRAP_AMBIGUOUS_ERROR;
            req.destroy(error);
        });
    });
}

export function parseSessionTokenFromSetCookie(setCookie: string[] | undefined): string | null {
    if (!setCookie) { return null; }

    for (const cookie of setCookie) {
        const match = cookie.match(SESSION_COOKIE_PATTERN);
        if (!match) { continue; }

        // Cookies URL-encode base64 characters (%2F, %2B, %3D).
        // Backend header auth expects the raw token value.
        const encodedToken = match[1].replace(/^"|"$/g, '');
        try {
            return decodeURIComponent(encodedToken);
        } catch {
            return encodedToken;
        }
    }

    return null;
}

/**
 * The backend prints its bootstrap URL as soon as Kestrel binds, which is a beat before
 * requests are actually served on a cold AOT start. Probe the unauthenticated /health
 * endpoint so a slow start surfaces here rather than as a blank dashboard.
 */
async function waitForHealthy(host: string, port: number, signal: AbortSignal): Promise<void> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= HEALTH_CHECK_ATTEMPTS; attempt++) {
        signal.throwIfAborted();
        try {
            await probeHealth(host, port, signal);
            return;
        } catch (error) {
            lastError = error;
            if (attempt < HEALTH_CHECK_ATTEMPTS) {
                await delay(HEALTH_CHECK_DELAY_MS);
            }
        }
    }

    const detail = lastError instanceof Error ? lastError.message : String(lastError);
    throw new Error(`Backend did not become ready on port ${port}: ${detail}`);
}

function probeHealth(host: string, port: number, signal: AbortSignal): Promise<void> {
    return new Promise((resolve, reject) => {
        const req = http.get({ host, port, path: HEALTH_PATH, signal }, (res) => {
            res.resume();
            const statusCode = res.statusCode ?? 0;
            if (statusCode >= 200 && statusCode < 300) {
                resolve();
            } else {
                reject(new Error(`health check returned status ${statusCode}`));
            }
        });
        req.on('error', (error) => reject(error));
        req.setTimeout(HEALTH_CHECK_TIMEOUT_MS, () => {
            req.destroy(new Error('health check timed out'));
        });
    });
}

function delay(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
}

function escapeRegExp(value: string): string {
    return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

export async function deactivate(): Promise<void> {
    deactivating = true;
    await closeDashboard(false, true);
    webviewManager = null;
    backendManager = null;
}

function registerTestCommands(context: vscode.ExtensionContext): void {
    if (context.extensionMode === vscode.ExtensionMode.Production) {
        return;
    }

    const getConnectionInfo = vscode.commands.registerCommand(
        COMMAND_TEST_CONNECTION_INFO,
        (): TestConnectionInfo => ({
            port: backendManager?.getPort() ?? null,
            sessionToken: backendManager?.getAuthTokens().sessionToken ?? null,
            tabToken: backendManager?.getAuthTokens().tabToken ?? null,
            webviewVisible: webviewManager?.isVisible() ?? false,
            webviewOpen: webviewManager?.hasPanel() ?? false,
            backendReady: isBackendReady()
        })
    );

    context.subscriptions.push(getConnectionInfo);

    // Lets the smoke test read what the launcher view shows without touching the tree UI.
    context.subscriptions.push(vscode.commands.registerCommand(COMMAND_TEST_LAUNCHER_ITEMS, async () => {
        await launcherProvider?.refresh();
        return {
            backendReady: isBackendReady(),
            items: launcherProvider?.getItems() ?? [],
            message: launcherProvider?.getMessage() ?? null
        };
    }));
}

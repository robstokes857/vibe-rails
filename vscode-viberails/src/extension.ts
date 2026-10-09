import * as vscode from 'vscode';
import * as http from 'http';
import * as path from 'path';
import * as fs from 'fs';
import { BackendManager } from './backend-manager';
import { WebviewPanelManager } from './webview-panel';
import { resolveScriptImportTarget } from './script-import';
import { BackendConnection, requestJson } from './backend-api';
import { LauncherItem, LauncherTreeDataProvider, runLauncherItem } from './launcher-view';
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
    COMMAND_STOP,
    COMMAND_TEST_CONNECTION_INFO,
    COMMAND_TEST_LAUNCHER_ITEMS,
    CONFIG_SECTION,
    CONFIG_STARTUP_TIMEOUT_MS,
    CONTEXT_DASHBOARD_OPEN,
    DEFAULT_STARTUP_TIMEOUT_MS,
    HEALTH_CHECK_ATTEMPTS,
    HEALTH_CHECK_DELAY_MS,
    HEALTH_CHECK_TIMEOUT_MS,
    HEALTH_PATH,
    LAUNCHER_STATUS_MESSAGE_MS,
    LAUNCHER_VIEW_ID,
    MAX_STARTUP_TIMEOUT_MS,
    MIN_STARTUP_TIMEOUT_MS,
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
let closingPromise: Promise<void> | null = null;
let openingPromise: Promise<void> | null = null;
/** Launcher rows with a run in flight (see runLauncherItem). */
const runningLauncherKeys = new Set<string>();

export function activate(context: vscode.ExtensionContext) {
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

    registerLauncherView(context);

    const openCommand = vscode.commands.registerCommand(COMMAND_OPEN, async () => {
        try {
            await openDashboard(context);
        } catch (error) {
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
            void closeDashboard(false, true);
        }
    });
}

async function closeDashboard(showMessage: boolean, shutdownBackend: boolean): Promise<void> {
    if (closingPromise) {
        await closingPromise;
        return;
    }

    closingPromise = (async () => {
        webviewManager?.dispose();
        webviewManager = null;
        stopBarItem?.hide();
        void setDashboardOpenContext(false);
        void launcherProvider?.refresh();

        const manager = backendManager;
        if (manager) {
            if (shutdownBackend) {
                if (backendManager === manager) { backendManager = null; }
                await manager.shutdown();
            } else {
                await manager.stop();
            }
        }

        if (showMessage) {
            vscode.window.showInformationMessage('VibeRails closed');
        }
    })();

    try {
        await closingPromise;
    } finally {
        closingPromise = null;
    }
}

async function openDashboard(context: vscode.ExtensionContext, projectFolder?: string): Promise<void> {
    if (closingPromise) await closingPromise;
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
    const targetProjectFolder = projectFolder ?? getCurrentWorkspaceFolder(context);
    const manager = await ensureBackendManager(bundledAssets.exePath);

    await vscode.window.withProgress({
        location: vscode.ProgressLocation.Notification,
        title: 'Starting VibeRails...',
        cancellable: false
    }, async (progress) => {
        if (!manager.isRunning()) {
            progress.report({ message: 'Starting backend server...' });
            await manager.start(targetProjectFolder, getStartupTimeoutMs());
        }

        const port = manager.getPort();
        if (!port) {
            throw new Error('Backend started but port not available');
        }
        const host = manager.getHost();
        if (!host) {
            throw new Error('Backend started but loopback host was not available');
        }

        const bootstrapUrl = manager.getBootstrapUrl();
        if (!bootstrapUrl) {
            throw new Error('Backend started but bootstrap URL was not available');
        }

        // Tokens are instance-wide and stay valid for the whole backend process lifetime,
        // so reuse them whenever the backend is already up. The bootstrap code embedded in
        // the URL is single-use: re-fetching against a still-running backend would spend an
        // already-consumed code and lock the dashboard out with a 403.
        let { sessionToken, tabToken } = manager.getAuthTokens();
        if (!sessionToken || !tabToken) {
            // Bootstrap before probing health — the one-time code expires two minutes after
            // it is minted, so it must not sit waiting behind anything.
            const fetched = await fetchTokensWithRetry(bootstrapUrl);
            sessionToken = fetched.sessionToken;
            tabToken = fetched.tabToken;
            manager.setAuthTokens(sessionToken, tabToken);
        }

        progress.report({ message: 'Waiting for backend to respond...' });
        await waitForHealthy(host, port);

        progress.report({ message: 'Creating dashboard...' });
        webviewManager = new WebviewPanelManager(bundledAssets.wwwrootPath);

        webviewManager.onCloseRequested(() => { void closeDashboard(true, false); });
        webviewManager.onLauncherChanged(() => { void launcherProvider?.refresh(); });

        await webviewManager.create(port, sessionToken, tabToken);
        stopBarItem?.show();
        void setDashboardOpenContext(true);
        void launcherProvider?.refresh();
    });
}

// --- Activity Bar launcher view -------------------------------------------

/**
 * The side-bar twin of the dashboard's nav Play button. The tree lists the same catalog;
 * running, customizing and managing behave like the flyout's rows and footer.
 */
function registerLauncherView(context: vscode.ExtensionContext): void {
    void setDashboardOpenContext(false);

    const provider = new LauncherTreeDataProvider({
        isDashboardOpen,
        fetchItems: () => requestJson(getBackendConnection(), 'GET', AUTOMATION_NAV_PREFERENCES_PATH)
    });
    launcherProvider = provider;

    const view = vscode.window.createTreeView<LauncherItem>(LAUNCHER_VIEW_ID, {
        treeDataProvider: provider,
        showCollapseAll: false
    });

    context.subscriptions.push(
        provider,
        view,
        provider.onDidChangeTreeData(() => { view.message = provider.getMessage(); }),
        // The flyout re-reads the catalog on every open; the view does so whenever it comes
        // back on screen, which also catches automations edited while it was hidden.
        view.onDidChangeVisibility((event) => { if (event.visible) { void provider.refresh(); } }),
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

/** The backend only runs while the panel exists, so this also means "the API is reachable". */
function isDashboardOpen(): boolean {
    if (!webviewManager?.hasPanel() || !backendManager?.isRunning()) {
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
        throw new Error('The VibeRails dashboard is closed.');
    }
    return { host, port, sessionToken: tokens.sessionToken, tabToken: tokens.tabToken };
}

function setDashboardOpenContext(open: boolean): Thenable<unknown> {
    return vscode.commands.executeCommand('setContext', CONTEXT_DASHBOARD_OPEN, open);
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

    backendManager ??= new BackendManager(exePath);
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
async function fetchTokensWithRetry(bootstrapUrl: string): Promise<{ sessionToken: string, tabToken: string }> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= BOOTSTRAP_RETRY_ATTEMPTS; attempt++) {
        try {
            return await fetchTokens(bootstrapUrl);
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

function fetchTokens(bootstrapUrl: string): Promise<{ sessionToken: string, tabToken: string }> {
    return new Promise((resolve, reject) => {
        const rejectWithStatus = (message: string) => {
            const error = new Error(message);
            error.name = BOOTSTRAP_STATUS_ERROR;
            reject(error);
        };

        const req = http.get(bootstrapUrl, (res) => {
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
async function waitForHealthy(host: string, port: number): Promise<void> {
    let lastError: unknown;

    for (let attempt = 1; attempt <= HEALTH_CHECK_ATTEMPTS; attempt++) {
        try {
            await probeHealth(host, port);
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

function probeHealth(host: string, port: number): Promise<void> {
    return new Promise((resolve, reject) => {
        const req = http.get({ host, port, path: HEALTH_PATH }, (res) => {
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
            webviewVisible: webviewManager?.isVisible() ?? false
        })
    );

    context.subscriptions.push(getConnectionInfo);

    // Lets the smoke test read what the launcher view shows without touching the tree UI.
    context.subscriptions.push(vscode.commands.registerCommand(COMMAND_TEST_LAUNCHER_ITEMS, async () => {
        await launcherProvider?.refresh();
        return {
            dashboardOpen: isDashboardOpen(),
            items: launcherProvider?.getItems() ?? [],
            message: launcherProvider?.getMessage() ?? null
        };
    }));
}

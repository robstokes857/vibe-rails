/**
 * The Activity Bar "Launch" view: the nav Play flyout's catalog as a VS Code tree.
 *
 * The list is the same `GET /api/v1/automation-nav/preferences` catalog the dashboard's
 * flyout renders, filtered and ordered by the same rules (see `normalizeLauncherItems` and
 * `isLauncherItemRunnable` in wwwroot/js/modules/automation-launcher.js — keep the two in
 * step). Clicking a row runs it, like a flyout row: automations are queued straight from the
 * extension host (`POST /api/v1/jobs/{id}/run`, no dashboard needed on screen), scripts are
 * handed to the dashboard because their run window collects arguments, stdin and a PIN.
 *
 * Everything the tree needs from the extension host arrives through small dependency
 * objects so the behaviour is unit-testable without a backend or a webview.
 */
import * as vscode from 'vscode';
import { BackendMethod, BackendRequestError } from './backend-api';
import { COMMAND_LAUNCHER_RUN, jobRunPath } from './constants';

export interface LauncherItem {
    key: string;
    kind: 'automation' | 'script';
    label: string;
    jobId: number;
    enabled: boolean;
    order: number;
    /** approved | modified | unapproved for scripts; null for automations. */
    status: string | null;
    scriptFileName?: string;
}

export const LAUNCHER_LOAD_FAILED_MESSAGE = 'Could not load automations.';
export const LAUNCHER_ALL_HIDDEN_MESSAGE = 'All automations are hidden. Use "Customize list…" to show some.';
export const LAUNCHER_NONE_RUNNABLE_MESSAGE = 'No automations or signed scripts available. Sign a script on the Automation page.';
export const LAUNCHER_EMPTY_MESSAGE = 'No automations yet. Create one from the Automation page.';
export const LAUNCHER_ITEM_CONTEXT = 'launcherItem';
const SCRIPT_KEY_PREFIX = 'script:';

/**
 * Shapes the server's launcher catalog: one entry per automation (`jobId`) or script
 * (`status`), malformed entries dropped, sorted by saved order.
 */
export function normalizeLauncherItems(raw: unknown): LauncherItem[] {
    if (!Array.isArray(raw)) {
        return [];
    }

    return raw
        .map((entry): LauncherItem => {
            const item = (entry && typeof entry === 'object' ? entry : {}) as Record<string, unknown>;
            const kind: LauncherItem['kind'] = item.kind === 'script' ? 'script' : 'automation';
            const scriptFileName = typeof item.scriptFileName === 'string' && item.scriptFileName
                ? item.scriptFileName
                : undefined;
            return {
                key: String(item.key || ''),
                kind,
                label: String(item.label || ''),
                ...(scriptFileName ? { scriptFileName } : {}),
                jobId: Number(item.jobId),
                enabled: Boolean(item.enabled),
                order: Number(item.order) || 0,
                status: kind === 'script' ? String(item.status || 'unapproved') : null
            };
        })
        .filter((item) => item.key && (item.kind === 'script' || Number.isFinite(item.jobId)))
        .sort((a, b) => a.order - b.order);
}

/** A launcher item can be run right now: automations always, scripts only while signed. */
export function isLauncherItemRunnable(item: LauncherItem): boolean {
    return item.kind !== 'script' || item.status === 'approved';
}

/** The rows the flyout shows: runnable and not hidden by the user's customization. */
export function visibleLauncherItems(items: LauncherItem[]): LauncherItem[] {
    return items.filter((item) => isLauncherItemRunnable(item) && item.enabled);
}

/**
 * The flyout's empty-state text for a catalog, or `undefined` when there is something to
 * show. `null` stands for a catalog that could not be loaded.
 */
export function launcherEmptyMessage(items: LauncherItem[] | null): string | undefined {
    if (items === null) {
        return LAUNCHER_LOAD_FAILED_MESSAGE;
    }
    const runnable = items.filter(isLauncherItemRunnable);
    const visible = runnable.filter((item) => item.enabled);
    if (visible.length > 0) {
        return undefined;
    }
    if (runnable.length > 0) {
        return LAUNCHER_ALL_HIDDEN_MESSAGE;
    }
    return items.length > 0 ? LAUNCHER_NONE_RUNNABLE_MESSAGE : LAUNCHER_EMPTY_MESSAGE;
}

/** What the flyout hands to `PythonScriptsController.run`: the registration id behind the key. */
export function scriptNameFromItem(item: LauncherItem): string {
    return item.key.startsWith(SCRIPT_KEY_PREFIX) ? item.key.slice(SCRIPT_KEY_PREFIX.length) : item.label;
}

/** Row description: the script runtime read from the file extension, as the flyout's icons do. */
export function launcherItemDescription(item: LauncherItem): string | undefined {
    if (item.kind !== 'script') {
        return undefined;
    }
    const fileName = (item.scriptFileName || item.label).toLowerCase();
    if (fileName.endsWith('.py')) { return 'Python script'; }
    if (fileName.endsWith('.ps1')) { return 'PowerShell script'; }
    if (fileName.endsWith('.sh')) { return 'Bash script'; }
    return 'Script';
}

export interface LauncherProviderDependencies {
    /** True while the dashboard panel exists, which is the only time the backend is reachable. */
    isDashboardOpen(): boolean;
    /** Resolves the body of `GET /api/v1/automation-nav/preferences`. */
    fetchItems(): Promise<unknown>;
}

export class LauncherTreeDataProvider implements vscode.TreeDataProvider<LauncherItem>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    public readonly onDidChangeTreeData: vscode.Event<void> = this._onDidChangeTreeData.event;
    private readonly dependencies: LauncherProviderDependencies;
    private items: LauncherItem[] = [];
    private message: string | undefined;
    private current: Promise<void> | null = null;
    private next: Promise<void> | null = null;

    constructor(dependencies: LauncherProviderDependencies) {
        this.dependencies = dependencies;
    }

    /** The rows currently shown, in order. */
    public getItems(): LauncherItem[] {
        return visibleLauncherItems(this.items);
    }

    /** The empty-state text to show in the view, if any. */
    public getMessage(): string | undefined {
        return this.message;
    }

    /**
     * Reloads the catalog (fresh on every call, like the flyout, which keeps no client
     * cache). Overlapping calls coalesce into one follow-up load so a burst of refresh
     * triggers costs at most two requests.
     */
    public refresh(): Promise<void> {
        if (!this.current) {
            this.current = this.load().finally(() => { this.current = null; });
            return this.current;
        }
        if (!this.next) {
            this.next = this.current.catch(() => undefined).then(() => {
                this.next = null;
                return this.refresh();
            });
        }
        return this.next;
    }

    public getTreeItem(item: LauncherItem): vscode.TreeItem {
        const node = new vscode.TreeItem(item.label, vscode.TreeItemCollapsibleState.None);
        node.description = launcherItemDescription(item);
        node.iconPath = new vscode.ThemeIcon(item.kind === 'script' ? 'terminal' : 'play');
        node.tooltip = `Run ${item.label} now`;
        node.contextValue = LAUNCHER_ITEM_CONTEXT;
        node.command = { command: COMMAND_LAUNCHER_RUN, title: 'Run now', arguments: [item] };
        return node;
    }

    public getChildren(element?: LauncherItem): LauncherItem[] {
        return element ? [] : this.getItems();
    }

    public dispose(): void {
        this._onDidChangeTreeData.dispose();
    }

    private async load(): Promise<void> {
        if (!this.dependencies.isDashboardOpen()) {
            this.apply([], undefined);
            return;
        }

        let items: LauncherItem[] | null;
        try {
            const response = await this.dependencies.fetchItems() as { items?: unknown } | null | undefined;
            items = normalizeLauncherItems(response?.items);
        } catch {
            items = null;
        }

        // The dashboard may have closed while the request was in flight.
        if (!this.dependencies.isDashboardOpen()) {
            this.apply([], undefined);
            return;
        }
        this.apply(items ?? [], launcherEmptyMessage(items));
    }

    private apply(items: LauncherItem[], message: string | undefined): void {
        this.items = items;
        this.message = message;
        this._onDidChangeTreeData.fire();
    }
}

export interface RunLauncherDependencies {
    /** Authenticated call to the running backend; rejects with `BackendRequestError` on a non-2xx. */
    request(method: BackendMethod, path: string): Promise<unknown>;
    /** Starts the backend and opens the panel when needed; resolves once it is open. */
    openDashboard(): Promise<void>;
    reveal(): void;
    /** Delivers a message to the dashboard once its bridge is ready; false when it never was. */
    post(message: object): Promise<boolean>;
    notifyQueued(label: string, message: string): void;
    notifyError(message: string): void;
    /** Asked for after a 404: the row no longer exists on the server. */
    refresh(): void;
    /** Keys with a run in flight; a second click on the same row is ignored meanwhile. */
    running: Set<string>;
}

/**
 * Runs one row the way a flyout click would. The in-flight set also defuses the double
 * delivery a tree row's command gets from a double-click.
 */
export async function runLauncherItem(item: LauncherItem | undefined, dependencies: RunLauncherDependencies): Promise<void> {
    if (!item || typeof item.key !== 'string' || !item.key || dependencies.running.has(item.key)) {
        return;
    }

    dependencies.running.add(item.key);
    try {
        if (item.kind === 'script') {
            await runScript(item, dependencies);
        } else {
            await runAutomation(item, dependencies);
        }
    } catch (error) {
        dependencies.notifyError(`${item.label}: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
        dependencies.running.delete(item.key);
    }
}

async function runAutomation(item: LauncherItem, dependencies: RunLauncherDependencies): Promise<void> {
    let message: string;
    try {
        const response = await dependencies.request('POST', jobRunPath(item.jobId)) as { message?: unknown } | null | undefined;
        const text = typeof response?.message === 'string' ? response.message.trim() : '';
        message = text || 'Queued.';
    } catch (error) {
        if (error instanceof BackendRequestError && error.status === 404) {
            dependencies.refresh();
        }
        throw error;
    }

    dependencies.notifyQueued(item.label, message);
    // The dashboard, when it is on screen, shows its usual toast and refreshes its run list.
    void dependencies.post({ command: 'automationQueued', jobId: item.jobId, message });
}

async function runScript(item: LauncherItem, dependencies: RunLauncherDependencies): Promise<void> {
    await dependencies.openDashboard();
    dependencies.reveal();
    const delivered = await dependencies.post({ command: 'runScript', name: scriptNameFromItem(item) });
    if (!delivered) {
        throw new Error('The dashboard did not respond. Try again.');
    }
}

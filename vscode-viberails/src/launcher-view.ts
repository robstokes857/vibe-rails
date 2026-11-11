/** Global launch definitions shared with the browser's AI Agents screen. */
import * as vscode from 'vscode';
import { randomUUID } from 'crypto';
import { BackendMethod, BackendRequestError } from './backend-api';
import { COMMAND_LAUNCHER_RUN, LAUNCH_RUNS_PATH } from './constants';

export interface LauncherItem {
    key: string;
    id: string;
    definitionType: 'preset' | 'action';
    kind: string;
    label: string;
    name: string;
    enabled: boolean;
    retired: boolean;
    canLaunch: boolean;
    ineligibleReason?: string;
}
export interface LauncherGroup {
    kind: 'group'; key: string; label: string; category: 'preset' | 'action';
}
export type LauncherNode = LauncherItem | LauncherGroup;
const LAUNCHER_GROUPS: LauncherGroup[] = [
    { kind: 'group', key: 'presets', label: 'Agents and Quick Launches', category: 'preset' },
    { kind: 'group', key: 'actions', label: 'Actions', category: 'action' }
];
export const LAUNCHER_LOAD_FAILED_MESSAGE = 'Could not load AI Agents.';
export const LAUNCHER_NONE_RUNNABLE_MESSAGE = 'No eligible launches. Manage definitions in AI Agents.';
export const LAUNCHER_EMPTY_MESSAGE = 'No definitions yet. Create one in AI Agents.';
export const LAUNCHER_ITEM_CONTEXT = 'launcherItem';
export function normalizeLauncherItems(raw: unknown): LauncherItem[] {
    if (!Array.isArray(raw)) return [];
    return raw.filter(item => item && typeof item.id === 'string' && item.id
        && typeof item.name === 'string' && item.name
        && ['preset', 'action'].includes(item.definitionType))
        .map(item => ({ id: item.id, key: `${item.definitionType}:${item.id}`,
            definitionType: item.definitionType, kind: String(item.kind), name: item.name, label: item.name,
            enabled: item.enabled === true, retired: item.retired === true, canLaunch: item.canLaunch === true,
            ineligibleReason: typeof item.ineligibleReason === 'string' ? item.ineligibleReason : undefined }));
}
export function isLauncherItemRunnable(item: LauncherItem): boolean {
    return item.kind !== 'code-quality' && item.enabled && !item.retired && item.canLaunch;
}
// VIBE-309: match the browser's isLaunchVisible without changing the stored catalog.
export function visibleLauncherItems(items: LauncherItem[]): LauncherItem[] {
    return items.filter(item => item.kind !== 'code-quality');
}
export function launcherEmptyMessage(items: LauncherItem[] | null): string | undefined {
    if (items === null) return LAUNCHER_LOAD_FAILED_MESSAGE;
    const visible = visibleLauncherItems(items);
    if (!visible.length) return LAUNCHER_EMPTY_MESSAGE;
    return visible.some(isLauncherItemRunnable) ? undefined : LAUNCHER_NONE_RUNNABLE_MESSAGE;
}
export function launcherItemDescription(item: LauncherItem): string {
    return isLauncherItemRunnable(item) ? item.kind : 'Unavailable';
}
export interface LauncherProviderDependencies {
    isBackendReady(): boolean;
    fetchItems(): Promise<unknown>;
}

export class LauncherTreeDataProvider implements vscode.TreeDataProvider<LauncherNode>, vscode.Disposable {
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

    public getTreeItem(item: LauncherNode): vscode.TreeItem {
        if ('category' in item) {
            const node = new vscode.TreeItem(item.label, vscode.TreeItemCollapsibleState.Expanded);
            node.id = `launcher-group:${item.key}`;
            node.description = String(this.getChildren(item).length);
            node.contextValue = 'launcherGroup';
            node.iconPath = new vscode.ThemeIcon(item.category === 'action' ? 'code' : 'rocket');
            return node;
        }
        const node = new vscode.TreeItem(item.label, vscode.TreeItemCollapsibleState.None);
        node.id = item.key;
        node.description = launcherItemDescription(item);
        node.iconPath = new vscode.ThemeIcon(item.definitionType === 'action' ? 'code' : 'zap');
        node.tooltip = item.ineligibleReason || `Run ${item.label} now`;
        node.contextValue = LAUNCHER_ITEM_CONTEXT;
        if (isLauncherItemRunnable(item)) node.command = { command: COMMAND_LAUNCHER_RUN, title: 'Run now', arguments: [item] };
        return node;
    }

    public getChildren(element?: LauncherNode): LauncherNode[] {
        const items = this.getItems();
        if (element && 'category' in element) return items.filter(item => item.definitionType === element.category);
        if (element) return [];
        // Keep catalog order within each section; omit empty headings.
        return LAUNCHER_GROUPS.filter(group => items.some(item => item.definitionType === group.category));
    }

    public dispose(): void {
        this._onDidChangeTreeData.dispose();
    }

    private async load(): Promise<void> {
        if (!this.dependencies.isBackendReady()) {
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

        // The backend may have stopped while the request was in flight.
        if (!this.dependencies.isBackendReady()) {
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
    request(method: BackendMethod, path: string, body?: unknown): Promise<unknown>;
    openDashboard(): Promise<void>;
    reveal(): void;
    post(message: object): Promise<boolean>;
    notifyQueued(label: string, message: string): void;
    notifyError(message: string): void;
    refresh(): void;
    running: Set<string>;
    clickCooldownMs?: number;
}
export const LAUNCHER_CLICK_COOLDOWN_MS = 1000;
export async function runLauncherItem(item: LauncherItem | undefined, dependencies: RunLauncherDependencies): Promise<void> {
    if (!item?.id || !isLauncherItemRunnable(item) || dependencies.running.has(item.key)) return;
    dependencies.running.add(item.key);
    try {
        if (item.definitionType === 'preset') {
            // Use the same template-value prompt, revision check and terminal
            // adoption as the browser picker before posting the typed run.
            await dependencies.openDashboard();
            dependencies.reveal();
            if (!await dependencies.post({ command: 'launchDefinition', definitionId: item.id, definitionType: 'preset' }))
                throw new Error('The dashboard could not receive this launch. Try again.');
            return;
        }
        await dependencies.request('POST', LAUNCH_RUNS_PATH,
            { definitionId: item.id, definitionType: item.definitionType, occurrenceId: randomUUID() });
        dependencies.notifyQueued(item.label, 'Queued.');
        void dependencies.post({ command: 'launchQueued', definitionId: item.id, definitionType: item.definitionType });
    } catch (error) {
        if (error instanceof BackendRequestError && [404, 409, 400].includes(error.status)) dependencies.refresh();
        dependencies.notifyError(`${item.label}: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
        setTimeout(() => dependencies.running.delete(item.key), dependencies.clickCooldownMs ?? LAUNCHER_CLICK_COOLDOWN_MS);
    }
}

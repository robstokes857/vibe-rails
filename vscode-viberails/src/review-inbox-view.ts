/**
 * The Activity Bar "Reviews requested of you" view (VB-50 slice D): the review requests
 * viberails.ai holds for the signed-in account's verified email, as the root backend last
 * fetched them (`GET /api/v1/review-inbox`, refreshed on the Board sync cadence; the dashboard
 * page is `review-inbox`). A row click opens that page; Approve asks the backend to open the
 * signing popup on its machine, which rebuilds the tree from the repository and signs it there;
 * Reject asks for a comment and posts it through the backend. Nothing here signs or sees a key.
 *
 * Wording is kept in step with wwwroot/js/modules/review-inbox-view.js. Everything the tree needs
 * from the extension host arrives through small dependency objects so it is unit-tested without
 * a backend or a webview.
 */
import * as vscode from 'vscode';
import { BackendMethod } from './backend-api';
import { COMMAND_REVIEW_INBOX_OPEN, reviewInboxActionPath } from './constants';

export interface ReviewInboxRow {
    id: string;
    repository: string;
    requestedBy: string;
    status: string;
    fileCount: number;
    sessionCount: number;
    cardKey: string | null;
    createdUtc: string;
    expiresUtc: string;
    decided: boolean;
    readUrl: string;
    /** signing | approved | not_approved | popup_unavailable, or null when no popup was opened from this backend. */
    signing: string | null;
}

export interface ReviewInboxState {
    hasApiKey: boolean;
    email: string | null;
    emailVerified: boolean;
    fetchedUtc: string | null;
    lastError: string | null;
    popupAvailable: boolean;
    requests: ReviewInboxRow[];
}

export const REVIEW_INBOX_ITEM_CONTEXT = 'reviewInboxItem';
/** Rows that can still be answered carry this context, which enables the inline Approve and Reject actions. */
export const REVIEW_INBOX_OPEN_ITEM_CONTEXT = 'reviewInboxOpenItem';
export const REVIEW_INBOX_LOAD_FAILED_MESSAGE = 'Could not read the review requests.';
export const REVIEW_INBOX_NO_KEY_MESSAGE = 'Sign in to viberails.ai in VibeRails Settings to see requests addressed to your account.';
export const REVIEW_INBOX_UNVERIFIED_MESSAGE = 'Your viberails.ai account has no verified email, so no review request can be addressed to it.';
export const REVIEW_INBOX_EMPTY_MESSAGE = 'Nobody has asked you to review a change yet.';
export const REVIEW_INBOX_PENDING_MESSAGE = 'Checking viberails.ai for requests addressed to you…';

const STATUS_WORDING: Record<string, string> = {
    open: 'Waiting for your decision',
    approved: 'Approved',
    rejected: 'Rejected',
    cancelled: 'Cancelled by the requester',
    expired: 'Expired'
};

const SIGNING_WORDING: Record<string, string> = {
    signing: 'Signing popup open',
    approved: 'Approved from this computer',
    not_approved: 'Popup closed without approving',
    popup_unavailable: 'Popup could not open'
};

/** A hosted reference: `rvw_` and 32 lowercase hex characters. */
export function isReviewRequestReference(value: unknown): value is string {
    return typeof value === 'string' && /^rvw_[0-9a-f]{32}$/.test(value);
}

/** Only the hosted reviewer page for a well-formed reference is ever opened in the browser. */
export function isReviewRequestPageUrl(url: unknown): url is string {
    return typeof url === 'string' && /^https:\/\/viberails\.ai\/Reviews\/requests\/rvw_[0-9a-f]{32}$/.test(url);
}

/** Shapes the backend's view: malformed rows dropped, every field typed. */
export function normalizeReviewInbox(raw: unknown): ReviewInboxState {
    const view = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
    const requests = Array.isArray(view.requests) ? view.requests : [];
    return {
        hasApiKey: Boolean(view.hasApiKey),
        email: typeof view.email === 'string' ? view.email : null,
        emailVerified: Boolean(view.emailVerified),
        fetchedUtc: typeof view.fetchedUtc === 'string' ? view.fetchedUtc : null,
        lastError: typeof view.lastError === 'string' && view.lastError ? view.lastError : null,
        popupAvailable: Boolean(view.popupAvailable),
        requests: requests.map(normalizeRow).filter((row): row is ReviewInboxRow => row !== null)
    };
}

function normalizeRow(entry: unknown): ReviewInboxRow | null {
    const item = (entry && typeof entry === 'object' ? entry : {}) as Record<string, unknown>;
    if (!isReviewRequestReference(item.id)) {
        return null;
    }
    const signing = item.signing && typeof item.signing === 'object'
        ? (item.signing as Record<string, unknown>).outcome
        : null;
    return {
        id: item.id,
        repository: String(item.repository || ''),
        requestedBy: String(item.requestedBy || ''),
        status: String(item.status || 'open'),
        fileCount: Math.max(0, Number(item.fileCount) || 0),
        sessionCount: Math.max(0, Number(item.sessionCount) || 0),
        cardKey: typeof item.cardKey === 'string' && item.cardKey ? item.cardKey : null,
        createdUtc: String(item.createdUtc || ''),
        expiresUtc: String(item.expiresUtc || ''),
        decided: Boolean(item.decided),
        readUrl: typeof item.readUrl === 'string' ? item.readUrl : '',
        signing: typeof signing === 'string' && signing ? signing : null
    };
}

/** The view's empty-state text, or `undefined` when there are rows. `null` stands for a list that could not be loaded. */
export function reviewInboxMessage(state: ReviewInboxState | null): string | undefined {
    if (state === null) {
        return REVIEW_INBOX_LOAD_FAILED_MESSAGE;
    }
    if (!state.hasApiKey) {
        return REVIEW_INBOX_NO_KEY_MESSAGE;
    }
    if (state.requests.length > 0) {
        return undefined;
    }
    if (state.lastError && !state.fetchedUtc) {
        return `viberails.ai could not be read: ${state.lastError}`;
    }
    if (state.fetchedUtc && !state.emailVerified) {
        return REVIEW_INBOX_UNVERIFIED_MESSAGE;
    }
    return state.fetchedUtc ? REVIEW_INBOX_EMPTY_MESSAGE : REVIEW_INBOX_PENDING_MESSAGE;
}

/**
 * viberails.ai keeps taking decisions after the first one (a rule may name several approvers, and an
 * approval after someone else's rejection is still this reviewer's to give), so only a cancelled or
 * expired request is closed to this account.
 */
export const DECIDABLE_STATUSES: readonly string[] = ['open', 'approved', 'rejected'];

/** Still decidable, unanswered by this account, with a key saved, and no popup already open for it. */
export function canAnswerReviewRequest(row: ReviewInboxRow, state: ReviewInboxState): boolean {
    return state.hasApiKey && DECIDABLE_STATUSES.includes(row.status) && !row.decided && row.signing !== 'signing';
}

export function describeReviewStatus(row: ReviewInboxRow): string {
    if (row.decided && row.status === 'open') {
        return 'You answered; waiting for others';
    }
    if (!row.decided && row.status === 'approved') {
        return 'Approved by another reviewer; your decision is still taken';
    }
    if (!row.decided && row.status === 'rejected') {
        return 'Rejected by another reviewer; your decision is still taken';
    }
    return STATUS_WORDING[row.status] ?? row.status;
}

/** Row description: who asked, how big, and where it stands. */
export function describeReviewRow(row: ReviewInboxRow): string {
    const parts = [row.requestedBy, `${row.fileCount} file${row.fileCount === 1 ? '' : 's'}`, describeReviewStatus(row)];
    if (row.signing && SIGNING_WORDING[row.signing]) {
        parts.push(SIGNING_WORDING[row.signing]);
    }
    return parts.filter(Boolean).join(' · ');
}

export interface ReviewInboxProviderDependencies {
    /** True once the backend is authenticated and healthy, with or without a dashboard panel. */
    isBackendReady(): boolean;
    /** Resolves the body of `GET /api/v1/review-inbox`. */
    fetchInbox(): Promise<unknown>;
}

export class ReviewInboxTreeDataProvider implements vscode.TreeDataProvider<ReviewInboxRow>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    public readonly onDidChangeTreeData: vscode.Event<void> = this._onDidChangeTreeData.event;
    private readonly dependencies: ReviewInboxProviderDependencies;
    private state: ReviewInboxState | null = null;
    private message: string | undefined;
    private current: Promise<void> | null = null;
    private next: Promise<void> | null = null;

    constructor(dependencies: ReviewInboxProviderDependencies) {
        this.dependencies = dependencies;
    }

    public getRows(): ReviewInboxRow[] {
        return this.state?.requests ?? [];
    }

    public getState(): ReviewInboxState | null {
        return this.state;
    }

    public getMessage(): string | undefined {
        return this.message;
    }

    /** Reloads the cached list. Overlapping calls coalesce into one follow-up load. */
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

    public getTreeItem(row: ReviewInboxRow): vscode.TreeItem {
        const label = row.cardKey ? `${row.repository} · ${row.cardKey}` : row.repository;
        const node = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
        node.id = row.id;
        node.description = describeReviewRow(row);
        const answerable = this.state ? canAnswerReviewRequest(row, this.state) : false;
        const [icon, color] = row.signing === 'signing' ? ['key', 'charts.blue']
            : row.status === 'approved' ? ['check', 'charts.green']
            : row.status === 'rejected' ? ['close', 'charts.red']
            : row.status === 'open' && !row.decided ? ['mail', 'charts.orange']
            : ['archive', 'disabledForeground'];
        node.iconPath = new vscode.ThemeIcon(icon, new vscode.ThemeColor(color));
        node.tooltip = [
            `${row.repository}${row.cardKey ? ` (${row.cardKey})` : ''}`,
            `Requested by ${row.requestedBy}`,
            `${row.fileCount} file(s), ${row.sessionCount} session(s)`,
            describeReviewStatus(row),
            'Click to open Reviews requested of you in the dashboard.'
        ].join('\n');
        node.contextValue = answerable ? REVIEW_INBOX_OPEN_ITEM_CONTEXT : REVIEW_INBOX_ITEM_CONTEXT;
        node.command = { command: COMMAND_REVIEW_INBOX_OPEN, title: 'Open in the dashboard', arguments: [row] };
        return node;
    }

    public getChildren(element?: ReviewInboxRow): ReviewInboxRow[] {
        return element ? [] : this.getRows();
    }

    public dispose(): void {
        this._onDidChangeTreeData.dispose();
    }

    private async load(): Promise<void> {
        if (!this.dependencies.isBackendReady()) {
            this.apply(null, undefined);
            return;
        }

        let state: ReviewInboxState | null;
        try {
            state = normalizeReviewInbox(await this.dependencies.fetchInbox());
        } catch {
            state = null;
        }

        // The backend may have stopped while the request was in flight.
        if (!this.dependencies.isBackendReady()) {
            this.apply(null, undefined);
            return;
        }
        this.apply(state, reviewInboxMessage(state));
    }

    private apply(state: ReviewInboxState | null, message: string | undefined): void {
        this.state = state;
        this.message = message;
        this._onDidChangeTreeData.fire();
    }
}

export interface ReviewInboxActionDependencies {
    /** Authenticated call to the running backend; rejects with `BackendRequestError` on a non-2xx. */
    request(method: BackendMethod, path: string, body?: unknown): Promise<unknown>;
    /** Asks the person why they reject the request; `undefined` means they cancelled. */
    askComment(row: ReviewInboxRow): Thenable<string | undefined>;
    notify(message: string): void;
    notifyError(message: string): void;
    refresh(): void;
    /** Opens a vetted https://viberails.ai page in the browser. */
    openExternal(url: string): Thenable<boolean>;
}

/** Asks the backend to open the signing popup on its machine. The popup, not this host, shows the request and signs. */
export async function approveReviewRequest(row: ReviewInboxRow | undefined, dependencies: ReviewInboxActionDependencies): Promise<void> {
    if (!isReviewRequestReference(row?.id)) {
        return;
    }
    try {
        const response = await dependencies.request('POST', reviewInboxActionPath(row.id, 'approve'), {}) as { message?: unknown } | null | undefined;
        const text = typeof response?.message === 'string' ? response.message.trim() : '';
        dependencies.notify(text || 'The signing popup opened in its own window on the VibeRails computer.');
    } catch (error) {
        dependencies.notifyError(`${row.repository}: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
        dependencies.refresh();
    }
}

/** Posts a rejection with the comment the person typed. Nothing is sent without one. */
export async function rejectReviewRequest(row: ReviewInboxRow | undefined, dependencies: ReviewInboxActionDependencies): Promise<void> {
    if (!isReviewRequestReference(row?.id)) {
        return;
    }
    const comment = (await dependencies.askComment(row))?.trim();
    if (!comment) {
        return;
    }
    try {
        const response = await dependencies.request('POST', reviewInboxActionPath(row.id, 'reject'), { comment }) as { message?: unknown } | null | undefined;
        const text = typeof response?.message === 'string' ? response.message.trim() : '';
        dependencies.notify(text || 'Your rejection was recorded on viberails.ai.');
    } catch (error) {
        dependencies.notifyError(`${row.repository}: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
        dependencies.refresh();
    }
}

/** Opens the request's page on viberails.ai, where the files and the attached sessions are read. */
export async function readReviewRequest(row: ReviewInboxRow | undefined, dependencies: ReviewInboxActionDependencies): Promise<void> {
    if (!isReviewRequestPageUrl(row?.readUrl)) {
        dependencies.notifyError('This request has no viberails.ai page to open.');
        return;
    }
    await dependencies.openExternal(row.readUrl);
}

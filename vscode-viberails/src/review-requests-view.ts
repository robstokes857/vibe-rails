/**
 * The Activity Bar "Review requests" view (VB-50 slice F): this project's own review requests as the
 * root backend lists them (`GET /api/v1/review-requests`, the same list a Board card's Request review
 * section shows), with the card's status wording. A row click opens the request's card on the
 * dashboard Board. The dashboard relays `review_request_updated` over the webview bridge
 * (`__viberails_reviewRequestChanged__`); the view reloads and, when a decision arrived, shows a
 * notification naming it. Nothing here creates, sends, decides or signs anything.
 *
 * Wording is kept in step with wwwroot/js/modules/board-card-request-review.js. Everything the tree
 * needs from the extension host arrives through small dependency objects so it is unit-tested without
 * a backend or a webview.
 */
import * as vscode from 'vscode';
import { COMMAND_REVIEW_REQUESTS_OPEN } from './constants';

export interface ReviewRequestDecision {
    reviewer: string;
    /** approved | rejected */
    decision: string;
    comment: string | null;
    /** How many line comments the decision carries (slice F); the card shows their text. */
    lineComments: number;
}

export interface ReviewRequestRow {
    id: string;
    /** open | approved | rejected | cancelled */
    status: string;
    treeId: string;
    cardKey: string | null;
    reviewers: string[];
    fileCount: number;
    sessionCount: number;
    createdUtc: string;
    /** True once viberails.ai holds a copy. */
    sent: boolean;
    /** A send began and the answer was lost; the backend recovers or clears it, and refuses to delete it meanwhile (VIBE-212). */
    sendPending: boolean;
    decisions: ReviewRequestDecision[];
}

/** What the dashboard relays for one `review_request_updated` event. `status` is a request status, or `deleted`. */
export interface ReviewRequestChange {
    id: string;
    status: string;
    cardKey: string | null;
    sent: boolean;
}

export const REVIEW_REQUEST_ITEM_CONTEXT = 'reviewRequestItem';
export const REVIEW_REQUESTS_LOAD_FAILED_MESSAGE = 'Could not read the review requests.';
export const REVIEW_REQUESTS_EMPTY_MESSAGE = 'No review requests in this project yet. Ask for one from a Board card\'s Request review section.';

/** The ledger's request id: a GUID. */
export function isReviewRequestId(value: unknown): value is string {
    return typeof value === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value);
}

/** Shapes the backend's list: malformed rows dropped, every field typed and bounded. */
export function normalizeReviewRequests(raw: unknown): ReviewRequestRow[] {
    const view = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
    const requests = Array.isArray(view.requests) ? view.requests : [];
    return requests.map(normalizeRow).filter((row): row is ReviewRequestRow => row !== null);
}

function text(value: unknown, max: number): string {
    return typeof value === 'string' ? value.trim().slice(0, max) : '';
}

function normalizeRow(entry: unknown): ReviewRequestRow | null {
    const item = (entry && typeof entry === 'object' ? entry : {}) as Record<string, unknown>;
    if (!isReviewRequestId(item.id)) {
        return null;
    }
    const decisions = Array.isArray(item.decisions) ? item.decisions : [];
    return {
        id: item.id.toLowerCase(),
        status: text(item.status, 32) || 'open',
        treeId: text(item.treeId, 64),
        cardKey: text(item.cardKey, 64) || null,
        reviewers: (Array.isArray(item.reviewers) ? item.reviewers : []).map((reviewer) => text(reviewer, 320)).filter(Boolean),
        fileCount: Math.max(0, Number(item.fileCount) || 0),
        sessionCount: Math.max(0, Number(item.sessionCount) || 0),
        createdUtc: text(item.createdUtc, 64),
        sent: Boolean(item.remote && typeof item.remote === 'object'),
        // A send began and viberails.ai's answer was lost (VIBE-212); the backend settles it automatically.
        sendPending: !item.remote && typeof item.sendAttemptedUtc === 'string' && item.sendAttemptedUtc.length > 0,
        decisions: decisions.map((raw): ReviewRequestDecision | null => {
            const decision = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
            const kind = text(decision.decision, 16);
            if (kind !== 'approved' && kind !== 'rejected') {
                return null;
            }
            return {
                reviewer: text(decision.reviewer, 320),
                decision: kind,
                comment: text(decision.comment, 4000) || null,
                lineComments: Array.isArray(decision.lineComments) ? decision.lineComments.length : 0
            };
        }).filter((decision): decision is ReviewRequestDecision => decision !== null)
    };
}

/** The bridge payload, typed and bounded; null for anything that is not a review request change. */
export function reviewRequestChangeFromMessage(raw: unknown): ReviewRequestChange | null {
    const payload = (raw && typeof raw === 'object' ? raw : {}) as Record<string, unknown>;
    if (!isReviewRequestId(payload.id)) {
        return null;
    }
    const status = text(payload.status, 32);
    if (!status) {
        return null;
    }
    return { id: payload.id.toLowerCase(), status, cardKey: text(payload.cardKey, 64) || null, sent: payload.sent === true };
}

/** The card's wording: who approved or rejected, and whether an open request was sent. */
export function describeReviewRequestStatus(row: ReviewRequestRow): string {
    if (row.status === 'cancelled') {
        return 'Cancelled';
    }
    if (row.status === 'approved') {
        const people = [...new Set(row.decisions.filter((d) => d.decision === 'approved').map((d) => d.reviewer).filter(Boolean))];
        return people.length ? `Approved by ${people.join(', ')}` : 'Approved';
    }
    if (row.status === 'rejected') {
        const rejected = row.decisions.filter((d) => d.decision === 'rejected');
        const last = rejected[rejected.length - 1];
        if (!last) {
            return 'Rejected';
        }
        return `Rejected by ${last.reviewer}${last.comment ? `: ${last.comment}` : ''}`;
    }
    if (row.sent) {
        return 'Sent, awaiting decisions';
    }
    return row.sendPending ? 'Send not confirmed yet; checked automatically' : 'Open, not sent yet';
}

/** How many line comments the request's decisions carry altogether. */
export function countLineComments(row: ReviewRequestRow): number {
    return row.decisions.reduce((sum, decision) => sum + decision.lineComments, 0);
}

/**
 * The notification for a relayed change, or undefined when it is not a decision (a send, a cancel or
 * a delete only refresh the view). The row, when the reload found it, supplies the card's wording.
 */
export function reviewRequestNotification(change: ReviewRequestChange, row: ReviewRequestRow | undefined): string | undefined {
    if (change.status !== 'approved' && change.status !== 'rejected') {
        return undefined;
    }
    const subject = change.cardKey ? `Review request for ${change.cardKey}` : 'Review request';
    if (!row) {
        return `${subject} ${change.status}.`;
    }
    const lines = countLineComments(row);
    return `${subject}: ${describeReviewRequestStatus(row)}${lines ? ` (${lines} line comment${lines === 1 ? '' : 's'})` : ''}.`;
}

export interface ReviewRequestsProviderDependencies {
    /** True while the dashboard panel exists, which is the only time the backend is reachable. */
    isDashboardOpen(): boolean;
    /** Resolves the body of `GET /api/v1/review-requests`. */
    fetchRequests(): Promise<unknown>;
    /** Shows a decision that just arrived. */
    notify(message: string, change: ReviewRequestChange): void;
}

export class ReviewRequestsTreeDataProvider implements vscode.TreeDataProvider<ReviewRequestRow>, vscode.Disposable {
    private readonly _onDidChangeTreeData = new vscode.EventEmitter<void>();
    public readonly onDidChangeTreeData: vscode.Event<void> = this._onDidChangeTreeData.event;
    private readonly dependencies: ReviewRequestsProviderDependencies;
    private rows: ReviewRequestRow[] = [];
    private message: string | undefined;
    private current: Promise<void> | null = null;
    private next: Promise<void> | null = null;

    constructor(dependencies: ReviewRequestsProviderDependencies) {
        this.dependencies = dependencies;
    }

    public getRows(): ReviewRequestRow[] {
        return this.rows;
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

    /** A relayed `review_request_updated`: reload, then say so when a decision arrived. */
    public async handleChange(change: ReviewRequestChange): Promise<void> {
        await this.refresh();
        const message = reviewRequestNotification(change, this.rows.find((row) => row.id === change.id));
        if (message) {
            this.dependencies.notify(message, change);
        }
    }

    public getTreeItem(row: ReviewRequestRow): vscode.TreeItem {
        const tree = row.treeId.slice(0, 12);
        const node = new vscode.TreeItem(row.cardKey ? `${row.cardKey} · ${tree}` : `Tree ${tree}`, vscode.TreeItemCollapsibleState.None);
        node.id = row.id;
        node.description = describeReviewRequestStatus(row);
        const [icon, color] = row.status === 'approved' ? ['check', 'charts.green']
            : row.status === 'rejected' ? ['close', 'charts.red']
            : row.status === 'cancelled' ? ['archive', 'disabledForeground']
            : row.sent ? ['mail', 'charts.blue']
            : ['edit', 'charts.orange'];
        node.iconPath = new vscode.ThemeIcon(icon, new vscode.ThemeColor(color));
        const lines = countLineComments(row);
        node.tooltip = [
            row.cardKey ? `Card ${row.cardKey}` : 'No card',
            `Tree ${row.treeId}`,
            `Reviewers: ${row.reviewers.join(', ') || 'none'}`,
            `${row.fileCount} file(s), ${row.sessionCount} session(s)`,
            describeReviewRequestStatus(row),
            ...row.decisions.map((d) => `${d.decision === 'approved' ? 'Approved' : 'Rejected'} by ${d.reviewer}${d.lineComments ? ` with ${d.lineComments} line comment${d.lineComments === 1 ? '' : 's'}` : ''}`),
            lines ? 'Line comments are shown on the card.' : '',
            'Click to open the card on the dashboard Board.'
        ].filter(Boolean).join('\n');
        node.contextValue = REVIEW_REQUEST_ITEM_CONTEXT;
        node.command = { command: COMMAND_REVIEW_REQUESTS_OPEN, title: 'Open on the Board', arguments: [row] };
        return node;
    }

    public getChildren(element?: ReviewRequestRow): ReviewRequestRow[] {
        return element ? [] : this.rows;
    }

    public dispose(): void {
        this._onDidChangeTreeData.dispose();
    }

    private async load(): Promise<void> {
        if (!this.dependencies.isDashboardOpen()) {
            this.apply([], undefined);
            return;
        }

        let rows: ReviewRequestRow[] | null;
        try {
            rows = normalizeReviewRequests(await this.dependencies.fetchRequests());
        } catch {
            rows = null;
        }

        // The dashboard may have closed while the request was in flight.
        if (!this.dependencies.isDashboardOpen()) {
            this.apply([], undefined);
            return;
        }
        if (rows === null) {
            this.apply([], REVIEW_REQUESTS_LOAD_FAILED_MESSAGE);
            return;
        }
        this.apply(rows, rows.length ? undefined : REVIEW_REQUESTS_EMPTY_MESSAGE);
    }

    private apply(rows: ReviewRequestRow[], message: string | undefined): void {
        this.rows = rows;
        this.message = message;
        this._onDidChangeTreeData.fire();
    }
}

import * as assert from 'assert/strict';
import * as vscode from 'vscode';
import { BackendRequestError } from '../../backend-api';
import { reviewInboxActionPath } from '../../constants';
import {
    REVIEW_INBOX_EMPTY_MESSAGE,
    REVIEW_INBOX_ITEM_CONTEXT,
    REVIEW_INBOX_LOAD_FAILED_MESSAGE,
    REVIEW_INBOX_NO_KEY_MESSAGE,
    REVIEW_INBOX_OPEN_ITEM_CONTEXT,
    REVIEW_INBOX_UNVERIFIED_MESSAGE,
    ReviewInboxActionDependencies,
    ReviewInboxRow,
    ReviewInboxTreeDataProvider,
    approveReviewRequest,
    canAnswerReviewRequest,
    describeReviewRow,
    isReviewRequestPageUrl,
    normalizeReviewInbox,
    readReviewRequest,
    rejectReviewRequest,
    reviewInboxMessage
} from '../../review-inbox-view';

const reference = 'rvw_0123456789abcdef0123456789abcdef';

function rawRow(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        id: reference, repository: 'vibe-rails', requestedBy: 'dev@example.test', status: 'open', fileCount: 2, sessionCount: 1,
        cardKey: 'VIBE-208', createdUtc: '2026-10-24T10:00:00Z', expiresUtc: '2026-11-24T10:00:00Z', decided: false,
        readUrl: `https://viberails.ai/Reviews/requests/${reference}`, signing: null, ...extra
    };
}

function rawView(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return { hasApiKey: true, email: 'me@example.test', emailVerified: true, fetchedUtc: '2026-10-24T12:00:00Z', lastError: null, popupAvailable: true, requests: [rawRow()], ...extra };
}

function tick(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

function actions(overrides: Partial<ReviewInboxActionDependencies> = {}) {
    const calls: { method: string; path: string; body: unknown }[] = [];
    const notices: string[] = [];
    const errors: string[] = [];
    const opened: string[] = [];
    let refreshes = 0;
    const dependencies: ReviewInboxActionDependencies = {
        request: async (method, path, body) => { calls.push({ method, path, body }); return { message: 'Done.' }; },
        askComment: async () => 'Retry path still swallows the error.',
        notify: (message) => { notices.push(message); },
        notifyError: (message) => { errors.push(message); },
        refresh: () => { refreshes += 1; },
        openExternal: async (url) => { opened.push(url); return true; },
        ...overrides
    };
    return { dependencies, calls, notices, errors, opened, refreshes: () => refreshes };
}

suite('Review inbox view rules', () => {
    test('normalizes the backend view: typed fields, malformed rows dropped, signing outcome flattened', () => {
        const state = normalizeReviewInbox(rawView({
            requests: [rawRow({ signing: { outcome: 'signing' } }), rawRow({ id: 'rvw_short' }), null, 'junk', rawRow({ id: 'rvw_' + 'f'.repeat(32), fileCount: 'x', cardKey: '' })]
        }));

        assert.equal(state.hasApiKey, true);
        assert.equal(state.email, 'me@example.test');
        assert.equal(state.requests.length, 2);
        assert.equal(state.requests[0].signing, 'signing');
        assert.equal(state.requests[1].fileCount, 0);
        assert.equal(state.requests[1].cardKey, null);
        assert.deepEqual(normalizeReviewInbox(undefined).requests, []);
    });

    test('the empty-state text names the key, the verified email, a failed read and an empty list', () => {
        assert.equal(reviewInboxMessage(null), REVIEW_INBOX_LOAD_FAILED_MESSAGE);
        assert.equal(reviewInboxMessage(normalizeReviewInbox(rawView({ hasApiKey: false }))), REVIEW_INBOX_NO_KEY_MESSAGE);
        assert.equal(reviewInboxMessage(normalizeReviewInbox(rawView({ requests: [], emailVerified: false }))), REVIEW_INBOX_UNVERIFIED_MESSAGE);
        assert.equal(reviewInboxMessage(normalizeReviewInbox(rawView({ requests: [] }))), REVIEW_INBOX_EMPTY_MESSAGE);
        assert.match(reviewInboxMessage(normalizeReviewInbox(rawView({ requests: [], fetchedUtc: null, lastError: 'offline' }))) ?? '', /offline/);
        assert.equal(reviewInboxMessage(normalizeReviewInbox(rawView())), undefined);
    });

    test('only open, unanswered rows can be answered, and the description says where a request stands', () => {
        const state = normalizeReviewInbox(rawView({ requests: [rawRow(), rawRow({ decided: true, status: 'rejected' }), rawRow({ signing: { outcome: 'approved' }, status: 'approved' })] }));
        assert.equal(canAnswerReviewRequest(state.requests[0], state), true);
        assert.equal(canAnswerReviewRequest(state.requests[1], state), false);
        assert.equal(canAnswerReviewRequest(state.requests[0], { ...state, hasApiKey: false }), false);
        assert.equal(describeReviewRow(state.requests[0]), 'dev@example.test · 2 files · Waiting for your decision');
        assert.equal(describeReviewRow(state.requests[2]), 'dev@example.test · 2 files · Approved · Approved from this computer');
    });

    test('only the hosted reviewer page for a well-formed reference is a page to open', () => {
        assert.equal(isReviewRequestPageUrl(`https://viberails.ai/Reviews/requests/${reference}`), true);
        assert.equal(isReviewRequestPageUrl(`https://evil.example/Reviews/requests/${reference}`), false);
        assert.equal(isReviewRequestPageUrl('https://viberails.ai/Reviews/requests/rvw_short'), false);
        assert.equal(isReviewRequestPageUrl(`https://viberails.ai/Reviews/requests/${reference}?x=1`), false);
    });
});

suite('Review inbox tree', () => {
    test('shows rows while the dashboard is open, with inline actions only on answerable rows', async () => {
        let open = true;
        const provider = new ReviewInboxTreeDataProvider({
            isDashboardOpen: () => open,
            fetchInbox: async () => rawView({ requests: [rawRow(), rawRow({ id: 'rvw_' + 'a'.repeat(32), status: 'approved', decided: true })] })
        });
        try {
            await provider.refresh();
            const rows = provider.getChildren();
            assert.equal(rows.length, 2);
            assert.equal(provider.getMessage(), undefined);
            const first = provider.getTreeItem(rows[0]);
            assert.equal(first.label, 'vibe-rails · VIBE-208');
            assert.equal(first.contextValue, REVIEW_INBOX_OPEN_ITEM_CONTEXT);
            assert.equal(first.command?.command, 'viberails.reviewInbox.open');
            assert.deepEqual(first.command?.arguments, [rows[0]]);
            assert.equal(provider.getTreeItem(rows[1]).contextValue, REVIEW_INBOX_ITEM_CONTEXT);
            assert.ok((first.iconPath as vscode.ThemeIcon).id === 'mail');

            open = false;
            await provider.refresh();
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), undefined);
        } finally {
            provider.dispose();
        }
    });

    test('a failed read shows the load-failed message and overlapping refreshes coalesce', async () => {
        let calls = 0;
        let fail = true;
        const provider = new ReviewInboxTreeDataProvider({
            isDashboardOpen: () => true,
            fetchInbox: async () => { calls += 1; await tick(); if (fail) { throw new BackendRequestError(500, 'boom'); } return rawView(); }
        });
        try {
            await provider.refresh();
            assert.equal(provider.getMessage(), REVIEW_INBOX_LOAD_FAILED_MESSAGE);
            fail = false;
            const a = provider.refresh();
            const b = provider.refresh();
            const c = provider.refresh();
            await Promise.all([a, b, c]);
            assert.equal(calls, 3); // one in flight plus one follow-up, not one per trigger
            assert.equal(provider.getChildren().length, 1);
        } finally {
            provider.dispose();
        }
    });
});

suite('Review inbox actions', () => {
    const row = normalizeReviewInbox(rawView()).requests[0] as ReviewInboxRow;

    test('approve posts to the approve route, reports the popup and refreshes', async () => {
        const seen = actions();
        await approveReviewRequest(row, seen.dependencies);
        assert.deepEqual(seen.calls, [{ method: 'POST', path: reviewInboxActionPath(reference, 'approve'), body: {} }]);
        assert.deepEqual(seen.notices, ['Done.']);
        assert.equal(seen.refreshes(), 1);

        const failing = actions({ request: async () => { throw new BackendRequestError(409, 'The signing popup for this request is already open.'); } });
        await approveReviewRequest(row, failing.dependencies);
        assert.match(failing.errors[0], /already open/);
        assert.equal(failing.refreshes(), 1);
        const nothing = actions();
        await approveReviewRequest(undefined, nothing.dependencies);
        assert.deepEqual(nothing.calls, []);
    });

    test('reject posts the typed comment and nothing when the prompt is cancelled or blank', async () => {
        const seen = actions();
        await rejectReviewRequest(row, seen.dependencies);
        assert.deepEqual(seen.calls, [{ method: 'POST', path: reviewInboxActionPath(reference, 'reject'), body: { comment: 'Retry path still swallows the error.' } }]);
        assert.equal(seen.refreshes(), 1);

        const cancelled = actions({ askComment: async () => undefined });
        await rejectReviewRequest(row, cancelled.dependencies);
        const blank = actions({ askComment: async () => '   ' });
        await rejectReviewRequest(row, blank.dependencies);
        assert.deepEqual(cancelled.calls, []);
        assert.deepEqual(blank.calls, []);
        assert.equal(cancelled.refreshes(), 0);
    });

    test('read opens only the vetted viberails.ai page', async () => {
        const seen = actions();
        await readReviewRequest(row, seen.dependencies);
        assert.deepEqual(seen.opened, [`https://viberails.ai/Reviews/requests/${reference}`]);

        const refused = actions();
        await readReviewRequest({ ...row, readUrl: 'https://evil.example/x' }, refused.dependencies);
        assert.deepEqual(refused.opened, []);
        assert.equal(refused.errors.length, 1);
    });
});

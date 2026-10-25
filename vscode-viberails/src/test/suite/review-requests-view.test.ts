import * as assert from 'assert/strict';
import * as vscode from 'vscode';
import { BackendRequestError } from '../../backend-api';
import {
    REVIEW_REQUESTS_EMPTY_MESSAGE,
    REVIEW_REQUESTS_LOAD_FAILED_MESSAGE,
    REVIEW_REQUEST_ITEM_CONTEXT,
    ReviewRequestChange,
    ReviewRequestsTreeDataProvider,
    countLineComments,
    describeReviewRequestStatus,
    normalizeReviewRequests,
    reviewRequestChangeFromMessage,
    reviewRequestNotification
} from '../../review-requests-view';

const id = '6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b';
const other = '7a1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b';
const tree = 'b'.repeat(40);

function rawRow(extra: Record<string, unknown> = {}): Record<string, unknown> {
    return {
        id, scope: 'staged', treeId: tree, status: 'open', reviewers: ['lead@example.test'], cardKey: 'VB-ABC12-7', fileCount: 2, sessionCount: 1,
        createdUtc: '2026-10-24T12:00:00Z', updatedUtc: '2026-10-24T12:00:00Z', decisions: [], ...extra
    };
}

const rejected = rawRow({
    status: 'rejected', remote: { id: 'rvw_' + '0'.repeat(32), sentUtc: '2026-10-24T12:30:00Z' },
    decisions: [{ id: 2, reviewer: 'lead@example.test', decision: 'rejected', comment: 'Rename the column first.', decidedUtc: '2026-10-25T13:00:00Z',
        lineComments: [{ path: 'db.cs', line: 3, side: 'after', text: 'here' }, { path: 'db.cs', line: 9, side: 'before', text: 'and here' }] }]
});

function tick(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

suite('Review requests view rules', () => {
    test('normalizes the backend list: typed fields, malformed rows and decisions dropped, line comments counted', () => {
        const rows = normalizeReviewRequests({ requests: [rejected, rawRow({ id: 'not-a-guid' }), null, 'junk',
            rawRow({ id: other, fileCount: 'x', cardKey: '', reviewers: ['a@example.test', 7], decisions: [{ decision: 'maybe' }, null] })] });

        assert.equal(rows.length, 2);
        assert.equal(rows[0].sent, true);
        assert.equal(rows[0].status, 'rejected');
        assert.deepEqual(rows[0].decisions, [{ reviewer: 'lead@example.test', decision: 'rejected', comment: 'Rename the column first.', lineComments: 2 }]);
        assert.equal(countLineComments(rows[0]), 2);
        assert.equal(rows[1].fileCount, 0);
        assert.equal(rows[1].cardKey, null);
        assert.equal(rows[1].sent, false);
        assert.equal(rows[1].sendPending, false);
        assert.deepEqual(rows[1].reviewers, ['a@example.test']);
        assert.deepEqual(rows[1].decisions, []);
        assert.deepEqual(normalizeReviewRequests(undefined), []);
        // VIBE-212: an attempted send without a reference is pending; a reference wins over the attempt stamp.
        const [pending, settled] = normalizeReviewRequests({ requests: [rawRow({ sendAttemptedUtc: '2026-10-25T05:00:00Z' }),
            rawRow({ id: other, sendAttemptedUtc: '2026-10-25T05:00:00Z', remote: { id: 'rvw_' + '1'.repeat(32), sentUtc: '2026-10-25T05:01:00Z' } })] });
        assert.deepEqual([pending.sent, pending.sendPending, settled.sent, settled.sendPending], [false, true, true, false]);
    });

    test('status wording matches the card: sent or not, who approved, who rejected and why', () => {
        const [row] = normalizeReviewRequests({ requests: [rejected] });
        assert.equal(describeReviewRequestStatus(row), 'Rejected by lead@example.test: Rename the column first.');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'open', sent: false }), 'Open, not sent yet');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'open', sent: true }), 'Sent, awaiting decisions');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'open', sent: false, sendPending: true }), 'Send not confirmed yet; checked automatically');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'cancelled' }), 'Cancelled');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'approved', decisions: [{ reviewer: 'a@example.test', decision: 'approved', comment: null, lineComments: 0 },
            { reviewer: 'a@example.test', decision: 'approved', comment: null, lineComments: 0 }] }), 'Approved by a@example.test');
        assert.equal(describeReviewRequestStatus({ ...row, status: 'approved', decisions: [] }), 'Approved');
    });

    test('only a well-formed bridge payload is a change, and only a decision makes a notification', () => {
        assert.deepEqual(reviewRequestChangeFromMessage({ id: id.toUpperCase(), status: ' approved ', cardKey: 'VB-1', sent: true }), { id, status: 'approved', cardKey: 'VB-1', sent: true });
        assert.deepEqual(reviewRequestChangeFromMessage({ id, status: 'deleted', cardKey: '', sent: 'yes' }), { id, status: 'deleted', cardKey: null, sent: false });
        for (const bad of [null, 'x', {}, { id: 'rvw_x', status: 'open' }, { id, status: '' }, { id, status: 7 }]) {
            assert.equal(reviewRequestChangeFromMessage(bad), null, JSON.stringify(bad));
        }

        const [row] = normalizeReviewRequests({ requests: [rejected] });
        const change: ReviewRequestChange = { id, status: 'rejected', cardKey: 'VB-ABC12-7', sent: true };
        assert.equal(reviewRequestNotification(change, row), 'Review request for VB-ABC12-7: Rejected by lead@example.test: Rename the column first. (2 line comments).');
        assert.equal(reviewRequestNotification(change, undefined), 'Review request for VB-ABC12-7 rejected.');
        assert.equal(reviewRequestNotification({ ...change, cardKey: null }, undefined), 'Review request rejected.');
        assert.equal(reviewRequestNotification({ ...change, status: 'open' }, row), undefined);
        assert.equal(reviewRequestNotification({ ...change, status: 'cancelled' }, row), undefined);
        assert.equal(reviewRequestNotification({ ...change, status: 'deleted' }, undefined), undefined);
    });
});

suite('Review requests tree', () => {
    test('shows rows while the dashboard is open, with the card wording and an open command', async () => {
        let open = true;
        const provider = new ReviewRequestsTreeDataProvider({
            isDashboardOpen: () => open,
            fetchRequests: async () => ({ requests: [rejected, rawRow({ id: other, cardKey: null })] }),
            notify: () => { assert.fail('a plain refresh notifies nobody'); }
        });
        try {
            await provider.refresh();
            const rows = provider.getChildren();
            assert.equal(rows.length, 2);
            assert.equal(provider.getMessage(), undefined);
            const first = provider.getTreeItem(rows[0]);
            assert.equal(first.label, `VB-ABC12-7 · ${'b'.repeat(12)}`);
            assert.equal(first.description, 'Rejected by lead@example.test: Rename the column first.');
            assert.equal(first.contextValue, REVIEW_REQUEST_ITEM_CONTEXT);
            assert.equal(first.command?.command, 'viberails.reviewRequests.open');
            assert.deepEqual(first.command?.arguments, [rows[0]]);
            assert.equal((first.iconPath as vscode.ThemeIcon).id, 'close');
            assert.match(String(first.tooltip), /with 2 line comments/);
            const second = provider.getTreeItem(rows[1]);
            assert.equal(second.label, `Tree ${'b'.repeat(12)}`);
            assert.equal((second.iconPath as vscode.ThemeIcon).id, 'edit');

            open = false;
            await provider.refresh();
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), undefined);
        } finally {
            provider.dispose();
        }
    });

    test('an empty list and a failed read have their own messages, and overlapping refreshes coalesce', async () => {
        let calls = 0;
        let answer: () => unknown = () => { throw new BackendRequestError(500, 'boom'); };
        const provider = new ReviewRequestsTreeDataProvider({
            isDashboardOpen: () => true,
            fetchRequests: async () => { calls += 1; await tick(); return answer(); },
            notify: () => undefined
        });
        try {
            await provider.refresh();
            assert.equal(provider.getMessage(), REVIEW_REQUESTS_LOAD_FAILED_MESSAGE);
            answer = () => ({ requests: [] });
            await provider.refresh();
            assert.equal(provider.getMessage(), REVIEW_REQUESTS_EMPTY_MESSAGE);
            answer = () => ({ requests: [rejected] });
            const a = provider.refresh();
            const b = provider.refresh();
            const c = provider.refresh();
            await Promise.all([a, b, c]);
            assert.equal(calls, 4); // one in flight plus one follow-up, not one per trigger
            assert.equal(provider.getChildren().length, 1);
        } finally {
            provider.dispose();
        }
    });

    test('a relayed change reloads the list and notifies only when a decision arrived', async () => {
        const notices: [string, ReviewRequestChange][] = [];
        let calls = 0;
        const provider = new ReviewRequestsTreeDataProvider({
            isDashboardOpen: () => true,
            fetchRequests: async () => { calls += 1; return { requests: [rejected] }; },
            notify: (message, change) => { notices.push([message, change]); }
        });
        try {
            await provider.handleChange({ id, status: 'open', cardKey: 'VB-ABC12-7', sent: true });
            assert.equal(calls, 1);
            assert.equal(notices.length, 0);
            await provider.handleChange({ id, status: 'rejected', cardKey: 'VB-ABC12-7', sent: true });
            assert.equal(calls, 2);
            assert.equal(notices.length, 1);
            assert.equal(notices[0][0], 'Review request for VB-ABC12-7: Rejected by lead@example.test: Rename the column first. (2 line comments).');
            assert.equal(notices[0][1].cardKey, 'VB-ABC12-7');
            await provider.handleChange({ id: other, status: 'approved', cardKey: null, sent: true });
            assert.equal(notices[1][0], 'Review request approved.');
            await provider.handleChange({ id: other, status: 'deleted', cardKey: null, sent: false });
            assert.equal(notices.length, 2);
        } finally {
            provider.dispose();
        }
    });
});

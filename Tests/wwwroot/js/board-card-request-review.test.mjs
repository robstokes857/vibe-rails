import test from 'node:test';
import assert from 'node:assert/strict';
import {
    approvalTrailer, bindRequestReview, cardKeySet, commitApprovalBadge, describeCounts, describeDisclosure, describeRequestStatus,
    formatFileChange, groupLineComments, isForCard, lineCommentsHtml, orderRequests, parseReviewers, pickableSessions, previewHtml,
    requestReviewSection, requestRowHtml, requestTone, reviewStatusBadge
} from '../../../VibeRails/wwwroot/js/modules/board-card-request-review.js';

const turn = () => new Promise(resolve => setImmediate(resolve));
const TREE = 'b'.repeat(40);
const TRAILER = `vibe-signature: v1 key=6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b fp=${'f'.repeat(64)} tree=${TREE} sig=${'A'.repeat(344)}`;
const card = { id: 'card_test', key: 'VB-ABC12-7', displayId: 'VIBE-7', title: 'Example', commits: [{ sha: 'E'.repeat(40), shortSha: 'eeeeeee' }],
    sessions: [{ id: 'session_test', displayName: 'Codex session', cli: 'codex', active: true }, { id: 'auto', displayName: 'Worker', isAutomation: true }] };
const request = (extra = {}) => ({ id: '6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b', scope: 'staged', treeId: TREE, status: 'open', reviewers: ['lead@example.test'],
    cardKey: 'VB-ABC12-7', fileCount: 2, sessionCount: 1, createdUtc: '2026-10-24T12:00:00Z', updatedUtc: '2026-10-24T12:00:00Z', decisions: [], ...extra });
const approved = request({ status: 'approved', decisions: [{ id: 1, reviewer: 'lead@example.test', decision: 'approved', trailer: TRAILER, decidedUtc: '2026-10-24T13:00:00Z' }],
    remote: { id: 'rvw_' + '0'.repeat(32), sentUtc: '2026-10-24T12:30:00Z', expiresUtc: '2026-11-24T12:30:00Z' } });
const rejected = request({ id: 'r', status: 'rejected', cardKey: 'VB-9', createdUtc: '2026-10-25T12:00:00Z',
    decisions: [{ id: 2, reviewer: 'lead@example.test', decision: 'rejected', comment: '<b>needs tests</b>', decidedUtc: '2026-10-25T13:00:00Z' }] });

function fixture(apiCall, { hasDraft = () => false, checked = ['session_test'], commits = card.commits, open = false } = {}) {
    const nodes = new Map(['[data-card-review-message]', '[data-card-review-requests]', '[data-card-review-preview]', '[data-card-review-reviewers]',
        '[data-card-review-note]', '[data-card-review-sessions]'].map(key => [key, { value: '', textContent: '', innerHTML: '' }]));
    const listeners = new Map();
    const host = { isConnected: true, open, querySelector: s => nodes.get(s),
        querySelectorAll: s => s === '[data-card-review-session]:checked' ? checked.map(value => ({ value })) : [],
        addEventListener: (type, fn) => listeners.set(type, fn), removeEventListener: type => listeners.delete(type) };
    const editor = { querySelector: () => host };
    const events = new Map();
    const approvals = [];
    const control = bindRequestReview(editor, { ...card, commits }, { app: { apiCall, appEventClient: { on: (type, fn) => { events.set(type, fn); return () => events.delete(type); } } },
        hasDraft, onCommitApprovals: map => approvals.push(map) });
    const row = (id, trailer = TRAILER) => ({ dataset: { cardReviewId: id }, querySelector: () => ({ value: trailer, focus() {}, select() {} }) });
    return { control, host, nodes, events, approvals, listeners,
        open: async () => { host.open = true; listeners.get('toggle')(); await turn(); await turn(); },
        click: async (action, id = null) => { await listeners.get('click')({ target: { closest: s => s === '[data-review-action]'
            ? { dataset: { reviewAction: action }, closest: sel => sel === '[data-card-review-id]' && id ? row(id) : null } : null } }); await turn(); } };
}

test('reviewers parse, cards match by key, display id and short key, and the card\'s requests come first', () => {
    assert.deepEqual(parseReviewers(' a@x.test, A@x.test;b@x.test\nb@x.test '), ['a@x.test', 'A@x.test', 'b@x.test']);
    assert.deepEqual([...cardKeySet(card)].sort(), ['vb-7', 'vb-abc12-7', 'vibe-7']);
    assert.equal(isForCard(request({ cardKey: 'vibe-7' }), card), true);
    assert.equal(isForCard(request({ cardKey: 'VB-7' }), card), true);
    assert.equal(isForCard(request({ cardKey: 'VB-9' }), card), false);
    assert.equal(isForCard(request({ cardKey: null }), card), false);
    assert.deepEqual(orderRequests([rejected, request({ id: 'old', createdUtc: '2026-10-01T00:00:00Z' }), approved], card).map(item => item.id),
        ['6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b', 'old', 'r']);
    assert.deepEqual(pickableSessions(card).map(session => session.id), ['session_test']);
});

test('statuses say who approved or rejected and whether an open request was sent', () => {
    assert.equal(describeRequestStatus(request()), 'Open, not sent yet');
    assert.equal(describeRequestStatus(request({ remote: { id: 'rvw_x' } })), 'Sent, awaiting decisions');
    // VIBE-212: a send whose answer was lost is neither sent nor unsent; a reference wins once it is known.
    assert.equal(describeRequestStatus(request({ sendAttemptedUtc: '2026-10-25T05:00:00Z' })), 'Send not confirmed yet; checked automatically');
    assert.equal(describeRequestStatus(request({ sendAttemptedUtc: '2026-10-25T05:00:00Z', remote: { id: 'rvw_x' } })), 'Sent, awaiting decisions');
    assert.equal(describeRequestStatus(approved), 'Approved by lead@example.test');
    assert.equal(describeRequestStatus(rejected), 'Rejected by lead@example.test: <b>needs tests</b>');
    assert.equal(describeRequestStatus(request({ status: 'cancelled' })), 'Cancelled');
    assert.deepEqual([request(), request({ remote: {} }), approved, rejected, request({ status: 'cancelled' })].map(requestTone), ['warning', 'info', 'success', 'danger', 'secondary']);
    assert.equal(approvalTrailer(approved), TRAILER);
    assert.equal(approvalTrailer(rejected), null);
    assert.equal(formatFileChange({ added: 3, removed: 1 }), '+3 −1');
    assert.equal(formatFileChange({ changedLines: 4, textIncluded: false, omitted: 'binary' }), '±4 · no text (binary), not approvable');
    assert.equal(formatFileChange({ changedLines: 4, textIncluded: false, omitted: 'binary', bytesIncluded: true }), '±4 · no text (binary), bytes sent');
    assert.equal(formatFileChange({ textIncluded: false }), 'no text (omitted), not approvable');
    assert.equal(describeCounts({ files: [{}, {}], filesWithText: 1, filesWithoutText: 1, textBytes: 2048 }), '2 staged files: 1 with text (2.0 KiB), 1 without');
});

test('badges and rows escape server text and offer Send only for an open unsent request, Copy only with an approval', () => {
    // VIBE-211: files listed without text may travel as exact bytes; the counts say so.
    assert.equal(describeCounts({ files: [{}, {}, {}], filesWithText: 1, filesWithoutText: 2, textBytes: 2048, filesWithBytes: 1, carriedBytes: 3072 }),
        '3 staged files: 1 with text (2.0 KiB), 2 without text, 1 of them as exact bytes (3.0 KiB)');
    assert.equal(describeCounts({ files: [{}, {}, {}], filesWithText: 1, filesWithoutText: 2, textBytes: 2048, filesWithBytes: 2, carriedBytes: 3072 }),
        '3 staged files: 1 with text (2.0 KiB), 2 without text, 2 of those as exact bytes (3.0 KiB)');
});

test('the disclosure says when the bytes of files listed without text travel too', () => {
    const base = { destination: 'https://viberails.ai', reviewers: ['lead@example.test'], expiresUtc: '2026-11-24T12:00:00Z', treeId: TREE, textBytes: 12, sessions: [] };
    const one = describeDisclosure({ ...base, carriedBytes: 4096,
        files: [{ path: 'db.cs', textIncluded: true }, { path: 'logo.png', textIncluded: false, omitted: 'binary', bytesIncluded: true }] });
    assert.match(one, /1 listed without text \(binary\), its exact bytes \(4\.0 KiB\) so the reviewer's VibeRails can rebuild the tree: db\.cs, logo\.png\./);
    const some = describeDisclosure({ ...base, carriedBytes: 4096,
        files: [{ path: 'logo.png', textIncluded: false, omitted: 'binary', bytesIncluded: true }, { path: 'huge.bin', textIncluded: false, omitted: 'binary' }] });
    assert.match(some, /2 listed without text \(binary\), 1 of them with their exact bytes \(4\.0 KiB\)/);
    const none = describeDisclosure({ ...base, files: [{ path: 'huge.bin', textIncluded: false, omitted: 'oversized' }] });
    assert.match(none, /1 listed without text \(oversized\): huge\.bin\./);
    assert.doesNotMatch(none, /exact bytes/);
    const badge = reviewStatusBadge([rejected, approved], card);
    assert.match(badge, /text-bg-success/);
    assert.match(badge, /Review approved by lead@example\.test/);
    assert.equal(reviewStatusBadge([request({ status: 'cancelled' })], card), '');
    assert.match(reviewStatusBadge([request()], card), /Review request not sent/);
    assert.match(reviewStatusBadge([request({ remote: {} })], card), /Review requested/);
    const other = reviewStatusBadge([rejected], card);
    assert.equal(other, '', 'another card\'s request is not this card\'s status');
    const commit = commitApprovalBadge([{ reviewer: '<img src=x onerror=alert(1)>', requestId: 'x' }, { reviewer: 'lead@example.test' }]);
    assert.doesNotMatch(commit, /<img/);
    assert.match(commit, /Approved by &lt;img src=x onerror=alert\(1\)&gt;, lead@example\.test/);
    assert.equal(commitApprovalBadge([]), '');

    const rowOpen = requestRowHtml(request(), card);
    assert.match(rowOpen, /data-review-action="send"/);
    assert.match(rowOpen, /data-review-action="cancel"/);
    assert.doesNotMatch(rowOpen, /data-review-action="copy"/);
    assert.match(rowOpen, /this card/);
    const rowApproved = requestRowHtml(approved, card);
    assert.doesNotMatch(rowApproved, /data-review-action="send"/);
    assert.doesNotMatch(rowApproved, /data-review-action="cancel"/);
    assert.match(rowApproved, /data-review-action="copy"/);
    assert.match(rowApproved, new RegExp(`value="${TRAILER.slice(0, 40)}`));
    const rowRejected = requestRowHtml(rejected, card);
    assert.doesNotMatch(rowRejected, /<b>needs tests<\/b>/);
    assert.match(rowRejected, /Rejected by lead@example\.test: &lt;b&gt;needs tests&lt;\/b&gt;/);
    assert.match(rowRejected, /card VB-9/);

    const preview = previewHtml({ treeId: TREE, files: [{ path: '<script>x.cs', change: 'modified', added: 1, removed: 1, textIncluded: true },
        { path: 'logo.png', change: 'added', textIncluded: false, omitted: 'binary' }], filesWithText: 1, filesWithoutText: 1, textBytes: 30,
        rules: [{ path: 'db.cs', approvers: ['lead@example.test'], sourcePath: 'vc.rules.md' }] });
    assert.doesNotMatch(preview, /<script>/);
    assert.match(preview, /modified &lt;script&gt;x\.cs/);
    assert.match(preview, /\+1 −1/);
    assert.match(preview, /no text \(binary\)/);
    assert.match(preview, /Signed Approval rule covering these paths/);
    assert.match(requestReviewSection(true), /Create request/);
    assert.match(requestReviewSection(false), /Save the card/);
    const text = describeDisclosure({ destination: 'https://viberails.ai', reviewers: ['lead@example.test'], expiresUtc: '2026-11-24T12:00:00Z', treeId: TREE,
        files: [{ path: 'db.cs', textIncluded: true }, { path: 'logo.png', textIncluded: false, omitted: 'binary' }], textBytes: 12,
        sessions: [{ name: 'Codex session', live: true }], sessionDisclosure: 'Each attached session uploads…', note: 'look here' });
    assert.match(text, /^To https:\/\/viberails\.ai, readable only by lead@example\.test/);
    assert.match(text, /2 files of tree bbbbbbbbbbbb with their text \(12 B\); 1 listed without text \(binary\): db\.cs, logo\.png\./);
    assert.match(text, /Codex session \(still running: uploads after it ends\)\. Each attached session uploads…/);
    assert.match(text, /Note: look here/);
});

test('line comments are grouped by file, ordered by side and line, escaped, and shown under the decision on the row', () => {
    const comments = [{ path: 'b.cs', line: 7, side: 'after', text: 'late' }, { path: 'a.cs', line: 12, side: 'after', text: '<b>x</b>' },
        { path: 'a.cs', line: 3, side: 'before', text: 'old' }, { path: 'a.cs', line: 5, side: 'after', text: 'new' }, { path: '', line: 1, side: 'after', text: 'dropped' }];
    assert.deepEqual(groupLineComments(comments).map(group => [group.path, group.comments.map(comment => `${comment.side}:${comment.line}`)]),
        [['a.cs', ['before:3', 'after:5', 'after:12']], ['b.cs', ['after:7']]]);
    assert.deepEqual(groupLineComments(null), []);
    assert.equal(lineCommentsHtml({ lineComments: [] }), '');
    assert.equal(lineCommentsHtml(null), '');
    const html = lineCommentsHtml({ lineComments: comments });
    assert.doesNotMatch(html, /<b>x<\/b>/);
    assert.match(html, /&lt;b&gt;x&lt;\/b&gt;/);
    assert.match(html, /−L3/);
    assert.match(html, /\+L5/);
    const withComments = request({ status: 'rejected', decisions: [{ id: 3, reviewer: 'lead@example.test', decision: 'rejected', comment: 'see lines', lineComments: comments.slice(0, 4), decidedUtc: '2026-10-25T13:00:00Z' }] });
    const row = requestRowHtml(withComments, card);
    assert.match(row, /Rejected by lead@example\.test · 4 line comments/);
    assert.match(row, /data-card-review-line-comments/);
    assert.doesNotMatch(requestRowHtml(rejected, card), /data-card-review-line-comments/, 'a decision without line comments adds nothing');
    assert.doesNotMatch(requestRowHtml(request({ decisions: [{ id: 4, reviewer: 'x@example.test', decision: 'rejected', lineComments: null }] }), card), /data-card-review-line-comments/);
});

test('binding lists the project\'s requests and asks which linked commits carry an approved tree', async () => {
    const calls = [];
    const f = fixture(async (path, method) => {
        calls.push([path, method]);
        if (path.startsWith('/api/v1/review-requests/commit-approvals')) return { commits: [{ sha: 'e'.repeat(40), treeId: TREE, approvals: [{ reviewer: 'lead@example.test', requestId: 'x' }] }] };
        return { requests: [approved, rejected] };
    });
    await turn(); await turn();
    assert.deepEqual(calls, [['/api/v1/review-requests', 'GET'], [`/api/v1/review-requests/commit-approvals?sha=${'e'.repeat(40)}`, 'GET']]);
    assert.deepEqual(f.approvals, [{ ['e'.repeat(40)]: [{ reviewer: 'lead@example.test', requestId: 'x' }] }]);
    const rows = f.nodes.get('[data-card-review-requests]').innerHTML;
    assert.ok(rows.indexOf('Approved by lead@example.test') < rows.indexOf('Rejected by'), 'this card\'s request comes first');
    assert.match(f.nodes.get('[data-card-review-sessions]').innerHTML, /value="session_test"[^>]*checked/);
    assert.doesNotMatch(f.nodes.get('[data-card-review-sessions]').innerHTML, /Worker/);
    f.control.dispose();
});

test('creating needs saved drafts and reviewers, then posts the card key, checked sessions and note and reloads', async () => {
    const calls = [];
    let drafts = true;
    const f = fixture(async (...args) => { calls.push(args); return args[1] === 'POST' ? { id: 'new', treeId: TREE } : { requests: [] }; }, { hasDraft: () => drafts });
    await turn();
    await f.click('create');
    assert.match(f.nodes.get('[data-card-review-message]').textContent, /drafts are still here/);
    drafts = false;
    await f.click('create');
    assert.match(f.nodes.get('[data-card-review-message]').textContent, /at least one reviewer/);
    f.nodes.get('[data-card-review-reviewers]').value = 'lead@example.test, lead@example.test';
    f.nodes.get('[data-card-review-note]').value = '  Check the migration  ';
    assert.equal(calls.filter(c => c[1] === 'POST').length, 0);
    await f.click('create');
    const post = calls.find(c => c[1] === 'POST');
    assert.equal(post[0], '/api/v1/review-requests');
    assert.deepEqual(post[2], { scope: 'staged', reviewers: ['lead@example.test'], sessions: ['session_test'], cardKey: 'VB-ABC12-7', note: 'Check the migration' });
    assert.match(f.nodes.get('[data-card-review-message]').textContent, /Nothing has been sent/);
    assert.equal(f.nodes.get('[data-card-review-note]').value, '');
    assert.equal(calls.filter(c => c[0] === '/api/v1/review-requests' && c[1] === 'GET').length, 2, 'the list is reloaded after creation');
    f.control.dispose();
});

test('sending reads the disclosure first and transfers nothing without the confirmation', async () => {
    const calls = [];
    const f = fixture(async (...args) => { calls.push(args); return args[0].endsWith('/disclosure')
        ? { destination: 'https://viberails.ai', reviewers: ['lead@example.test'], expiresUtc: '2026-11-24T12:00:00Z', treeId: TREE, files: [], sessions: [], textBytes: 0 }
        : { requests: [request()] }; }, { commits: [] });
    await turn();
    await f.click('send', request().id);
    await turn();
    // confirmDialog cannot open without a document and answers false: the disclosure was read, nothing was posted.
    assert.deepEqual(calls.map(c => `${c[1]} ${c[0]}`), ['GET /api/v1/review-requests', `GET /api/v1/review-requests/${request().id}/disclosure`]);
    assert.equal(f.nodes.get('[data-card-review-message]').textContent, 'Not sent.');
    f.control.dispose();
});

test('opening the section reads the staged preview once and prefills untouched reviewers from the matched rules', async () => {
    const calls = [];
    const f = fixture(async (path) => { calls.push(path); return path.endsWith('/staged')
        ? { treeId: TREE, files: [{ path: 'db.cs', change: 'modified', added: 1, removed: 1, textIncluded: true }], filesWithText: 1, filesWithoutText: 0, textBytes: 10,
            suggestedReviewers: ['lead@example.test', 'second@example.test'], rules: [{ path: 'db.cs', approvers: ['lead@example.test', 'second@example.test'], sourcePath: 'vc.rules.md' }] }
        : { requests: [] }; }, { commits: [] });
    await turn();
    await f.open();
    assert.equal(calls.filter(path => path.endsWith('/staged')).length, 1);
    assert.match(f.nodes.get('[data-card-review-preview]').innerHTML, /modified db\.cs/);
    assert.equal(f.nodes.get('[data-card-review-reviewers]').value, 'lead@example.test\nsecond@example.test');
    f.nodes.get('[data-card-review-reviewers]').value = 'me@example.test';
    f.listeners.get('input')({ target: f.nodes.get('[data-card-review-reviewers]') });
    await f.open();
    assert.equal(calls.filter(path => path.endsWith('/staged')).length, 1, 'a second open does not re-read');
    await f.click('reload');
    assert.equal(calls.filter(path => path.endsWith('/staged')).length, 2);
    assert.equal(f.nodes.get('[data-card-review-reviewers]').value, 'me@example.test', 'typed reviewers are kept');
    f.control.dispose();
});

test('a review_request_updated event reloads the list once, and disposal stops listening and aborts reads', async () => {
    const calls = [];
    let signal;
    const f = fixture((path, method, body, options) => { calls.push(path); signal = options?.signal ?? signal; return Promise.resolve({ requests: [] }); }, { commits: [] });
    await turn();
    assert.equal(calls.length, 1);
    f.events.get('review_request_updated')();
    f.events.get('review_request_updated')();
    await turn(); await turn();
    assert.equal(calls.length, 2, 'two events in one tick make one reload');
    f.control.dispose();
    assert.equal(signal.aborted, true);
    assert.equal(f.events.has('review_request_updated'), false);
    assert.equal(f.listeners.size, 0);
});

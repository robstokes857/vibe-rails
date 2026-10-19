import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { boardBrandLogo, boardTracker, cardTracker, trackerLabel } from '../../../VibeRails/wwwroot/js/modules/board-brand.js';

const ADO_URL = 'https://dev.azure.com/contoso/Fabrikam%20Web/_workitems/edit/123';
const adoCard = overrides => ({ id: 'card', boardId: 'ado', remoteProvider: 'ado', remoteIssueKey: '#123', remoteIssueUrl: ADO_URL, ...overrides });

test('the Azure DevOps badge opens the work item safely and rejects unsafe URLs', () => {
    const controller = new BoardController({ apiCall: async () => ({}) });
    const html = controller.remoteBadge(adoCard());
    assert.match(html, /Azure DevOps · #123/);
    assert.match(html, /src="assets\/img\/azure-devops.svg"/);
    assert.ok(html.includes(`href="${ADO_URL}"`), html);
    assert.match(html, /rel="noopener noreferrer"/);
    assert.match(html, /aria-label="Open #123 in Azure DevOps"/);
    for (const remoteIssueUrl of ['javascript:alert(1)', 'https://user:pat@dev.azure.com/contoso/_workitems/edit/1',
        'data:text/html,hello', 'http://dev.azure.com/contoso/_workitems/edit/1', null]) {
        const unsafe = controller.remoteBadge(adoCard({ remoteIssueUrl }));
        assert.doesNotMatch(unsafe, /href=/);
        assert.match(unsafe, /Azure DevOps · #123/);
    }
    const escaped = controller.remoteBadge(adoCard({ remoteIssueKey: '#1<svg onload=x>' }));
    assert.doesNotMatch(escaped, /<svg/);
    assert.match(escaped, /#1&lt;svg onload=x&gt;/);
});

test('tracker lookups follow remoteProvider, fall back to the Jira fields and ignore unknown providers', () => {
    assert.deepEqual(cardTracker(adoCard()), { provider: 'ado', key: '#123', url: ADO_URL });
    // An ADO card never borrows a Jira URL, and an older response with only Jira fields still works.
    assert.equal(cardTracker(adoCard({ remoteIssueUrl: null, jiraIssueUrl: 'https://jira.example/browse/A-1' })).url, null);
    assert.deepEqual(cardTracker({ jiraIssueKey: 'APP-1', jiraIssueUrl: 'https://jira.example/browse/APP-1' }),
        { provider: 'jira', key: 'APP-1', url: 'https://jira.example/browse/APP-1' });
    assert.deepEqual(cardTracker({ remoteProvider: 'jira', remoteIssueKey: 'APP-1', jiraIssueKey: 'APP-1', jiraIssueUrl: 'https://jira.example/browse/APP-1' }),
        { provider: 'jira', key: 'APP-1', url: 'https://jira.example/browse/APP-1' });
    assert.equal(cardTracker({ remoteProvider: 'github', remoteIssueKey: '#9' }), null);
    assert.equal(cardTracker({}), null);
    assert.equal(boardTracker({ remoteProvider: 'ado', isJiraBoard: false }), 'ado');
    assert.equal(boardTracker({ isJiraBoard: true }), 'jira');
    assert.equal(boardTracker({ remoteProvider: 'github' }), null);
    assert.equal(boardTracker(null), null);
    assert.equal(trackerLabel('ado'), 'Azure DevOps');
    assert.equal(trackerLabel(null), '');
    assert.match(boardBrandLogo('ado'), /alt="Azure DevOps board"/);
    assert.match(boardBrandLogo(null), /src="assets\/img\/logo.png" alt="VibeRails board"/);
    assert.match(boardBrandLogo('ado', ''), /aria-hidden="true"/);
});

test('the comment checkbox names the card tracker and posts syncToRemote', async () => {
    globalThis.window = {};
    const calls = [];
    const controller = new BoardController({ apiCall: async (...args) => { calls.push(args); return {}; } });
    const choice = controller.remoteSyncChoice(adoCard());
    assert.match(choice, /data-board-remote-sync checked/);
    assert.match(choice, /Post this comment to Azure DevOps/);
    assert.match(choice, /public replay link posted to Azure DevOps/);
    assert.match(controller.remoteSyncChoice({ jiraIssueKey: 'APP-1' }), /Post this comment to Jira/);
    assert.equal(controller.remoteSyncChoice({ id: 'local' }), '');

    controller.cardIdFromEditor = () => 'card';
    controller.reloadEditingCard = async () => null;
    let checked = true;
    const editor = { querySelector: selector => selector === '[data-board-remote-sync]' ? { checked } : null };
    await controller.postComment(editor, 'Shared');
    checked = false;
    await controller.postComment(editor, 'Internal');
    await controller.postComment({ querySelector: () => null }, 'A local card');
    assert.deepEqual(calls.map(call => [call[0], call[2].syncToRemote]), [
        ['/api/v1/board/cards/card/comments', true],
        ['/api/v1/board/cards/card/comments', false],
        ['/api/v1/board/cards/card/comments', true]
    ]);
});

test('the local card editor offers the Azure DevOps checkbox and its API defaults to delivery', async () => {
    const { localCardEditorHtml } = await import('../../../VibeRails/wwwroot/js/modules/board-local-card.js');
    const html = localCardEditorHtml({ card: adoCard({ title: 'Work', columnId: 'lane' }), projectPath: 'C:/other', boardName: 'Azure DevOps · Team', columns: [] });
    assert.match(html, /name="syncToRemote" checked> Post this comment to Azure DevOps/);
    assert.doesNotMatch(localCardEditorHtml({ card: { id: 'c', title: 'Local' }, projectPath: 'C:/other', boardName: 'Main', columns: [] }), /syncToRemote/);
    const calls = [];
    BoardApi.attach({ apiCall: async (...args) => { calls.push(args); return {}; } });
    await BoardApi.addLocalBoardCommentAsync('card', { body: 'Shared' });
    assert.equal(calls[0][2].syncToRemote, true);
});

test('unsent comments say "Not sent to Azure DevOps" only on the card\'s own Azure DevOps board', t => {
    globalThis.window = {};
    const controller = new BoardController({ apiCall: async () => ({}) });
    const comment = { id: 'comment', body: 'Note', author: {}, syncToJira: false };
    assert.match(controller.cardLogCommentHtml(comment, {}, 'ado'), /Not sent to Azure DevOps/);
    assert.doesNotMatch(controller.cardLogCommentHtml(comment, {}, 'ado'), /Not sent to Jira/);
    assert.doesNotMatch(controller.cardLogCommentHtml({ ...comment, syncToJira: true }, {}, 'ado'), /Not sent/);
    assert.doesNotMatch(controller.cardLogCommentHtml(comment, {}, 'github'), /Not sent/);

    const savedFrame = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = () => 0;
    t.after(() => { globalThis.requestAnimationFrame = savedFrame; });
    const host = { innerHTML: '', isConnected: false, querySelectorAll: () => [] };
    const discussion = { querySelector: selector => selector === '[data-board-comments]' ? host : null, querySelectorAll: () => [] };
    controller.state.boards = [{ id: 'local', isJiraBoard: false, remoteProvider: null }, { id: 'ado', isJiraBoard: false, remoteProvider: 'ado' }];
    controller.state.boardId = 'ado';
    controller.renderCardDiscussion(discussion, adoCard({ boardId: 'local', comments: [comment] }));
    assert.doesNotMatch(host.innerHTML, /Not sent/, 'a retained work item and the selected board do not decide the card board type');
    controller.state.boardId = 'local';
    controller.renderCardDiscussion(discussion, { boardId: 'ado', comments: [comment] });
    assert.match(host.innerHTML, /Not sent to Azure DevOps/);
});

test('delivery status reports Azure DevOps failures and waiting updates, escaped', t => {
    globalThis.window = {};
    const controller = new BoardController({ apiCall: async () => ({}) });
    const savedFrame = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = () => 0;
    t.after(() => { globalThis.requestAnimationFrame = savedFrame; });
    const status = { innerHTML: '' };
    const host = { innerHTML: '', isConnected: false, querySelectorAll: () => [] };
    const editor = {
        querySelector: selector => selector === '[data-board-comments]' ? host
            : selector === '[data-board-remote-delivery-status]' ? status : null,
        querySelectorAll: () => []
    };
    controller.renderCardDiscussion(editor, adoCard({ comments: [], jiraDeliveries: [{ kind: 'comment', status: 'failed', message: 'jira list' }],
        remoteDeliveries: [
            { kind: 'comment', status: 'failed', message: 'Azure DevOps refused <b>it</b>' },
            { kind: 'session', status: 'uncertain', message: null },
            { kind: 'comment', status: 'pending' },
            { kind: 'comment', status: 'sent' }
        ] }));
    assert.match(status.innerHTML, /Azure DevOps comment: Azure DevOps refused &lt;b&gt;it&lt;\/b&gt;/);
    assert.match(status.innerHTML, /Azure DevOps session link: uncertain/);
    assert.match(status.innerHTML, /1 Azure DevOps update waiting for delivery\./);
    assert.doesNotMatch(status.innerHTML, /jira list|<b>/);

    // An older response with only the Jira list still reports Jira delivery.
    controller.renderCardDiscussion(editor, { comments: [], jiraIssueKey: 'APP-1', jiraDeliveries: [{ kind: 'comment', status: 'sending' }, { kind: 'comment', status: 'pending' }] });
    assert.match(status.innerHTML, /2 Jira updates waiting for delivery\./);
    controller.renderCardDiscussion(editor, { comments: [] });
    assert.equal(status.innerHTML, '');
});

test('the 10 s activity poll re-renders the discussion when Azure DevOps delivery changes', async t => {
    const original = globalThis.document;
    const card = adoCard({ comments: [], notes: [], attachments: [], commits: [], sessions: [],
        remoteDeliveries: [{ id: 'd1', kind: 'comment', status: 'pending' }] });
    const editor = { dataset: { cardId: 'card' }, isConnected: true, _boardCard: card,
        querySelector: () => null, querySelectorAll: () => [] };
    globalThis.document = { querySelector: () => editor };
    t.after(() => { globalThis.document = original; });
    const requests = [];
    const controller = new BoardController({
        currentView: 'terminal-focus',
        apiCall(url, method, body, options) { return new Promise(resolve => requests.push({ url, resolve })); }
    });
    controller.cardAutomations = null;
    const rendered = [];
    controller.renderCardDiscussion = (_, current) => rendered.push(current.remoteDeliveries.map(d => d.status));
    controller.renderSessionsPanel = () => {};
    const poll = controller.refreshSessionActivity();
    requests[0].resolve({ ...card, remoteDeliveries: [{ id: 'd1', kind: 'comment', status: 'failed', message: 'Rejected' }],
        remoteIssueUrl: ADO_URL + '?moved' });
    await poll;
    assert.deepEqual(rendered, [['failed']]);
    assert.equal(editor._boardCard.remoteIssueUrl, ADO_URL + '?moved');
});

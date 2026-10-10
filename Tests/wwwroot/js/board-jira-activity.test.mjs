import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';

test('Jira badge opens the issue safely and rejects executable URLs', () => {
    const controller = new BoardController({ apiCall: async () => ({}) });
    const card = { jiraIssueKey: 'APP-1', jiraIssueUrl: 'https://jira.example/browse/APP-1' };
    assert.match(controller.jiraBadge(card), /href="https:\/\/jira.example\/browse\/APP-1"/);
    assert.match(controller.jiraBadge(card), /rel="noopener noreferrer"/);
    for (const jiraIssueUrl of ['javascript:alert(1)', 'https://user:password@jira.example/', 'data:text/html,hello'])
        assert.doesNotMatch(controller.jiraBadge({ ...card, jiraIssueUrl }), /href=/);
});

test('comment API defaults to Jira delivery and preserves an explicit opt-out on both routes', async () => {
    const calls = [];
    BoardApi.attach({ apiCall: async (...args) => { calls.push(args); return {}; } });
    await BoardApi.addBoardCommentAsync('card', { body: 'Shared' });
    await BoardApi.addBoardCommentAsync('card', { body: 'Internal', syncToJira: false });
    await BoardApi.addLocalBoardCommentAsync('card', { body: 'Internal elsewhere', syncToJira: false });
    assert.deepEqual(calls.map(c => c[2].syncToJira), [true, false, false]);
});

test('posting reads the current editor sync choice and labels unsynced comments only on Jira boards', async t => {
    globalThis.window = {};
    const calls = [];
    const controller = new BoardController({ apiCall: async (...args) => { calls.push(args); return {}; } });
    controller.cardIdFromEditor = () => 'card';
    controller.reloadEditingCard = async () => null;
    const editor = { querySelector: selector => selector === '[data-board-jira-sync]' ? { checked: false } : null };
    await controller.postComment(editor, 'Keep here');
    assert.equal(calls[0][2].syncToJira, false);
    const comment = { id: 'comment', body: 'Note', author: {}, syncToJira: false };
    assert.match(controller.cardLogCommentHtml(comment, {}, true), /Not sent to Jira/);
    assert.doesNotMatch(controller.cardLogCommentHtml(comment, {}, false), /Not sent to Jira/);
    assert.doesNotMatch(controller.cardLogCommentHtml(comment, {}), /Not sent to Jira/);
    assert.doesNotMatch(controller.cardLogCommentHtml({ ...comment, syncToJira: true }, {}, true), /Not sent to Jira/);

    const savedFrame = globalThis.requestAnimationFrame;
    globalThis.requestAnimationFrame = () => 0;
    t.after(() => { globalThis.requestAnimationFrame = savedFrame; });
    const host = { innerHTML: '', isConnected: false, querySelectorAll: () => [] };
    const discussion = { querySelector: selector => selector === '[data-board-comments]' ? host : null, querySelectorAll: () => [] };
    controller.state.boards = [{ id: 'local', isJiraBoard: false }, { id: 'jira', isJiraBoard: true }];
    controller.state.boardId = 'jira';
    controller.renderCardDiscussion(discussion, { boardId: 'local', jiraIssueKey: 'OLD-1', comments: [comment] });
    assert.doesNotMatch(host.innerHTML, /Not sent to Jira/, 'retained issue keys and the selected board do not determine the card board type');
    controller.state.boardId = 'local';
    controller.renderCardDiscussion(discussion, { boardId: 'jira', comments: [comment] });
    assert.match(host.innerHTML, /Not sent to Jira/);
});

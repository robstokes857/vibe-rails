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

test('posting reads the current editor sync choice and unsynced comments are labelled', async () => {
    globalThis.window = {};
    const calls = [];
    const controller = new BoardController({ apiCall: async (...args) => { calls.push(args); return {}; } });
    controller.cardIdFromEditor = () => 'card';
    controller.reloadEditingCard = async () => null;
    const editor = { querySelector: selector => selector === '[data-board-jira-sync]' ? { checked: false } : null };
    await controller.postComment(editor, 'Keep here');
    assert.equal(calls[0][2].syncToJira, false);
    assert.match(controller.cardLogCommentHtml({ id: 'comment', body: 'Note', author: {}, syncToJira: false }, {}), /Not sent to Jira/);
});

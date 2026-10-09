import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function card(overrides) {
    return { id: 'card', key: 'VB-1', displayId: 'VIBE-1', title: 'Work', description: '', type: 'task',
        priority: 'medium', columnId: 'backlog', tags: [], agentMade: false, ...overrides };
}

function controller() {
    const instance = new BoardController({ currentView: 'board', apiCall() { return Promise.resolve({}); } });
    instance.state.columns = [{ id: 'backlog', name: 'Backlog' }];
    return instance;
}

test('the origin filter keeps agent-made cards apart from human-made ones', () => {
    const board = controller();
    board.state.cards = [card({ id: 'agent', agentMade: true }), card({ id: 'human' })];

    board.state.filters.origin = 'agent';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['agent']);

    board.state.filters.origin = 'human';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['human']);

    board.state.filters.origin = '';
    assert.equal(board.filteredCards().length, 2);
    assert.equal(board.hasActiveFilters(), false);
    board.state.filters.origin = 'agent';
    assert.equal(board.hasActiveFilters(), true);
});

test('an agent-made tile carries the robot mark and a human tile does not', () => {
    const board = controller();
    const agent = board.renderCard(card({ agentMade: true, title: 'Filed by <agent>' }));
    assert.match(agent, /board-agent-mark/);
    assert.match(agent, /Made by an agent/);
    assert.match(agent, /Filed by &lt;agent&gt;/);
    assert.doesNotMatch(agent, /Filed by <agent>/);

    const human = board.renderCard(card({ title: 'Typed by hand' }));
    assert.doesNotMatch(human, /board-agent-mark/);
});

test('Jira origins have an escaped logo badge and are excluded from the human filter', () => {
    const board = controller();
    const imported = card({ id: 'jira', jiraIssueKey: 'PROJ-1<svg>' });
    board.state.cards = [imported, card({ id: 'human' }), card({ id: 'agent', agentMade: true })];
    board.state.filters.origin = 'jira';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['jira']);
    board.state.filters.origin = 'human';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['human']);
    const html = board.renderCard(imported);
    assert.match(html, /class="board-brand-logo" src="assets\/img\/jira.svg"/);
    assert.match(html, /Jira · PROJ-1&lt;svg&gt;/);
    assert.doesNotMatch(html, /<svg>/);
});

test('legacy and current Grok selections produce one filter option in paged and fallback boards', () => {
    globalThis.window = {};
    const board = controller();
    board.state.cards = [card({ assignee: 'base:grok-4.6' }), card({ assignee: 'base:grok' })];
    assert.equal(board.allAssignees().length, 1);
    board.cardPage = { assignees: ['base:grok-4.6', 'base:grok'] };
    assert.equal(board.allAssignees().length, 1);
    assert.equal(board.allAssignees()[0].key, 'base:grok');
});

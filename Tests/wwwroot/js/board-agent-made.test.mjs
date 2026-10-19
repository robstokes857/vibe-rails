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

test('Azure DevOps origins have their own filter and badge, and Human-made excludes both trackers', () => {
    const board = controller();
    const ado = card({ id: 'ado', remoteProvider: 'ado', remoteIssueKey: '#42<svg>' });
    const jira = card({ id: 'jira', remoteProvider: 'jira', remoteIssueKey: 'PROJ-1', jiraIssueKey: 'PROJ-1' });
    board.state.cards = [ado, jira, card({ id: 'human' }), card({ id: 'agent', agentMade: true })];
    board.state.filters.origin = 'ado';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['ado']);
    board.state.filters.origin = 'jira';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['jira']);
    board.state.filters.origin = 'human';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['human']);
    board.state.filters.origin = 'agent';
    assert.deepEqual(board.filteredCards().map(item => item.id), ['agent']);
    const html = board.renderCard(ado);
    assert.match(html, /class="board-card-origin"/);
    assert.match(html, /class="board-brand-logo" src="assets\/img\/azure-devops.svg"/);
    assert.match(html, /Azure DevOps · #42&lt;svg&gt;/);
    assert.doesNotMatch(html, /<svg>/);
    assert.doesNotMatch(board.renderCard(card({ id: 'local' })), /board-card-origin/);
});

test('the toolbar offers an Azure DevOps origin beside Jira', async () => {
    const { readFileSync } = await import('node:fs');
    const html = readFileSync(new URL('../../../VibeRails/wwwroot/index.html', import.meta.url), 'utf8');
    const filter = html.slice(html.indexOf('data-board-filter-origin'), html.indexOf('</select>', html.indexOf('data-board-filter-origin')));
    assert.match(filter, /<option value="jira">Jira<\/option>\s*<option value="ado">Azure DevOps<\/option>/);
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

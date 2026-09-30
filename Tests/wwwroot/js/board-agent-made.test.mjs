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

import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { bindCardOrganization } from '../../../VibeRails/wwwroot/js/modules/board-card-organize.js';

test('merge candidate client requests project scope while link candidates remain global', async () => {
    const calls = [];
    const signal = new AbortController().signal;
    BoardApi.attach({ async apiCall(...args) { calls.push(args); return { cards: [] }; } });
    await BoardApi.getProjectCardCandidatesAsync('tire & wheel', { signal });
    await BoardApi.getCardLinkCandidatesAsync(null, 'tire & wheel', { signal });
    assert.equal(calls[0][0], '/api/v1/board/cards/link-candidates?q=tire%20%26%20wheel&currentProjectOnly=true');
    assert.equal(calls[1][0], '/api/v1/board/cards/link-candidates?q=tire%20%26%20wheel');
    assert.ok(calls.every(call => call[1] === 'GET' && call[3].signal === signal && call[3].preferErrorResponseMessage));
});

test('merge picker uses project-scoped search and keeps already-linked destinations', async () => {
    const elements = Object.fromEntries(['move-board', 'move-lane', 'merge-search', 'merge-target', 'organize-status', 'move-card', 'merge-card']
        .map(name => [name, { value: '', innerHTML: '', addEventListener() {} }]));
    elements['merge-search'].value = 'Tire';
    const host = { querySelector(selector) { return elements[selector.slice(6, -1)]; } };
    const editor = { isConnected: true, querySelector() { return host; } };
    const calls = [];
    BoardApi.attach({ async apiCall(url) {
        calls.push(url);
        if (url === '/api/v1/board/boards') return { boards: [{ id: 'board', name: 'Board', columns: [{ id: 'lane', name: 'Todo' }] }] };
        assert.equal(url, '/api/v1/board/cards/link-candidates?q=Tire&currentProjectOnly=true');
        return { cards: [
            { id: 'source', title: 'Source', displayId: 'B-1', isCurrentProject: true },
            { id: 'linked', title: 'Tire destination', displayId: 'B-2', boardName: 'Board', isCurrentProject: true }
        ] };
    } });
    const dispose = bindCardOrganization(editor, { id: 'source', boardId: 'board', columnId: 'lane', linkedCards: [{ id: 'linked' }] },
        { hasDraft: () => false, onChanged() {} });
    try {
        await new Promise(resolve => setImmediate(resolve));
        assert.equal(calls.length, 2);
        assert.match(elements['merge-target'].innerHTML, /value="linked"/);
        assert.doesNotMatch(elements['merge-target'].innerHTML, /value="source"/);
    } finally { dispose(); }
});

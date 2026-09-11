import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

function harness() {
    const calls = [];
    const toasts = [];
    let closed = false;
    let titleFocused = false;
    const fields = {
        '#board-card-title': { value: 'Edited title', focus() { titleFocused = true; } },
        '[data-board-composer="description"] [data-board-composer-input]': { value: 'Unsaved description' },
        '#board-card-lane': { value: 'build' },
        '#board-card-assignee': { value: 'base:claude' },
        '#board-card-priority': { value: 'high' },
        '#board-card-points': { value: '5' },
        '#board-card-tags': { value: 'bug, auth' },
        '[data-board-blocked]': { checked: true },
        '[data-board-start-work]': { disabled: false }
    };
    const app = {
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            return { tabId: 'tab-1', cardKey: 'VB-1', selection: 'base:claude' };
        },
        showToast(...args) { toasts.push(args); },
        closeModal() { closed = true; }
    };
    const controller = new BoardController(app);
    controller.refresh = async () => {};
    controller.assigneeInfo = () => ({ label: 'Claude' });
    return {
        app, controller, fields, calls, toasts,
        editor: {
            dataset: { cardId: 'card-1' },
            querySelector: selector => fields[selector] ?? null
        },
        card: { id: 'card-1', key: 'VB-1', title: 'Stored title' },
        get closed() { return closed; },
        get titleFocused() { return titleFocused; }
    };
}

test('Save updates from the editor card id after shared editor state was cleared', async () => {
    const h = harness();
    h.editor.dataset.cardId = 'card-1';

    await h.controller.saveCard(h.editor);

    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].url, '/api/v1/board/cards/card-1');
    assert.equal(h.calls[0].method, 'PUT');
    assert.equal(h.closed, true);
});

test('Save creates when the editor has no card id', async () => {
    const h = harness();
    h.editor.dataset.cardId = '';
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return { key: 'VB-2' };
    };

    await h.controller.saveCard(h.editor);

    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].url, '/api/v1/board/cards');
    assert.equal(h.calls[0].method, 'POST');
    assert.deepEqual(h.toasts, [['Board', 'Created VB-2.', 'success']]);
});

test('Save sends an explicit empty assignee when the LLM picker is cleared', async () => {
    const h = harness();
    h.fields['#board-card-assignee'].value = '';

    await h.controller.saveCard(h.editor);

    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].url, '/api/v1/board/cards/card-1');
    assert.equal(h.calls[0].method, 'PUT');
    assert.equal(h.calls[0].body.assignee, '');
    assert.equal(h.closed, true);
});

for (const title of ['', ' \t\n ']) {
    for (const action of ['saveCard', 'startWork']) {
        test(`${action} keeps edits open and focuses a blank title (${JSON.stringify(title)})`, async () => {
            const h = harness();
            h.fields['#board-card-title'].value = title;
            const before = h.controller.readCardForm(h.editor);

            await h.controller[action](h.editor, h.card);

            assert.deepEqual(h.calls, [], 'neither save nor launch may reach the server');
            assert.deepEqual(h.toasts, [['Board', 'A card needs a title.', 'warning']]);
            assert.equal(h.titleFocused, true);
            assert.equal(h.closed, false);
            assert.equal(h.fields['[data-board-start-work]'].disabled, false);
            assert.deepEqual(h.controller.readCardForm(h.editor), before);
        });
    }
}

test('Start work waits for the current edits to save before launching', async () => {
    const h = harness();
    const payload = h.controller.readCardForm(h.editor);
    const apiCall = h.app.apiCall;
    let finishSave;
    const saving = new Promise(resolve => { finishSave = resolve; });
    h.app.apiCall = async (...args) => {
        const result = await apiCall(...args);
        if (args[1] === 'PUT') await saving;
        return result;
    };

    const starting = h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.calls, [{ url: '/api/v1/board/cards/card-1', method: 'PUT', body: payload }]);
    assert.equal(h.closed, false);
    finishSave();
    await starting;

    assert.deepEqual(h.calls[1], {
        url: '/api/v1/board/cards/card-1/launch', method: 'POST', body: { selection: 'base:claude' }
    });
    assert.equal(h.closed, true);
});

test('Start work keeps edits open and skips launch if saving fails', async () => {
    const h = harness();
    const apiCall = h.app.apiCall;
    h.app.apiCall = async (...args) => {
        await apiCall(...args);
        throw new Error('Save failed');
    };

    await h.controller.startWork(h.editor, h.card);

    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'PUT');
    assert.equal(h.closed, false);
    assert.equal(h.fields['[data-board-start-work]'].disabled, false);
    assert.deepEqual(h.toasts, [['Board', 'Save failed', 'error']]);
});

test('Start work is disabled for a running session and returns without saving or launching', async () => {
    const h = harness();
    h.card.sessions = [{ id: 'running-session', active: true }];
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.fields['[data-board-start-work]'].disabled, true);
    await h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.calls, []);
    assert.equal(h.closed, false);

    h.card.sessions[0].active = false;
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.fields['[data-board-start-work]'].disabled, false);
});

test('Start work stops when saving reveals a session started from another window', async () => {
    const h = harness();
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return { ...h.card, activeSessionId: 'running-session' };
    };
    await h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'PUT');
    assert.equal(h.fields['[data-board-start-work]'].disabled, true);
    assert.equal(h.closed, false);
});

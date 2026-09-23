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
        '#board-card-assignee': { value: 'base:claude', focus() {} },
        '#board-card-priority': { value: 'high' },
        '#board-card-points': { value: '5' },
        '#board-card-tags': { value: 'bug, auth' },
        '[data-board-blocked]': { checked: true },
        '[data-board-chat-agent]': { value: 'base:claude' },
        '[data-board-chat]': { disabled: false },
        '[data-board-start-work]': { disabled: false }
    };
    const app = {
        async apiCall(url, method, body) {
            calls.push({ url, method, body });
            return { tabId: 'tab-1', cardKey: 'VB-1', selection: body?.selection || 'base:claude' };
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
        url: '/api/v1/board/cards/card-1/launch', method: 'POST', body: { selection: 'base:claude', intent: 'work' }
    });
    assert.equal(h.closed, true);
});

test('Start work records the full card label and stays on the board', async () => {
    const h = harness();
    let metadata;
    h.app.navigate = () => assert.fail('must not navigate');
    h.app.terminalController = {
        rememberTabLaunch: (tabId, value) => { assert.equal(tabId, 'tab-1'); metadata = value; },
        adoptLaunchedTab: () => assert.fail('must not focus or adopt the launched tab')
    };
    await h.controller.startWork(h.editor, h.card);
    assert.equal(metadata.label, 'VB-1 · Edited title');
    assert.equal(metadata.title, 'VB-1 · Edited title');
});

test('description saves submit current fields without revision bookkeeping', async () => {
    const h = harness();
    await h.controller.saveCard(h.editor);
    assert.equal('expectedDescriptionRevision' in h.calls[0].body, false);
});

for (const adopted of [true, false]) {
    test(`Chat saves the card then focuses its terminal (adopted=${adopted})`, async () => {
        const h = harness();
        const actions = [];
        h.app.terminalController = { rememberTabLaunch() {}, async adoptLaunchedTab(id) { actions.push(['adopt', id]); return adopted; } };
        h.app.navigate = (view, data) => actions.push([view, data]);
        await h.controller.startWork(h.editor, h.card, 'chat');
        assert.equal(h.calls[0].method, 'PUT');
        assert.deepEqual(h.calls[1].body, { selection: 'base:claude', intent: 'chat' });
        assert.equal(h.closed, true);
        assert.deepEqual(actions[0], ['adopt', 'tab-1']);
        assert.equal(actions.length, adopted ? 1 : 2);
        if (!adopted) assert.deepEqual(actions[1], ['terminal-focus', { preferredTabId: 'tab-1', preferredSelection: 'base:claude' }]);
    });
}

for (const [assignee, selection] of [
    ['base:claude', 'base:codex'],
    ['base:claude', 'env:7:codex'],
    ['', 'base:codex']
]) {
    test(`Chat launches ${selection} while preserving assignment ${assignee || '(unassigned)'}`, async () => {
        const h = harness();
        h.fields['#board-card-assignee'].value = assignee;
        h.fields['[data-board-chat-agent]'].value = selection;
        let metadata;
        let navigation;
        h.app.terminalController = { rememberTabLaunch: (_tab, value) => { metadata = value; } };
        h.app.navigate = (_view, value) => { navigation = value; };

        await h.controller.startWork(h.editor, h.card, 'chat');

        assert.equal(h.calls[0].body.assignee, assignee);
        assert.equal('selection' in h.calls[0].body, false);
        assert.deepEqual(h.calls[1].body, { selection, intent: 'chat' });
        assert.equal(metadata.selection, selection);
        assert.equal(navigation.preferredSelection, selection);
        assert.equal(h.fields['#board-card-assignee'].value, assignee);
    });
}

test('Chat requires its own selection before saving, even when the card is assigned', async () => {
    const h = harness();
    let focused = false;
    h.fields['[data-board-chat-agent]'] = { value: '', focus() { focused = true; } };

    await h.controller.startWork(h.editor, h.card, 'chat');

    assert.deepEqual(h.calls, []);
    assert.equal(focused, true);
    assert.deepEqual(h.toasts, [['Board', 'Choose an LLM beside Chat with.', 'warning']]);
});

test('Start work still uses the assignee when another chat target is selected', async () => {
    const h = harness();
    h.fields['[data-board-chat-agent]'].value = 'base:codex';
    await h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.calls[1].body, { selection: 'base:claude', intent: 'work' });

    h.calls.length = 0;
    h.fields['#board-card-assignee'].value = '';
    h.fields['[data-board-start-work]'].disabled = false;
    await h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.calls, []);
    assert.deepEqual(h.toasts.at(-1), ['Board', 'Assign an LLM to this card first.', 'warning']);
});

test('Chat waits for the save and keeps the selection captured at click time', async () => {
    const h = harness();
    const apiCall = h.app.apiCall;
    let finishSave;
    const saving = new Promise(resolve => { finishSave = resolve; });
    h.app.apiCall = async (...args) => {
        const result = await apiCall(...args);
        if (args[1] === 'PUT') await saving;
        return result;
    };
    h.fields['[data-board-chat-agent]'].value = 'base:codex';

    const starting = h.controller.startWork(h.editor, h.card, 'chat');
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'PUT');
    h.fields['[data-board-chat-agent]'].value = 'env:7:codex';
    await h.controller.startWork(h.editor, h.card, 'chat');
    assert.equal(h.calls.length, 1, 'another click cannot start a second launch');
    finishSave();
    await starting;
    assert.deepEqual(h.calls[1].body, { selection: 'base:codex', intent: 'chat' });
});

test('Chat cannot start alongside an in-flight work launch or a running session', async () => {
    const h = harness();
    h.editor._boardStarting = true;
    await h.controller.startWork(h.editor, h.card, 'chat');
    h.editor._boardStarting = false;
    await h.controller.startWork(h.editor, { ...h.card, activeSessionId: 'running' }, 'chat');
    assert.deepEqual(h.calls, []);
});

test('saving a description never sends terminal input to a running agent', async () => {
    const h = harness();
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return { ...h.card, sessions: [{ id: 'live', active: true }] };
    };
    await h.controller.saveCard(h.editor);
    // One PUT and nothing else: there is no notify surface to reach even with a live session.
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'PUT');
});

test('a queued upload that fails after create says the card was saved', async () => {
    const h = harness();
    h.editor.dataset.cardId = '';
    h.editor._boardCard = { pendingAttachments: [{ name: 'notes.zip', dataUrl: 'data:application/zip;base64,AAAA' }] };
    h.controller.renderAttachmentsPanel = () => {};
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        if (url.endsWith('/attachments')) throw new Error('Disk full');
        return { id: 'card_new', key: 'VB-2' };
    };

    await h.controller.saveCard(h.editor);

    assert.equal(h.editor.dataset.cardId, 'card_new', 'retrying Save must not create a second card');
    assert.deepEqual(h.toasts.at(-1), ['Board', 'VB-2 was saved, but a file did not upload. Disk full', 'warning']);
});

test('a partial upload failure retries only unfinished uploads without fetching history', async () => {
    const h = harness();
    h.editor.dataset.cardId = 'card-1';
    h.editor._boardCard = {
        id: 'card-1',
        pendingAttachments: [
            { name: 'a.zip', dataUrl: 'data:application/zip;base64,AAAA' },
            { name: 'b.zip', dataUrl: 'data:application/zip;base64,AAAA' }
        ]
    };
    h.controller.renderAttachmentsPanel = () => {};
    let uploads = 0;
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        if (url.endsWith('/attachments')) {
            uploads += 1;
            if (uploads === 1) return { id: 'att_1', name: 'a.zip' };
            if (uploads === 2) throw new Error('Disk full');
            return { id: 'att_2', name: 'b.zip' };
        }
        return { id: 'card-1', key: 'VB-1' };
    };

    await h.controller.saveCard(h.editor);

    assert.equal(h.calls.filter(call => call.method === 'GET').length, 0);
    assert.equal(h.editor._boardCard.pendingAttachments.length, 1);
    assert.deepEqual(h.toasts.at(-1), ['Board', 'VB-1 was saved, but a file did not upload. Disk full', 'warning']);
    await h.controller.saveCard(h.editor);
    assert.deepEqual(h.calls.filter(call => call.url.endsWith('/attachments')).map(call => call.body.name), ['a.zip', 'b.zip', 'b.zip']);
    assert.equal(h.editor._boardCard.pendingAttachments.length, 0);
    assert.equal(h.closed, true);
    assert.deepEqual(h.toasts.at(-1), ['Board', 'Card saved.', 'success']);
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

test('Go to agent focuses a running session without saving or launching', async () => {
    const h = harness();
    const label = {};
    h.fields['[data-board-start-work]'].querySelector = selector => selector === '[data-board-start-work-label]' ? label : null;
    h.card.sessions = [{ id: 'running-session', tabId: 'live-tab', active: true }];
    let focused;
    h.app.terminalController = { async adoptLaunchedTab(tab) { focused = tab; return true; } };
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.fields['[data-board-start-work]'].disabled, false);
    assert.equal(label.textContent, 'Go to agent');
    await h.controller.startWork(h.editor, h.card);
    assert.deepEqual(h.calls, []);
    assert.equal(focused, 'live-tab');
    assert.equal(h.closed, true);

    h.card.sessions[0].active = false;
    h.controller.updateStartWorkButton(h.editor, h.card);
    assert.equal(h.fields['[data-board-start-work]'].disabled, false);
    assert.equal(label.textContent, 'Start work');
});

test('Start work opens a session that started in another window while saving', async () => {
    const h = harness();
    let navigation;
    h.app.terminalController = {};
    h.app.navigate = (view, data) => { navigation = { view, data }; };
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return { ...h.card, activeSessionId: 'running-session', activeTabId: 'live-tab' };
    };
    await h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'PUT');
    assert.equal(h.fields['[data-board-start-work]'].disabled, false);
    assert.equal(h.closed, true);
    assert.equal(navigation.view, 'terminal-focus');
    assert.equal(navigation.data.preferredTabId, 'live-tab');
});

test('Go to agent refreshes an incomplete active session and opens its tab', async () => {
    const h = harness();
    h.card.activeSessionId = 'running-session';
    let focused;
    h.app.terminalController = { async adoptLaunchedTab(tab) { focused = tab; return true; } };
    h.app.apiCall = async (url, method, body) => {
        h.calls.push({ url, method, body });
        return { ...h.card, sessions: [{ id: 'running-session', tabId: 'live-tab', active: true }] };
    };
    await h.controller.startWork(h.editor, h.card);
    assert.equal(h.calls.length, 1);
    assert.equal(h.calls[0].method, 'GET');
    assert.equal(focused, 'live-tab');
});

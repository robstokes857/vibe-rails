import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import { cardIdentityHtml, ghostSettingHtml, ghostSummary, readGhostField } from '../../../VibeRails/wwwroot/js/modules/board-ghost.js';

function card(overrides) {
    return { id: 'card', key: 'VB-1', displayId: 'VIBE-1', title: 'Work', description: '', type: 'task',
        priority: 'medium', columnId: 'done', tags: [], agentMade: false, ...overrides };
}

function controller() {
    const instance = new BoardController({ currentView: 'board', apiCall() { return Promise.resolve({}); } });
    instance.state.columns = [{ id: 'backlog', name: 'Backlog' }, { id: 'done', name: 'Done' }];
    return instance;
}

test('saved cards keep their identity and maker but hide recheck without a ghost', () => {
    assert.equal(cardIdentityHtml(null), '');
    const html = cardIdentityHtml(card({ agentMade: true, agentMadeBy: 'Codex', key: 'VB-NW72V-297', displayId: 'VIBE-225' }));
    assert.match(html, /id="board-card-display-id"[^>]*value="VIBE-225"/);
    assert.match(html, /Permanent ID: <code>VB-NW72V-297<\/code>/);
    assert.match(html, /data-board-recheck-ghost hidden/);
    assert.match(html, /Made by <strong>Codex<\/strong>/);
    const human = cardIdentityHtml(card());
    assert.match(human, /data-board-recheck-ghost hidden/);
    assert.doesNotMatch(human, /data-board-agent-provenance/);
});

test('recheck is visible only for ghost cards and uses a single refresh icon', () => {
    for (const ghostReason of [undefined, null, '']) {
        assert.match(cardIdentityHtml(card({ ghostReason })), /data-board-recheck-ghost hidden/);
    }
    for (const ghostReason of ['no-code', 'not-landed']) {
        const html = cardIdentityHtml(card({ ghostReason }));
        assert.match(html, /data-board-recheck-ghost title=/);
        assert.match(html, /fa-rotate-right/);
        assert.doesNotMatch(html, /fa-ban|board-ghost-buster/);
    }
});

test('the ghost filter matches both reasons and excludes unmarked cards', () => {
    const board = controller();
    board.state.cards = [card({ id: 'missing', ghostReason: 'no-code' }), card({ id: 'branch', ghostReason: 'not-landed' }), card({ id: 'clear' })];
    board.state.filters.ghost = 'true';
    assert.equal(board.hasActiveFilters(), true);
    assert.deepEqual(board.filteredCards().map(card => card.id), ['missing', 'branch']);
    board.state.filters.ghost = 'false';
    assert.deepEqual(board.filteredCards().map(card => card.id), ['clear']);
});

test('a ghost tile keeps a permanent marker outside the animated key and robot mark', () => {
    const html = controller().renderCard(card({ agentMade: true, ghostReason: 'no-code' }));
    assert.match(html, /<span class="board-key has-ghost" title="Ghost: an agent worked on this card, but no commit or GitHub link was linked">/);
    assert.match(html, /<i class="fa-solid fa-ghost board-ghost-mark" aria-hidden="true"><\/i><span class="board-ghost-stage"><span class="board-key-label"><i class="fa-solid fa-robot board-agent-mark"[^>]*><\/i> VIBE-1<\/span>/);
    assert.match(html, /<span class="board-ghost-runner" aria-hidden="true"><i class="fa-solid fa-ghost"><\/i><\/span>/);
    assert.match(html, /aria-label="VIBE-1: Work — Made by an agent — Ghost: an agent worked on this card/);
});

test('the not-landed ghost names the default branch', () => {
    assert.match(ghostSummary(card({ ghostReason: 'not-landed' })), /linked code has not been verified on the default branch/);
    assert.match(controller().renderCard(card({ ghostReason: 'not-landed' })), /board-ghost-runner/);
});

test('a card without a ghost keeps the plain key markup', () => {
    for (const ghostReason of [undefined, null, '']) {
        const html = controller().renderCard(card({ ghostReason }));
        assert.match(html, /<span class="board-key">VIBE-1<\/span>/);
        assert.doesNotMatch(html, /has-ghost|board-ghost-runner|board-key-label|Ghost:/);
    }
});

test('an unknown ghost reason renders the fixed generic text, never the raw value', () => {
    const html = controller().renderCard(card({ ghostReason: '<img src=x onerror=alert(1)>' }));
    assert.doesNotMatch(html, /<img/);
    assert.match(html, /no commit or GitHub link was linked/);
});

test('Advanced offers a checked Ghost box only on a ghost card', () => {
    assert.equal(ghostSettingHtml(card()), '');
    const html = ghostSettingHtml(card({ ghostReason: 'no-code' }));
    assert.match(html, /<input class="form-check-input" type="checkbox" id="board-card-ghost" data-board-ghost checked>/);
    assert.match(html, /Uncheck and save to clear it\./);
});

test('the save payload carries clearGhost only while the checkbox is shown', () => {
    assert.deepEqual(readGhostField({ querySelector: () => null }), {});
    assert.deepEqual(readGhostField({ querySelector: () => ({ checked: true }) }), { clearGhost: false });
    assert.deepEqual(readGhostField({ querySelector: () => ({ checked: false }) }), { clearGhost: true });
});

test('unchecking the ghost and saving sends clearGhost and nothing else that did not change', async () => {
    const calls = [];
    const ghost = { checked: true };
    const fields = {
        '#board-card-title': { value: 'Work' },
        '[data-board-composer="description"] [data-board-composer-input]': { value: '' },
        '#board-card-lane': { value: 'done' },
        '#board-card-assignee': { value: '' },
        '#board-card-display-id': { value: 'VIBE-1' },
        '[data-board-ghost]': ghost
    };
    const app = {
        async apiCall(url, method, body) { calls.push({ url, method, body }); return { id: 'card-1', key: 'VB-1' }; },
        showToast() {},
        closeModal() {}
    };
    const board = new BoardController(app);
    board.refresh = async () => {};
    const editor = { dataset: { cardId: 'card-1' }, querySelector: selector => fields[selector] ?? null, _boardCard: card({ id: 'card-1' }) };
    editor._boardFormBaseline = board.readCardForm(editor);
    assert.equal(editor._boardFormBaseline.clearGhost, false);

    ghost.checked = false;
    assert.equal(board.cardHasDraft(editor), true);
    await board.saveCard(editor);

    assert.deepEqual(calls, [{ url: '/api/v1/board/cards/card-1', method: 'PUT', body: { clearGhost: true } }]);
});

test('the ghost animation lives in the board template and stops for reduced motion', () => {
    const html = readFileSync(path.resolve('VibeRails/wwwroot/index.html'), 'utf8');
    const start = html.indexOf('id="board-template"');
    const css = html.slice(start, html.indexOf('</template>', start));
    for (const name of ['board-ghost-key', 'board-ghost-run', 'board-ghost-bob'])
        assert.match(css, new RegExp(`@keyframes ${name} \\{`));
    // The card already uses .is-ghost for the drag placeholder; the run must not reuse it.
    assert.doesNotMatch(css, /\.board-key\.is-ghost|has-ghost\.is-ghost/);
    const reduced = css.slice(css.indexOf('Without motion the key and permanent ghost stay put'));
    assert.match(reduced, /@media \(prefers-reduced-motion: reduce\)[\s\S]*\.board-ghost-runner \{[\s\S]*display: none;[\s\S]*animation: none;/);
});

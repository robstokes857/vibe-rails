import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';
import {
    agentMadeSummary, agentProvenanceHtml, bindAgentProvenance, formatMadeAt
} from '../../../VibeRails/wwwroot/js/modules/board-agent-provenance.js';

const SESSION = '0f8fad5b-d9cb-469f-a165-70867728950e';
const MADE_AT = '2026-10-05T04:38:51Z';

function card(overrides) {
    return { id: 'card', key: 'VB-1', displayId: 'VIBE-1', title: 'Work', description: '', type: 'task',
        priority: 'medium', columnId: 'backlog', tags: [], agentMade: true, createdAt: MADE_AT, ...overrides };
}

test('the editor names the agent, the session and when the card was made', () => {
    const html = agentProvenanceHtml(card({ agentMadeBy: 'Claude Opus 5.5', agentMadeSessionId: SESSION }));

    assert.match(html, /Made by <strong>Claude Opus 5\.5<\/strong>/);
    assert.match(html, new RegExp(`<time datetime="${MADE_AT}">`));
    assert.ok(formatMadeAt(MADE_AT).includes('2026'), 'the date keeps its year');
    assert.ok(html.includes(formatMadeAt(MADE_AT)));
    assert.match(html, new RegExp(`data-board-agent-session="${SESSION}"`));
    assert.match(html, new RegExp(`data-board-agent-session-at="${MADE_AT}"`));
    assert.match(html, /Session 0f8fad5b</);
    assert.match(html, new RegExp(`title="Replay session ${SESSION}`));
});

test('agent text is escaped wherever it appears', () => {
    const hostile = card({ agentMadeBy: '<img src=x onerror=alert(1)>"', agentMadeSessionId: '"><b>x' });
    const html = agentProvenanceHtml(hostile);
    assert.doesNotMatch(html, /<img/);
    assert.doesNotMatch(html, /<b>x/);
    assert.match(html, /&lt;img src=x onerror=alert\(1\)&gt;&quot;/);

    const tile = controller().renderCard(hostile);
    assert.doesNotMatch(tile, /<img/);
    assert.match(tile, /title="Made by &lt;img/);
});

test('a card an agent made before names were recorded still reads as made by an agent', () => {
    const html = agentProvenanceHtml(card({}));
    assert.match(html, /Made by <strong>an agent<\/strong>/);
    assert.doesNotMatch(html, /data-board-agent-session/);
    assert.match(html, /<time /);
});

test('a human card has no provenance block', () => {
    assert.equal(agentProvenanceHtml(card({ agentMade: false, agentMadeBy: 'Claude' })), '');
    assert.equal(agentProvenanceHtml(null), '');
});

test('the lane tile says who made it and when in the robot tooltip', () => {
    const tile = controller().renderCard(card({ agentMadeBy: 'Codex GPT-5' }));
    const summary = agentMadeSummary(card({ agentMadeBy: 'Codex GPT-5' }));
    assert.equal(summary, `Made by Codex GPT-5 · ${formatMadeAt(MADE_AT)}`);
    assert.ok(tile.includes(`board-agent-mark" title="${summary}"`));
    assert.ok(tile.includes(`— ${summary}`), 'the tile names the maker to assistive technology');
});

test('a missing or broken creation time leaves only the name', () => {
    assert.equal(agentMadeSummary(card({ agentMadeBy: 'Claude', createdAt: 'not a date' })), 'Made by Claude');
    assert.doesNotMatch(agentProvenanceHtml(card({ createdAt: undefined })), /<time/);
});

test('the session button replays that session at the moment the card was made', () => {
    let handler = null;
    let stopped = false;
    const button = {
        dataset: { boardAgentSession: SESSION, boardAgentSessionAt: MADE_AT },
        addEventListener(type, listener) { assert.equal(type, 'click'); handler = listener; }
    };
    const calls = [];
    bindAgentProvenance({ querySelectorAll: () => [button] }, (id, at) => calls.push([id, at]));
    handler({ stopPropagation() { stopped = true; } });
    assert.deepEqual(calls, [[SESSION, MADE_AT]]);
    assert.ok(stopped);
    bindAgentProvenance(null, () => assert.fail('nothing to bind'));
});

function controller() {
    const instance = new BoardController({ currentView: 'board', apiCall() { return Promise.resolve({}); } });
    instance.state.columns = [{ id: 'backlog', name: 'Backlog' }];
    return instance;
}

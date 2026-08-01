import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

class FakeClassList {
    constructor(element) {
        this.element = element;
    }

    add(...names) {
        const namesSet = this.#names();
        for (const name of names) {
            namesSet.add(name);
            this.element.classMutations.push({ operation: 'add', name });
        }
        this.#write(namesSet);
    }

    remove(...names) {
        const namesSet = this.#names();
        for (const name of names) namesSet.delete(name);
        this.#write(namesSet);
    }

    contains(name) {
        return this.#names().has(name);
    }

    toggle(name, force) {
        const namesSet = this.#names();
        const shouldAdd = force === undefined ? !namesSet.has(name) : Boolean(force);
        if (shouldAdd) namesSet.add(name);
        else namesSet.delete(name);
        this.#write(namesSet);
        return shouldAdd;
    }

    #names() {
        return new Set(this.element.className.split(/\s+/).filter(Boolean));
    }

    #write(names) {
        this.element.className = [...names].join(' ');
    }
}

class FakeElement {
    constructor(tagName) {
        this.tagName = tagName.toUpperCase();
        this.children = [];
        this.parentElement = null;
        this.style = {};
        this.className = '';
        this.classList = new FakeClassList(this);
        this.attributes = new Map();
        this.listeners = new Map();
        this.animations = [];
        this.classMutations = [];
        this.textContent = '';
    }

    appendChild(child) {
        if (child.parentElement) {
            const oldIndex = child.parentElement.children.indexOf(child);
            if (oldIndex >= 0) child.parentElement.children.splice(oldIndex, 1);
        }
        child.parentElement = this;
        this.children.push(child);
        return child;
    }

    replaceChildren(...children) {
        this.children = [];
        for (const child of children) this.appendChild(child);
    }

    setAttribute(name, value) {
        this.attributes.set(name, String(value));
    }

    getAttribute(name) {
        return this.attributes.get(name) ?? null;
    }

    addEventListener(type, listener) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push(listener);
        this.listeners.set(type, listeners);
    }

    dispatchEvent(event) {
        event.target ??= this;
        for (const listener of this.listeners.get(event.type) ?? []) listener.call(this, event);
    }

    animate(keyframes, options) {
        const animation = { keyframes, options };
        this.animations.push(animation);
        return animation;
    }

    querySelector(selector) {
        const matches = selector.startsWith('.')
            ? (element) => element.classList.contains(selector.slice(1))
            : (element) => element.tagName === selector.toUpperCase();

        for (const child of this.children) {
            if (matches(child)) return child;
            const nested = child.querySelector(selector);
            if (nested) return nested;
        }
        return null;
    }
}

const body = new FakeElement('body');
globalThis.document = {
    body,
    createElement: (tagName) => new FakeElement(tagName)
};

const sourceModule = path.resolve('VibeRails/wwwroot/js/modules/terminal-token-compression.js');
const {
    TerminalTokenCompressionMeter,
    getTokenSaverEnabledSources,
    isTokenSaverEnabled
} = await import(pathToFileURL(sourceModule).href);

function renderedText(element) {
    return [element.textContent, ...element.children.map(renderedText)].join(' ');
}

test('Token saver enabled state requires a proxy and saver toggle for the same provider', () => {
    assert.equal(isTokenSaverEnabled({
        claudeLlmProxyEnabled: true,
        claudeTokenSaverEnabled: false,
        codexLlmProxyEnabled: false,
        codexTokenSaverEnabled: true,
        openCodeLlmProxyEnabled: false,
        openCodeTokenSaverEnabled: true
    }), false);
    assert.equal(isTokenSaverEnabled({
        openCodeLlmProxyEnabled: true,
        openCodeTokenSaverEnabled: true
    }), true, 'OpenCode participates in the same state calculation');
    assert.equal(isTokenSaverEnabled({
        codexLlmProxyEnabled: true
    }), true, 'missing legacy saver settings retain the server default-on behavior');
    assert.deepEqual(getTokenSaverEnabledSources({
        claudeLlmProxyEnabled: true,
        claudeTokenSaverEnabled: false,
        codexLlmProxyEnabled: true,
        codexTokenSaverEnabled: true,
        openCodeLlmProxyEnabled: true,
        openCodeTokenSaverEnabled: true
    }), ['Codex proxy', 'OpenCode proxy']);
});

test('Mixed-provider settings pulse only traffic for the provider whose saver is enabled', () => {
    const mount = new FakeElement('div');
    const meter = new TerminalTokenCompressionMeter({
        mount,
        enabledSources: ['Codex proxy']
    });
    const host = mount.querySelector('.vb-activity-blinker');

    assert.equal(host.classList.contains('is-enabled'), true, 'aggregate styling stays enabled');

    meter.report({ source: 'Claude proxy', label: 'POST', status: 200 });
    assert.equal(host.classList.contains('is-active'), false, 'Claude saver is disabled');
    assert.equal(meter.sources.get('Claude proxy').count, 1, 'proxy diagnostics are still retained');

    meter.report({ source: 'Codex proxy', label: 'POST', status: 200 });
    assert.equal(host.classList.contains('is-active'), true, 'Codex saver is enabled');
    assert.equal(
        host.classMutations.filter(mutation =>
            mutation.operation === 'add' && mutation.name === 'is-active'
        ).length,
        1
    );
});

test('Proxy activity does not enable or pulse a disabled token saver meter', () => {
    const mount = new FakeElement('div');
    const meter = new TerminalTokenCompressionMeter({ mount, enabled: false });
    const host = mount.querySelector('.vb-activity-blinker');

    meter.report({ source: 'Claude proxy', label: 'POST', status: 200 });

    assert.equal(meter.totalCount, 1, 'request history remains available for diagnostics');
    assert.equal(host.classList.contains('is-enabled'), false);
    assert.equal(host.classList.contains('is-active'), false);
    assert.match(host.querySelector('.vb-activity-blinker-trigger').getAttribute('aria-label'), /disabled/i);
});

test('TerminalTokenCompressionMeter stays visibly mounted while idle and exposes enabled state', () => {
    const mount = new FakeElement('div');
    const blinker = new TerminalTokenCompressionMeter({ mount, title: 'Proxy activity' });
    const host = mount.querySelector('.vb-activity-blinker');

    assert.ok(host, 'the proxy activity indicator should mount immediately');
    assert.notEqual(host.style.display, 'none', 'idle state must remain visible');
    assert.ok(host.querySelector('.vb-activity-blinker-trigger'));
    assert.ok(host.querySelector('.vb-activity-blinker-popover'));
    assert.ok(host.querySelector('.fa-piggy-bank'));
    assert.match(renderedText(host.querySelector('.vb-activity-blinker-trigger')), /Token saver/);
    assert.match(renderedText(host.querySelector('.vb-activity-blinker-metric')), /— tokens saved/);
    assert.equal(
        host.querySelector('.vb-activity-blinker-trigger').getAttribute('aria-controls'),
        host.querySelector('.vb-activity-blinker-popover').getAttribute('id')
    );
    assert.equal(host.classList.contains('is-active'), false);

    blinker.setEnabled(false);
    assert.equal(host.classList.contains('is-enabled'), false);
    assert.notEqual(host.style.display, 'none', 'disabled state must remain visible');

    blinker.setEnabled(true);
    assert.equal(host.classList.contains('is-enabled'), true);
    assert.equal(host.classList.contains('is-active'), false);
});

test('TerminalTokenCompressionMeter pulses and groups Claude and Codex proxy reports', () => {
    const mount = new FakeElement('div');
    const blinker = new TerminalTokenCompressionMeter({ mount, title: 'Proxy activity' });
    const host = mount.querySelector('.vb-activity-blinker');
    blinker.setEnabled(true);

    blinker.report({
        source: 'Claude proxy',
        label: 'POST',
        target: 'https://api.anthropic.com/v1/messages',
        status: '200'
    });
    blinker.report({
        source: 'Claude proxy',
        label: 'POST',
        target: 'https://api.anthropic.com/v1/messages',
        status: '200'
    });
    blinker.report({
        source: 'Codex proxy',
        label: 'POST',
        target: 'https://api.openai.com/v1/responses',
        status: '200'
    });

    assert.equal(blinker.totalCount, 3);
    assert.equal(blinker.sources.get('Claude proxy').count, 2);
    assert.equal(blinker.sources.get('Codex proxy').count, 1);
    assert.equal(host.classList.contains('is-active'), true);
    const trigger = host.querySelector('.vb-activity-blinker-trigger');
    assert.match(trigger.getAttribute('aria-label'), /proxy traffic detected/i);
    assert.equal(
        host.classMutations.filter(mutation =>
            mutation.operation === 'add' && mutation.name === 'is-active'
        ).length,
        3,
        'each proxy report should restart the active-state pulse'
    );

    host.dispatchEvent({ type: 'mouseenter' });
    const popover = host.querySelector('.vb-activity-blinker-popover');
    const text = renderedText(popover);
    assert.match(text, /3 requests/);
    assert.match(text, /Tokens saved/);
    assert.match(text, /Savings measurement not connected yet/);
    assert.match(text, /Claude proxy/);
    assert.match(text, /2 ·/);
    assert.match(text, /Codex proxy/);
    assert.match(text, /1 ·/);
});

test('TerminalTokenCompressionMeter shows an honest savings placeholder and a session/month/all-time breakdown', () => {
    const mount = new FakeElement('div');
    const blinker = new TerminalTokenCompressionMeter({ mount, title: 'Token compression', enabled: true });
    const host = mount.querySelector('.vb-activity-blinker');
    const trigger = host.querySelector('.vb-activity-blinker-trigger');
    const metric = host.querySelector('.vb-activity-blinker-metric');

    assert.match(renderedText(metric), /— tokens saved/);
    assert.equal(metric.classList.contains('is-placeholder'), true);
    assert.match(trigger.getAttribute('aria-label'), /token savings not yet measured/i);

    blinker.setTokensSaved({ session: 12400, month: 90200, allTime: 1500000 });
    // The compact trigger shows this app run's savings — the window that shows the saver working
    // now; the popover carries the full session/month/all-time breakdown.
    assert.match(renderedText(metric), /12\.4K tokens saved/);
    assert.equal(metric.classList.contains('is-placeholder'), false);
    assert.match(trigger.getAttribute('aria-label'), /12,400 this session/i);
    assert.match(trigger.getAttribute('aria-label'), /90,200 this month/i);
    assert.match(trigger.getAttribute('aria-label'), /1,500,000 all time/i);

    host.dispatchEvent({ type: 'mouseenter' });
    const popoverText = renderedText(host.querySelector('.vb-activity-blinker-popover'));
    assert.match(popoverText, /This session/);
    assert.match(popoverText, /12\.4K/);
    assert.match(popoverText, /This month/);
    assert.match(popoverText, /90\.2K/);
    assert.match(popoverText, /All time/);
    assert.match(popoverText, /1\.5M/);
    assert.match(popoverText, /Across proxied traffic/);

    // A bare number (legacy caller) is treated as all time and still fills the trigger.
    blinker.setTokensSaved(800);
    assert.match(renderedText(metric), /800 tokens saved/);
    host.dispatchEvent({ type: 'mouseenter' });
    assert.match(
        renderedText(host.querySelector('.vb-activity-blinker-popover')),
        /This session\s+—/
    );

    blinker.setTokensSaved(null);
    assert.match(renderedText(metric), /— tokens saved/);
    assert.equal(metric.classList.contains('is-placeholder'), true);
});

test('TerminalTokenCompressionMeter owns app event wiring and the initial savings seed', async () => {
    const mount = new FakeElement('div');
    const calls = [];
    // Keyed by event name: the meter owns more than one subscription (proxy traffic and the
    // agent-driven pause), and each must land on its own handler.
    const handlers = new Map();
    const meter = new TerminalTokenCompressionMeter({ mount }).connect({
        appEventClient: {
            on: (eventName, handler) => handlers.set(eventName, handler)
        },
        apiCall: async (...args) => {
            calls.push(args);
            return { tokensSavedSession: 900, tokensSavedMonth: 2400, tokensSaved: 8100 };
        }
    });
    const proxyActivityHandler = handlers.get('proxy_activity');

    assert.deepEqual([...handlers.keys()], ['proxy_activity', 'token_saver_pause']);
    await meter.initialSavingsReady;
    assert.deepEqual(calls, [[
        '/api/v1/token-savings',
        'GET',
        null,
        { showLoading: false }
    ]]);
    // The trigger shows the session tally (tokensSavedSession), not the all-time one.
    assert.match(renderedText(mount.querySelector('.vb-activity-blinker-metric')), /900 tokens saved/);

    proxyActivityHandler({
        source: 'Codex proxy',
        label: 'POST',
        status: 200,
        tokensSavedSession: 1100,
        tokensSavedMonth: 2600,
        tokensSavedTotal: 8400
    });
    assert.equal(meter.totalCount, 1);
    assert.match(renderedText(mount.querySelector('.vb-activity-blinker-metric')), /1\.1K tokens saved/);
});

test('TerminalTokenCompressionMeter does not let a delayed startup seed overwrite a live tally', async () => {
    const mount = new FakeElement('div');
    const handlers = new Map();
    let resolveInitialSavings;
    const initialSavings = new Promise(resolve => {
        resolveInitialSavings = resolve;
    });
    const meter = new TerminalTokenCompressionMeter({ mount }).connect({
        appEventClient: {
            on: (eventName, handler) => handlers.set(eventName, handler)
        },
        apiCall: async () => initialSavings
    });

    handlers.get('proxy_activity')({
        source: 'Claude proxy',
        label: 'POST',
        status: 200,
        tokensSavedSession: 858,
        tokensSavedMonth: 858,
        tokensSavedTotal: 858
    });
    assert.match(renderedText(mount.querySelector('.vb-activity-blinker-metric')), /858 tokens saved/);

    // Reproduce the startup race: the request began before the event, then its stale zero
    // response completed afterward. The live tally must remain authoritative.
    resolveInitialSavings({
        tokensSavedSession: 0,
        tokensSavedMonth: 0,
        tokensSaved: 0
    });
    await meter.initialSavingsReady;

    assert.match(renderedText(mount.querySelector('.vb-activity-blinker-metric')), /858 tokens saved/);
    assert.deepEqual(meter._savings, { session: 858, month: 858, allTime: 858 });
});

test('TerminalTokenCompressionMeter constructs detached and relocate() re-homes it without losing state', () => {
    // No mount → the host starts detached so app.js / TerminalManager.initialize() can drop it into
    // the terminal controls bar once that bar renders.
    const blinker = new TerminalTokenCompressionMeter({ title: 'Proxy activity' });
    assert.equal(blinker._host.parentElement, null, 'a mountless blinker stays detached until relocated');

    blinker.report({
        source: 'Codex proxy',
        label: 'POST',
        target: 'https://api.openai.com/v1/responses',
        status: '200'
    });
    assert.equal(blinker.totalCount, 1, 'report() accrues state while detached');

    const controlsBar = new FakeElement('div');
    blinker.relocate(controlsBar);
    assert.equal(blinker._host.parentElement, controlsBar, 'relocate mounts the same host into the target');
    assert.equal(blinker.totalCount, 1, 're-parenting preserves accumulated history');

    // Navigating away and back rebuilds the controls bar; the singleton re-homes into the new one.
    const rerenderedBar = new FakeElement('div');
    blinker.relocate(rerenderedBar);
    assert.equal(blinker._host.parentElement, rerenderedBar);
    assert.equal(controlsBar.querySelector('.vb-activity-blinker'), null, 'the old slot no longer owns the host');
    assert.equal(blinker.sources.get('Codex proxy').count, 1, 'per-source history survives the move');
});

test('TerminalTokenCompressionMeter.relocate() is a safe no-op for missing or unchanged targets', () => {
    const mount = new FakeElement('div');
    const blinker = new TerminalTokenCompressionMeter({ mount, title: 'Proxy activity' });

    // A possibly-null slot (e.g. no terminal view mounted yet) must not throw or detach the host.
    blinker.relocate(null);
    blinker.relocate(undefined);
    blinker.relocate({});
    assert.equal(blinker._host.parentElement, mount, 'a missing target leaves the host where it is');

    blinker.relocate(mount);
    assert.equal(blinker._host.parentElement, mount, 'relocating to the current parent is a no-op');
});

// --- Agent-driven pause (MCP token-saver tools) -----------------------------------------------
//
// The pause is per-tab but the meter is app-wide, so these pin the two things that follow from
// that: one terminal's pause must not claim the others are paused, and the badge must clear itself
// from the absolute expiry rather than from a "it ended" message that may never arrive.

function pausableMeter(startMs = Date.parse('2026-07-31T12:00:00Z')) {
    const clock = { now: startMs };
    const mount = new FakeElement('div');
    const meter = new TerminalTokenCompressionMeter({
        mount,
        enabledSources: ['Claude proxy'],
        now: () => clock.now
    });
    return { meter, clock, host: mount.querySelector('.vb-activity-blinker') };
}

const inFiveMinutes = (clock) => new Date(clock.now + 5 * 60_000).toISOString();

test('An agent pause shows a countdown badge on the meter', () => {
    const { meter, clock, host } = pausableMeter();
    const badge = host.querySelector('.vb-activity-blinker-paused');

    assert.equal(host.classList.contains('is-paused'), false);
    assert.equal(badge.textContent, '', 'the badge carries no text until something is paused');

    meter.setPaused('tab-1', inFiveMinutes(clock));

    assert.equal(host.classList.contains('is-paused'), true);
    assert.equal(badge.textContent, 'Paused 5:00');
    assert.match(
        host.querySelector('.vb-activity-blinker-trigger').getAttribute('aria-label'),
        /paused by an agent for 1 terminal/i
    );
});

test('The paused badge counts down and clears itself when the window lapses', () => {
    const { meter, clock, host } = pausableMeter();
    const badge = host.querySelector('.vb-activity-blinker-paused');
    meter.setPaused('tab-1', inFiveMinutes(clock));

    clock.now += 4 * 60_000 + 31_000;
    meter.refreshPausedState();
    assert.equal(badge.textContent, 'Paused 0:29');

    // Expiry is derived from the absolute instant the server sent, so a late or throttled tick
    // still clears at the right state rather than leaving a stale "Paused" badge up forever.
    clock.now += 60_000;
    meter.refreshPausedState();
    assert.equal(host.classList.contains('is-paused'), false);
    assert.equal(badge.textContent, '');
    assert.equal(meter.pausedTabCount, 0);
});

test('Pauses are tracked per tab and the badge reflects the longest remaining window', () => {
    const { meter, clock, host } = pausableMeter();
    const badge = host.querySelector('.vb-activity-blinker-paused');

    meter.setPaused('tab-1', new Date(clock.now + 60_000).toISOString());
    meter.setPaused('tab-2', new Date(clock.now + 300_000).toISOString());
    assert.equal(meter.pausedTabCount, 2);
    assert.equal(badge.textContent, 'Paused 5:00');

    // tab-1 lapses; tab-2 is still compressing-free, so the meter must stay paused.
    clock.now += 61_000;
    meter.refreshPausedState();
    assert.equal(meter.pausedTabCount, 1);
    assert.equal(host.classList.contains('is-paused'), true);

    meter.setPaused('tab-2', null);
    assert.equal(meter.pausedTabCount, 0);
    assert.equal(host.classList.contains('is-paused'), false);
});

test('A pause on a saver that is switched off never claims output is about to change', () => {
    const { meter, clock, host } = pausableMeter();
    const events = new Map();
    meter.connect({
        appEventClient: { on: (type, handler) => events.set(type, handler) },
        apiCall: async () => ({})
    });

    events.get('token_saver_pause')({
        tabId: 'tab-1',
        pausedUntilUtc: inFiveMinutes(clock),
        saverEnabled: false
    });

    assert.equal(host.classList.contains('is-paused'), false, 'nothing was being compressed to pause');

    events.get('token_saver_pause')({
        tabId: 'tab-1',
        pausedUntilUtc: inFiveMinutes(clock),
        saverEnabled: true
    });
    assert.equal(host.classList.contains('is-paused'), true);

    // A resume arrives as a null expiry rather than a separate event type.
    events.get('token_saver_pause')({ tabId: 'tab-1', pausedUntilUtc: null, saverEnabled: true });
    assert.equal(host.classList.contains('is-paused'), false);
});

test('An already-expired or unparseable expiry is ignored rather than pinning the badge on', () => {
    const { meter, clock, host } = pausableMeter();

    meter.setPaused('tab-1', new Date(clock.now - 1000).toISOString());
    assert.equal(host.classList.contains('is-paused'), false, 'a stale event must not paint a badge');

    meter.setPaused('tab-2', 'not-a-timestamp');
    assert.equal(host.classList.contains('is-paused'), false);
    assert.equal(meter.pausedTabCount, 0);
});

test('The popover explains a pause instead of leaving the savings tally looking broken', () => {
    const { meter, clock, host } = pausableMeter();
    meter.setPaused('tab-1', inFiveMinutes(clock));
    meter.report({ source: 'Claude proxy', label: 'POST', status: 200 });

    host.querySelector('.vb-activity-blinker-trigger').dispatchEvent({ type: 'click' });

    const popoverText = renderedText(host.querySelector('.vb-activity-blinker-popover'));
    assert.match(popoverText, /Paused for 1 terminal/);
    assert.match(popoverText, /resumes in 5:00/i);
    assert.match(popoverText, /Tokens saved/, 'the tally is still true while paused and stays visible');
});

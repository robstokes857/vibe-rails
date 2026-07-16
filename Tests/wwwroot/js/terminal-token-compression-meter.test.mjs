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
const { TerminalTokenCompressionMeter } = await import(pathToFileURL(sourceModule).href);

function renderedText(element) {
    return [element.textContent, ...element.children.map(renderedText)].join(' ');
}

test('TerminalTokenCompressionMeter stays visibly mounted while idle and exposes enabled state', () => {
    const mount = new FakeElement('div');
    const blinker = new TerminalTokenCompressionMeter({ mount, title: 'Proxy activity' });
    const host = mount.querySelector('.vb-activity-blinker');

    assert.ok(host, 'the proxy activity indicator should mount immediately');
    assert.notEqual(host.style.display, 'none', 'idle state must remain visible');
    assert.ok(host.querySelector('.vb-activity-blinker-trigger'));
    assert.ok(host.querySelector('.vb-activity-blinker-popover'));
    assert.ok(host.querySelector('.vb-activity-blinker-zip-outline'));
    assert.ok(host.querySelector('.vb-activity-blinker-zip-solid'));
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
    // The compact trigger shows this session's tally; the popover carries the full breakdown.
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
    let proxyActivityHandler;
    const meter = new TerminalTokenCompressionMeter({ mount }).connect({
        appEventClient: {
            on: (eventName, handler) => {
                assert.equal(eventName, 'proxy_activity');
                proxyActivityHandler = handler;
            }
        },
        apiCall: async (...args) => {
            calls.push(args);
            return { tokensSavedSession: 900, tokensSavedMonth: 2400, tokensSaved: 8100 };
        }
    });

    await meter.initialSavingsReady;
    assert.deepEqual(calls, [[
        '/api/v1/token-savings',
        'GET',
        null,
        { showLoading: false }
    ]]);
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

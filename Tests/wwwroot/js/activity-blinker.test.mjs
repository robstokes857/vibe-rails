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

const sourceModule = path.resolve('VibeRails/wwwroot/js/modules/activity-blinker.js');
const { ActivityBlinker } = await import(pathToFileURL(sourceModule).href);

function renderedText(element) {
    return [element.textContent, ...element.children.map(renderedText)].join(' ');
}

test('ActivityBlinker stays visibly mounted while idle and exposes enabled state', () => {
    const mount = new FakeElement('div');
    const blinker = new ActivityBlinker({ mount, title: 'Proxy activity' });
    const host = mount.querySelector('.vb-activity-blinker');

    assert.ok(host, 'the proxy activity indicator should mount immediately');
    assert.notEqual(host.style.display, 'none', 'idle state must remain visible');
    assert.ok(host.querySelector('.vb-activity-blinker-trigger'));
    assert.ok(host.querySelector('.vb-activity-blinker-popover'));
    assert.equal(host.classList.contains('is-active'), false);

    blinker.setEnabled(false);
    assert.equal(host.classList.contains('is-enabled'), false);
    assert.notEqual(host.style.display, 'none', 'disabled state must remain visible');

    blinker.setEnabled(true);
    assert.equal(host.classList.contains('is-enabled'), true);
    assert.equal(host.classList.contains('is-active'), false);
});

test('ActivityBlinker pulses and groups Claude and Codex proxy reports', () => {
    const mount = new FakeElement('div');
    const blinker = new ActivityBlinker({ mount, title: 'Proxy activity' });
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
    assert.match(text, /3 total/);
    assert.match(text, /Claude proxy/);
    assert.match(text, /2 ·/);
    assert.match(text, /Codex proxy/);
    assert.match(text, /1 ·/);
});

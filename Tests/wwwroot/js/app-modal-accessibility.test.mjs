import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const appPath = path.resolve('VibeRails/wwwroot/app.js');
const appSource = readFileSync(appPath, 'utf8');
const classStart = appSource.indexOf('export class VibeControlApp');
const classEnd = appSource.indexOf('// Initialize the app', classStart);
const classSource = appSource
    .slice(classStart, classEnd)
    .replace('export class VibeControlApp', 'class VibeControlApp');
const VibeControlApp = new Function(`${classSource}\nreturn VibeControlApp;`)();

class FakeElement {
    constructor(documentRef, { id = '', tagName = 'DIV', className = '' } = {}) {
        this.documentRef = documentRef;
        this.id = id;
        this.tagName = tagName;
        this.className = className;
        this.children = [];
        this.attributes = new Map();
        this.listeners = new Map();
        this.inert = false;
        this.isConnected = true;
        this.focusCount = 0;
        this.focusables = [];
        this.autofocus = null;
        this.body = null;
    }

    setAttribute(name, value) { this.attributes.set(name, String(value)); }
    getAttribute(name) { return this.attributes.has(name) ? this.attributes.get(name) : null; }
    removeAttribute(name) { this.attributes.delete(name); }
    addEventListener(type, listener) { this.listeners.set(type, listener); }
    getClientRects() { return [{}]; }
    closest() { return null; }
    contains(element) { return element === this || this.focusables.includes(element); }
    focus() {
        this.focusCount += 1;
        this.documentRef.activeElement = this;
    }

    matches(selector) {
        return selector.split(',').some(part => {
            const className = part.trim().replace(/^\./, '');
            return className && this.className.split(/\s+/).includes(className);
        });
    }

    querySelector(selector) {
        if (selector === '[autofocus]') return this.autofocus;
        if (selector === '.modal-body') return this.body;
        return null;
    }

    querySelectorAll() { return this.focusables; }
}

class FakeModalContainer extends FakeElement {
    constructor(documentRef) {
        super(documentRef, { id: 'modal-container' });
        this.html = '';
        this.dialog = null;
        this.backdrop = null;
        this.closeButton = null;
        this.initialInput = null;
        this.submitButton = null;
    }

    get firstElementChild() { return this.children[0] || null; }
    get innerHTML() { return this.html; }
    set innerHTML(value) {
        this.children.forEach(child => { child.isConnected = false; });
        this.html = String(value);
        this.children = [];
        if (!this.html) return;

        this.dialog = new FakeElement(this.documentRef, { className: 'modal' });
        this.backdrop = new FakeElement(this.documentRef, { className: 'modal-backdrop' });
        this.closeButton = new FakeElement(this.documentRef, { tagName: 'BUTTON' });
        this.initialInput = new FakeElement(this.documentRef, { tagName: 'INPUT' });
        this.submitButton = new FakeElement(this.documentRef, { tagName: 'BUTTON' });
        const body = new FakeElement(this.documentRef);
        body.focusables = [this.initialInput, this.submitButton];
        this.dialog.body = body;
        this.dialog.autofocus = this.initialInput;
        this.dialog.focusables = [this.closeButton, this.initialInput, this.submitButton];
        this.children.push(this.dialog, this.backdrop);
    }

    querySelector(selector) {
        if (selector === '.modal[role="dialog"]') return this.dialog;
        if (selector === '.modal-backdrop') return this.backdrop;
        return null;
    }

    querySelectorAll(selector) {
        return selector === '[data-action="close-modal"]' ? [this.closeButton] : [];
    }
}

function setupModalHarness(t) {
    const originalDocument = globalThis.document;
    const originalRaf = globalThis.requestAnimationFrame;
    t.after(() => {
        if (originalDocument === undefined) delete globalThis.document;
        else globalThis.document = originalDocument;
        if (originalRaf === undefined) delete globalThis.requestAnimationFrame;
        else globalThis.requestAnimationFrame = originalRaf;
    });

    const listeners = new Map();
    const documentRef = {
        activeElement: null,
        confirmOpen: false,
        body: { children: [] },
        addEventListener(type, listener, capture) { listeners.set(type, { listener, capture }); },
        removeEventListener(type, listener, capture) {
            const registered = listeners.get(type);
            if (registered?.listener === listener && registered.capture === capture) listeners.delete(type);
        },
        querySelector(selector) {
            return selector === '.vb-confirm-overlay' && this.confirmOpen ? {} : null;
        },
        getElementById(id) { return id === 'modal-container' ? modalContainer : null; }
    };
    const trigger = new FakeElement(documentRef, { tagName: 'BUTTON' });
    const nav = new FakeElement(documentRef, { tagName: 'NAV' });
    const layout = new FakeElement(documentRef);
    const preInert = new FakeElement(documentRef);
    preInert.inert = true;
    preInert.setAttribute('aria-hidden', 'false');
    const toast = new FakeElement(documentRef, { id: 'toast-container' });
    const loading = new FakeElement(documentRef, { id: 'loading-overlay' });
    const modalContainer = new FakeModalContainer(documentRef);
    documentRef.body.children = [nav, layout, preInert, toast, modalContainer, loading];
    documentRef.activeElement = trigger;
    globalThis.document = documentRef;
    globalThis.requestAnimationFrame = callback => { callback(); return 1; };

    const app = Object.create(VibeControlApp.prototype);
    Object.assign(app, {
        modalCleanup: null,
        modalState: null,
        modalSequence: 0,
        escapeHtml(value) { return String(value).replaceAll('&', '&amp;').replaceAll('<', '&lt;'); }
    });
    return { app, documentRef, listeners, modalContainer, trigger, nav, layout, preInert, toast, loading };
}

function tabEvent({ shiftKey = false } = {}) {
    return {
        key: 'Tab',
        shiftKey,
        defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; }
    };
}

test('top-level modal exposes dialog semantics, isolates the app, and restores lifecycle state exactly once', t => {
    const {
        app, listeners, modalContainer, trigger, nav, layout, preInert, toast, loading
    } = setupModalHarness(t);
    let cleanupCount = 0;

    app.showModal('Manage <rules>', '<input autofocus><button>Save</button>', {
        onClose() { cleanupCount += 1; }
    });

    assert.match(modalContainer.html, /role="dialog"/);
    assert.match(modalContainer.html, /aria-modal="true"/);
    const label = modalContainer.html.match(/aria-labelledby="([^"]+)"/)[1];
    assert.match(modalContainer.html, new RegExp(`id="${label}"`));
    assert.match(modalContainer.html, /aria-label="Close dialog"/);
    assert.match(modalContainer.html, /Manage &lt;rules>/);
    assert.equal(nav.inert, true);
    assert.equal(layout.inert, true);
    assert.equal(nav.getAttribute('aria-hidden'), 'true');
    assert.equal(preInert.inert, true);
    assert.equal(toast.inert, false, 'live toasts remain available to assistive technology');
    assert.equal(loading.inert, false, 'the global loading status remains available');
    assert.equal(modalContainer.initialInput.focusCount, 1, 'autofocus control receives initial focus');
    assert.equal(listeners.get('keydown')?.capture, true);

    app.closeModal();
    app.closeModal();

    assert.equal(cleanupCount, 1);
    assert.equal(modalContainer.innerHTML, '');
    assert.equal(nav.inert, false);
    assert.equal(nav.getAttribute('aria-hidden'), null);
    assert.equal(layout.inert, false);
    assert.equal(preInert.inert, true);
    assert.equal(preInert.getAttribute('aria-hidden'), 'false');
    assert.equal(trigger.focusCount, 1);
    assert.equal(listeners.has('keydown'), false);
});

test('top-level modal traps Tab but yields to specialized nested layers and confirm overlays', t => {
    const { app, documentRef, modalContainer } = setupModalHarness(t);
    app.showModal('Keyboard test', '<input autofocus><button>Save</button>');
    const state = app.modalState;
    const [first, , last] = state.dialog.focusables;

    documentRef.activeElement = last;
    const forward = tabEvent();
    app.handleTopLevelModalKeydown(forward, state);
    assert.equal(forward.defaultPrevented, true);
    assert.equal(documentRef.activeElement, first);

    documentRef.activeElement = first;
    const backward = tabEvent({ shiftKey: true });
    app.handleTopLevelModalKeydown(backward, state);
    assert.equal(backward.defaultPrevented, true);
    assert.equal(documentRef.activeElement, last);

    const nested = new FakeElement(documentRef, { className: 'vb-file-explorer-layer' });
    modalContainer.children.push(nested);
    const nestedTab = tabEvent();
    app.handleTopLevelModalKeydown(nestedTab, state);
    assert.equal(nestedTab.defaultPrevented, false, 'file explorer owns its Tab event');
    modalContainer.children.pop();

    documentRef.confirmOpen = true;
    const confirmTab = tabEvent();
    app.handleTopLevelModalKeydown(confirmTab, state);
    assert.equal(confirmTab.defaultPrevented, false, 'confirm overlay owns its Tab event');
});

test('replacing a modal closes the old lifecycle before capturing the replacement', t => {
    const { app, trigger, nav } = setupModalHarness(t);
    const closed = [];
    app.showModal('First', '<button>One</button>', { onClose: () => closed.push('first') });
    app.showModal('Second', '<button>Two</button>', { onClose: () => closed.push('second') });

    assert.deepEqual(closed, ['first']);
    assert.equal(app.modalState.previousFocus, trigger);
    assert.equal(nav.inert, true);

    app.closeModal();
    assert.deepEqual(closed, ['first', 'second']);
    assert.equal(nav.inert, false);
    assert.equal(trigger.focusCount, 2, 'replacement and final close both restore the original trigger');
});

test('Back navigation closes the modal and restores the page before loading the prior view', t => {
    const { app, nav, trigger } = setupModalHarness(t);
    const loaded = [];
    let cleanupCount = 0;
    Object.assign(app, {
        navigationStack: [
            { view: 'dashboard', data: { from: 'modal-test' } },
            { view: 'agent-edit', data: {} }
        ],
        currentView: 'agent-edit',
        canNavigateTo() { return true; },
        loadView(view, data) { loaded.push({ view, data, backgroundInert: nav.inert }); }
    });
    app.showModal('Edit rule', '<button>Save</button>', { onClose: () => { cleanupCount += 1; } });

    assert.equal(app.goBack(), true);
    assert.equal(cleanupCount, 1);
    assert.equal(app.modalState, null);
    assert.equal(nav.inert, false);
    assert.equal(trigger.focusCount, 1);
    assert.deepEqual(loaded, [{
        view: 'dashboard',
        data: { from: 'modal-test' },
        backgroundInert: false
    }]);
});

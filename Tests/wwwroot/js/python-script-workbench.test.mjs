import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/python-script-workbench.js');
const appPath = path.resolve('VibeRails/wwwroot/app.js');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');
const terminalPath = path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js');
const agentsMdPath = path.resolve('VibeRails/wwwroot/AGENTS.md');

const {
    PythonScriptWorkbench,
    TERMINAL_HEIGHT_STORAGE_KEY,
    TERMINAL_WIDTH_STORAGE_KEY,
    TERMINAL_MIN_HEIGHT,
    TERMINAL_MIN_WIDTH,
    EDITOR_MIN_WIDTH,
    SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH,
    isSideBySideLayout,
    clampTerminalWidth,
    readStoredTerminalWidth,
    TERMINAL_MIN_HEIGHT_COMPACT,
    COMPACT_VIEWPORT_MAX_HEIGHT,
    EDITOR_MIN_HEIGHT,
    SPLITTER_KEY_STEP,
    buildAskAgentBrief,
    buildAskAgentInitialPrompt,
    clampTerminalHeight,
    terminalMinHeight,
    readStoredTerminalHeight,
    isStaleSaveError
} = await import(pathToFileURL(modulePath).href);

// --- minimal stand-ins (no jsdom: the view code only touches a handful of element APIs) ---

function fakeElement(extra = {}) {
    const el = {
        hidden: false,
        innerHTML: '',
        textContent: '',
        dataset: {},
        disabled: false,
        title: '',
        open: false,
        attributes: {},
        classes: new Set(),
        listeners: {},
        style: { props: {}, setProperty(key, value) { this.props[key] = value; } },
        setAttribute(key, value) { el.attributes[key] = String(value); },
        getAttribute(key) { return el.attributes[key] ?? null; },
        focus() { el.focused = true; },
        closest: () => null,
        contains: () => false,
        querySelector: () => null,
        querySelectorAll: () => [],
        getBoundingClientRect: () => ({ height: extra.height ?? 0, width: extra.width ?? 0 }),
        addEventListener(type, fn) { (el.listeners[type] ||= []).push(fn); },
        removeEventListener(type, fn) { el.listeners[type] = (el.listeners[type] || []).filter((f) => f !== fn); },
        // Test-only: runs the recorded listeners for `type` (a copy, so a listener may unbind itself).
        dispatch(type, event) { for (const fn of [...(el.listeners[type] || [])]) fn(event); },
        setPointerCapture() { el.captured = true; },
        releasePointerCapture() { el.captured = false; }
    };
    el.classList = {
        add: (name) => el.classes.add(name),
        remove: (name) => el.classes.delete(name),
        contains: (name) => el.classes.has(name)
    };
    return Object.assign(el, extra);
}

/** Every selector resolves to a (cached) fake element, so render helpers can run. */
function fakeRoot(known = {}) {
    const cache = new Map();
    const root = fakeElement({
        contains: () => true,
        querySelector(selector) {
            if (known[selector]) return known[selector];
            if (!cache.has(selector)) cache.set(selector, fakeElement());
            return cache.get(selector);
        }
    });
    root.el = (selector) => root.querySelector(selector);
    return root;
}

function fakeEditor(initial = '') {
    const editor = {
        value: initial,
        position: { lineNumber: 2, column: 3 },
        scrollTop: 40,
        scrollLeft: 5,
        setPositionCalls: [],
        setValueCalls: 0,
        focusCalls: 0,
        getValue() { return this.value; },
        setValue(text) { this.value = text; this.setValueCalls += 1; },
        getPosition() { return this.position; },
        getScrollTop() { return this.scrollTop; },
        getScrollLeft() { return this.scrollLeft; },
        setPosition(position) { this.setPositionCalls.push(position); this.position = position; },
        setScrollTop(top) { this.scrollTop = top; },
        setScrollLeft(left) { this.scrollLeft = left; },
        focus() { this.focusCalls += 1; },
        dispose() { this.disposed = true; },
        getModel() {
            return {
                getFullModelRange: () => ({ full: true }),
                pushEditOperations: (_selections, edits) => { editor.value = edits[0].text; }
            };
        }
    };
    return editor;
}

/**
 * What monaco.editor.create hands back: a fakeEditor that, like the real model,
 * normalizes CRLF on setValue and records the wiring the workbench installs.
 */
function fakeMonacoEditor() {
    const editor = fakeEditor('');
    editor.contentListeners = [];
    editor.commands = [];
    editor.layoutCalls = 0;
    editor.setValue = function (text) { this.value = String(text).replace(/\r\n/g, '\n'); this.setValueCalls += 1; };
    editor.onDidChangeModelContent = (cb) => { editor.contentListeners.push(cb); };
    editor.addCommand = (keys, cb) => { editor.commands.push({ keys, cb }); };
    editor.layout = () => { editor.layoutCalls += 1; };
    return editor;
}

function fakeMonaco() {
    const monaco = {
        created: [],
        KeyMod: { CtrlCmd: 2048 },
        KeyCode: { KeyS: 49 },
        editor: {
            create(mount, options) {
                const editor = fakeMonacoEditor();
                monaco.created.push({ mount, options, editor });
                return editor;
            }
        }
    };
    return monaco;
}

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => { resolve = res; reject = rej; });
    return { promise, resolve, reject };
}

function createApp() {
    const app = {
        calls: [],
        toasts: [],
        errors: [],
        navigations: [],
        viewData: [],
        navigationStack: [{ view: 'jobs', data: {} }, { view: 'python-script', data: { name: 'nightly.py' } }],
        async apiCall(url, method = 'GET', body = null) {
            app.calls.push({ url, method, body });
            return { name: 'nightly.py', content: 'print(1)\n', status: 'approved', version: 'v1' };
        },
        showToast(title, message, tone, options) { app.toasts.push({ title, message, tone, options }); },
        showError(message) { app.errors.push(message); },
        navigate(view, data = {}, options = {}) { app.navigations.push({ view, data, options }); return true; },
        goBack() { app.navigations.push({ view: 'back' }); return true; },
        updateCurrentViewData(data) { app.viewData.push(data); },
        registerNavigationGuard() { return () => {}; },
        terminalController: null,
        jobController: null
    };
    app.jobController = {
        pythonScripts: {
            state: { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [] },
            lastRunByName: new Map(),
            refreshes: 0,
            async ensureState() { return this.state; },
            scriptByName(name) { return this.state.scripts.find((script) => script.name === name) || null; },
            async refresh() { this.refreshes += 1; },
            canOpenInVsCode() { return false; },
            onStateChange() { return () => {}; },
            async saveContent() { return { status: 'modified', version: 'v2' }; }
        }
    };
    return app;
}

const SCRIPT = Object.freeze({
    name: 'nightly.py',
    status: 'approved',
    modifiedUtc: '2026-08-18T09:00:00.0000000Z',
    sizeBytes: 2048,
    path: '/scripts/nightly.py'
});

function mountedWorkbench({ app = createApp(), scripts = [SCRIPT], root = fakeRoot(), editor = fakeEditor('print(1)\n') } = {}) {
    app.jobController.pythonScripts.state.scripts = scripts;
    const workbench = new PythonScriptWorkbench(app);
    workbench.root = root;
    workbench.name = 'nightly.py';
    workbench.script = scripts[0];
    workbench.state = app.jobController.pythonScripts.state;
    workbench.status = 'approved';
    workbench.editor = editor;
    workbench.baseline = editor.value;
    workbench.version = 'v1';
    return { workbench, app, root, editor };
}

/**
 * The browser globals loadView touches (document/window/requestAnimationFrame), torn
 * down after the test. `flushRaf()` runs queued animation frames deterministically.
 */
function installViewGlobals(t, { contentEl, visibilityState = 'visible' } = {}) {
    const previous = {
        document: globalThis.document,
        window: globalThis.window,
        requestAnimationFrame: globalThis.requestAnimationFrame
    };
    const rafQueue = [];
    const documentListeners = [];
    globalThis.document = {
        visibilityState,
        getElementById: (id) => (id === 'app-content' ? contentEl : null),
        addEventListener(type, fn) { documentListeners.push({ type, fn }); },
        removeEventListener(type, fn) {
            const at = documentListeners.findIndex((entry) => entry.type === type && entry.fn === fn);
            if (at !== -1) documentListeners.splice(at, 1);
        }
    };
    globalThis.window = { addEventListener() {}, removeEventListener() {} };
    globalThis.requestAnimationFrame = (fn) => { rafQueue.push(fn); return rafQueue.length; };
    t.after(() => {
        for (const key of Object.keys(previous)) {
            if (previous[key] === undefined) delete globalThis[key];
            else globalThis[key] = previous[key];
        }
    });
    return {
        documentListeners,
        flushRaf() {
            const pending = rafQueue.splice(0);
            for (const fn of pending) fn();
        }
    };
}

/** An #app-content stand-in whose innerHTML assignment yields the given root. */
function fakeContent(root) {
    const content = {
        html: '',
        set innerHTML(value) { content.html = value; },
        get innerHTML() { return content.html; },
        querySelector: (selector) => (selector === '[data-python-workbench]' ? root : null)
    };
    return content;
}

/** Mounts through the real loadView; the fake Monaco is injected via the _loadMonaco seam. */
async function loadedWorkbench(t, { app = createApp(), scripts = [SCRIPT], root = fakeRoot(), name = 'nightly.py', monaco = fakeMonaco() } = {}) {
    app.jobController.pythonScripts.state.scripts = scripts;
    const content = fakeContent(root);
    const globals = installViewGlobals(t, { contentEl: content });
    const workbench = new PythonScriptWorkbench(app);
    workbench._loadMonaco = async () => monaco;
    t.after(() => workbench.unload());
    await workbench.loadView({ name });
    return { workbench, app, root, content, monaco, ...globals };
}

// --- app.js wiring ---

test('The workbench is registered as the python-script view and torn down like every other view', () => {
    const app = readFileSync(appPath, 'utf8');

    assert.match(app, /import \{ PythonScriptWorkbench \} from '\.\/js\/modules\/python-script-workbench\.js'/);
    assert.match(app, /this\.pythonScriptWorkbench = new PythonScriptWorkbench\(this\)/);
    assert.match(app, /'python-script': \(\) => this\.pythonScriptWorkbench\.loadView\(data\)/);

    // Teardown runs in loadView, before the next view mounts.
    const loadViewIndex = app.indexOf('loadView(view, data = {}) {');
    const unloadIndex = app.indexOf('this.pythonScriptWorkbench?.unload?.();');
    const viewsIndex = app.indexOf('const views = {');
    assert.ok(loadViewIndex > 0 && loadViewIndex < unloadIndex && unloadIndex < viewsIndex,
        'unload must sit in loadView\'s teardown list');

    // Automation stays highlighted, and a duplicated tab lands on the Automation page.
    const subNav = app.slice(app.indexOf('updateActiveSubNav(view) {'), app.indexOf('goBack() {'));
    assert.match(subNav, /view === 'python-script'\s*\?\s*'jobs'/);
    const duplicate = app.slice(app.indexOf('getDuplicateTabViewName(view) {'), app.indexOf('async openDuplicateTab() {'));
    assert.match(duplicate, /view === 'python-script'\s*\?\s*'jobs'/);
    assert.doesNotMatch(duplicate, /'python-script',/, 'the workbench itself is not a duplicateable view');

    // Same viewport-filling shell as the Code quality page.
    assert.match(app, /const isRulesWorkspace = view === 'dashboard' \|\| view === 'agents' \|\| view === 'python-script';/);
    // In-place script switching keeps the stack honest without a reload.
    assert.match(app, /updateCurrentViewData\(data = \{\}\) \{[\s\S]*?this\.navigationStack\[last\] = \{ view: this\.currentView, data \};/);
});

/** Pulls a one-argument method body out of app.js so it can run without a browser. */
function appMethod(source, signature) {
    const start = source.indexOf(`    ${signature} {`);
    assert.ok(start > 0, `${signature} must exist in app.js`);
    const bodyStart = source.indexOf('{', start) + 1;
    const end = source.indexOf('\n    }', bodyStart);
    return source.slice(bodyStart, end);
}

test('Escape: widgets first, then an open modal, then plain fields, then Back', () => {
    const app = readFileSync(appPath, 'utf8');
    const handler = app.slice(app.indexOf('setupKeyboardShortcuts() {'));

    // Source pin of the ORDER: (a) consumed / widget → return, (b) modal → close + return,
    // (c) plain field → return, (d) Back. Bubble phase and no stopPropagation, so the
    // terminal still receives the key.
    assert.match(handler,
        /if \(e\.key === 'Escape'\) \{[\s\S]*?if \(e\.defaultPrevented \|\| this\.isEscapeOwnedByWidget\(e\.target\)\) \{\s*return;\s*\}[\s\S]*?if \(modalContainer\?\.firstElementChild\) \{\s*this\.closeModal\(\);\s*return;\s*\}[\s\S]*?if \(this\.isEscapeOwnedByTarget\(e\.target\)\) \{\s*return;\s*\}[\s\S]*?if \(this\.navigationStack\.length > 1\) \{\s*this\.goBack\(\);/);
    assert.doesNotMatch(handler.slice(0, handler.indexOf('} else if')), /stopPropagation|stopImmediatePropagation/);
    assert.match(app, /document\.addEventListener\('keydown', \(e\) => \{\s*if \(e\.key === 'Escape'\)/);

    // Behavioural pin of the two predicates.
    const isWidget = new Function('target', appMethod(app, 'isEscapeOwnedByWidget(target)'));
    const isField = new Function('target', appMethod(app, 'isEscapeOwnedByTarget(target)'));
    const inside = (selectorPart) => ({ closest: (selector) => (selector.includes(selectorPart) ? {} : null) });
    for (const owner of ['.xterm', '.monaco-editor']) {
        assert.equal(isWidget(inside(owner)), true, `${owner} must own Escape as a widget`);
        assert.equal(isField(inside(owner)), false, `${owner} is not a plain field`);
    }
    for (const owner of ['input', 'textarea', 'select', '[contenteditable="true"]']) {
        assert.equal(isField(inside(owner)), true, `${owner} must own Escape as a field`);
        assert.equal(isWidget(inside(owner)), false);
    }
    assert.equal(isField({ closest: () => null }), false);
    assert.equal(isField(null), false);
    assert.equal(isWidget(null), false);

    // Behavioural pin of the handler itself, driven with a fake `this` and document.
    const bodyStart = app.indexOf("document.addEventListener('keydown', (e) => {") + "document.addEventListener('keydown', (e) => {".length;
    const bodyEnd = app.indexOf('\n        });', bodyStart);
    const keydown = new Function('e', 'document', app.slice(bodyStart, bodyEnd));
    const drive = ({ target, modalOpen = false, defaultPrevented = false, stack = 2 }) => {
        const log = [];
        const self = {
            isEscapeOwnedByWidget: isWidget,
            isEscapeOwnedByTarget: isField,
            navigationStack: new Array(stack).fill({}),
            closeModal() { log.push('closeModal'); },
            goBack() { log.push('goBack'); },
            toggleSidebar() { log.push('toggleSidebar'); }
        };
        const document = {
            getElementById: (id) => (id === 'modal-container' ? { firstElementChild: modalOpen ? {} : null } : null),
            activeElement: null
        };
        keydown.call(self, { key: 'Escape', defaultPrevented, target, preventDefault() {} }, document);
        return log;
    };
    const body = { closest: () => null };
    // The bug: Esc with focus in a modal's own input must still close the modal.
    assert.deepEqual(drive({ target: inside('input'), modalOpen: true }), ['closeModal']);
    assert.deepEqual(drive({ target: inside('select'), modalOpen: true }), ['closeModal']);
    // A plain field with no modal keeps the key (a <select> closing its list is not Back).
    assert.deepEqual(drive({ target: inside('input'), modalOpen: false }), []);
    // Widgets keep it even with a modal open; a handler that already consumed it wins too.
    assert.deepEqual(drive({ target: inside('.xterm'), modalOpen: true }), []);
    assert.deepEqual(drive({ target: inside('.monaco-editor'), modalOpen: false }), []);
    assert.deepEqual(drive({ target: body, modalOpen: true, defaultPrevented: true }), []);
    // Nothing focused, nothing open: Back — but only with somewhere to go back to.
    assert.deepEqual(drive({ target: body, modalOpen: false }), ['goBack']);
    assert.deepEqual(drive({ target: body, modalOpen: false, stack: 1 }), []);
    // A modal closes without ALSO navigating back.
    assert.deepEqual(drive({ target: body, modalOpen: true }), ['closeModal']);
});

// --- Ask agent ---

test('Ask agent pastes the brief without submitting when a session socket is open', async () => {
    const { workbench, app } = mountedWorkbench();
    const injected = [];
    const started = [];
    const tab = {
        // Belongs to this script: askAgent only pastes into the script's own agent tab.
        state: { hasActiveSession: true, taskKey: 'python-script:nightly.py' },
        instance: {
            hasOpenSocket: () => true,
            injectText: (text) => { injected.push(text); return true; },
            focusInput() { this.focused = true; },
            focus() {}
        }
    };
    app.terminalController = {
        manager: { getActiveTab: () => tab, getLaunchSelection: () => 'base:codex', getSelectionMeta: () => ({ cli: 'codex' }) },
        async startTerminalWithOptions(options) { started.push(options); return { started: true }; }
    };

    const result = await workbench.askAgent();

    assert.deepEqual(result, { mode: 'inject' });
    assert.equal(started.length, 0, 'an open session must be reused, not replaced');
    assert.equal(injected.length, 1);
    assert.match(injected[0], /\/scripts\/nightly\.py/);
    assert.match(injected[0], /single self-contained file at that path/);
    assert.match(injected[0], /it only runs after I sign it in VibeRails/);
    assert.doesNotMatch(injected[0], /re-sign/, 'the constraint says sign, not re-sign (the script may be unsigned)');
    assert.ok(injected[0].endsWith('\n\nChange: '), 'the paste ends mid-sentence so the user finishes and submits it');
    assert.equal(tab.instance.focused, true);
    assert.equal(app.toasts.at(-1).options?.compact, true);
});

test('Ask agent reconnects a dropped agent socket before pasting, and pastes into a re-activated task tab', async () => {
    // Session alive but the socket is closed (webview slept): connect(), then inject.
    {
        const { workbench, app } = mountedWorkbench();
        const order = [];
        const tab = {
            // No task key, but parked in the scripts folder: the panel's own agent.
            state: { hasActiveSession: true, cli: 'claude', workingDirectory: '/scripts' },
            instance: {
                _open: false,
                hasOpenSocket() { return this._open; },
                async connect() { order.push('connect'); this._open = true; },
                injectText: (text) => { order.push(`inject:${text.slice(0, 6)}`); return true; },
                focusInput() {},
                focus() {}
            }
        };
        app.terminalController = {
            manager: { getActiveTab: () => tab, getLaunchSelection: () => null, getSelectionMeta: () => null },
            async startTerminalWithOptions() { order.push('start'); return { started: true }; }
        };
        assert.deepEqual(await workbench.askAgent(), { mode: 'inject' });
        assert.deepEqual(order, ['connect', 'inject:Please']);
    }
    // No active tab, but the task tab for this script already had a session: the manager
    // re-activates it (reusedExisting, not started) and the brief goes straight in.
    {
        const { workbench, app } = mountedWorkbench();
        const injected = [];
        const taskTab = {
            state: { hasActiveSession: true, cli: 'claude' },
            instance: { hasOpenSocket: () => true, injectText: (text) => { injected.push(text); return true; }, focusInput() {}, focus() {} }
        };
        let active = null;
        app.terminalController = {
            manager: { getActiveTab: () => active, getLaunchSelection: () => null, getSelectionMeta: () => null },
            async startTerminalWithOptions() { active = taskTab; return { reusedExisting: true, started: false }; }
        };
        const result = await workbench.askAgent();
        assert.deepEqual(result, { mode: 'inject', cli: 'claude' });
        assert.equal(injected.length, 1);
        assert.match(injected[0], /Change: $/);
        assert.doesNotMatch(app.toasts.map((toast) => toast.title).join(), /Agent started/);
    }
});
test('Ask agent never pastes into an unrelated active agent tab', async () => {
    // The pre-2026-08-24 behavior — brief goes to whatever tab is active — pasted
    // into completely unrelated sessions. An active agent that neither carries this
    // script's task key nor lives in the scripts folder must be left alone.
    const { workbench, app } = mountedWorkbench();
    const injected = [];
    const started = [];
    const unrelated = {
        state: { hasActiveSession: true, cli: 'claude', taskKey: 'automation:deploy', workingDirectory: 'C:/source/project' },
        instance: { hasOpenSocket: () => true, injectText: (text) => { injected.push(text); return true; }, focusInput() {}, focus() {} }
    };
    app.terminalController = {
        manager: { getActiveTab: () => unrelated, getLaunchSelection: () => null, getSelectionMeta: () => null },
        async startTerminalWithOptions(options) { started.push(options); return { started: true }; }
    };

    const result = await workbench.askAgent();

    assert.equal(result.mode, 'start');
    assert.equal(injected.length, 0, 'the unrelated session must not receive the brief');
    assert.equal(started.length, 1);
    assert.equal(started[0].taskKey, 'python-script:nightly.py');
});
test('Ask agent activates the script task tab and pastes into it, even when another tab is active', async () => {
    const { workbench, app } = mountedWorkbench();
    const injected = [];
    const activations = [];
    const taskTab = {
        state: { id: 'task-1', hasActiveSession: true, cli: 'claude' },
        instance: { hasOpenSocket: () => true, injectText: (text) => { injected.push(text); return true; }, focusInput() {}, focus() {} }
    };
    app.terminalController = {
        manager: {
            activeTabId: 'other-tab',
            findTabByTaskKey: (key) => (key === 'python-script:nightly.py' ? taskTab : null),
            async activateTab(tabId, options) { activations.push({ tabId, options }); this.activeTabId = tabId; },
            getActiveTab: () => null,
            getLaunchSelection: () => null,
            getSelectionMeta: () => null
        },
        async startTerminalWithOptions() { throw new Error('the live task tab must be reused, not restarted'); }
    };

    assert.deepEqual(await workbench.askAgent(), { mode: 'inject' });
    assert.deepEqual(activations, [{ tabId: 'task-1', options: { connectIfNeeded: true } }]);
    assert.equal(injected.length, 1);
});
test('Ask agent re-keys a stale shell task tab instead of pasting into it', async () => {
    // A pre-rename build tagged RUN shell tabs with the agent key; the brief must
    // never feed a running python process, and the key moves to python-script-run:.
    const { workbench, app } = mountedWorkbench();
    const rekeys = [];
    const started = [];
    const shellTab = {
        state: { id: 'run-1', hasActiveSession: true, cli: 'Shell', taskKey: 'python-script:nightly.py' },
        instance: { hasOpenSocket: () => true, injectText: () => { throw new Error('must not inject into the shell'); } }
    };
    app.terminalController = {
        manager: {
            findTabByTaskKey: (key) => (key === 'python-script:nightly.py' ? shellTab : null),
            updateTabMetadata: (tab, metadata) => { rekeys.push({ ...metadata }); Object.assign(tab.state, metadata); },
            getActiveTab: () => shellTab,
            getLaunchSelection: () => null,
            getSelectionMeta: () => null
        },
        async startTerminalWithOptions(options) { started.push(options); return { started: true }; }
    };

    const result = await workbench.askAgent();

    assert.deepEqual(rekeys, [{ taskKey: 'python-script-run:nightly.py' }]);
    assert.equal(result.mode, 'start');
    assert.equal(started.length, 1, 'a fresh agent starts once the stale key is out of the way');
});

test('Ask agent never pastes into a plain shell tab: it starts Claude instead', async () => {
    const { workbench, app } = mountedWorkbench();
    const injected = [];
    const started = [];
    // The server reports the wire name "Shell" (capital S).
    const shellTab = {
        state: { hasActiveSession: true, cli: 'Shell', selection: 'base:shell' },
        instance: { hasOpenSocket: () => true, injectText: (text) => { injected.push(text); return true; }, focusInput() {}, focus() {} }
    };
    app.terminalController = {
        manager: {
            getActiveTab: () => shellTab,
            getLaunchSelection: () => 'base:shell',
            getSelectionMeta: () => ({ cli: 'shell', environmentName: null, displayName: 'Shell' })
        },
        async startTerminalWithOptions(options) { started.push(options); return { started: true }; }
    };

    const result = await workbench.askAgent();

    assert.equal(injected.length, 0, 'a shell would only echo the brief');
    assert.equal(started.length, 1);
    assert.equal(started[0].cli, 'claude');
    assert.equal(result.mode, 'start');
    assert.equal(result.cli, 'claude');

    // Without state.cli the picker selection decides, still case-insensitively.
    shellTab.state.cli = null;
    await workbench.askAgent();
    assert.equal(injected.length, 0);
    assert.equal(started.length, 2);
});

test('Ask agent starts the picked CLI in the scripts directory when nothing is running (default claude)', async () => {
    const { workbench, app } = mountedWorkbench();
    const started = [];
    let selection = null;
    let meta = null;
    app.terminalController = {
        manager: {
            getActiveTab: () => null,
            getLaunchSelection: () => selection,
            getSelectionMeta: () => meta
        },
        async startTerminalWithOptions(options, host) { started.push({ options, host }); return { started: true }; }
    };
    const host = workbench.root.el('[data-terminal-content]');

    // Nothing picked → claude.
    let result = await workbench.askAgent();
    assert.equal(result.mode, 'start');
    assert.equal(started[0].host, host);
    assert.equal(started[0].options.cli, 'claude');
    assert.equal(started[0].options.environmentName, null);
    assert.equal(started[0].options.workingDirectory, '/scripts');
    assert.equal(started[0].options.tabLabel, 'nightly.py');
    assert.equal(started[0].options.taskKey, 'python-script:nightly.py');
    assert.match(started[0].options.initialPrompt, /Read \/scripts\/nightly\.py/);
    assert.match(started[0].options.initialPrompt, /wait for my change request/);
    assert.doesNotMatch(started[0].options.initialPrompt, /Change: /,
        'initialPrompt is auto-submitted, so it must not carry the half-finished sentence');
    assert.doesNotMatch(started[0].options.initialPrompt, /\{\{/, 'no template placeholders — they would pop the fill-in modal');
    assert.match(app.toasts.at(-1).message, /^Claude opened in the scripts folder/);

    // The panel's picker wins when it names an agent, and the toast uses its display
    // name rather than the wire id ('agy')…
    selection = 'env:7:agy';
    meta = { cli: 'agy', environmentName: 'Reviewer', displayName: 'Antigravity · Reviewer' };
    result = await workbench.askAgent();
    assert.equal(started[1].options.cli, 'agy');
    assert.equal(started[1].options.environmentName, 'Reviewer');
    assert.equal(result.cli, 'agy', 'the returned id stays the wire name');
    assert.match(app.toasts.at(-1).message, /^Antigravity · Reviewer opened in the scripts folder/);
    assert.doesNotMatch(app.toasts.at(-1).message, /\bagy\b/);

    // …but the plain shell is not an agent, so it falls back to claude.
    selection = 'base:shell';
    meta = { cli: 'shell', environmentName: null, displayName: 'Shell' };
    await workbench.askAgent();
    assert.equal(started[2].options.cli, 'claude');
    assert.match(app.toasts.at(-1).message, /^Claude opened/);
});

test('Ask agent briefs name the absolute path and the signing constraint, once each', () => {
    const brief = buildAskAgentBrief({ name: 'nightly.py', path: 'C:\\Users\\rob\\.vibe_rails\\scripts\\nightly.py' });
    assert.match(brief, /C:\\Users\\rob\\\.vibe_rails\\scripts\\nightly\.py/);
    assert.match(brief, /tell me when you're done and what changed/);
    assert.match(brief, /it only runs after I sign it in VibeRails/);
    assert.doesNotMatch(brief, /re-sign/);
    const initial = buildAskAgentInitialPrompt({ name: 'nightly.py', path: '/scripts/nightly.py' });
    assert.equal(initial,
        "Read /scripts/nightly.py and summarize what it does in 2-3 lines, then wait for my change request. "
        + "It's a VibeRails Automation script: keep it a single self-contained file at that path; "
        + "it only runs after I sign it in VibeRails, so tell me when you're done and what changed.");
    assert.equal(initial.match(/VibeRails Automation script/g).length, 1, 'the phrase is not repeated');
});

// --- loading the view (real loadView, fake DOM) ---

test('loadView mounts the shell, fetches the content and starts polling; unload stops it', async (t) => {
    const app = createApp();
    let ensureStateCalls = 0;
    app.jobController.pythonScripts.ensureState = async function () { ensureStateCalls += 1; return this.state; };
    app.apiCall = async (url, method) => {
        app.calls.push({ url, method });
        return { name: 'nightly.py', content: 'print(1)\r\nprint(2)\r\n', status: 'approved', version: 'v1' };
    };
    const { workbench, root, content, monaco, flushRaf, documentListeners } = await loadedWorkbench(t, { app });

    assert.equal(ensureStateCalls, 1);
    assert.match(content.html, /data-python-workbench/);
    assert.deepEqual(app.calls, [{ url: '/api/v1/python-scripts/content?name=nightly.py', method: 'GET' }]);
    assert.equal(monaco.created.length, 1);
    assert.equal(monaco.created[0].mount, root.el('[data-workbench-editor-mount]'));
    assert.equal(monaco.created[0].options.language, 'python');
    const editor = monaco.created[0].editor;
    assert.equal(workbench.editor, editor);
    // The baseline is what the editor reports (CRLF normalized), never the raw response.
    assert.equal(editor.getValue(), 'print(1)\nprint(2)\n');
    assert.equal(workbench.baseline, editor.getValue());
    assert.equal(workbench.isDirty, false);
    assert.equal(workbench.version, 'v1');
    assert.equal(workbench.status, 'approved');
    assert.equal(root.el('[data-workbench-editor-state]').hidden, true);
    assert.equal(root.el('[data-workbench-hint]').textContent, 'Signed. Saving an edit clears the signature until you sign again.');
    assert.equal(root.el('[data-workbench-action="save"]').disabled, true, 'nothing to save yet');
    assert.match(root.el('[data-workbench-rail-list]').innerHTML, /aria-current="page"/);
    // Ctrl/⌘+S is bound in the editor and typing re-renders the hint.
    assert.deepEqual(editor.commands.map((command) => command.keys), [2048 | 49]);
    editor.value = 'changed';
    for (const cb of editor.contentListeners) cb();
    assert.equal(root.el('[data-workbench-hint]').textContent, 'Unsaved changes · Ctrl/⌘+S saves');
    assert.equal(root.el('[data-workbench-action="save"]').disabled, false);
    // Animation frames: the editor lays out + focuses, and the separator gets its aria values.
    flushRaf();
    assert.equal(editor.layoutCalls, 1);
    assert.equal(editor.focusCalls, 1);
    assert.equal(root.el('[data-workbench-splitter]').getAttribute('aria-valuenow'), String(TERMINAL_MIN_HEIGHT));
    assert.equal(root.el('[data-workbench-splitter]').getAttribute('aria-valuemin'), String(TERMINAL_MIN_HEIGHT));
    // Live reload is polling, and disk/visibility listeners are installed.
    assert.ok(workbench._pollTimer, 'the disk poll is running');
    assert.deepEqual(documentListeners.map((entry) => entry.type).sort(), ['click', 'visibilitychange']);

    workbench.unload();
    assert.equal(workbench._pollTimer, null);
    assert.equal(editor.disposed, true);
    assert.equal(documentListeners.length, 0, 'document listeners are removed');
});

test('loadView without a name, or for a script that is not in the folder, says so and leaves', async (t) => {
    const app = createApp();
    const root = fakeRoot();
    const { workbench } = await loadedWorkbench(t, { app, root, name: '   ' });
    assert.equal(app.toasts.at(-1).title, 'No script selected');
    assert.deepEqual(app.navigations, [{ view: 'back' }]);
    assert.equal(workbench.root, null);
    assert.equal(app.calls.length, 0);

    app.navigations.length = 0;
    await workbench.loadView({});
    assert.equal(app.toasts.at(-1).title, 'No script selected');
    assert.deepEqual(app.navigations, [{ view: 'back' }]);

    app.navigations.length = 0;
    await workbench.loadView({ name: 'ghost.py' });
    assert.equal(app.toasts.at(-1).title, 'Script not found');
    assert.match(app.toasts.at(-1).message, /ghost\.py is no longer in the scripts folder/);
    assert.deepEqual(app.navigations, [{ view: 'back' }]);
    assert.equal(app.calls.length, 0, 'no content fetch for a script the list does not know');
});

test('loadView shows an error state with Retry when the list or the content cannot be loaded', async (t) => {
    // The list endpoint fails: retry re-runs loadView.
    {
        const app = createApp();
        app.jobController.pythonScripts.ensureState = async () => null;
        const root = fakeRoot();
        const { workbench } = await loadedWorkbench(t, { app, root });
        const state = root.el('[data-workbench-editor-state]');
        assert.equal(state.hidden, false);
        assert.match(state.innerHTML, /Could not load the script list\./);
        assert.match(state.innerHTML, /data-workbench-action="retry"/);
        assert.equal(app.calls.length, 0);
        assert.equal(workbench.state, null);
        // Retry with the list back → content loads.
        app.jobController.pythonScripts.ensureState = async function () { return this.state; };
        await workbench.retryLoad();
        assert.equal(app.calls.length, 1);
        assert.equal(workbench.baseline, 'print(1)\n');
    }
    // The content endpoint fails: the message is shown, the shell stays.
    {
        const app = createApp();
        app.apiCall = async () => { throw new Error('Script file is locked.'); };
        const root = fakeRoot();
        const { workbench, monaco } = await loadedWorkbench(t, { app, root });
        const state = root.el('[data-workbench-editor-state]');
        assert.equal(state.hidden, false);
        assert.match(state.innerHTML, /Script file is locked\./);
        assert.match(state.innerHTML, /data-workbench-action="retry"/);
        assert.equal(monaco.created.length, 0, 'no editor until there is content to show');
        assert.equal(workbench.editor, null);
        assert.equal(app.navigations.length, 0);
    }
});

test('A second loadView while the first is still awaiting the list wins (generation guard)', async (t) => {
    const app = createApp();
    const other = { name: 'weekly.py', status: 'unapproved', path: '/scripts/weekly.py', sizeBytes: 10 };
    app.jobController.pythonScripts.state.scripts = [SCRIPT, other];
    const gate = deferred();
    let ensureStateCalls = 0;
    app.jobController.pythonScripts.ensureState = async function () {
        ensureStateCalls += 1;
        if (ensureStateCalls === 1) await gate.promise;
        return this.state;
    };
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return { name: 'weekly.py', content: 'print("weekly")\n', status: 'unapproved', version: 'w1' };
    };
    const monaco = fakeMonaco();
    const root = fakeRoot();
    installViewGlobals(t, { contentEl: fakeContent(root) });
    const workbench = new PythonScriptWorkbench(app);
    workbench._loadMonaco = async () => monaco;
    t.after(() => workbench.unload());

    const first = workbench.loadView({ name: 'nightly.py' });
    const second = workbench.loadView({ name: 'weekly.py' });
    await second;
    gate.resolve();
    await first;

    assert.equal(workbench.name, 'weekly.py');
    assert.equal(workbench.version, 'w1');
    assert.deepEqual(app.calls.map((call) => call.url), ['/api/v1/python-scripts/content?name=weekly.py'],
        'the superseded load never fetches its content');
    assert.equal(monaco.created.length, 1);
    assert.equal(root.el('[data-workbench-name]').textContent, 'weekly.py');
});

test('Overlapping content loads share one Monaco mount instead of stacking editors', async (t) => {
    const app = createApp();
    const root = fakeRoot();
    installViewGlobals(t, { contentEl: fakeContent(root) });
    const workbench = new PythonScriptWorkbench(app);
    workbench.root = root;
    workbench.name = 'nightly.py';
    const monaco = fakeMonaco();
    let gate = deferred();
    workbench._loadMonaco = () => gate.promise;
    t.after(() => workbench.unload());

    const generation = workbench._generation;
    const first = workbench._ensureEditor(generation);
    const second = workbench._ensureEditor(generation);
    assert.ok(workbench._editorMounting, 'the in-flight mount is memoized');
    gate.resolve(monaco);
    assert.deepEqual(await Promise.all([first, second]), [true, true]);
    assert.equal(monaco.created.length, 1, 'exactly one editor for two concurrent loads');
    assert.equal(workbench._editorMounting, null);
    assert.equal(await workbench._ensureEditor(generation), true);
    assert.equal(monaco.created.length, 1);

    // A mount superseded by unload() must not clear the newer mount's memo when it settles.
    workbench.unload();
    workbench.root = root;
    workbench.name = 'nightly.py';
    const staleGate = deferred();
    const freshGate = deferred();
    workbench._loadMonaco = () => staleGate.promise;
    const stale = workbench._ensureEditor(workbench._generation);
    workbench.unload();
    workbench.root = root;
    workbench.name = 'nightly.py';
    workbench._loadMonaco = () => freshGate.promise;
    const fresh = workbench._ensureEditor(workbench._generation);
    const memo = workbench._editorMounting;
    staleGate.resolve(monaco);
    assert.equal(await stale, false);
    assert.equal(workbench._editorMounting, memo, 'the stale mount left the live memo alone');
    freshGate.resolve(monaco);
    assert.equal(await fresh, true);
    assert.equal(monaco.created.length, 2);
    assert.equal(workbench._editorMounting, null);
});

// --- live reload ---

test('A new disk version swaps the text in place when the editor is clean, preserving cursor and scroll', () => {
    const { workbench, app, editor, root } = mountedWorkbench();

    assert.equal(workbench.applyDiskCheck({ version: 'v1', content: 'ignored' }), 'none');
    assert.equal(editor.value, 'print(1)\n');

    const result = workbench.applyDiskCheck({ version: 'v2', content: 'print(2)\n', status: 'modified' });

    assert.equal(result, 'reloaded');
    assert.equal(editor.value, 'print(2)\n');
    assert.equal(editor.setValueCalls, 0, 'a full-range edit keeps the undo stack; setValue would not');
    assert.deepEqual(editor.setPositionCalls, [{ lineNumber: 2, column: 3 }]);
    assert.equal(editor.scrollTop, 40);
    assert.equal(workbench.baseline, 'print(2)\n');
    assert.equal(workbench.isDirty, false);
    assert.equal(workbench.version, 'v2');
    assert.equal(workbench.status, 'modified');
    assert.equal(root.el('[data-workbench-banner]').hidden, true);
    assert.equal(app.jobController.pythonScripts.refreshes, 1, 'the list is refreshed for size/edited/status');
    // No toast: an agent editing the file would raise one every few seconds. The hint
    // line says when the text was picked up, and it survives the list refresh that follows.
    assert.equal(app.toasts.length, 0);
    const hint = root.el('[data-workbench-hint]');
    assert.match(hint.textContent, /^Reloaded from disk · \S/);
    workbench._onSharedStateChange(app.jobController.pythonScripts.state);
    assert.match(hint.textContent, /^Reloaded from disk · /);
    // The next keystroke replaces it.
    editor.value = 'print(2)\nprint(3)\n';
    workbench._renderHint();
    assert.equal(hint.textContent, 'Unsaved changes · Ctrl/⌘+S saves');
    workbench._onSharedStateChange(app.jobController.pythonScripts.state);
    assert.equal(hint.textContent, 'Unsaved changes · Ctrl/⌘+S saves');
});

test('A new disk version while the editor is dirty raises the banner and leaves the text alone', async () => {
    const { workbench, app, editor, root } = mountedWorkbench();
    editor.value = 'print(1)\nprint("mine")\n';
    assert.equal(workbench.isDirty, true);

    const result = workbench.applyDiskCheck({ version: 'v2', content: 'print(2)\n', status: 'modified' });

    assert.equal(result, 'banner');
    assert.equal(editor.value, 'print(1)\nprint("mine")\n');
    assert.equal(workbench.version, 'v1');
    const banner = root.el('[data-workbench-banner]');
    assert.equal(banner.hidden, false);
    assert.equal(banner.dataset.kind, 'disk');
    assert.match(banner.innerHTML, /nightly\.py changed on disk\./);
    assert.match(banner.innerHTML, /data-workbench-action="reload"/);
    assert.match(banner.innerHTML, /data-workbench-action="keep-edits"/);
    assert.equal(app.toasts.length, 0, 'no toast while the banner is up');

    // "Keep my edits" adopts the disk version so the next save is allowed to overwrite it,
    // and the same change does not raise the banner again.
    assert.equal(await workbench.keepMyEdits(), true);
    assert.equal(workbench.version, 'v2');
    assert.equal(banner.hidden, true);
    assert.equal(workbench.applyDiskCheck({ version: 'v2', content: 'print(2)\n' }), 'none');
    assert.equal(editor.value, 'print(1)\nprint("mine")\n');
});

test('A stale save (400 from the server) surfaces the message and offers Reload', async () => {
    const { workbench, app, root } = mountedWorkbench();
    workbench.editor.value = 'print(3)\n';
    app.jobController.pythonScripts.saveContent = async () => {
        throw new Error("'nightly.py' changed after it was opened. Reopen it before saving so newer edits are not overwritten.");
    };

    assert.equal(await workbench.save(), false);

    const banner = root.el('[data-workbench-banner]');
    assert.equal(banner.hidden, false);
    assert.equal(banner.dataset.kind, 'stale');
    assert.match(banner.innerHTML, /changed after it was opened/);
    assert.match(banner.innerHTML, /data-workbench-action="reload"/);
    assert.equal(app.errors.length, 0, 'the banner replaces the generic error toast');
    assert.equal(workbench.saving, false);
    assert.equal(isStaleSaveError('Could not save'), false);

    // Any other failure is a plain error.
    app.jobController.pythonScripts.saveContent = async () => { throw new Error('disk full'); };
    await workbench.save();
    assert.deepEqual(app.errors, ['disk full']);
});

test('A successful save updates the baseline, version and status and never carries a PIN', async () => {
    const { workbench, app } = mountedWorkbench();
    const saves = [];
    app.jobController.pythonScripts.saveContent = async (name, content, expectedVersion) => {
        saves.push({ name, content, expectedVersion });
        return { status: 'modified', version: 'v2' };
    };
    workbench.editor.value = 'print(2)\n';

    assert.equal(await workbench.save(), true);

    assert.deepEqual(saves, [{ name: 'nightly.py', content: 'print(2)\n', expectedVersion: 'v1' }]);
    assert.equal(workbench.baseline, 'print(2)\n');
    assert.equal(workbench.version, 'v2');
    assert.equal(workbench.status, 'modified');
    assert.equal(workbench.isDirty, false);
    assert.equal(workbench.root.el('[data-workbench-hint]').textContent, 'Saved.');
    assert.equal(workbench.root.el('[data-workbench-action="save"]').disabled, true);
});

test('Save & sign runs the shared PIN flow only after the save landed', async () => {
    const { workbench, app } = mountedWorkbench();
    const order = [];
    app.jobController.pythonScripts.saveContent = async () => { order.push('save'); return { status: 'modified', version: 'v2' }; };
    app.jobController.pythonScripts.approve = async (name) => { order.push(`approve:${name}`); return true; };

    await workbench.save({ sign: true });
    assert.deepEqual(order, ['save', 'approve:nightly.py']);

    // A failed save must not sign whatever is on disk.
    order.length = 0;
    app.jobController.pythonScripts.saveContent = async () => { throw new Error('nope'); };
    await workbench.save({ sign: true });
    assert.deepEqual(order, []);
});

test('The polling loop skips hidden documents, in-flight saves, loads and authoring flows', async (t) => {
    const { workbench, app } = mountedWorkbench();
    const shim = { visibilityState: 'hidden', addEventListener() {}, removeEventListener() {}, getElementById: () => null };
    const previous = globalThis.document;
    globalThis.document = shim;
    t.after(() => {
        if (previous === undefined) delete globalThis.document;
        else globalThis.document = previous;
    });

    await workbench.checkDisk();
    assert.equal(app.calls.length, 0, 'a hidden document is not polled');
    shim.visibilityState = 'visible';
    workbench.saving = true;
    await workbench.checkDisk();
    assert.equal(app.calls.length, 0);
    workbench.saving = false;
    workbench._loadToken = Symbol('loading');
    await workbench.checkDisk();
    assert.equal(app.calls.length, 0);
    workbench._loadToken = null;
    workbench._mutating = true;
    await workbench.checkDisk();
    assert.equal(app.calls.length, 0, 'rename/delete/duplicate hold the poll off');
    workbench._mutating = false;
    await workbench.checkDisk();
    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=nightly.py');
});

test('A poll that started before a save is discarded when it lands after the save', async () => {
    const { workbench, app, editor, root } = mountedWorkbench();
    const pollResponse = deferred();
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return pollResponse.promise;
    };
    app.jobController.pythonScripts.saveContent = async () => ({ status: 'modified', version: 'v2' });

    const poll = workbench.checkDisk();
    assert.equal(app.calls.length, 1);
    editor.value = 'print(2)\n';
    assert.equal(await workbench.save(), true);
    assert.equal(workbench.version, 'v2');
    // The old snapshot (v1, old text) arrives after the save.
    pollResponse.resolve({ version: 'v1', content: 'print(1)\n', status: 'approved' });
    await poll;

    assert.equal(editor.value, 'print(2)\n', 'the saved text is not reverted');
    assert.equal(workbench.baseline, 'print(2)\n');
    assert.equal(workbench.version, 'v2');
    assert.equal(root.el('[data-workbench-banner]').dataset.kind, undefined, 'no banner was ever raised');
    assert.equal(root.el('[data-workbench-hint]').textContent, 'Saved.', 'the stale poll did not touch the hint either');
});

test('A script deleted on disk sends a clean editor back to Automation, and offers re-create when dirty', async () => {
    const notFound = (app) => async (url) => {
        app.calls.push({ url });
        if (url.includes('/content?')) throw new Error("Script 'nightly.py' was not found.");
        return {};
    };
    // The list endpoint agrees the file is gone.
    const gone = (scripts) => { scripts.refresh = async function () { this.refreshes += 1; this.state.scripts = []; }; };

    const { workbench, app, root } = mountedWorkbench();
    app.apiCall = notFound(app);
    gone(app.jobController.pythonScripts);
    await workbench.checkDisk();
    assert.deepEqual(app.navigations, [{ view: 'back' }]);
    assert.equal(app.jobController.pythonScripts.refreshes, 1, 'the list is asked before the file counts as deleted');
    assert.equal(app.toasts.at(-1).title, 'Script removed');
    assert.match(app.toasts.at(-1).message, /deleted from the scripts folder/);
    const cleanBanner = root.el('[data-workbench-banner]');
    assert.equal(cleanBanner.dataset.kind, undefined, 'the clean case never rendered a banner');
    assert.equal(cleanBanner.innerHTML, '');

    const dirty = mountedWorkbench({ app: createApp() });
    dirty.app.apiCall = notFound(dirty.app);
    gone(dirty.app.jobController.pythonScripts);
    dirty.editor.value = 'print("keep me")\n';
    await dirty.workbench.checkDisk();
    assert.equal(dirty.app.navigations.length, 0);
    const banner = dirty.root.el('[data-workbench-banner]');
    assert.equal(banner.hidden, false);
    assert.equal(banner.dataset.kind, 'deleted');
    assert.match(banner.innerHTML, /Your unsaved edits are still here/);
    assert.match(banner.innerHTML, /data-workbench-action="recreate"/);
    assert.match(banner.innerHTML, /data-workbench-action="leave"/);
    assert.equal(dirty.app.toasts.length, 0, 'the banner carries the message; no toast repeats it');
    // The next poll's 404 does not re-render (and re-focus) the banner.
    banner.innerHTML = 'untouched';
    await dirty.workbench.checkDisk();
    assert.equal(banner.innerHTML, 'untouched');
});

test('A 404 from the poll is not "deleted" while the list still knows the script (rename/delete in flight)', async () => {
    const { workbench, app, root } = mountedWorkbench();
    app.apiCall = async (url) => {
        app.calls.push({ url });
        if (url.includes('/content?')) throw new Error("Script 'nightly.py' was not found.");
        return {};
    };
    // The list refresh still returns nightly.py (the shared flow has not applied yet).
    await workbench.checkDisk();
    assert.equal(app.jobController.pythonScripts.refreshes, 1);
    assert.equal(app.navigations.length, 0);
    assert.equal(app.toasts.length, 0);
    assert.equal(root.el('[data-workbench-banner]').dataset.kind, undefined);
    assert.equal(workbench._deletedOnDisk, false);
    // The same holds when a shared flow started while the request was out.
    app.jobController.pythonScripts.refresh = async function () { this.refreshes += 1; workbench._mutating = true; };
    await workbench.checkDisk();
    assert.equal(app.navigations.length, 0);
    workbench._mutating = false;
});

// --- switching scripts in place ---

test('Switching scripts from the rail confirms unsaved changes and keeps the navigation stack honest', async () => {
    const other = { name: 'weekly.py', status: 'unapproved', path: '/scripts/weekly.py', sizeBytes: 10 };
    const { workbench, app, editor } = mountedWorkbench({ scripts: [SCRIPT, other] });
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return { name: 'weekly.py', content: 'print("weekly")\n', status: 'unapproved', version: 'w1' };
    };
    editor.value = 'dirty';
    let asked = 0;
    workbench.confirm = async () => { asked += 1; return false; };

    assert.equal(await workbench.switchTo('weekly.py'), false);
    assert.equal(asked, 1);
    assert.equal(workbench.name, 'nightly.py');
    assert.equal(app.calls.length, 0);

    workbench.confirm = async () => { asked += 1; return true; };
    assert.equal(await workbench.switchTo('weekly.py'), true);
    assert.equal(workbench.name, 'weekly.py');
    assert.equal(workbench.version, 'w1');
    assert.equal(workbench.status, 'unapproved');
    assert.equal(editor.value, 'print("weekly")\n');
    assert.equal(workbench.isDirty, false);
    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=weekly.py');
    assert.deepEqual(app.viewData, [{ name: 'weekly.py' }], 'the stack entry follows the open script');
    // The rail highlights the new script and always ends with New script.
    const rail = workbench.renderRailItems();
    assert.match(rail, /data-workbench-open="weekly\.py"[^>]*aria-current="page"/);
    assert.doesNotMatch(rail, /data-workbench-open="nightly\.py"[^>]*aria-current/);
    assert.match(rail, /data-workbench-action="new"[\s\S]*New script/);

    // Same script → no-op.
    assert.equal(await workbench.switchTo('weekly.py'), false);
});

// --- authoring wrappers (shared flows) ---

test('Rename hands off to the shared flow, holds the poll, and moves the identity without reloading the editor', async () => {
    const { workbench, app, editor, root } = mountedWorkbench();
    const scripts = app.jobController.pythonScripts;
    const renamed = [];
    const taskKeyUpdates = [];
    const agentTab = { state: { taskKey: 'python-script:nightly.py' } };
    app.terminalController = {
        manager: {
            findTabByTaskKey(key) {
                return key === agentTab.state.taskKey ? agentTab : null;
            },
            updateTabMetadata(tab, metadata) {
                taskKeyUpdates.push({ tab, metadata });
                Object.assign(tab.state, metadata);
            }
        }
    };
    editor.value = 'print(1)\nprint("unsaved")\n';
    scripts.rename = async (name) => {
        renamed.push({ name, mutating: workbench._mutating });
        scripts.state.scripts = [{ ...SCRIPT, name: 'weekly.py', status: 'unapproved', path: '/scripts/weekly.py' }];
        return 'weekly.py';
    };

    assert.equal(await workbench.rename(), 'weekly.py');

    assert.deepEqual(renamed, [{ name: 'nightly.py', mutating: true }]);
    assert.equal(workbench._mutating, false);
    assert.equal(workbench.name, 'weekly.py');
    assert.equal(workbench.status, 'unapproved', 'renaming clears the signature');
    assert.equal(workbench.script.path, '/scripts/weekly.py');
    assert.equal(editor.value, 'print(1)\nprint("unsaved")\n', 'edits survive the rename');
    assert.deepEqual(app.viewData, [{ name: 'weekly.py' }]);
    assert.equal(root.el('[data-workbench-name]').textContent, 'weekly.py');
    assert.equal(root.el('[data-workbench-approve-label]').textContent, 'Sign');
    assert.deepEqual(taskKeyUpdates, [{
        tab: agentTab,
        metadata: { taskKey: 'python-script:weekly.py' }
    }], 'the dedicated agent tab follows the renamed script');

    // Cancelled: nothing moves.
    scripts.rename = async () => null;
    assert.equal(await workbench.rename(), null);
    assert.equal(workbench.name, 'weekly.py');
});

test('Delete hands off to the shared flow and leaves without the unsaved-changes guard', async () => {
    const { workbench, app, editor } = mountedWorkbench();
    const scripts = app.jobController.pythonScripts;
    editor.value = 'dirty';
    const deleted = [];
    scripts.deleteScript = async (name) => { deleted.push({ name, mutating: workbench._mutating }); return false; };

    assert.equal(await workbench.deleteScript(), false);
    assert.equal(app.navigations.length, 0, 'a cancelled delete stays put');

    scripts.deleteScript = async (name) => { deleted.push({ name, mutating: workbench._mutating }); return true; };
    assert.equal(await workbench.deleteScript(), true);
    assert.deepEqual(deleted, [{ name: 'nightly.py', mutating: true }, { name: 'nightly.py', mutating: true }]);
    assert.equal(workbench._leaveConfirmed, true, 'the guard stands down: there is no file left to protect');
    assert.deepEqual(app.navigations, [{ view: 'back' }]);
    assert.equal(workbench._mutating, false);
});

test('Run goes through the shared interactive-terminal flow only when signed', async () => {
    // The drawer's summary/body are looked up from the <details> element itself.
    const details = fakeElement();
    const root = fakeRoot({ '[data-workbench-output]': details });
    details.querySelector = (selector) => root.el(selector);
    const { workbench, app } = mountedWorkbench({ root });
    const scripts = app.jobController.pythonScripts;
    const runs = [];
    scripts.run = async (name, button) => {
        runs.push({ name, button, running: workbench._running });
        return { name, tabId: 'python-tab', message: 'started' };
    };

    workbench.status = 'unapproved';
    assert.equal(await workbench.run(), null);
    assert.equal(runs.length, 0);
    assert.equal(app.toasts.at(-1).title, 'Not signed');

    workbench.status = 'approved';
    workbench.editor.value = 'print("unsaved")\n';
    assert.equal(await workbench.run(), null);
    assert.equal(runs.length, 0);
    assert.equal(app.toasts.at(-1).title, 'Unsaved changes');

    workbench.editor.value = workbench.baseline;
    const button = fakeElement();
    const result = await workbench.run(button);
    assert.deepEqual(runs, [{ name: 'nightly.py', button, running: true }]);
    assert.equal(result.tabId, 'python-tab');
    assert.equal(workbench._running, false);
    assert.equal(workbench.lastRun, null);
});

test('Re-create writes the editor text back through the shared create flow and clears the deleted state', async () => {
    const { workbench, app, editor, root } = mountedWorkbench();
    const scripts = app.jobController.pythonScripts;
    editor.value = 'print("keep me")\n';
    workbench._deletedOnDisk = true;
    workbench._showBanner({ kind: 'deleted' });
    const created = [];
    scripts.createScript = async (name, content) => { created.push({ name, content, mutating: workbench._mutating }); };
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return { name: 'nightly.py', content: 'print("keep me")\n', status: 'unapproved', version: 'v9' };
    };

    assert.equal(await workbench.recreateFromEditor(), true);

    assert.deepEqual(created, [{ name: 'nightly.py', content: 'print("keep me")\n', mutating: true }]);
    assert.equal(workbench._mutating, false);
    assert.equal(workbench._deletedOnDisk, false);
    assert.equal(workbench.baseline, 'print("keep me")\n');
    assert.equal(workbench.isDirty, false);
    assert.equal(workbench.version, 'v9');
    assert.equal(workbench.status, 'unapproved');
    assert.equal(root.el('[data-workbench-banner]').hidden, true);
    assert.equal(app.toasts.at(-1).title, 'Script re-created');

    // A refused create is an error, and the deleted state stays.
    workbench._deletedOnDisk = true;
    scripts.createScript = async () => { throw new Error('Name is taken.'); };
    assert.equal(await workbench.recreateFromEditor(), false);
    assert.deepEqual(app.errors, ['Name is taken.']);
    assert.equal(workbench._deletedOnDisk, true);
});

test('Re-create leaves edits typed while its request is in flight unsaved', async () => {
    const { workbench, app, editor } = mountedWorkbench();
    const scripts = app.jobController.pythonScripts;
    const create = deferred();
    const created = [];
    editor.value = 'print("submitted")\n';
    workbench._deletedOnDisk = true;
    scripts.createScript = async (name, content) => {
        created.push({ name, content });
        await create.promise;
    };
    app.apiCall = async () => ({
        name: 'nightly.py',
        content: 'print("submitted")\n',
        status: 'unapproved',
        version: 'v10'
    });

    const recreating = workbench.recreateFromEditor();
    editor.value = 'print("typed later")\n';
    create.resolve();

    assert.equal(await recreating, true);
    assert.deepEqual(created, [{ name: 'nightly.py', content: 'print("submitted")\n' }]);
    assert.equal(workbench.baseline, 'print("submitted")\n');
    assert.equal(editor.value, 'print("typed later")\n');
    assert.equal(workbench.isDirty, true);
});

test('Reload from disk confirms over unsaved edits, then adopts the disk copy and says so in the hint', async () => {
    const { workbench, app, editor, root } = mountedWorkbench();
    editor.value = 'print("mine")\n';
    let asked = 0;
    workbench.confirm = async () => { asked += 1; return false; };
    assert.equal(await workbench.reloadFromDisk(), false);
    assert.equal(asked, 1);
    assert.equal(app.calls.length, 0);
    assert.equal(editor.value, 'print("mine")\n');

    workbench.confirm = async () => { asked += 1; return true; };
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return { name: 'nightly.py', content: 'print(7)\n', status: 'modified', version: 'v7' };
    };
    assert.equal(await workbench.reloadFromDisk(), true);
    assert.equal(asked, 2);
    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=nightly.py');
    assert.equal(editor.value, 'print(7)\n');
    assert.equal(workbench.baseline, 'print(7)\n');
    assert.equal(workbench.version, 'v7');
    assert.equal(workbench.status, 'modified');
    assert.match(root.el('[data-workbench-hint]').textContent, /^Reloaded from disk · /);
    assert.equal(app.jobController.pythonScripts.refreshes, 1);

    // A clean editor reloads without asking.
    assert.equal(await workbench.reloadFromDisk(), true);
    assert.equal(asked, 2);
});

// --- navigation guard ---

test('The navigation guard blocks while dirty and replays the navigation after a confirmed leave', async () => {
    const { workbench } = mountedWorkbench();
    workbench.editor.value = 'dirty';
    let retried = 0;
    let duringRetry = null;
    const retry = () => {
        retried += 1;
        // The replayed navigation consults the guard again: it must pass this time.
        duringRetry = workbench._guardNavigation({ from: 'python-script', to: 'jobs', retry: () => {} });
    };

    // Clean editor or another view: never blocks.
    assert.equal(workbench._guardNavigation({ from: 'jobs', to: 'settings', retry }), true);
    workbench.editor.value = workbench.baseline;
    assert.equal(workbench._guardNavigation({ from: 'python-script', to: 'jobs', retry }), true);
    workbench.editor.value = 'dirty';

    // Declined: blocked, no replay.
    workbench.confirm = async () => false;
    assert.equal(workbench._guardNavigation({ from: 'python-script', to: 'jobs', retry }), false);
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(retried, 0);

    // Confirmed: blocked now, replayed once the dialog resolves, then the flag is reset.
    workbench.confirm = async () => true;
    assert.equal(workbench._guardNavigation({ from: 'python-script', to: 'jobs', retry }), false);
    await new Promise((resolve) => setTimeout(resolve, 0));
    assert.equal(retried, 1);
    assert.equal(duringRetry, true);
    assert.equal(workbench._leaveConfirmed, false);
    assert.equal(workbench._guardNavigation({ from: 'python-script', to: 'jobs', retry: () => {} }), false, 'still dirty afterwards');
});

// --- splitter ---

function withStorage(t) {
    const stored = new Map();
    const originalStorage = globalThis.localStorage;
    globalThis.localStorage = {
        getItem: (key) => (stored.has(key) ? stored.get(key) : null),
        setItem: (key, value) => stored.set(key, value)
    };
    t.after(() => {
        if (originalStorage === undefined) delete globalThis.localStorage;
        else globalThis.localStorage = originalStorage;
    });
    return stored;
}

test('The terminal split clamps to its floor and persists in localStorage', (t) => {
    const stored = withStorage(t);

    const splitter = fakeElement();
    const panes = fakeElement({ height: 900 });
    const root = fakeRoot({ '[data-workbench-splitter]': splitter, '[data-workbench-panes]': panes });
    const { workbench } = mountedWorkbench({ root });

    assert.equal(workbench.setTerminalHeight(320), 320);
    assert.equal(root.style.props['--python-workbench-terminal-height'], '320px');
    assert.equal(stored.get(TERMINAL_HEIGHT_STORAGE_KEY), '320');
    assert.equal(splitter.getAttribute('aria-valuenow'), '320');
    assert.equal(splitter.getAttribute('aria-valuemin'), String(TERMINAL_MIN_HEIGHT));
    assert.equal(splitter.getAttribute('aria-valuemax'), String(900 - EDITOR_MIN_HEIGHT));

    // Floor: the terminal never drops below what you can drive an agent in.
    assert.equal(workbench.setTerminalHeight(80), TERMINAL_MIN_HEIGHT);
    assert.equal(stored.get(TERMINAL_HEIGHT_STORAGE_KEY), String(TERMINAL_MIN_HEIGHT));
    // Ceiling: leave the editor its minimum inside the measured panes.
    assert.equal(workbench.setTerminalHeight(5000), 900 - EDITOR_MIN_HEIGHT);
    // Drag frames do not persist; pointerup does.
    workbench.setTerminalHeight(400, { persist: false });
    assert.equal(stored.get(TERMINAL_HEIGHT_STORAGE_KEY), String(900 - EDITOR_MIN_HEIGHT));

    // A fresh mount picks the stored value back up.
    stored.set(TERMINAL_HEIGHT_STORAGE_KEY, '333');
    const again = mountedWorkbench({ root: fakeRoot() });
    again.workbench._applyStoredTerminalHeight();
    assert.equal(again.root.style.props['--python-workbench-terminal-height'], '333px');

    assert.equal(clampTerminalHeight(1000, 500), 500);
    assert.equal(clampTerminalHeight('abc'), TERMINAL_MIN_HEIGHT);
    assert.equal(clampTerminalHeight(300, 100), TERMINAL_MIN_HEIGHT, 'a ceiling below the floor yields the floor');
    assert.equal(readStoredTerminalHeight({ getItem: () => 'abc' }), null);
    assert.equal(readStoredTerminalHeight(undefined), null);
    assert.equal(SPLITTER_KEY_STEP, 24);
});

test('Short viewports lower the terminal floor (JS and CSS agree on threshold and value)', (t) => {
    const previous = globalThis.innerHeight;
    t.after(() => {
        if (previous === undefined) delete globalThis.innerHeight;
        else globalThis.innerHeight = previous;
    });

    delete globalThis.innerHeight;
    assert.equal(terminalMinHeight(), TERMINAL_MIN_HEIGHT, 'no viewport (tests) → the desktop floor');
    globalThis.innerHeight = 900;
    assert.equal(terminalMinHeight(), TERMINAL_MIN_HEIGHT);
    globalThis.innerHeight = COMPACT_VIEWPORT_MAX_HEIGHT;
    assert.equal(terminalMinHeight(), TERMINAL_MIN_HEIGHT_COMPACT, 'the threshold itself is compact');
    globalThis.innerHeight = 640;
    assert.equal(terminalMinHeight(), TERMINAL_MIN_HEIGHT_COMPACT);
    assert.equal(clampTerminalHeight(80), TERMINAL_MIN_HEIGHT_COMPACT);
    assert.equal(clampTerminalHeight('abc'), TERMINAL_MIN_HEIGHT_COMPACT);
    assert.equal(clampTerminalHeight(300, 100), TERMINAL_MIN_HEIGHT_COMPACT);

    const splitter = fakeElement();
    const panes = fakeElement({ height: 600 });
    const root = fakeRoot({ '[data-workbench-splitter]': splitter, '[data-workbench-panes]': panes });
    const { workbench } = mountedWorkbench({ root });
    assert.equal(workbench.setTerminalHeight(80, { persist: false }), TERMINAL_MIN_HEIGHT_COMPACT);
    assert.equal(splitter.getAttribute('aria-valuemin'), String(TERMINAL_MIN_HEIGHT_COMPACT));
    assert.equal(splitter.getAttribute('aria-valuenow'), String(TERMINAL_MIN_HEIGHT_COMPACT));
    assert.match(workbench.renderShell('x.py'), new RegExp(`aria-valuemin="${TERMINAL_MIN_HEIGHT_COMPACT}"`));

    // The CSS media query mirrors the constants exactly.
    const css = readFileSync(stylePath, 'utf8');
    const block = css.slice(css.indexOf("Python script workbench (view 'python-script')"));
    // Scoped to stacked widths: side by side the terminal column is the full row height.
    const media = block.match(/@media \(max-height: (\d+)px\) and \(max-width: 1179\.98px\) \{([\s\S]*?)\n\}/);
    assert.ok(media, 'the workbench block has a max-height media query scoped below the side-by-side threshold');
    assert.equal(Number(media[1]), COMPACT_VIEWPORT_MAX_HEIGHT);
    assert.match(media[2], /\.python-workbench \{\s*--python-workbench-terminal-height: clamp\(180px, 30dvh, 34dvh\);/);
    assert.match(media[2], new RegExp(`\\.python-workbench \\.python-workbench-terminal \\{\\s*min-height: ${TERMINAL_MIN_HEIGHT_COMPACT}px;`));
});

test('Dragging the splitter resizes live and persists only on release; the separator reports its range after mount', (t) => {
    const stored = withStorage(t);
    const splitter = fakeElement({ height: 12 });
    const panes = fakeElement({ height: 900 });
    const terminal = fakeElement({ height: 300 });
    const root = fakeRoot({
        '[data-workbench-splitter]': splitter,
        '[data-workbench-panes]': panes,
        '[data-terminal-section]': terminal
    });
    const { workbench, app } = mountedWorkbench({ root });
    let layouts = 0;
    app.terminalController = { refreshLayout() { layouts += 1; } };
    workbench._bindSplitter();

    // After mount the separator knows where it is even without a stored height.
    workbench._syncSplitterAria();
    assert.equal(splitter.getAttribute('aria-valuenow'), '300');
    assert.equal(splitter.getAttribute('aria-valuemin'), String(TERMINAL_MIN_HEIGHT));
    assert.equal(splitter.getAttribute('aria-valuemax'), String(900 - EDITOR_MIN_HEIGHT - 12));
    assert.equal(root.style.props['--python-workbench-terminal-height'], undefined, 'the CSS default stays fluid');

    let prevented = 0;
    splitter.dispatch('pointerdown', { pointerType: 'mouse', button: 0, pointerId: 7, clientY: 500, preventDefault: () => { prevented += 1; } });
    assert.equal(prevented, 1);
    assert.equal(splitter.captured, true);
    assert.ok(root.classList.contains('python-workbench-resizing'));
    assert.equal((splitter.listeners.pointermove || []).length, 1);

    // The terminal sits below the handle: dragging DOWN 40px shrinks it 300 → 260.
    splitter.dispatch('pointermove', { pointerId: 7, clientY: 540 });
    assert.equal(root.style.props['--python-workbench-terminal-height'], '260px');
    assert.equal(splitter.getAttribute('aria-valuenow'), '260');
    assert.equal(stored.has(TERMINAL_HEIGHT_STORAGE_KEY), false, 'drag frames are not persisted');
    assert.equal(layouts, 1, 'the terminal re-fits while dragging');
    // Another pointer's moves are ignored.
    splitter.dispatch('pointermove', { pointerId: 8, clientY: 900 });
    assert.equal(root.style.props['--python-workbench-terminal-height'], '260px');
    // Dragging up grows it, clamped to the editor's minimum.
    splitter.dispatch('pointermove', { pointerId: 7, clientY: -5000 });
    assert.equal(root.style.props['--python-workbench-terminal-height'], `${900 - EDITOR_MIN_HEIGHT - 12}px`);

    splitter.dispatch('pointerup', { pointerId: 7 });
    assert.equal(stored.get(TERMINAL_HEIGHT_STORAGE_KEY), String(900 - EDITOR_MIN_HEIGHT - 12));
    assert.equal(splitter.captured, false);
    assert.equal(root.classList.contains('python-workbench-resizing'), false);
    assert.equal((splitter.listeners.pointermove || []).length, 0, 'drag listeners are unbound on release');
    assert.equal((splitter.listeners.pointerup || []).length, 0);
    assert.equal((splitter.listeners.pointercancel || []).length, 0);
    assert.ok(layouts >= 2, 'a final layout pass follows the release');

    // A right-click does not start a drag; the keyboard nudges by SPLITTER_KEY_STEP.
    splitter.dispatch('pointerdown', { pointerType: 'mouse', button: 2, pointerId: 9, clientY: 0, preventDefault: () => { prevented += 1; } });
    assert.equal(prevented, 1);
    splitter.dispatch('keydown', { key: 'ArrowDown', preventDefault: () => { prevented += 1; } });
    assert.equal(prevented, 2);
    assert.equal(root.style.props['--python-workbench-terminal-height'], `${300 - SPLITTER_KEY_STEP}px`);
    assert.equal(stored.get(TERMINAL_HEIGHT_STORAGE_KEY), String(300 - SPLITTER_KEY_STEP));
});

// --- shell markup ---

test('The shell renders a Back bar, the identity pill, the editor card with a rail, a splitter and the docked terminal', () => {
    const { workbench } = mountedWorkbench();
    const html = workbench.renderShell('night <b>ly</b>.py');

    assert.match(html, /data-view="python-script"/);
    assert.match(html, /data-action="go-back"[^>]*title="Back to Automation"/);
    assert.match(html, /fa-brands fa-python/);
    assert.match(html, /night &lt;b&gt;ly&lt;\/b&gt;\.py/, 'names are escaped');
    assert.match(html, /class="python-script-status" data-tone="neutral" data-workbench-status/);
    for (const action of ['run', 'approve', 'menu', 'ask-agent', 'save', 'save-sign']) {
        assert.match(html, new RegExp(`data-workbench-action="${action}"`), `missing ${action}`);
    }
    assert.match(html, /<section class="rules-section card python-workbench-editor-card"/);
    assert.match(html, /<nav class="python-workbench-rail" aria-label="Scripts"/);
    assert.match(html, /role="list" data-workbench-rail-list/);
    assert.match(html, /data-workbench-editor-mount/);
    // No matchMedia in node → stacked layout → a horizontal separator.
    assert.match(html, /role="separator"\s+aria-orientation="horizontal"[^>]*tabindex="0"/);
    assert.match(html, /<div class="rules-pane rules-pane-terminal python-workbench-terminal" data-terminal-section[^>]*>\s*<div class="rules-terminal-host" data-terminal-content>/);
    // Run starts disabled and gets enabled by the identity render.
    assert.match(html, /data-workbench-action="run" disabled/);
    // The dirty mark is announced as an image, not read as a stray bullet.
    assert.match(html, /data-workbench-dirty hidden role="img" title="Unsaved changes" aria-label="Unsaved changes"/);
    // One primary per screen (Run); Save starts disabled until something changed.
    assert.match(html, /class="btn btn-sm btn-primary" type="button" data-workbench-action="run"/);
    assert.match(html, /class="btn btn-sm btn-outline-secondary" type="button" data-workbench-action="save"\s+title="Save \(Ctrl\/⌘\+S\)" disabled/);
    assert.match(html, /class="btn btn-sm btn-outline-primary" type="button" data-workbench-action="save-sign"/);
    assert.equal(html.match(/btn-primary(?![-\w])/g).length, 1, 'exactly one btn-primary in the shell');
    // Signing tooltips say sign (the pill/label vocabulary), never approve.
    assert.match(html, /data-workbench-action="approve"\s+title="Sign the saved file with your PIN"/);
    assert.match(html, /data-workbench-action="save-sign"\s+title="Save, then sign this exact version with your PIN"/);
    assert.doesNotMatch(html, /[Aa]pprove the|approve this/);
});

test('The identity render enables Run only when signed and the kebab adapts to host and status', () => {
    const { workbench, app, root } = mountedWorkbench();
    workbench._renderIdentity();
    assert.equal(root.el('[data-workbench-name]').textContent, 'nightly.py');
    assert.equal(root.el('[data-workbench-status]').dataset.tone, 'success');
    assert.match(root.el('[data-workbench-status]').innerHTML, /Signed/);
    assert.match(root.el('[data-workbench-meta]').textContent, /\/scripts\/nightly\.py · 2\.00 KB · edited /);
    assert.equal(root.el('[data-workbench-action="run"]').disabled, false);
    assert.equal(root.el('[data-workbench-action="run"]').title, 'Run nightly.py now');
    assert.equal(root.el('[data-workbench-approve-label]').textContent, 'Re-sign');
    assert.match(root.el('[data-workbench-menu]').innerHTML, /data-workbench-action="revoke"/);
    assert.doesNotMatch(root.el('[data-workbench-menu]').innerHTML, /Open in VS Code/);

    workbench.editor.value = 'print("unsaved")\n';
    workbench._renderIdentity();
    assert.equal(root.el('[data-workbench-action="run"]').disabled, true);
    assert.equal(root.el('[data-workbench-action="run"]').title, 'Save and sign your changes before running');
    workbench.editor.value = workbench.baseline;

    // 'modified' still carries a (stale) signature: Run is off, but signing again is a re-sign.
    workbench.status = 'modified';
    workbench._renderIdentity();
    assert.equal(root.el('[data-workbench-action="run"]').disabled, true);
    assert.equal(root.el('[data-workbench-approve-label]').textContent, 'Re-sign');
    assert.match(root.el('[data-workbench-menu]').innerHTML, /data-workbench-action="revoke"/);

    workbench.status = 'unapproved';
    app.jobController.pythonScripts.canOpenInVsCode = () => true;
    workbench._renderIdentity();
    assert.equal(root.el('[data-workbench-action="run"]').disabled, true);
    assert.equal(root.el('[data-workbench-approve-label]').textContent, 'Sign');
    assert.doesNotMatch(root.el('[data-workbench-menu]').innerHTML, /data-workbench-action="revoke"/);
    assert.match(root.el('[data-workbench-menu]').innerHTML, /data-workbench-action="open-vscode"[\s\S]*Open in VS Code/);
    assert.match(root.el('[data-workbench-menu]').innerHTML, /python-script-menu-item-danger[^>]*data-workbench-action="delete"/);
});

test('The hint line, dirty mark and Save button follow the editor state', () => {
    const { workbench, root, editor } = mountedWorkbench();
    const hint = root.el('[data-workbench-hint]');
    const save = root.el('[data-workbench-action="save"]');
    const mark = root.el('[data-workbench-dirty]');
    const run = root.el('[data-workbench-action="run"]');

    workbench._renderHint();
    assert.equal(hint.textContent, 'Signed. Saving an edit clears the signature until you sign again.');
    assert.equal(save.disabled, true);
    assert.equal(mark.hidden, true);
    assert.equal(run.disabled, false);

    workbench.status = 'unapproved';
    workbench._renderHint();
    assert.equal(hint.textContent, 'Saving does not sign the script — sign it before it can run.');

    editor.value = 'changed';
    workbench._renderHint();
    assert.equal(hint.textContent, 'Unsaved changes · Ctrl/⌘+S saves');
    assert.equal(save.disabled, false);
    assert.equal(mark.hidden, false);
    assert.equal(run.disabled, true);
    assert.equal(run.title, 'Save and sign your changes before running');

    // An explicit message wins while it is shown, and sticks across re-renders once clean.
    workbench._renderHint('Saving…');
    assert.equal(hint.textContent, 'Saving…');
    editor.value = workbench.baseline;
    workbench._renderHint('Saved.');
    workbench._renderHint();
    assert.equal(hint.textContent, 'Saved.');
    assert.equal(save.disabled, true);
    assert.equal(run.disabled, true, 'an unsigned clean script still cannot run');

    workbench.status = 'approved';
    workbench._renderHint();
    assert.equal(run.disabled, false);
});

test('The terminal mounts with the scripts directory as its working folder', async () => {
    const { workbench, app, root } = mountedWorkbench();
    const rendered = [];
    const bound = [];
    app.terminalController = {
        renderTerminalPanel(options) { rendered.push(options); return '<div id="vb-terminal-panel"></div>'; },
        async bindTerminalActions(host, preselected, options) { bound.push({ host, preselected, options }); }
    };

    await workbench._mountTerminal();

    const host = root.el('[data-terminal-content]');
    assert.deepEqual(rendered, [{ workingDirectory: '/scripts' }]);
    assert.equal(host.innerHTML, '<div id="vb-terminal-panel"></div>');
    assert.deepEqual(bound, [{ host, preselected: null, options: { defaultWorkingDirectory: '/scripts' } }]);

    // And the manager honours it (source pins for the two tiny terminal-multitab changes).
    const terminal = readFileSync(terminalPath, 'utf8');
    assert.match(terminal, /getDefaultWorkingDirectory\(\) \{[\s\S]*?cleanString\(this\.options\?\.defaultWorkingDirectory\)\s*\|\| cleanString\(this\.app\.data\.configs\?\.rootPath\)/);
    assert.match(terminal, /const rootPath = cleanString\(options\.workingDirectory\) \|\| this\.app\.data\.configs\?\.rootPath \|\| '';/);
});

test('Escape closes an open kebab menu, or hands a toolbar button\'s focus back to the editor; Ctrl+S saves outside Monaco and the terminal', () => {
    const { workbench, root, editor } = mountedWorkbench();
    const menu = root.el('[data-workbench-menu]');
    menu.hidden = false;
    let prevented = 0;
    const escape = { key: 'Escape', preventDefault: () => { prevented += 1; }, target: { closest: () => null } };
    workbench._handleKeydown(escape);
    assert.equal(menu.hidden, true);
    assert.equal(prevented, 1);
    workbench._handleKeydown(escape);
    assert.equal(prevented, 1, 'with no menu open and nothing focused Escape is left to app.js');
    assert.equal(editor.focusCalls, 0);

    // From a focused toolbar button Escape must never mean "leave": it is consumed (app.js
    // stands down on defaultPrevented) and focus returns to the code.
    const inside = (...parts) => ({ closest: (selector) => (parts.some((part) => selector.includes(part)) ? {} : null) });
    workbench._handleKeydown({ key: 'Escape', preventDefault: () => { prevented += 1; }, target: inside('button') });
    assert.equal(prevented, 2);
    assert.equal(editor.focusCalls, 1);
    // The keyboard-focusable splitter behaves the same way.
    workbench._handleKeydown({ key: 'Escape', preventDefault: () => { prevented += 1; }, target: inside('[data-workbench-splitter]') });
    assert.equal(prevented, 3);
    assert.equal(editor.focusCalls, 2);
    // …but buttons inside Monaco or the terminal belong to those widgets.
    workbench._handleKeydown({ key: 'Escape', preventDefault: () => { prevented += 1; }, target: inside('button', '.xterm') });
    workbench._handleKeydown({ key: 'Escape', preventDefault: () => { prevented += 1; }, target: inside('button', '.monaco-editor') });
    assert.equal(prevented, 3);
    assert.equal(editor.focusCalls, 2);
    // A menu open + a button focused: closing the menu wins (focus goes to the toggle).
    menu.hidden = false;
    workbench._handleKeydown({ key: 'Escape', preventDefault: () => { prevented += 1; }, target: inside('button') });
    assert.equal(menu.hidden, true);
    assert.equal(prevented, 4);
    assert.equal(editor.focusCalls, 2);

    const saves = [];
    workbench.save = async () => { saves.push(1); return true; };
    const ctrlS = (part) => ({
        key: 's', ctrlKey: true, metaKey: false, altKey: false,
        preventDefault: () => { prevented += 1; },
        target: { closest: (selector) => (part && selector.includes(part) ? {} : null) }
    });
    workbench._handleKeydown(ctrlS('.xterm'));
    workbench._handleKeydown(ctrlS('.monaco-editor'));
    assert.equal(saves.length, 0, 'the terminal and Monaco own their own Ctrl+S');
    workbench._handleKeydown(ctrlS(null));
    assert.equal(saves.length, 1);
    assert.equal(prevented, 5);
});

test('unload is safe before any mount and stops everything after one', () => {
    const app = createApp();
    const idle = new PythonScriptWorkbench(app);
    idle.unload();
    assert.equal(idle.root, null);

    const { workbench, editor } = mountedWorkbench();
    let removed = 0;
    workbench._removeNavigationGuard = () => { removed += 1; };
    workbench._unsubscribeState = () => { removed += 1; };
    workbench._pollTimer = setInterval(() => {}, 100000);
    workbench._editorMounting = Promise.resolve(true);
    workbench._mutating = true;
    workbench._hintNotice = 'Saved.';
    workbench.unload();
    assert.equal(editor.disposed, true);
    assert.equal(workbench.editor, null);
    assert.equal(workbench._pollTimer, null);
    assert.equal(workbench._editorMounting, null);
    assert.equal(workbench._mutating, false);
    assert.equal(workbench._hintNotice, '');
    assert.equal(removed, 2);
    assert.equal(workbench.isDirty, false);
});

// --- CSS ---

test('The workbench styles fill the viewport without overlap and carry fallbacks', () => {
    const css = readFileSync(stylePath, 'utf8');
    const marker = css.indexOf("Python script workbench (view 'python-script')");
    assert.ok(marker > css.indexOf('Environment Steps editor'), 'the section is appended after the fallback marker');
    assert.ok(marker > css.indexOf('Python scripts (Automation page section'), 'the section is appended at the very end');
    const block = css.slice(marker);

    // Editor host: flex child with min-height 0 inside a flex column, Monaco filling it absolutely.
    assert.match(block, /\.python-workbench-body \{[^}]*display: flex;[^}]*min-height: 0;/);
    assert.match(block, /\.python-workbench-editor-host \{[^}]*position: relative;[^}]*flex: 1 1 0;[^}]*min-height: 0;/);
    assert.match(block, /\.python-workbench-editor-mount \{[^}]*position: absolute;[^}]*inset: 0;/);
    assert.match(block, /\.python-workbench-editor-card \{[^}]*min-height: 260px;/);
    // Terminal pane: fixed to the splitter's custom property, never under 240px on a desktop viewport.
    assert.match(block, /\.python-workbench \.python-workbench-terminal \{[^}]*flex: 0 0 auto;[^}]*height: var\(--python-workbench-terminal-height, 34dvh\);[^}]*min-height: 240px;/);
    assert.match(block, /--python-workbench-terminal-height: max\(240px, 34dvh\);/);
    // Short viewports: the floor and the default share both drop (see the JS mirror test).
    assert.match(block, /@media \(max-height: 720px\) and \(max-width: 1179\.98px\) \{[\s\S]*?\.python-workbench \.python-workbench-terminal \{\s*min-height: 180px;/);
    // Wide windows: editor | splitter | terminal as grid columns, the terminal column width
    // on its own custom property, and a working height that lets the page scroll instead
    // of squeezing the panes (Rob: "the terminal is too small… side by side… let it scroll").
    const wide = block.slice(block.indexOf('@media (min-width: 1180px)'));
    assert.ok(wide.length > 0, 'the workbench block has a side-by-side media query');
    assert.match(wide, /\.python-workbench-panes \{[^}]*display: grid;[^}]*grid-template-columns: minmax\(0, 1fr\) 12px var\(--python-workbench-terminal-width, 46%\);[^}]*min-height: clamp\(560px, calc\(100dvh - 168px\), 1400px\);/);
    assert.match(wide, /\.python-workbench-splitter \{[^}]*width: 12px;[^}]*height: auto;[^}]*cursor: col-resize;/);
    assert.match(wide, /\.python-workbench \.python-workbench-terminal \{[^}]*height: auto;[^}]*min-height: 0;/);
    assert.match(block, /--python-workbench-terminal-width: 46%;/);
    // Splitter affordance + reduced motion.
    assert.match(block, /\.python-workbench-splitter \{[^}]*cursor: row-resize;[^}]*touch-action: none;/);
    assert.match(block, /@media \(prefers-reduced-motion: reduce\) \{\s*\.python-workbench-splitter::before \{\s*transition: none;/);
    // The rail collapses into a chip strip on narrow layouts (CSS only).
    const narrow = block.slice(block.indexOf('@media (max-width: 900px)'));
    assert.match(narrow, /\.python-workbench-body \{\s*flex-direction: column;/);
    assert.match(narrow, /\.python-workbench-rail-list \{[^}]*position: static;[^}]*flex-direction: row;[^}]*overflow-x: auto;/);
    // Desktop: the list is absolutely filled so a long rail scrolls instead of growing the page.
    assert.match(block, /\.python-workbench-rail \{[^}]*position: relative;/);
    assert.match(block, /\.python-workbench-rail-list \{[^}]*position: absolute;[^}]*inset: 0;[^}]*overflow-y: auto;/);

    for (const match of block.matchAll(/var\((--color-[a-z-]+)([^)]*)\)/g)) {
        assert.ok(match[2].includes(','), `${match[1]} is used without a fallback: ${match[0]}`);
    }
});

test('The wwwroot AGENTS.md documents the workbench view', () => {
    const doc = readFileSync(agentsMdPath, 'utf8');
    assert.match(doc, /## Python script workbench/);
    assert.match(doc, /python-script-workbench\.js/);
    assert.match(doc, /viberails\.pythonWorkbench\.terminalHeight/);
    assert.match(doc, /scripts directory/);
});

// --- side-by-side layout (wide windows) ---

function withMatchMedia(t, { matches }) {
    const original = globalThis.matchMedia;
    const lists = [];
    globalThis.matchMedia = (query) => {
        const list = { query, matches, listeners: [], addEventListener(type, fn) { this.listeners.push({ type, fn }); }, removeEventListener(type, fn) { this.listeners = this.listeners.filter((l) => l.fn !== fn); } };
        lists.push(list);
        return list;
    };
    t.after(() => {
        if (original === undefined) delete globalThis.matchMedia;
        else globalThis.matchMedia = original;
    });
    return lists;
}

test('Side-by-side detection follows the CSS threshold and never throws without matchMedia', (t) => {
    assert.equal(isSideBySideLayout(), false, 'no matchMedia (node) → stacked');
    const lists = withMatchMedia(t, { matches: true });
    assert.equal(isSideBySideLayout(), true);
    assert.equal(lists[0].query, `(min-width: ${SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH}px)`);
    assert.equal(SIDE_BY_SIDE_MIN_VIEWPORT_WIDTH, 1180);
    // The JS threshold and the CSS media query are the same number.
    const css = readFileSync(stylePath, 'utf8');
    assert.match(css.slice(css.indexOf("Python script workbench (view 'python-script')")), /@media \(min-width: 1180px\)/);
});

test('The terminal column is clamped between its floor and what leaves the editor its minimum width', () => {
    assert.equal(clampTerminalWidth(100), TERMINAL_MIN_WIDTH);
    assert.equal(clampTerminalWidth('abc'), TERMINAL_MIN_WIDTH);
    assert.equal(clampTerminalWidth(700), 700);
    assert.equal(clampTerminalWidth(900, 800), 800);
    assert.equal(clampTerminalWidth(900, 100), TERMINAL_MIN_WIDTH, 'a ceiling below the floor still honours the floor');
    assert.equal(readStoredTerminalWidth({ getItem: () => '520' }), 520);
    assert.equal(readStoredTerminalWidth({ getItem: () => 'nope' }), null);
    assert.equal(readStoredTerminalWidth({ getItem: () => { throw new Error('denied'); } }), null);
    assert.equal(TERMINAL_WIDTH_STORAGE_KEY, 'viberails.pythonWorkbench.terminalWidth');
});

test('Side by side, the splitter moves the terminal WIDTH: dragging right shrinks it, persists on release, Left/Right keys step it', (t) => {
    const stored = withStorage(t);
    withMatchMedia(t, { matches: true });
    const splitter = fakeElement({ width: 12 });
    const panes = fakeElement({ width: 1600 });
    const terminal = fakeElement({ width: 600 });
    const root = fakeRoot({
        '[data-workbench-splitter]': splitter,
        '[data-workbench-panes]': panes,
        '[data-terminal-section]': terminal
    });
    const { workbench, app } = mountedWorkbench({ root });
    let layouts = 0;
    app.terminalController = { refreshLayout() { layouts += 1; } };
    workbench._bindSplitter();

    const down = { pointerType: 'mouse', button: 0, pointerId: 7, clientX: 1000, clientY: 500, preventDefault() {} };
    splitter.dispatch('pointerdown', down);
    // Drag 100px to the right → the right-hand terminal column loses 100px.
    splitter.dispatch('pointermove', { pointerId: 7, clientX: 1100, clientY: 500 });
    assert.equal(root.style.props['--python-workbench-terminal-width'], '500px');
    assert.equal(root.style.props['--python-workbench-terminal-height'], undefined, 'the height axis is untouched');
    assert.equal(stored.has(TERMINAL_WIDTH_STORAGE_KEY), false, 'not persisted mid-drag');
    // Cannot push the editor under its minimum: 1600 - 440 - 12 = 1148 ceiling.
    splitter.dispatch('pointermove', { pointerId: 7, clientX: 100, clientY: 500 });
    assert.equal(root.style.props['--python-workbench-terminal-width'], `${1600 - EDITOR_MIN_WIDTH - 12}px`);
    splitter.dispatch('pointerup', { pointerId: 7 });
    assert.equal(stored.get(TERMINAL_WIDTH_STORAGE_KEY), String(1600 - EDITOR_MIN_WIDTH - 12));
    assert.equal(splitter.getAttribute('aria-orientation'), 'vertical');
    assert.equal(splitter.getAttribute('aria-valuemin'), String(TERMINAL_MIN_WIDTH));
    assert.ok(layouts >= 1, 'the terminal is refitted');

    // Keyboard: Left grows the column, Right shrinks it; Up/Down do nothing here.
    terminal.getBoundingClientRect = () => ({ width: 700, height: 0 });
    splitter.dispatch('keydown', { key: 'ArrowLeft', preventDefault() {} });
    assert.equal(root.style.props['--python-workbench-terminal-width'], `${700 + SPLITTER_KEY_STEP}px`);
    splitter.dispatch('keydown', { key: 'ArrowRight', preventDefault() {} });
    assert.equal(root.style.props['--python-workbench-terminal-width'], `${700 - SPLITTER_KEY_STEP}px`);
    const before = root.style.props['--python-workbench-terminal-width'];
    splitter.dispatch('keydown', { key: 'ArrowUp', preventDefault() {} });
    assert.equal(root.style.props['--python-workbench-terminal-width'], before);
    assert.equal(root.style.props['--python-workbench-terminal-height'], undefined);
});

test('A stored terminal width is applied on mount and the media list is watched then released', (t) => {
    const stored = withStorage(t);
    stored.set(TERMINAL_WIDTH_STORAGE_KEY, '520');
    const lists = withMatchMedia(t, { matches: true });
    const splitter = fakeElement({ width: 12 });
    const root = fakeRoot({ '[data-workbench-splitter]': splitter });
    const { workbench, app } = mountedWorkbench({ root });
    let layouts = 0;
    app.terminalController = { refreshLayout() { layouts += 1; } };

    workbench._applyStoredTerminalWidth();
    assert.equal(root.style.props['--python-workbench-terminal-width'], '520px');
    workbench._watchLayoutMedia();
    const list = lists.find((l) => l.listeners.length > 0);
    assert.ok(list, 'a change listener is registered on the side-by-side media list');
    // Crossing the threshold re-labels the separator and refits the terminal.
    list.listeners[0].fn();
    assert.equal(splitter.getAttribute('aria-orientation'), 'vertical');
    assert.equal(layouts, 1);
    workbench._unwatchLayoutMedia();
    assert.equal(list.listeners.length, 0, 'unload releases the listener');
    assert.match(workbench.renderShell('x.py'), /aria-orientation="vertical"/);
});

import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/automation-launcher.js');
const scriptsModulePath = path.resolve('VibeRails/wwwroot/js/modules/python-scripts-controller.js');
const workbenchModulePath = path.resolve('VibeRails/wwwroot/js/modules/python-script-workbench.js');
const jobsControllerPath = path.resolve('VibeRails/wwwroot/js/modules/jobs-controller.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');

const { AutomationNavLauncher, normalizeLauncherItems, isLauncherItemRunnable } =
    await import(pathToFileURL(modulePath).href);
const { PythonScriptsController } = await import(pathToFileURL(scriptsModulePath).href);

function createApp() {
    const app = {
        calls: [],
        toasts: [],
        errors: [],
        navigations: [],
        response: { items: [] },
        async apiCall(url, method = 'GET', body = null, requestOptions = undefined) {
            app.calls.push({ url, method, body, requestOptions });
            return app.response;
        },
        showToast(title, message, tone) { app.toasts.push({ title, message, tone }); },
        showError(message) { app.errors.push(message); },
        navigate(view) { app.navigations.push(view); return true; }
    };
    app.jobController = {
        launched: [],
        async launchFromNav(jobId) { this.launched.push(jobId); },
        pythonScripts: {
            runningNames: new Set(),
            runs: [],
            async run(name, button, options) {
                this.runs.push({ name, button, options });
                return { exitCode: 0 };
            }
        }
    };
    return app;
}

/** The flyout's item host: records the markup and hands out one fake button per row. */
function fakeFlyout() {
    const target = {
        html: '',
        buttons: [],
        set innerHTML(value) {
            this.html = value;
            this.buttons = [...value.matchAll(/data-automation-launch-index="(\d+)"/g)].map((match) => ({
                dataset: { automationLaunchIndex: match[1] },
                handlers: {},
                addEventListener(type, handler) { this.handlers[type] = handler; },
                click() { return this.handlers.click?.(); }
            }));
        },
        get innerHTML() { return this.html; },
        querySelectorAll(selector) {
            return selector === '[data-automation-launch-index]' ? this.buttons : [];
        }
    };
    return {
        target,
        removed: false,
        querySelector(selector) { return selector === '[data-automation-launch-items]' ? target : null; },
        querySelectorAll() { return []; },
        contains() { return false; },
        remove() { this.removed = true; }
    };
}

function launcherWith(items, app = createApp()) {
    const launcher = new AutomationNavLauncher(app);
    launcher.items = normalizeLauncherItems(items);
    launcher.flyout = fakeFlyout();
    launcher.triggerElement = null;
    return launcher;
}

const CATALOG = [
    { key: 'script:tidy.py', kind: 'script', label: 'tidy.py', jobId: 0, enabled: true, order: 2, status: 'unapproved' },
    { key: 'job:7', kind: 'automation', label: 'Nightly sweep', jobId: 7, enabled: true, order: 0 },
    { key: 'script:backup.py', kind: 'script', label: 'backup.py', jobId: 0, enabled: true, order: 1, status: 'approved' },
    { key: 'job:9', kind: 'automation', label: 'Hidden one', jobId: 9, enabled: false, order: 3 }
];

test('normalizeLauncherItems sorts by saved order, keeps hidden rows, and drops malformed entries', () => {
    const items = normalizeLauncherItems([
        ...CATALOG,
        { key: '', kind: 'automation', label: 'no key', jobId: 1, enabled: true, order: 9 },
        { key: 'job:x', kind: 'automation', label: 'no id', jobId: 'nope', enabled: true, order: 9 },
        { key: 'script:changed.py', kind: 'script', label: 'changed.py', enabled: true, order: 4, status: 'modified' },
        { key: 'script:nostatus.py', kind: 'script', label: 'nostatus.py', enabled: true, order: 5 }
    ]);

    assert.deepEqual(items.map((item) => item.key), [
        'job:7', 'script:backup.py', 'script:tidy.py', 'job:9', 'script:changed.py', 'script:nostatus.py'
    ]);
    // Hidden rows survive normalisation (the customize modal needs them); the flyout filters.
    assert.equal(items.find((item) => item.key === 'job:9').enabled, false);
    // Status is a script thing: automations carry null, a missing script status reads as unsigned.
    assert.equal(items.find((item) => item.key === 'job:7').status, null);
    assert.equal(items.find((item) => item.key === 'script:nostatus.py').status, 'unapproved');
    assert.equal(items.find((item) => item.key === 'script:changed.py').status, 'modified');
    assert.deepEqual(normalizeLauncherItems(undefined), []);
    assert.deepEqual(normalizeLauncherItems({ not: 'an array' }), []);

    assert.equal(isLauncherItemRunnable({ kind: 'automation' }), true);
    assert.equal(isLauncherItemRunnable({ kind: 'script', status: 'approved' }), true);
    assert.equal(isLauncherItemRunnable({ kind: 'script', status: 'modified' }), false);
    assert.equal(isLauncherItemRunnable({ kind: 'script', status: 'unapproved' }), false);
});

test('unsigned scripts are listed but disabled; hidden rows never render; icons follow the kind', () => {
    const launcher = launcherWith(CATALOG);
    launcher._renderFlyoutItems();
    const html = launcher.flyout.target.html;

    // Visible rows only, in saved order.
    assert.deepEqual([...html.matchAll(/automation-launch-item-label">([^<]+)</g)].map((match) => match[1]),
        ['Nightly sweep', 'backup.py', 'tidy.py']);
    assert.doesNotMatch(html, /Hidden one/);

    const rows = html.split('<button').slice(1);
    const [job, signed, unsigned] = rows;
    assert.match(job, /fa-solid fa-play/);
    assert.match(job, /title="Run Nightly sweep now"/);
    assert.doesNotMatch(job, /disabled/);
    assert.match(signed, /fa-brands fa-python/);
    assert.doesNotMatch(signed, /disabled/);
    // The unsigned script keeps its row (so hide/order preferences apply) but cannot be clicked.
    assert.match(unsigned, /is-unsigned/);
    assert.match(unsigned, /disabled aria-disabled="true"/);
    assert.match(unsigned, /title="Sign it on the Automation page first"/);
    assert.match(unsigned, /automation-launch-item-note">Not signed</);
    assert.doesNotMatch(signed, /Not signed/);
    // The muted suffix sits outside the label span (the e2e spec reads labels verbatim).
    assert.match(unsigned, /automation-launch-item-label">tidy\.py<\/span>/);
});

test('a script with a run in flight shows a busy row instead of a second Run', () => {
    const app = createApp();
    app.jobController.pythonScripts.runningNames.add('backup.py');
    const launcher = launcherWith(CATALOG, app);
    launcher._renderFlyoutItems();
    const row = launcher.flyout.target.html.split('<button').slice(1)
        .find((chunk) => chunk.includes('backup.py'));

    assert.match(row, /is-running/);
    assert.match(row, /aria-busy="true"/);
    assert.match(row, /disabled aria-disabled="true"/);
    assert.match(row, /spinner-border/);
    assert.match(row, /title="backup\.py is running"/);
});

test('clicking an automation launches it through JobController; a script goes to _runScript', async () => {
    const app = createApp();
    const launcher = launcherWith(CATALOG, app);
    const scriptRuns = [];
    launcher._runScript = async (name, index) => { scriptRuns.push({ name, index }); };
    launcher._renderFlyoutItems();
    const [jobButton, scriptButton, unsignedButton] = launcher.flyout.target.buttons;

    await scriptButton.click();
    assert.deepEqual(scriptRuns, [{ name: 'backup.py', index: 1 }]);
    assert.equal(launcher.flyout?.removed, false, 'the flyout stays open so the busy row is visible');

    await unsignedButton.click();
    assert.equal(scriptRuns.length, 1, 'an unsigned script must never be sent to run');

    await jobButton.click();
    assert.deepEqual(app.jobController.launched, [7]);
    assert.equal(launcher.flyout, null, 'launching an automation closes the flyout (its tab is the feedback)');
});

test('_runScript delegates the interactive launch to the shared controller', async () => {
    const app = createApp();
    const launcher = launcherWith(CATALOG, app);
    launcher._renderFlyoutItems();

    await launcher._runScript('backup.py', 1);

    assert.deepEqual(app.jobController.pythonScripts.runs, [
        { name: 'backup.py', button: undefined, options: undefined }
    ]);
    assert.deepEqual(app.toasts, [], 'the launch toast belongs to PythonScriptsController.run');
});

test('a nav script launch opens the returned interactive terminal through the shared controller', async () => {
    const app = createApp();
    app.apiCall = async (url, method = 'GET', body = null) => {
        app.calls.push({ url, method, body });
        if (url === '/api/v1/python-scripts/run/interactive') {
            return { name: 'backup.py', tabId: 'python-tab', message: 'started' };
        }
        return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [] };
    };
    const scripts = new PythonScriptsController(app);
    app.jobController = { pythonScripts: scripts };
    const launcher = launcherWith(CATALOG, app);

    await launcher._runScript('backup.py', 1);

    assert.deepEqual(app.calls[0], { url: '/api/v1/python-scripts/run/interactive', method: 'POST', body: { name: 'backup.py' } });
    assert.deepEqual(app.toasts, [{
        title: 'Script started',
        message: 'backup.py is running in an interactive terminal.',
        tone: 'success'
    }]);
    assert.equal(app.navigations.at(-1), 'terminal-focus');
    assert.equal(scripts.lastRunByName.has('backup.py'), false);
    assert.equal(scripts.runningNames.size, 0);
    assert.equal((readFileSync(scriptsModulePath, 'utf8').match(/'Script finished'/g) || []).length, 1);
    assert.doesNotMatch(readFileSync(modulePath, 'utf8'), /'Script finished'|'Script failed'/);
});

test('arrow keys, Home and End cycle through the rows and footer buttons', (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const focused = [];
    const item = (name) => ({ name, focus() { focused.push(name); } });
    const menuItems = [item('row-0'), item('row-1'), item('customize'), item('manage')];
    const flyout = {
        querySelector: () => null,
        querySelectorAll: (selector) => (selector === '[role="menuitem"]:not([disabled])' ? menuItems : []),
        contains: (element) => menuItems.includes(element)
    };
    const body = {};
    globalThis.document = { activeElement: body, body };
    const launcher = new AutomationNavLauncher(createApp());
    launcher.flyout = flyout;

    const press = (key) => {
        let prevented = false;
        launcher._handleFlyoutNavigation({ key, preventDefault() { prevented = true; } });
        return prevented;
    };
    assert.equal(press('ArrowDown'), true);
    assert.deepEqual(focused, ['row-0']);
    globalThis.document.activeElement = menuItems[3];
    assert.equal(press('ArrowDown'), true);
    assert.deepEqual(focused.at(-1), 'row-0', 'wraps from the last footer button to the first row');
    globalThis.document.activeElement = menuItems[0];
    press('ArrowUp');
    assert.equal(focused.at(-1), 'manage', 'wraps upward into the footer');
    press('End');
    assert.equal(focused.at(-1), 'manage');
    press('Home');
    assert.equal(focused.at(-1), 'row-0');
    // Arrows typed elsewhere on the page keep their meaning.
    globalThis.document.activeElement = { name: 'terminal textarea' };
    const before = focused.length;
    assert.equal(press('ArrowDown'), false);
    assert.equal(focused.length, before);
    assert.equal(press('Tab'), false);
});

test('flyout keyboard contract: menu roles, focus hand-off, Escape restores the trigger while the flyout owns focus', () => {
    const source = readFileSync(modulePath, 'utf8');

    // Footer buttons are part of the menu, so the arrow-key cycle reaches them.
    assert.equal((source.match(/automation-launch-footer-btn" role="menuitem"/g) || []).length, 2);
    // Opening hands focus to the first reachable item; Escape hands it back.
    assert.match(source, /await this\._loadPreferences\(\);\s*this\._renderFlyoutItems\(\);\s*\/\/[^\n]*\n\s*this\._focusFlyoutItem\(\);/);
    // Escape restores the trigger only while the flyout owns focus; when a script
    // run has adopted its tab in place and the terminal has focus, the flyout closes
    // without stealing the keystroke (Escape is the terminal's interrupt key there).
    assert.match(source, /if \(this\._flyoutOwnsFocus\(\)\) \{\s*event\.preventDefault\(\);\s*this\.closeFlyout\(\{ restoreFocus: true \}\);\s*\} else \{\s*this\.closeFlyout\(\);\s*\}/);
    assert.match(source, /this\._handleFlyoutNavigation\(event\);/);
    // The confirm overlay still owns Escape while it is up.
    assert.match(source, /if \(isConfirmDialogOpen\(\)\) return;\s*if \(event\.key === 'Escape'\)/);
});

test('the customize modal restores focus to the button that opened it, not the first Launch button in the DOM', () => {
    const launcher = new AutomationNavLauncher(createApp());
    const focused = [];
    const trigger = { isConnected: true, focus() { focused.push('trigger'); } };
    launcher._restoreTriggerFocus(trigger);
    assert.deepEqual(focused, ['trigger']);

    // Without a remembered trigger, pick the visible one of index.html's two buttons.
    const originalDocument = globalThis.document;
    try {
        const hiddenTop = { getClientRects: () => [], focus() { focused.push('hidden top nav'); } };
        const sidebar = { getClientRects: () => [{}], focus() { focused.push('sidebar'); } };
        globalThis.document = { querySelectorAll: () => [hiddenTop, sidebar] };
        launcher._restoreTriggerFocus(null);
        assert.deepEqual(focused, ['trigger', 'sidebar']);
    } finally {
        globalThis.document = originalDocument;
    }

    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /const triggerElement = this\.triggerElement;\s*this\.closeFlyout\(\);\s*this\.openCustomizationModal\(\{ triggerElement \}\);/);
    assert.match(source, /if \(restoreFocus\) this\._restoreTriggerFocus\(state\.triggerElement\);/);
    // Rows in the flyout and the customize modal share one icon per kind.
    assert.equal((source.match(/launcherItemIcon\(item\)/g) || []).length, 3, 'one helper, used by both renderers plus its definition');
    assert.doesNotMatch(source, /fa-robot/);
    const index = readFileSync(indexPath, 'utf8');
    assert.equal((index.match(/title="Launch an automation or script"/g) || []).length, 2);
    assert.doesNotMatch(index, /Launch an Automation now/);
});

test('the flyout reloads on every open, so nothing invalidates a cache anywhere', () => {
    const launcherSource = readFileSync(modulePath, 'utf8');
    assert.doesNotMatch(launcherSource, /invalidate\(/);
    assert.doesNotMatch(launcherSource, /cached catalog/);
    for (const file of [scriptsModulePath, workbenchModulePath, jobsControllerPath]) {
        assert.doesNotMatch(readFileSync(file, 'utf8'), /automationNavLauncher\?\.invalidate|\.invalidate\?\.\(\)/, `${path.basename(file)} still invalidates`);
    }
    // openFlyout always fetches before rendering.
    assert.match(launcherSource, /document\.addEventListener\('keydown', onKeydown, true\);[\s\S]*?await this\._loadPreferences\(\);/);
});

test('a failed load leaves the customize modal honest: error alert and no Save; a stale save reloads the list', async (t) => {
    t.mock.method(console, 'error', () => {});
    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /const loadFailed = this\.items === null;/);
    assert.match(source, /data-automation-nav-action="save" \$\{loadFailed \? 'disabled' : ''\}/);
    assert.match(source, /Could not load the automations and scripts\. Close this dialog and try again\./);
    assert.match(source, /if \(!state \|\| state\.pending \|\| state\.loadFailed\) return;/);
    assert.match(source, /The list changed — review and save again\./);

    // The merge keeps the draft's arrangement for surviving keys and appends newcomers.
    const app = createApp();
    const launcher = new AutomationNavLauncher(app);
    const state = {
        disposed: false,
        items: normalizeLauncherItems([
            { key: 'job:2', kind: 'automation', label: 'Second', jobId: 2, enabled: false, order: 0 },
            { key: 'job:1', kind: 'automation', label: 'First', jobId: 1, enabled: true, order: 1 },
            { key: 'job:3', kind: 'automation', label: 'Gone', jobId: 3, enabled: true, order: 2 }
        ])
    };
    launcher.modalState = state;
    let rendered = 0;
    launcher._renderModalRows = () => { rendered += 1; };

    // Same catalog → nothing to do, the draft is untouched.
    app.response = { items: state.items.map((item) => ({ ...item, enabled: true })) };
    assert.equal(await launcher._refreshModalCatalog(state), false);
    assert.equal(rendered, 0);

    // Renamed + removed + added → merged draft, re-rendered.
    app.response = { items: [
        { key: 'job:1', kind: 'automation', label: 'First (renamed)', jobId: 1, enabled: true, order: 0 },
        { key: 'job:2', kind: 'automation', label: 'Second', jobId: 2, enabled: true, order: 1 },
        { key: 'job:4', kind: 'automation', label: 'Newcomer', jobId: 4, enabled: true, order: 2 }
    ] };
    assert.equal(await launcher._refreshModalCatalog(state), true);
    assert.equal(rendered, 1);
    assert.deepEqual(state.items.map((item) => [item.key, item.label, item.enabled]), [
        ['job:2', 'Second', false],
        ['job:1', 'First (renamed)', true],
        ['job:4', 'Newcomer', true]
    ]);

    // A load failure keeps the draft rather than wiping it.
    app.apiCall = async () => { throw new Error('offline'); };
    assert.equal(await launcher._refreshModalCatalog(state), false);
    assert.equal(state.items.length, 3);
});

test('launcher CSS keeps a fallback on every colour token', () => {
    const css = readFileSync(stylePath, 'utf8');
    const start = css.indexOf('Nav Automation launcher');
    const block = css.slice(start, css.indexOf('Python scripts (Automation page section', start));
    assert.ok(block.includes('.automation-launch-item-note'), 'expected the launcher CSS block');
    assert.match(block, /\.automation-launch-item > \.fa-python/);
    assert.match(block, /\.automation-launch-item:disabled/);
    for (const match of block.matchAll(/var\((--color-[a-z-]+)([^)]*)\)/g)) {
        assert.ok(match[2].includes(','), `${match[1]} is used without a fallback: ${match[0]}`);
    }
});

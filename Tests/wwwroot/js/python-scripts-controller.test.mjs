import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/python-scripts-controller.js');
const servicePath = path.resolve('VibeRails/Services/PythonScripts/PythonScriptService.cs');
const routesPath = path.resolve('VibeRails/Routes/PythonScriptRoutes.cs');
const webviewPanelPath = path.resolve('vscode-viberails/src/webview-panel.ts');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');
const wwwrootPath = path.resolve('VibeRails/wwwroot');

const { PythonScriptsController, defaultPythonMcpToolName, formatPythonRunOutput, PYTHON_SCRIPT_STATUS_META } =
    await import(pathToFileURL(modulePath).href);

function createApp() {
    const calls = [];
    return {
        calls,
        toasts: [],
        errors: [],
        navigations: [],
        async apiCall(url, method = 'GET', body = null, requestOptions = undefined) {
            calls.push({ url, method, body, requestOptions });
            return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [] };
        },
        navigate(view, data = {}, options = {}) { this.navigations.push({ view, data, options }); return true; },
        showToast(title, message, tone) { this.toasts.push({ title, message, tone }); },
        showError(message) { this.errors.push(message); },
        async copyTextToClipboard() { return true; }
    };
}

function controllerWith(scripts, app = createApp()) {
    const controller = new PythonScriptsController(app);
    controller.state = { pinConfigured: true, scriptsDirectory: '/scripts', scripts };
    return controller;
}

const SCRIPT = Object.freeze({
    name: 'nightly.py',
    status: 'approved',
    approvedUtc: '2026-08-18T09:00:00.0000000Z',
    modifiedUtc: '2026-08-18T09:00:00.0000000Z',
    sizeBytes: 2048,
    path: '/scripts/nightly.py'
});

test('A row offers the whole file lifecycle, not just run and sign', () => {
    const controller = controllerWith([SCRIPT]);
    const html = controller._renderRow(SCRIPT);

    // The name itself opens the file — the discoverable way in.
    assert.match(html, /data-python-scripts-action="open"[^>]*data-name="nightly\.py"/);
    for (const action of ['run', 'approve', 'edit', 'duplicate', 'rename', 'copy-path', 'delete']) {
        assert.match(html, new RegExp(`data-python-scripts-action="${action}"`), `missing ${action}`);
    }
    // A visible Edit button (before Run) says a script opens into an editor + agent
    // terminal; nothing else on the row did. It replaces the menu's "Edit script" item.
    const editIndex = html.indexOf('data-python-scripts-action="edit"');
    const runIndex = html.indexOf('data-python-scripts-action="run"');
    assert.ok(editIndex > 0 && editIndex < runIndex, 'Edit sits before Run in the row actions');
    // Primary-outlined so it reads as THE way in (Rob: "make the edit button more obvious").
    assert.match(html, /<button class="btn btn-sm btn-outline-primary python-script-edit" type="button" data-python-scripts-action="edit"\s+data-name="nightly\.py" title="Open nightly\.py in the editor with an agent terminal beside it">\s*<i class="fa-solid fa-pen-to-square me-1" aria-hidden="true"><\/i>Edit\s*<\/button>/);
    assert.doesNotMatch(html, /Edit script/);
    assert.equal(html.match(/data-python-scripts-action="edit"/g).length, 1, 'one Edit affordance besides the name');
    // The section copy says what opening does.
    const source = readFileSync(modulePath, 'utf8');
    assert.match(source, /Edit a script here with an agent terminal beside it\./);
    // Size and edit time are on the row so "did my edit land?" needs no round trip.
    assert.match(html, /2\.00 KB/);
    assert.match(html, /edited /);
    assert.match(html, /signed /);
});

test('Revoke is offered only for a script that carries a signature', () => {
    const controller = controllerWith([SCRIPT]);

    assert.match(controller._renderRow(SCRIPT), /data-python-scripts-action="revoke"/);
    assert.doesNotMatch(
        controller._renderRow({ ...SCRIPT, status: 'unapproved' }),
        /data-python-scripts-action="revoke"/);
});

test('Run stays disabled until the server says the script is signed', () => {
    const controller = controllerWith([SCRIPT]);

    assert.doesNotMatch(controller._renderRow(SCRIPT), /action="run"[^>]*disabled/);
    for (const status of ['modified', 'unapproved']) {
        const html = controller._renderRow({ ...SCRIPT, status });
        assert.match(html, /data-python-scripts-action="run"[\s\S]*?disabled/);
    }
});

test('The empty state offers both ways to add a script', () => {
    const controller = controllerWith([]);
    let listHtml = '';
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-python-scripts-list]') {
                return { set innerHTML(value) { listHtml = value; } };
            }
            return null;
        }
    };

    controller._render();

    assert.match(listHtml, /data-python-scripts-action="new"/);
    assert.match(listHtml, /data-python-scripts-action="import"/);
    assert.match(listHtml, /drop a <code>\.py<\/code> file/);
});

test('Clicking a script opens the workbench view in every host', () => {
    // Plain browser: no bridge → the name goes to the workbench.
    {
        const app = createApp();
        const controller = controllerWith([SCRIPT], app);
        assert.doesNotMatch(controller._renderRow(SCRIPT), /Open in VS Code/);
        controller.openScript('nightly.py');
        assert.deepEqual(app.navigations, [{ view: 'python-script', data: { name: 'nightly.py' }, options: {} }]);
        // Opening never fetches content itself; the workbench does that.
        assert.equal(app.calls.length, 0);
    }
    // VS Code webview: the primary click STILL opens the workbench (Rob: "no back button
    // from a script"), and "Open in VS Code" is offered as a secondary menu item.
    const opened = [];
    globalThis.window = {
        __viberails_VSCODE__: true,
        __viberails_openFile__: (filePath) => opened.push(filePath)
    };
    try {
        const app = createApp();
        const controller = controllerWith([SCRIPT], app);
        const html = controller._renderRow(SCRIPT);
        assert.match(html, /data-python-scripts-action="open-vscode"[\s\S]*?Open in VS Code/);
        assert.match(html, /data-python-scripts-action="edit"[\s\S]*?>Edit\s*<\/button>/);

        // Drive the row's click dispatcher: both the name ("open") and the row's Edit
        // button ("edit") go to the workbench; only "open-vscode" hands off.
        controller.root = { contains: () => true, querySelectorAll: () => [] };
        const click = (action) => controller._onClick({
            target: { closest: () => ({ dataset: { pythonScriptsAction: action, name: 'nightly.py' } }) }
        });
        click('open');
        click('edit');
        assert.deepEqual(app.navigations.map((entry) => entry.view), ['python-script', 'python-script']);
        assert.equal(opened.length, 0, 'the primary click must not hand off to VS Code');

        click('open-vscode');
        assert.deepEqual(opened, ['/scripts/nightly.py']);

        assert.equal(app.calls.length, 0);
    } finally {
        delete globalThis.window;
    }
});

test('New script and Duplicate land in the workbench for the new file', async () => {
    const app = createApp();
    app.apiCall = async (url, method = 'GET', body = null) => {
        app.calls.push({ url, method, body });
        if (url.includes('/content?')) {
            return { name: 'nightly.py', content: 'print("copy me")\n', version: 'v1' };
        }
        return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [SCRIPT] };
    };
    const controller = controllerWith([SCRIPT], app);
    controller._promptForm = async ({ title }) => ({ name: title.startsWith('New') ? 'fresh.py' : 'nightly-copy.py' });

    await controller._newScriptAndOpen();
    assert.equal(app.calls[0].url, '/api/v1/python-scripts/create');
    assert.equal(app.calls[0].body.name, 'fresh.py');
    // The starter script demonstrates both halves of the run window: argv in, and a
    // JSON object on the last line of stdout as the return value it shows.
    assert.match(app.calls[0].body.content, /def main\(argv: list\[str\]\) -> dict:/);
    assert.match(app.calls[0].body.content, /print\(json\.dumps\(main\(sys\.argv\[1:\]\)\)\)/);
    assert.deepEqual(app.navigations.at(-1), { view: 'python-script', data: { name: 'fresh.py' }, options: {} });

    await controller._duplicateAndOpen('nightly.py');
    assert.deepEqual(app.navigations.at(-1), { view: 'python-script', data: { name: 'nightly-copy.py' }, options: {} });
});

test('Saving posts the content with its optimistic version and never a PIN', async () => {
    const app = createApp();
    app.apiCall = async (url, method, body) => {
        app.calls.push({ url, method, body });
        return {
            state: {
                pinConfigured: true,
                scriptsDirectory: '/scripts',
                scripts: [{ ...SCRIPT, status: 'modified' }]
            },
            version: 'next-version'
        };
    };
    const controller = controllerWith([SCRIPT], app);
    const seen = [];
    controller.onStateChange((state) => seen.push(state.scripts[0].status));

    const result = await controller.saveContent('nightly.py', 'print(2)\n', 'opened-version');

    assert.deepEqual(app.calls[0], {
        url: '/api/v1/python-scripts/content',
        method: 'POST',
        body: { name: 'nightly.py', content: 'print(2)\n', expectedVersion: 'opened-version' }
    });
    assert.deepEqual(result, { status: 'modified', version: 'next-version' });
    // The workbench follows the list through onStateChange rather than re-fetching.
    assert.deepEqual(seen, ['modified']);
});

test('Delete confirms first, sends the name as a query parameter, and drops its run output', async () => {
    const app = createApp();
    const controller = controllerWith([SCRIPT], app);
    controller.lastRunByName.set('nightly.py', { exitCode: 0 });

    controller.confirm = async () => false;
    assert.equal(await controller.deleteScript('nightly.py'), false);
    assert.equal(app.calls.length, 0, 'a declined confirmation must not delete');
    assert.ok(controller.lastRunByName.has('nightly.py'));

    controller.confirm = async () => true;
    assert.equal(await controller.deleteScript('nightly.py'), true);
    assert.equal(app.calls[0].url, '/api/v1/python-scripts?name=nightly.py');
    assert.equal(app.calls[0].method, 'DELETE');
    assert.equal(controller.lastRunByName.has('nightly.py'), false);
});

test('Duplicate uses content + create so it works without host-path import', async () => {
    const app = createApp();
    app.apiCall = async (url, method = 'GET', body = null) => {
        app.calls.push({ url, method, body });
        if (url.includes('/content?')) {
            return { name: 'nightly.py', content: 'print("copy me")\n', version: 'v1' };
        }
        return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [] };
    };
    const controller = controllerWith([SCRIPT], app);
    controller._promptForm = async () => ({ name: 'nightly-copy.py' });

    assert.equal(await controller.duplicate('nightly.py'), 'nightly-copy.py');

    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=nightly.py');
    assert.equal(app.calls[1].url, '/api/v1/python-scripts/create');
    assert.deepEqual(app.calls[1].body, {
        name: 'nightly-copy.py',
        content: 'print("copy me")\n'
    });
});

test('Rename resolves with the new name and forgets the old run output', async () => {
    const app = createApp();
    const controller = controllerWith([SCRIPT], app);
    controller.lastRunByName.set('nightly.py', { exitCode: 0 });
    controller._promptForm = async () => ({ name: 'weekly.py' });

    assert.equal(await controller.rename('nightly.py'), 'weekly.py');
    assert.deepEqual(app.calls[0], {
        url: '/api/v1/python-scripts/rename',
        method: 'POST',
        body: { name: 'nightly.py', newName: 'weekly.py' },
        requestOptions: { showLoading: false, preferErrorResponseMessage: true }
    });
    assert.equal(controller.lastRunByName.has('nightly.py'), false);

    controller._promptForm = async () => null;
    assert.equal(await controller.rename('nightly.py'), null);
});

test('The shared flows fetch the list themselves when nothing is mounted', async () => {
    const app = createApp();
    app.apiCall = async (url, method = 'GET', body = null) => {
        app.calls.push({ url, method, body });
        return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [SCRIPT] };
    };
    const controller = new PythonScriptsController(app);
    assert.equal(controller.state, null);

    const state = await controller.ensureState();
    assert.equal(state.scriptsDirectory, '/scripts');
    assert.equal(app.calls[0].url, '/api/v1/python-scripts');
    // Cached afterwards: a second call is free.
    await controller.ensureState();
    assert.equal(app.calls.length, 2);
    assert.equal(app.calls[1].url, '/api/v1/python-scripts/mcp');
    assert.equal(controller.scriptByName('nightly.py').path, '/scripts/nightly.py');
});

test('A signed script has an MCP switch and configured tools can be edited separately', () => {
    const controller = controllerWith([SCRIPT]);
    assert.equal(defaultPythonMcpToolName('Nightly Report.py'), 'python_nightly_report');

    let html = controller._renderRow(SCRIPT);
    assert.match(html, /role="switch"[\s\S]*?aria-checked="false"[\s\S]*?data-python-scripts-action="mcp-toggle"/);
    assert.doesNotMatch(html, /data-python-scripts-action="mcp-configure"/);

    controller.mcpConfigurations = [{
        scriptName: 'nightly.py',
        toolName: 'python_nightly',
        description: 'Run nightly.',
        parameters: []
    }];
    html = controller._renderRow(SCRIPT);
    assert.match(html, /role="switch"[\s\S]*?aria-checked="true"/);
    assert.match(html, /data-python-scripts-action="mcp-configure"[\s\S]*?Edit MCP tool/);
});

test('MCP parameter editor captures schema fields and argv mapping fields', () => {
    const controller = controllerWith([SCRIPT]);
    const html = controller._renderMcpParameterEditor({
        name: 'output_path',
        description: 'Where to write the report.',
        type: 'string',
        required: true,
        defaultValue: null,
        argumentMode: 'option',
        flag: '--output'
    });

    assert.match(html, /data-python-mcp-param="name" value="output_path"/);
    assert.match(html, /data-python-mcp-param="description" value="Where to write the report\."/);
    assert.match(html, /value="option" selected/);
    assert.match(html, /data-python-mcp-param="flag" value="--output"/);
    assert.match(html, /data-python-mcp-param="required" checked/);
});

test('The behavior declaration starts blank, and read-only locks in the idempotent claim', () => {
    const controller = controllerWith([SCRIPT]);

    // Nothing is preselected: the author has to say what the script does.
    const blank = controller._renderMcpBehaviorSection(null);
    assert.equal((blank.match(/type="radio"/g) || []).length, 3);
    assert.doesNotMatch(blank, /checked/);
    assert.match(blank, /value="read-only"\s+required/);

    const readOnly = controller._renderMcpBehaviorSection({ behavior: 'read-only' });
    assert.match(readOnly, /value="read-only"\s+checked required/);
    // A script that changes nothing cannot change more on a second run.
    assert.match(readOnly, /data-python-mcp-field="repeatSafe"\s+checked disabled/);

    const destructive = controller._renderMcpBehaviorSection(
        { behavior: 'destructive', reachesNetwork: true });
    assert.match(destructive, /value="destructive"\s+checked required/);
    assert.doesNotMatch(destructive, /data-python-mcp-field="repeatSafe"[^>]*disabled/);
    assert.match(destructive, /data-python-mcp-field="reachesNetwork"\s+checked/);
});

test('The MCP form collects a declaration and no PIN, which is asked for after it', () => {
    const controller = controllerWith([SCRIPT]);
    const layer = (behavior, boxes = {}) => {
        const fields = {
            toolName: { value: 'python_nightly' },
            description: { value: 'Run the nightly report.' },
            repeatSafe: { checked: Boolean(boxes.repeatSafe) },
            reachesNetwork: { checked: Boolean(boxes.reachesNetwork) }
        };
        return {
            querySelector(selector) {
                if (selector.includes(':checked')) return behavior ? { value: behavior } : null;
                const key = selector.match(/\[data-python-mcp-field="([^"]+)"\]/)?.[1];
                return key ? fields[key] ?? null : null;
            },
            querySelectorAll() { return []; }
        };
    };

    assert.match(
        controller._collectMcpConfiguration(layer(null), 'nightly.py').error,
        /what this script does/);

    const readOnly = controller._collectMcpConfiguration(layer('read-only'), 'nightly.py').value;
    assert.equal(readOnly.behavior, 'read-only');
    assert.equal(readOnly.repeatSafe, true, 'read-only implies it even with the box unticked');
    assert.equal(readOnly.reachesNetwork, false);
    assert.equal('pin' in readOnly, false, 'the form must not collect a PIN');

    const destructive = controller._collectMcpConfiguration(
        layer('destructive', { reachesNetwork: true }), 'nightly.py').value;
    assert.equal(destructive.repeatSafe, false);
    assert.equal(destructive.reachesNetwork, true);

    // The PIN moved out of the dialog and is prompted for once the form is complete.
    const source = readFileSync(modulePath, 'utf8');
    assert.doesNotMatch(source, /data-python-mcp-field="pin"/);
    const configure = source.slice(
        source.indexOf('async configureMcp('), source.indexOf('async disableMcp('));
    assert.ok(
        configure.indexOf('_openMcpConfigurationModal') < configure.indexOf('_promptPin'),
        'the PIN must be asked for after the configuration form, not inside it');
});

test('Suggested names are sanitised, extended to .py, and made unique', () => {
    const controller = controllerWith([
        { ...SCRIPT, name: 'report.py' },
        { ...SCRIPT, name: 'report-2.py' }
    ]);

    assert.equal(controller._sanitizeName('C:\\tmp\\weekly report(v2).txt'), 'weekly report-v2-.txt.py');
    assert.equal(controller._sanitizeName('/home/me/clean.py'), 'clean.py');
    assert.equal(controller._sanitizeName('.hidden.py'), 'hidden.py');
    assert.equal(controller._uniqueName('report.py'), 'report-3.py');
    assert.equal(controller._uniqueName('other.py'), 'other.py');
});

test('Name validation mirrors the server rule and catches clashes before the round trip', () => {
    const controller = controllerWith([{ ...SCRIPT, name: 'report.py' }]);

    assert.equal(controller._validateNewName('fresh.py'), null);
    assert.match(controller._validateNewName('fresh.txt'), /plain \.py file name/);
    assert.match(controller._validateNewName('sub/dir.py'), /plain \.py file name/);
    assert.match(controller._validateNewName('..\\escape.py'), /plain \.py file name/);
    assert.match(controller._validateNewName('REPORT.py'), /already exists/);
    // Renaming a file to the casing it already has is not a clash with itself.
    assert.equal(controller._validateNewName('report.py', { allow: 'report.py' }), null);
});

test('Dropped files are added by content, and non-Python files are skipped', async () => {
    const app = createApp();
    const controller = controllerWith([], app);

    await controller._acceptDroppedFiles([
        {
            name: 'from-desktop.py',
            size: 17,
            arrayBuffer: async () => new TextEncoder().encode('print("dropped")\n').buffer
        },
        {
            name: 'notes.md',
            size: 4,
            arrayBuffer: async () => new TextEncoder().encode('# no').buffer
        }
    ]);

    assert.equal(app.calls.length, 1);
    assert.equal(app.calls[0].url, '/api/v1/python-scripts/create');
    assert.deepEqual(app.calls[0].body, { name: 'from-desktop.py', content: 'print("dropped")\n' });
    assert.match(app.toasts.at(-1).message, /1 non-\.py file skipped/);
});

test('Dropped scripts with invalid UTF-8 are rejected before upload', async () => {
    const app = createApp();
    const controller = controllerWith([], app);

    await controller._acceptDroppedFiles([{
        name: 'latin.py',
        size: 2,
        arrayBuffer: async () => Uint8Array.from([0xC3, 0x28]).buffer
    }]);

    assert.equal(app.calls.length, 0);
    assert.match(app.errors[0], /not valid UTF-8/);
});

test('Host-file import is unavailable on non-root backends while duplicate remains available', async () => {
    const app = createApp();
    app.data = { configs: { isActiveRootBackend: false } };
    const controller = controllerWith([SCRIPT], app);

    assert.equal(controller._canImportFromHost(), false);
    assert.match(controller._renderRow(SCRIPT), /data-python-scripts-action="duplicate"/);

    let emptyHtml = '';
    controller.state.scripts = [];
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-python-scripts-list]') {
                return { set innerHTML(value) { emptyHtml = value; } };
            }
            return null;
        }
    };
    controller._render();
    assert.doesNotMatch(emptyHtml, /data-python-scripts-action="import"/);

    await controller._importScript(null);
    assert.equal(app.calls.length, 0);
    assert.match(app.errors[0], /main VibeRails dashboard/);
});

test('Run output combines stdout and stderr the same way for the row and the workbench', () => {
    assert.equal(formatPythonRunOutput({ standardOutput: 'ok\n', standardError: '' }), 'ok');
    assert.equal(formatPythonRunOutput({ standardOutput: '', standardError: 'boom\n' }), '[stderr]\nboom');
    assert.equal(formatPythonRunOutput({ standardOutput: 'a', standardError: 'b' }), 'a\n\n[stderr]\nb');
    assert.equal(formatPythonRunOutput({}), '(no output)');
    assert.deepEqual(Object.keys(PYTHON_SCRIPT_STATUS_META), ['approved', 'modified', 'unapproved']);
});

test('Authoring endpoints never carry a PIN, and only approve/revoke ask for one', () => {
    const source = readFileSync(modulePath, 'utf8');

    for (const endpoint of ['/create', '/content', '/rename', '/import']) {
        const call = source.slice(source.indexOf(`${endpoint}\``));
        assert.doesNotMatch(call.slice(0, 400), /\bpin\b/i, `${endpoint} must not send a PIN`);
    }
    assert.match(source, /_promptPin\([\s\S]*?Sign \$\{name\}/);
    assert.match(source, /_promptPin\([\s\S]*?Remove signature from \$\{name\}/);
});

test('The Monaco modal is gone: the workbench view replaced it everywhere', () => {
    assert.equal(existsSync(path.join(wwwrootPath, 'js/modules/python-script-editor.js')), false);
    const style = readFileSync(stylePath, 'utf8');
    assert.doesNotMatch(style, /vb-python-editor/);
    for (const file of ['app.js', 'js/modules/python-scripts-controller.js', 'js/modules/jobs-controller.js']) {
        const source = readFileSync(path.join(wwwrootPath, file), 'utf8');
        assert.doesNotMatch(source, /python-script-editor|PythonScriptEditorModal/, `${file} still references the modal`);
    }
    // The row menu keeps its own stacking styles; the drop target keeps its highlight.
    assert.match(style, /\.python-script-menu \{[\s\S]*?position: absolute/);
    assert.match(style, /\.python-scripts-list-dropping/);
});

test('The signing gate still owns what can run: writes cannot approve', () => {
    const service = readFileSync(servicePath, 'utf8');
    const routes = readFileSync(routesPath, 'utf8');

    // Every authoring entry point returns the recomputed status; none writes an approval.
    for (const method of ['SaveContentAsync', 'CreateAsync', 'ImportAsync', 'RenameAsync', 'DeleteAsync']) {
        const methodIndex = service.indexOf(` ${method}(`);
        assert.notEqual(methodIndex, -1, `${method} must exist`);
        const body = service.slice(methodIndex);
        const upToNextMethod = body.slice(0, body.indexOf('\n    public ', 10));
        assert.doesNotMatch(upToNextMethod, /new PythonScriptApprovalRecord/, `${method} must not sign anything`);
    }
    // Import reads an arbitrary host path, so it is root-dashboard only like the picker.
    assert.match(routes, /if \(isActiveRootBackend\)\s*\{\s*app\.MapPost\("\/api\/v1\/python-scripts\/import"/);
});

test('The VS Code bridge is a one-way postMessage the dashboard feature-detects', () => {
    const panel = readFileSync(webviewPanelPath, 'utf8');
    const source = readFileSync(modulePath, 'utf8');

    assert.match(panel, /window\.__viberails_openFile__ = function\(path\)/);
    assert.match(panel, /message\.command === 'openFile' && typeof message\.path === 'string'/);
    assert.match(panel, /viewColumn: vscode\.ViewColumn\.Beside/);
    assert.match(source, /typeof host\.__viberails_openFile__ === 'function'/);
});

test('Run in terminal stays busy until its tab is ready, and cannot start twice', async () => {
    const app = createApp();
    let releaseRun;
    const runResponse = new Promise((resolve) => { releaseRun = resolve; });
    app.apiCall = async (url, method = 'GET', body = null) => {
        app.calls.push({ url, method, body });
        if (url === '/api/v1/python-scripts/run/interactive') return runResponse;
        return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [SCRIPT] };
    };
    const controller = controllerWith([SCRIPT], app);
    let listHtml = '';
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-python-scripts-list]') {
                return { set innerHTML(value) { listHtml = value; } };
            }
            return null;
        }
    };

    const first = controller.runInTerminal('nightly.py');
    assert.ok(controller.runningNames.has('nightly.py'), 'registered before the request resolves');
    // The rebuilt row (what any background refresh would draw) is the busy one.
    assert.match(listHtml, /data-python-scripts-action="run"[^>]*disabled/);
    assert.match(listHtml, /Running…/);
    assert.match(controller._renderRow(SCRIPT), /nightly\.py is running/);
    // A second click while in flight is a no-op, not a second POST.
    assert.equal(await controller.runInTerminal('nightly.py'), null);
    assert.equal(app.calls.filter((call) => call.url === '/api/v1/python-scripts/run/interactive').length, 1);

    // Background refreshes stand down while a run is in flight.
    const originalDocument = globalThis.document;
    globalThis.document = { visibilityState: 'visible' };
    try {
        controller._lastRefreshAt = 0;
        const callsBefore = app.calls.length;
        controller._refreshIfIdle();
        assert.equal(app.calls.length, callsBefore, 'no refresh while running');
    } finally {
        globalThis.document = originalDocument;
    }

    releaseRun({ name: 'nightly.py', tabId: 'python-tab', message: 'started' });
    const result = await first;
    assert.equal(result.tabId, 'python-tab');
    assert.equal(controller.runningNames.size, 0);
    assert.doesNotMatch(listHtml, /Running…/);
    assert.deepEqual(app.toasts.at(-1), {
        title: 'Script started',
        message: 'nightly.py is running in an interactive terminal.',
        tone: 'success'
    });
    assert.deepEqual(app.navigations.at(-1), {
        view: 'terminal-focus',
        data: { preferredSelection: 'base:shell', preferredTabId: 'python-tab' },
        options: {}
    });
    // The workbench passes its own button; it is restored after the launch.
    const button = { disabled: false, innerHTML: '<i></i>Run' };
    app.apiCall = async (url) => (url === '/api/v1/python-scripts/run/interactive'
        ? { name: 'nightly.py', tabId: 'second-tab', message: 'started' }
        : { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [SCRIPT] });
    await controller.runInTerminal('nightly.py', button);
    assert.deepEqual(button, { disabled: false, innerHTML: '<i></i>Run' });
    assert.equal(app.navigations.at(-1).data.preferredTabId, 'second-tab');
});

// Both run surfaces refuse over a run in flight. Silence here is what made the run window's
// "Run in terminal" close onto nothing, and what let a reopened window start a second
// interpreter for the same script.
test('Neither run surface starts over a run in flight, and both say why', async () => {
    const app = createApp();
    const controller = controllerWith([SCRIPT], app);
    let opened = 0;
    controller.runWindow = { open() { opened += 1; return Promise.resolve(null); } };
    controller.runningNames.add('nightly.py');

    assert.equal(await controller.run('nightly.py'), null);
    assert.equal(opened, 0, 'the little window never opens over a run that is still going');
    assert.equal(app.toasts.at(-1).title, 'Already running');

    assert.equal(await controller.runInTerminal('nightly.py'), null);
    assert.equal(app.calls.filter((call) => call.url.endsWith('/run/interactive')).length, 0);
    assert.equal(app.toasts.at(-1).title, 'Already running');

    // Released, both work again.
    controller.runningNames.delete('nightly.py');
    await controller.run('nightly.py');
    assert.equal(opened, 1);
});

test('markRunning/clearRunning tell every surface, including views this section cannot render', () => {
    const controller = controllerWith([SCRIPT]);
    const seen = [];
    const unsubscribe = controller.onRunChanged((name) => seen.push(name));

    controller.markRunning('nightly.py');
    assert.ok(controller.runningNames.has('nightly.py'));
    controller.recordRun('nightly.py', { exitCode: 0, timedOut: false, durationMs: 3 });
    controller.clearRunning('nightly.py');
    assert.equal(controller.runningNames.size, 0);
    assert.deepEqual(seen, ['nightly.py', 'nightly.py', 'nightly.py'],
        'start, result recorded, finish — the workbench needs all three');

    unsubscribe();
    controller.markRunning('nightly.py');
    assert.equal(seen.length, 3, 'and it stops when the view unloads');
});

test('The last-run drawer remembers whether it was open when the list is rebuilt', () => {
    const controller = controllerWith([SCRIPT]);
    controller.recordRun('nightly.py', { exitCode: 0, timedOut: false, durationMs: 3 });
    assert.equal(controller.lastRunByName.get('nightly.py').open, true);
    assert.match(controller._renderRow(SCRIPT), /<details class="python-script-output" open>/);

    const row = { dataset: { pythonScript: 'nightly.py' } };
    const details = {
        open: false,
        closest(selector) { return selector === '.python-script-output' ? details : row; }
    };
    controller._onOutputToggle({ target: details });
    assert.equal(controller.lastRunByName.get('nightly.py').open, false);
    assert.match(controller._renderRow(SCRIPT), /<details class="python-script-output" >/);

    details.open = true;
    controller._onOutputToggle({ target: details });
    assert.equal(controller.lastRunByName.get('nightly.py').open, true);
    // Toggling something that is not a run drawer is ignored.
    controller._onOutputToggle({ target: { closest: () => null } });
    // The listener is bound in the capture phase because `toggle` does not bubble.
    assert.match(readFileSync(modulePath, 'utf8'), /root\.addEventListener\('toggle', \(event\) => this\._onOutputToggle\(event\), true\);/);
});

test('The row menu closes on Escape (refocusing its toggle), cycles with arrows, and closes when focus leaves it', () => {
    const controller = controllerWith([SCRIPT]);
    const focused = [];
    const toggle = { expanded: 'true', focus() { focused.push('toggle'); }, setAttribute(name, value) { this.expanded = value; } };
    const items = ['edit', 'rename', 'delete'].map((name) => ({ name, focus() { focused.push(name); } }));
    const wrap = {
        querySelector(selector) { return selector === '.python-script-menu-toggle' ? toggle : null; },
        contains(node) { return node === toggle || items.includes(node); }
    };
    const menu = {
        hidden: false,
        closest(selector) { return selector === '.python-script-menu-wrap' ? wrap : null; },
        querySelectorAll(selector) { return selector === '.python-script-menu-item' ? items : []; }
    };
    controller.root = {
        querySelector(selector) { return selector === '.python-script-menu:not([hidden])' && !menu.hidden ? menu : null; },
        querySelectorAll(selector) {
            if (selector === '.python-script-menu') return [menu];
            if (selector === '.python-script-menu-toggle') return [toggle];
            return [];
        }
    };
    const key = (name, target = null) => {
        let prevented = false;
        controller._onKeydown({ key: name, target, preventDefault() { prevented = true; } });
        return prevented;
    };

    // Arrows move through the items and wrap; the page's own Escape shortcut is not reached.
    assert.equal(key('ArrowDown', { closest: () => null }), true);
    assert.deepEqual(focused, ['edit']);
    assert.equal(key('ArrowDown', { closest: () => items[2] }), true);
    assert.equal(focused.at(-1), 'edit');
    assert.equal(key('ArrowUp', { closest: () => items[0] }), true);
    assert.equal(focused.at(-1), 'delete');
    assert.equal(key('Tab', { closest: () => items[0] }), false, 'Tab is left alone');

    assert.equal(key('Escape', { closest: () => items[0] }), true);
    assert.equal(menu.hidden, true);
    assert.equal(toggle.expanded, 'false');
    assert.equal(focused.at(-1), 'toggle');
    // Nothing open: keys fall through untouched.
    assert.equal(key('Escape'), false);

    // Focus leaving the row (Tab past the last item) closes it; a click on something
    // unfocusable (relatedTarget null) is left to the document click handler.
    menu.hidden = false;
    controller._onFocusOut({ relatedTarget: null });
    assert.equal(menu.hidden, false);
    controller._onFocusOut({ relatedTarget: items[1] });
    assert.equal(menu.hidden, false, 'moving between items keeps it open');
    controller._onFocusOut({ relatedTarget: { name: 'next row button' } });
    assert.equal(menu.hidden, true);
});

test('Opening in VS Code tells a never-signed script to sign, and a signed one to re-sign', () => {
    const opened = [];
    globalThis.window = {
        __viberails_VSCODE__: true,
        __viberails_openFile__: (filePath) => opened.push(filePath)
    };
    try {
        const app = createApp();
        const controller = controllerWith([SCRIPT, { ...SCRIPT, name: 'draft.py', status: 'unapproved', path: '/scripts/draft.py' }], app);
        controller.openInVsCode('nightly.py');
        controller.openInVsCode('draft.py');
        assert.deepEqual(opened, ['/scripts/nightly.py', '/scripts/draft.py']);
        assert.match(app.toasts[0].message, /Save there, then re-sign it here\.$/);
        assert.match(app.toasts[1].message, /Save there, then sign it here when it is ready\.$/);
    } finally {
        delete globalThis.window;
    }
});

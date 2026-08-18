import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/python-scripts-controller.js');
const editorModulePath = path.resolve('VibeRails/wwwroot/js/modules/python-script-editor.js');
const servicePath = path.resolve('VibeRails/Services/PythonScripts/PythonScriptService.cs');
const routesPath = path.resolve('VibeRails/Routes/PythonScriptRoutes.cs');
const webviewPanelPath = path.resolve('vscode-viberails/src/webview-panel.ts');
const stylePath = path.resolve('VibeRails/wwwroot/style.css');

const { PythonScriptsController } = await import(pathToFileURL(modulePath).href);

function createApp() {
    const calls = [];
    return {
        calls,
        toasts: [],
        errors: [],
        async apiCall(url, method = 'GET', body = null, requestOptions = undefined) {
            calls.push({ url, method, body, requestOptions });
            return { pinConfigured: true, scriptsDirectory: '/scripts', scripts: [] };
        },
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

test('Opening a script hands off to VS Code when the extension injected its bridge', async () => {
    const opened = [];
    globalThis.window = {
        __viberails_VSCODE__: true,
        __viberails_openFile__: (filePath) => opened.push(filePath)
    };
    try {
        const app = createApp();
        const controller = controllerWith([SCRIPT], app);
        assert.match(controller._renderRow(SCRIPT), /Open in VS Code/);

        await controller._openScript('nightly.py');

        assert.deepEqual(opened, ['/scripts/nightly.py']);
        // No content fetch: VS Code reads the file itself.
        assert.equal(app.calls.length, 0);
    } finally {
        delete globalThis.window;
    }
});

test('Without the bridge, opening a script loads its content for the in-app editor', async () => {
    const app = createApp();
    app.apiCall = async (url) => {
        app.calls.push({ url });
        return { name: 'nightly.py', content: 'print(1)\n', status: 'approved', version: 'abc123' };
    };
    const controller = controllerWith([SCRIPT], app);
    const opens = [];
    controller.editor = { isOpen: false, open: (script) => { opens.push(script); return Promise.resolve({}); } };
    controller.refresh = async () => {};

    await controller._openScript('nightly.py');

    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=nightly.py');
    assert.equal(opens[0].content, 'print(1)\n');
    assert.equal(opens[0].path, '/scripts/nightly.py');
    assert.equal(opens[0].version, 'abc123');
});

test('Saving from the editor posts the content and never a PIN', async () => {
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

    const result = await controller._saveContent('nightly.py', 'print(2)\n', 'opened-version');

    assert.deepEqual(app.calls[0], {
        url: '/api/v1/python-scripts/content',
        method: 'POST',
        body: { name: 'nightly.py', content: 'print(2)\n', expectedVersion: 'opened-version' }
    });
    assert.deepEqual(result, { status: 'modified', version: 'next-version' });
});

test('Delete confirms first, sends the name as a query parameter, and drops its run output', async () => {
    const app = createApp();
    const controller = controllerWith([SCRIPT], app);
    controller.lastRunByName.set('nightly.py', { exitCode: 0 });

    controller.confirm = async () => false;
    await controller._delete('nightly.py');
    assert.equal(app.calls.length, 0, 'a declined confirmation must not delete');
    assert.ok(controller.lastRunByName.has('nightly.py'));

    controller.confirm = async () => true;
    await controller._delete('nightly.py');
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

    await controller._duplicate('nightly.py');

    assert.equal(app.calls[0].url, '/api/v1/python-scripts/content?name=nightly.py');
    assert.equal(app.calls[1].url, '/api/v1/python-scripts/create');
    assert.deepEqual(app.calls[1].body, {
        name: 'nightly-copy.py',
        content: 'print("copy me")\n'
    });
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

test('Authoring endpoints never carry a PIN, and only approve/revoke ask for one', () => {
    const source = readFileSync(modulePath, 'utf8');
    const editor = readFileSync(editorModulePath, 'utf8');

    for (const endpoint of ['/create', '/content', '/rename', '/import']) {
        const call = source.slice(source.indexOf(`${endpoint}\``));
        assert.doesNotMatch(call.slice(0, 400), /\bpin\b/i, `${endpoint} must not send a PIN`);
    }
    assert.match(source, /_promptPin\([\s\S]*?Sign \$\{name\}/);
    // The editor never calls the API itself — it saves through the controller and hands
    // signing back to the PIN prompt — so no PIN can ever reach it.
    assert.doesNotMatch(editor, /apiCall|\/api\/v1\//);
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

test('The editor modal and row menu carry their own sizing and stacking styles', () => {
    const style = readFileSync(stylePath, 'utf8');

    // Monaco collapses to 0px without an explicitly sized flex host.
    assert.match(style, /\.vb-python-editor-modal \.modal-dialog \{[\s\S]*?height: 82vh/);
    assert.match(style, /\.vb-python-editor-host \{[\s\S]*?min-height: 0/);
    assert.match(style, /\.python-script-menu \{[\s\S]*?position: absolute/);
    assert.match(style, /\.python-scripts-list-dropping/);
});

test('Editor loads and saves are scoped to the modal generation that started them', () => {
    const source = readFileSync(modulePath, 'utf8');
    const editor = readFileSync(editorModulePath, 'utf8');

    assert.match(source, /onSave: \(name, content, expectedVersion\) =>[\s\S]*?_saveContent\(name, content, expectedVersion\)/);
    assert.match(editor, /_mountEditor\(generation, layer\)/);
    assert.match(editor, /generation !== this\._generation \|\| this\.closed \|\| this\.layer !== layer/);
    assert.match(editor, /const name = this\.name;[\s\S]*?const expectedVersion = this\.version;/);
    assert.match(editor, /if \(generation !== this\._generation \|\| this\.editor !== editor\) return;/);
});

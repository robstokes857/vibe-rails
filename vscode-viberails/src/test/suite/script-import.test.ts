import * as assert from 'assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';
import { resolveScriptImportTarget } from '../../script-import';
import { WebviewPanelManager } from '../../webview-panel';

suite('Script import', () => {
    test('accepts script files, uses the selected file over the editor, and rejects non-files', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-import-'));
        try {
            for (const name of ['test.ps1', 'test.sh', 'test.py', 'UPPER.PS1', 'not-a-script.txt']) {
                fs.writeFileSync(path.join(directory, name), '');
            }
            const selected = vscode.Uri.file(path.join(directory, 'test.sh'));
            await vscode.window.showTextDocument(vscode.Uri.file(path.join(directory, 'not-a-script.txt')));
            assert.deepEqual(await resolveScriptImportTarget(selected), {
                filePath: selected.fsPath, projectFolder: vscode.Uri.file(directory).fsPath
            });
            for (const name of ['test.ps1', 'test.py', 'UPPER.PS1']) {
                assert.equal((await resolveScriptImportTarget(vscode.Uri.file(path.join(directory, name)))).projectFolder, vscode.Uri.file(directory).fsPath);
            }
            await assert.rejects(resolveScriptImportTarget(), /Select a local/);
            await assert.rejects(resolveScriptImportTarget(vscode.Uri.parse('https://example.com/test.py')), /Select a local/);
            fs.mkdirSync(path.join(directory, 'folder.py'));
            await assert.rejects(resolveScriptImportTarget(vscode.Uri.file(path.join(directory, 'folder.py'))), /not a folder/);
            await assert.rejects(resolveScriptImportTarget(vscode.Uri.file(path.join(directory, 'missing.py'))));
        } finally {
            await vscode.commands.executeCommand('workbench.action.closeActiveEditor');
            await fs.promises.rm(directory, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
        }
    });

    test('delivers a cold-start request into the real registration form in a webview', async function () {
        this.timeout(60000);
        const wwwroot = path.resolve(__dirname, '..', '..', '..', '..', 'VibeRails', 'wwwroot');
        const manager = new WebviewPanelManager(wwwroot);
        const builder = manager as unknown as { buildHtml: (webview: vscode.Webview, port: number) => string };
        const build = builder.buildHtml.bind(manager);
        // Keep the production CSP, bridge, resource mapping and actual form. Fake only
        // HTTP persistence: this regression must not register files in the user's library.
        builder.buildHtml = (webview, port) => {
            const html = build(webview, port);
            const injection = html.match(/<head>([\s\S]*?<\/script>)/)?.[1];
            const nonce = html.match(/<script nonce="([^"]+)"/)?.[1];
            assert.ok(injection && nonce);
            return `<!doctype html><html><head>${injection}</head><body><div id="modal-container"></div>
<script type="module" nonce="${nonce}">
import { PythonScriptsController } from './js/modules/python-scripts-controller.js';
import { setupVSCodeScriptImport } from './js/modules/vscode-script-import.js';
const check = (value, message) => { if (!value) throw new Error(message); };
const calls = [], errors = [], navigations = [];
const app = {
    data: { configs: { rootPath: 'C:/repo' } },
    apiCall: async (url, method, body) => {
        calls.push({url, method, body});
        return {scripts: method === 'POST' ? [{id:'registered.ps1', name:'My script.ps1', path:body.sourcePath, status:'unapproved'}] : []};
    },
    showError: message => errors.push(message), showToast: () => {},
    navigate: (view, data) => navigations.push({view, data})
};
const scripts = new PythonScriptsController(app);
app.jobController = {pythonScripts: scripts};
const original = scripts.importScript.bind(scripts);
scripts.importScript = async filePath => {
    try {
        const pending = original(filePath);
        const deadline = Date.now() + 10000;
        while (!document.querySelector('[data-pin-form]')) {
            check(Date.now() < deadline, 'Registration form did not open: ' + errors.join('; '));
            await new Promise(resolve => setTimeout(resolve, 10));
        }
        check(document.body.textContent.includes(filePath), 'Selected path is missing');
        check(document.querySelector('[data-pin-field="scope"]').value === 'repo', 'Wrong default scope');
        document.querySelector('[data-pin-field="displayName"]').value = 'My launcher';
        document.querySelector('[data-pin-field="requirePinEachRun"]').value = 'true';
        document.querySelector('[data-pin-form]').dispatchEvent(new Event('submit', {bubbles:true, cancelable:true}));
        await pending;
        check(errors.length === 0, errors.join('; '));
        check(calls.length === 2 && calls[1].url === '/api/v1/python-scripts/import', 'Unexpected API call');
        check(calls[1].body.sourcePath === filePath && calls[1].body.requirePinEachRun === true, 'Lost selected path or PIN option');
        check(calls[1].body.displayName === 'My launcher', 'Lost display name');
        check(scripts.state.scripts[0].status === 'unapproved', 'Registration granted approval');
        check(navigations[0].view === 'python-script' && navigations[0].data.name === 'registered.ps1', 'Did not open registration');
        window.__viberails_setTitle__('import-test:ok');
    } catch (error) { window.__viberails_setTitle__('import-test:' + String(error.stack || error)); }
};
setTimeout(() => setupVSCodeScriptImport(app), 150);
</script></body></html>`;
        };
        let timer: ReturnType<typeof setTimeout> | undefined;
        let subscription: vscode.Disposable | undefined;
        try {
            const panel = await manager.create(5000);
            const outcome = new Promise<string>((resolve, reject) => {
                timer = setTimeout(() => reject(new Error('Registration webview did not finish')), 45000);
                subscription = panel.webview.onDidReceiveMessage(message => {
                    if (message.command === 'setTitle' && message.title.startsWith('import-test:')) resolve(message.title);
                });
            });
            await manager.importScript('C:/repo/My script.ps1');
            assert.equal(await outcome, 'import-test:ok');
            assert.equal(manager.hasPanel(), true);
        } finally {
            clearTimeout(timer);
            subscription?.dispose();
            manager.dispose();
        }
    });

    test('pending imports settle on timeout and disposal', async () => {
        const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'viberails-import-idle-'));
        fs.writeFileSync(path.join(directory, 'index.html'), '<!doctype html><html><head></head><body></body></html>');
        const manager = new WebviewPanelManager(directory);
        try {
            await manager.create(5000);
            const pending = manager.importScript('/repo/a.py', 30);
            await assert.rejects(manager.importScript('/repo/b.py'), /already being opened/);
            await assert.rejects(pending, /did not respond/);
            const closing = assert.rejects(manager.importScript('/repo/c.py'), /was closed/);
            manager.dispose();
            await closing;
            assert.equal(manager.hasPanel(), false);
        } finally {
            manager.dispose();
            fs.rmSync(directory, { recursive: true, force: true });
        }
    });
});

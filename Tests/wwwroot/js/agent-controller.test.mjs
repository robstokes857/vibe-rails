import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/agent-controller.js');
const { AgentController, buildAgentFilePath, relativeRulePath, ruleFileDirectory } = await import(pathToFileURL(modulePath).href);
const appSource = readFileSync(path.resolve('VibeRails/wwwroot/app.js'), 'utf8');
const appClassSource = appSource.slice(appSource.indexOf('export class VibeControlApp'), appSource.indexOf('// Initialize the app'))
    .replace('export class VibeControlApp', 'class VibeControlApp');
const VibeControlApp = new Function(`${appClassSource}\nreturn VibeControlApp;`)();

function escapeHtml(value) {
    return String(value ?? '')
        .replaceAll('&', '&amp;')
        .replaceAll('<', '&lt;')
        .replaceAll('>', '&gt;')
        .replaceAll('"', '&quot;')
        .replaceAll("'", '&#39;');
}

function createApp() {
    return {
        escapeHtml,
        data: {},
        showToast() {},
        showError(message) { throw new Error(message); }
    };
}

// The inline rule editor writes into [data-agent-rule-editor] and then binds handlers by
// selector. These stubs capture the HTML without pulling in a DOM implementation, matching
// how the rest of this file exercises the string renderers.
function createRuleEditorHost() {
    return {
        innerHTML: '',
        querySelector() { return null; },
        querySelectorAll() { return []; }
    };
}

function createWorkspaceRoot(host) {
    return {
        querySelector(selector) {
            return selector === '[data-agent-rule-editor]' ? host : null;
        }
    };
}

test('Rule-file wizard trims a manually entered directory before appending vc.rules.md', () => {
    assert.equal(buildAgentFilePath('  C:\\repo\\  '), 'C:\\repo/vc.rules.md');
    assert.equal(buildAgentFilePath('  /repo/  '), '/repo/vc.rules.md');
    assert.equal(buildAgentFilePath('   '), null);
});

test('Rule-file cards escape rule text and enforcement values', () => {
    const controller = new AgentController(createApp());
    const html = controller.renderAgentRules({
        rules: [{
            text: '<img src=x onerror="alert(1)">',
            enforcement: 'STOP" onmouseover="alert(2)'
        }]
    });

    assert.doesNotMatch(html, /<img src=x/);
    assert.doesNotMatch(html, /class="[^"]*" onmouseover=/);
    assert.match(html, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;/);
    assert.match(html, /STOP&quot; onmouseover=&quot;alert\(2\)/);
    assert.match(html, /bg-secondary/);
});

test('Enforcement picker escapes a rule read back from a data attribute', (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    globalThis.document = { querySelectorAll: () => [] };

    const app = createApp();
    let modalHtml = '';
    app.showModal = (_title, html) => { modalHtml = html; };
    const controller = new AgentController(app);

    controller.showEnforcementPicker({ rules: [] }, '<svg onload="alert(1)">');

    assert.doesNotMatch(modalHtml, /<svg onload=/);
    assert.match(modalHtml, /&lt;svg onload=&quot;alert\(1\)&quot;&gt;/);
});

test('Rule-file wizard review normalizes enforcement before using it in badge markup', (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    globalThis.document = {
        getElementById() { return { addEventListener() {} }; }
    };

    const controller = new AgentController(createApp());
    controller.wizardState.directory = 'C:\\repo';
    controller.wizardState.selectedRules = [{
        text: '<img src=x onerror="alert(1)">',
        enforcement: 'STOP" onmouseover="alert(2)'
    }];
    const container = { innerHTML: '' };

    controller.renderStep4Review(container);

    assert.doesNotMatch(container.innerHTML, /<img src=x/);
    assert.match(container.innerHTML, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;/);
    assert.doesNotMatch(container.innerHTML, /onmouseover/);
    assert.match(container.innerHTML, /class="badge badge-warn"/);
    assert.match(container.innerHTML, /⚠️ WARN/);
});

test('Inline rule editor escapes rule text and drives the row tone from a normalized level', () => {
    const app = createApp();
    app.getAgentFileViewModel = () => ({ displayName: 'repo/Rules', relativePath: 'vc.rules.md' });
    app.data.agents = [{
        path: 'C:\\repo\\vc.rules.md',
        rules: [
            { text: '<img src=x onerror="alert(1)">', enforcement: 'STOP' },
            { text: 'Tests accompany behavior changes', enforcement: 'bogus" data-owned="yes' }
        ]
    }];

    const controller = new AgentController(app);
    controller.selectedAgentPath = 'C:\\repo\\vc.rules.md';
    const host = createRuleEditorHost();
    controller.renderInlineRuleEditor(createWorkspaceRoot(host));

    assert.doesNotMatch(host.innerHTML, /<img src=x/);
    assert.match(host.innerHTML, /&lt;img src=x onerror=&quot;alert\(1\)&quot;&gt;/);
    // An unrecognized enforcement falls back to WARN rather than reaching the attribute.
    assert.doesNotMatch(host.innerHTML, /data-owned=/);
    assert.match(host.innerHTML, /data-level="STOP"/);
    assert.match(host.innerHTML, /data-level="WARN"/);
    assert.match(host.innerHTML, /data-rule-remove="1"/);
});

test('Inline rule editor shows an empty state for a rule file that enforces nothing', () => {
    const app = createApp();
    app.getAgentFileViewModel = () => ({ displayName: 'repo/Rules', relativePath: 'Tests/vc.rules.md' });
    app.data.agents = [{ path: 'C:\\repo\\Tests\\vc.rules.md', rules: [] }];

    const controller = new AgentController(app);
    controller.selectedAgentPath = 'C:\\repo\\Tests\\vc.rules.md';
    const host = createRuleEditorHost();
    controller.renderInlineRuleEditor(createWorkspaceRoot(host));

    assert.match(host.innerHTML, /No rules yet/);
    assert.doesNotMatch(host.innerHTML, /rules-rule-list/);
    // Add rule stays reachable — the empty state is the main path to a first rule.
    assert.match(host.innerHTML, /data-rule-editor-add/);
});

test('Rule-file selection exposes aria-current without replacing the focused tree button', () => {
    const app = createApp();
    app.data.agents = [
        { path: 'C:\\repo\\vc.rules.md' },
        { path: 'C:\\repo\\src\\vc.rules.md' }
    ];
    const items = [{ selected: false }, { selected: false }];
    const attributes = [new Map(), new Map()];
    const buttons = [0, 1].map(index => ({
        dataset: { agentTreeIndex: String(index) },
        closest() {
            return { classList: { toggle(_name, selected) { items[index].selected = selected; } } };
        },
        setAttribute(name, value) { attributes[index].set(name, value); },
        removeAttribute(name) { attributes[index].delete(name); }
    }));
    const controller = new AgentController(app);
    controller.selectedAgentPath = app.data.agents[1].path;

    controller.updateAgentFileSelection({ querySelectorAll: () => buttons });

    assert.equal(items[0].selected, false);
    assert.equal(items[1].selected, true);
    assert.equal(attributes[0].has('aria-current'), false);
    assert.equal(attributes[1].get('aria-current'), 'true');
});

test('Rule-manager mutations focus the replacement control after a rerender', (t) => {
    const originalRequestAnimationFrame = globalThis.requestAnimationFrame;
    t.after(() => { globalThis.requestAnimationFrame = originalRequestAnimationFrame; });
    globalThis.requestAnimationFrame = callback => callback();

    let focusOptions = null;
    const replacement = {
        focus(options) { focusOptions = options; }
    };
    const manager = {
        isConnected: true,
        matches: selector => selector === '[data-rule-manager-modal]',
        querySelector(selector) {
            return selector === '[data-rule-editor-add]' ? replacement : null;
        }
    };
    const controller = new AgentController(createApp());

    controller.focusRuleManagerControl(manager, ['[data-rule-editor-add]']);

    assert.deepEqual(focusOptions, { preventScroll: true });
});

test('Inline add-rule modal escapes rule names and the target path', (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    globalThis.document = { getElementById: () => null };

    const app = createApp();
    let modalHtml = '';
    app.showModal = (_title, html) => { modalHtml = html; };
    app.data.availableRulesWithDescriptions = [
        { name: '<svg onload="alert(1)">', description: '<b>desc</b>' }
    ];

    const controller = new AgentController(app);
    controller.showInlineAddRule({ path: 'C:\\repo\\"><script>', rules: [] }, null);

    assert.doesNotMatch(modalHtml, /<svg onload=/);
    assert.doesNotMatch(modalHtml, /<script>/);
    assert.match(modalHtml, /&lt;svg onload=&quot;alert\(1\)&quot;&gt;/);
    assert.match(modalHtml, /&lt;b&gt;desc&lt;\/b&gt;/);
});

test('Path lock templates materialize canonical relative rules', () => {
    const controller = new AgentController(createApp());

    assert.equal(
        controller.buildPathLockRuleText("File Lock('path to file')", String.raw`src\config.json`),
        "File Lock('src/config.json')");
    assert.equal(
        controller.buildPathLockRuleText("Directory Lock('path to directory')", './src/generated/'),
        "Directory Lock('src/generated')");
    assert.equal(
        controller.extractPathLockPath("Directory Lock('src/generated')"),
        'src/generated');
});

test('Path lock templates reject absolute and escaping paths', () => {
    const controller = new AgentController(createApp());

    assert.throws(
        () => controller.buildPathLockRuleText("File Lock('path to file')", 'C:\\secrets.txt'),
        /relative/);
    assert.throws(
        () => controller.buildPathLockRuleText("Directory Lock('path to directory')", '../outside'),
        /cannot leave/);
    assert.throws(
        () => controller.buildPathLockRuleText("File Lock('path to file')", "it's.txt"),
        /single quote/);
});

// A rule is one line of vc.rules.md. A line break in the path is written back as two lines, and a
// second line opening with '#' ends the rules section — silently unenforcing every rule below it
// in both the Git hook and this page. Rejected server-side too; this is the readable error.
test('Path lock templates reject a path carrying a line break', () => {
    const controller = new AgentController(createApp());

    assert.throws(
        () => controller.buildPathLockRuleText("File Lock('path to file')", 'x\n## Injected'),
        /line break/);
    assert.throws(
        () => controller.buildPathLockRuleText("Directory Lock('path to directory')", 'x\r\n## Injected'),
        /line break/);
});

// Reading is not writing: the editor must still show what a hand-edited file actually says.
test('Path lock paths round-trip through the editor even across a line break', () => {
    const controller = new AgentController(createApp());

    assert.equal(
        controller.extractPathLockPath("File Lock('x\n## Injected')"),
        'x\n## Injected');
});

test('Validate rule file renders successful API responses without a missing renderer call', async (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const section = { style: {} };
    const resultsContainer = { innerHTML: '' };
    const root = {
        querySelector(selector) {
            if (selector === '[data-agent-validation-section]') return section;
            if (selector === '[data-agent-validation-results]') return resultsContainer;
            return null;
        }
    };
    globalThis.document = {
        querySelector(selector) {
            return selector === '[data-view="agent-edit"]' ? root : null;
        }
    };

    const app = createApp();
    app.ruleController = {};
    app.apiCall = async () => ({
        passed: true,
        message: 'Validation <passed>',
        results: [{
            ruleName: '<rule>',
            enforcement: 'STOP',
            passed: true,
            message: '<ok>',
            affectedFiles: ['src/<unsafe>.js']
        }]
    });
    const controller = new AgentController(app);

    await controller.validateAgent({ path: 'C:\\repo\\vc.rules.md' });

    assert.equal(section.style.display, 'block');
    assert.match(resultsContainer.innerHTML, /Validation &lt;passed&gt;/);
    assert.match(resultsContainer.innerHTML, /&lt;rule&gt;/);
    assert.match(resultsContainer.innerHTML, /src\/&lt;unsafe&gt;\.js/);
    assert.doesNotMatch(resultsContainer.innerHTML, /<rule>/);
});

test('Manage rules records the modal and selected file as the page Back destination', () => {
    const app = createApp();
    const calls = [];
    app.navigationStack = [{ view: 'dashboard', data: { keep: 'context' } }];
    app.closeModal = () => calls.push('close');
    app.updateCurrentViewData = data => {
        calls.push('save-parent');
        app.navigationStack[0].data = data;
    };
    app.navigate = (view, data) => calls.push({ view, data });
    const controller = new AgentController(app);
    const agent = { path: 'C:\\repo\\src\\vc.rules.md' };
    controller.selectedAgentPath = agent.path;
    controller.navigateFromRuleManager('agent-edit', agent, {
        matches: selector => selector === '[data-rule-manager-modal]'
    });
    assert.deepEqual(app.navigationStack[0].data, {
        keep: 'context', reopenRuleManager: true, selectedAgentPath: agent.path
    });
    assert.deepEqual(calls, ['close', 'save-parent', { view: 'agent-edit', data: agent }]);
});

test('Full editor offers rule-specific Edit and Remove controls with no selection prerequisite', () => {
    const controller = new AgentController(createApp());
    const html = controller.renderAgentRules({ rules: [{ text: 'Log all file changes', enforcement: 'STOP' }] });
    assert.match(html, /data-rule-edit="0"/);
    assert.match(html, /data-rule-delete="0"/);
    assert.doesNotMatch(html, /data-rule-select|>\s*Select\s*</);
    const empty = controller.renderAgentRules({ rules: [] });
    assert.match(empty, /Use Add rule above/);
    assert.doesNotMatch(empty, /data-rule-edit|data-rule-delete/);
});

test('Refreshing a mutation from the full editor paints updated rules in that editor', () => {
    const app = createApp();
    const updated = { path: 'C:\\repo\\vc.rules.md', rules: [{ text: 'New rule', enforcement: 'WARN' }] };
    app.data.agents = [updated];
    const controller = new AgentController(app);
    controller.currentAgent = { path: updated.path, rules: [] };
    let loaded = null;
    controller.loadAgentEdit = agent => { loaded = agent; };
    controller.refreshRuleSurface({ isConnected: true, matches: () => true });
    assert.equal(loaded, updated);
    loaded = null;
    controller.refreshRuleSurface({ isConnected: false, matches: () => true });
    assert.equal(loaded, null, 'a completed request must not reopen an editor after navigation');
});

test('Open file in VS Code uses the existing webview file bridge', async t => {
    const originalWindow = globalThis.window;
    t.after(() => { globalThis.window = originalWindow; });
    const app = createApp();
    let opened = null;
    globalThis.window = { __viberails_VSCODE__: true, __viberails_openFile__: file => { opened = file; } };
    app.apiCall = () => assert.fail('webview file opening must stay in the existing VS Code window');
    const controller = new AgentController(app);
    await controller.editInVSCode({ path: 'C:\\repo with spaces\\src\\vc.rules.md' });
    assert.equal(opened, 'C:\\repo with spaces\\src\\vc.rules.md');
});

test('Open file in VS Code sends the exact selected file from a browser', async t => {
    const originalWindow = globalThis.window;
    t.after(() => { globalThis.window = originalWindow; });
    globalThis.window = {};
    const app = createApp();
    let request = null;
    app.apiCall = async (...args) => { request = args; };
    const controller = new AgentController(app);
    await controller.editInVSCode({ path: 'C:\\repo with spaces\\vc.rules.md' });
    assert.deepEqual(request, ['/api/v1/cli/launch/vscode', 'POST', { path: 'C:\\repo with spaces\\vc.rules.md' }]);
});

test('New rule file returns to Manage rules and selects the created path across separator differences', async t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    const createButton = { disabled: false };
    globalThis.document = { getElementById: () => createButton };
    const app = createApp();
    app.navigationStack = [{ view: 'dashboard', data: { reopenRuleManager: true } }, { view: 'agent-create', data: {} }];
    app.apiCall = async () => ({ path: 'C:\\repo\\src/vc.rules.md' });
    app.refreshDashboardData = async () => {};
    app.data.agents = [{ path: 'C:\\repo\\src\\vc.rules.md', rules: [] }];
    let returned = false;
    app.goBack = () => { returned = true; };
    app.navigate = () => assert.fail('creation from Manage rules should return directly to the modal');
    const controller = new AgentController(app);
    controller.wizardState.directory = 'C:\\repo\\src/vc.rules.md';
    await controller.createAgent();
    assert.equal(returned, true);
    assert.equal(app.navigationStack[0].data.selectedAgentPath, 'C:\\repo\\src\\vc.rules.md');
    assert.equal(createButton.disabled, false);
});

test('Rule-file path filtering keeps full matching paths and hides unrelated groups', () => {
    const controller = new AgentController(createApp());
    const rows = [
        { textContent: 'src/shared/deep/vc.rules.md Applies to src/shared/deep', hidden: false },
        { textContent: 'tests/vc.rules.md Applies to tests', hidden: false }
    ];
    const groups = rows.map(row => ({ hidden: false, open: false, querySelectorAll: () => [row], matches: () => true }));
    const input = { value: 'shared/deep' };
    const empty = { hidden: true };
    const root = {
        querySelector: selector => selector === '[data-rule-file-search]' ? input : empty,
        querySelectorAll: selector => selector === '.agent-file-tree-item' ? rows : groups
    };
    controller.filterRuleFiles(root);
    assert.deepEqual(rows.map(row => row.hidden), [false, true]);
    assert.deepEqual(groups.map(group => group.hidden), [false, true]);
    assert.equal(empty.hidden, true);
    input.value = 'does-not-exist';
    controller.filterRuleFiles(root);
    assert.equal(empty.hidden, false);
    input.value = '';
    controller.filterRuleFiles(root);
    assert.deepEqual(rows.map(row => row.hidden), [false, false]);
    assert.deepEqual(groups.map(group => group.hidden), [false, false]);
});

test('Rule pages rely on the single global Back handler instead of popping history twice', () => {
    assert.doesNotMatch(AgentController.prototype.loadAgentEdit.toString(), /bindAction\(root, '\[data-action="go-back"\]'/);
    assert.doesNotMatch(AgentController.prototype.loadAgentCreate.toString(), /bindAction\(root, '\[data-action="go-back"\]'/);
});

test('Back reopens Manage rules only after the parent page finishes loading', async () => {
    const app = Object.create(VibeControlApp.prototype);
    let finishLoad;
    app.dashboardController = { loadDashboard: () => new Promise(resolve => { finishLoad = resolve; }) };
    app.updateActiveSubNav = app.applyViewLayoutState = app.queueScrollPageToTop = () => {};
    let opened = 0;
    app.agentController = { openRuleManager: () => { opened++; } };
    const data = { reopenRuleManager: true, selectedAgentPath: 'C:\\repo\\Tests\\vc.rules.md' };
    app.currentView = 'dashboard';
    app.navigationStack = [{ view: 'dashboard', data }];
    app.loadView('dashboard', data);
    assert.equal(opened, 0);
    finishLoad();
    await Promise.resolve();
    assert.equal(opened, 1);
    assert.equal(app.agentController.selectedAgentPath, data.selectedAgentPath);
    assert.equal(app.navigationStack[0].data.reopenRuleManager, false, 'the return flag is consumed');
});

test('A stale parent-page load cannot reopen Manage rules after a later navigation', async () => {
    const app = Object.create(VibeControlApp.prototype);
    let finishLoad;
    app.dashboardController = { loadDashboard: () => new Promise(resolve => { finishLoad = resolve; }) };
    app.updateActiveSubNav = app.applyViewLayoutState = app.queueScrollPageToTop = () => {};
    app.agentController = { openRuleManager: () => assert.fail('stale load must not reopen the modal') };
    const data = { reopenRuleManager: true };
    app.currentView = 'dashboard';
    app.navigationStack = [{ view: 'dashboard', data }];
    app.loadView('dashboard', data);
    app.navigationStack = [{ view: 'dashboard', data: {} }];
    finishLoad();
    await Promise.resolve();
});

test('Rule-file identities expose the complete relative path and affected folder', () => {
    const app = Object.create(VibeControlApp.prototype);
    app.data = { configs: { rootPath: 'C:\\repo' } };
    app.getCurrentProjectDisplayName = () => 'My project';
    const nested = app.getAgentFileViewModel({ path: 'C:\\repo\\src\\deep\\long-folder\\vc.rules.md', rules: [] }, 0);
    assert.equal(nested.relativePath, 'src/deep/long-folder/vc.rules.md');
    assert.equal(nested.scopeLabel, 'src/deep/long-folder and its subfolders');
    const root = app.getAgentFileViewModel({ path: 'C:\\repo\\vc.rules.md', rules: [] }, 1);
    assert.equal(root.scopeLabel, 'Entire repository');
});

test('Files in scope excludes Git internals consistently from the count and includes covered dotfiles', async () => {
    const app = createApp();
    app.apiCall = async () => ({
        totalCount: 5,
        files: ['.git/config', '.git/objects/example', '.editorconfig', 'src/main.js', '.config/settings.json']
    });
    const controller = new AgentController(app);
    const list = { innerHTML: '' };
    const count = { textContent: '' };
    await controller.loadAgentFiles('C:\\repo\\vc.rules.md', list, count);
    assert.equal(count.textContent, '3 files');
    assert.doesNotMatch(list.innerHTML, /\.git/);
    assert.match(list.innerHTML, /\.editorconfig/);
    assert.match(list.innerHTML, /\.config/);
});

test('Path lock Browse converts absolute selections relative to the declaring rule file', () => {
    assert.equal(relativeRulePath('C:\\repo\\src\\vc.rules.md', 'C:\\repo\\src\\config.json'), 'config.json');
    assert.equal(relativeRulePath('C:\\repo\\src\\vc.rules.md', 'c:\\REPO\\src\\generated\\file.cs'), 'generated/file.cs');
    assert.equal(relativeRulePath('C:\\repo\\src\\vc.rules.md', 'C:\\repo\\src'), '.');
    assert.equal(relativeRulePath('/repo/src/vc.rules.md', '/repo/src/generated'), 'generated');
    assert.equal(ruleFileDirectory('C:/vc.rules.md'), 'C:/');
    assert.equal(relativeRulePath('C:/vc.rules.md', 'C:/'), '.');
    assert.equal(relativeRulePath('/vc.rules.md', '/src/config.json'), 'src/config.json');
    assert.throws(() => relativeRulePath('C:/repo/src/vc.rules.md', 'C:/repo/src-other/file.cs'), /outside/);
    assert.throws(() => relativeRulePath('C:/repo/src/vc.rules.md', 'D:/repo/src/file.cs'), /outside/);
    assert.throws(() => relativeRulePath('/repo/src/vc.rules.md', '/repo/src/../outside'), /inside/);
});

test('Directory Lock dot means the declaring folder while slash and drive-relative paths are rejected', () => {
    const controller = new AgentController(createApp());
    assert.equal(controller.buildPathLockRuleText("Directory Lock('path to directory')", '.'), "Directory Lock('.')");
    assert.equal(controller.buildPathLockRuleText("Directory Lock('path to directory')", './'), "Directory Lock('.')");
    assert.throws(() => controller.buildPathLockRuleText("Directory Lock('path to directory')", '/'), /Use '\.'/);
    assert.throws(() => controller.buildPathLockRuleText("File Lock('path to file')", 'C:config.json'), /relative/);
    assert.throws(() => controller.buildPathLockRuleText("File Lock('path to file')", '.'), /identify a file/);
    assert.match(controller.renderPathLockHelp('directory'), /containing/);
    assert.match(controller.renderPathLockHelp('directory'), /absolute path/);
});

test('Browse uses the right file/folder picker mode, scope, cancellation and outside-scope feedback', async () => {
    const app = createApp();
    const requests = [];
    const toasts = [];
    let response = { canceled: false, path: 'C:/repo/src/generated' };
    app.pickFileSystemEntry = async options => { requests.push(options); return response; };
    app.showToast = (...args) => toasts.push(args);
    const controller = new AgentController(app);
    const agent = { path: 'C:/repo/src/vc.rules.md' };
    const input = { value: '', isConnected: true };
    const button = { disabled: false };
    const directory = controller.getPathLockDefinition("Directory Lock('path to directory')");
    await controller.browseRulePath(agent, directory, input, button);
    assert.equal(requests[0].mode, 'directory');
    assert.equal(requests[0].initialPath, 'C:/repo/src');
    assert.equal(input.value, 'generated');
    response = { canceled: true };
    await controller.browseRulePath(agent, directory, input, button);
    assert.equal(input.value, 'generated');
    response = { canceled: false, path: 'C:/repo/elsewhere' };
    await controller.browseRulePath(agent, directory, input, button);
    assert.equal(input.value, 'generated');
    assert.match(toasts[0][1], /outside/);
    response = { canceled: false, path: 'C:/repo/src/config.json' };
    await controller.browseRulePath(agent, controller.getPathLockDefinition("File Lock('path to file')"), input, button);
    assert.equal(requests.at(-1).mode, 'file');
    assert.equal(input.value, 'config.json');
    assert.equal(button.disabled, false);
});

test('Forbidden-word input creates a populated canonical rule and rejects malformed CSV', () => {
    const controller = new AgentController(createApp());
    assert.equal(controller.buildCommitWordRuleText(' WIP, fix later, temporary '), 'Check commit message for: WIP, fix later, temporary');
    assert.equal(controller.buildCommitWordRuleText("don't, do not merge"), "Check commit message for: don't, do not merge");
    for (const input of ['', '  ', 'wip,,todo', ',wip', 'wip,', 'do\t not merge', 'wip\n## Injected', '"wip", todo', "'wip', todo"]) {
        assert.throws(() => controller.buildCommitWordRuleText(input), Error, input);
    }
});

test('Manage/full-editor Add submits the configured forbidden list instead of the bare rule template', async () => {
    const app = createApp();
    app.data.availableRulesWithDescriptions = [{ name: 'Check commit message for', description: 'Forbidden words' }];
    app.refreshDashboardData = async () => {};
    const requests = [];
    app.apiCall = async (...args) => requests.push(args);
    const controller = new AgentController(app);
    let submitHandler;
    let html;
    const submit = { disabled: false };
    const words = { value: 'WIP, fix later' };
    const form = {
        querySelector(selector) {
            if (selector === 'input[name="inline-rule-pick"]:checked') return { value: 'Check commit message for' };
            if (selector === 'input[name="inline-rule-level"]:checked') return { value: 'STOP' };
            if (selector === 'input[name="inline-commit-words"]') return words;
            if (selector === 'button[type="submit"]') return submit;
            return null;
        },
        querySelectorAll: () => [],
        addEventListener: (_name, handler) => { submitHandler = handler; }
    };
    controller.openRuleCrudModal = (_title, content) => {
        html = content;
        return { root: { querySelector: () => form }, close() {} };
    };
    controller.refreshRuleSurface = controller.focusRuleManagerControl = () => {};
    controller.showInlineAddRule({ path: 'C:/repo/vc.rules.md', rules: [] }, null);
    assert.match(html, /inline-commit-words/);
    assert.match(html, /WIP, fix later, temporary/);
    assert.match(html, /data-inline-path-lock-browse/);
    await submitHandler({ preventDefault() {}, currentTarget: form });
    assert.equal(requests[0][2].ruleText, 'Check commit message for: WIP, fix later');
    assert.equal(requests[0][2].enforcement, 'STOP');
    words.value = '';
    await submitHandler({ preventDefault() {}, currentTarget: form });
    assert.equal(requests.length, 1, 'empty list never reaches the API');
});

test('The wizard configures forbidden words and lock paths before moving to enforcement', t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    const handlers = {};
    globalThis.document = { getElementById: id => ({ addEventListener: (_type, handler) => { handlers[id] = handler; } }) };
    const app = createApp();
    app.data.availableRulesWithDescriptions = [
        { name: 'Check commit message for', description: 'Forbidden words' },
        { name: "Directory Lock('path to directory')", description: 'Lock directory' }
    ];
    const checkboxes = app.data.availableRulesWithDescriptions.map((rule, index) => ({
        id: `rule-${index}`, checked: true, dataset: { rule: rule.name }, addEventListener() {}
    }));
    const words = { value: 'WIP, fix later' };
    const container = {
        innerHTML: '',
        querySelectorAll: selector => selector.startsWith('input[type="checkbox"]') ? checkboxes : [],
        querySelector: selector => selector === '[data-commit-words-index="0"]' ? words : { value: '.' }
    };
    const controller = new AgentController(app);
    controller.wizardState.currentStep = 2;
    controller.renderWizardStep = () => {};
    controller.renderStep2Rules(container);
    assert.match(container.innerHTML, /data-wizard-path-browse="1"/);
    assert.match(container.innerHTML, /data-commit-words-index="0"/);
    handlers['wizard-next-btn']();
    assert.equal(controller.wizardState.currentStep, 3);
    assert.deepEqual(controller.wizardState.selectedRules, [
        { text: 'Check commit message for: WIP, fix later', enforcement: 'WARN' },
        { text: "Directory Lock('.')", enforcement: 'WARN' }
    ]);
    words.value = '';
    controller.wizardState.currentStep = 2;
    handlers['wizard-next-btn']();
    assert.equal(controller.wizardState.currentStep, 2, 'an unconfigured list stays on the input step');
    words.value = 'WIP,,todo';
    handlers['wizard-prev-btn']();
    assert.equal(controller.wizardState.currentStep, 1, 'invalid drafts must not prevent going Back');
    assert.equal(controller.wizardState.ruleDrafts['Check commit message for'].words, 'WIP,,todo');
    controller.renderStep2Rules(container);
    assert.match(container.innerHTML, /value="WIP,,todo"/, 'raw input survives Back to Directory and return');
});

test('Wizard Directory -> Rules -> Back -> Directory -> Review keeps one vc.rules.md suffix', t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    const handlers = new Map();
    const directoryInput = { value: '' };
    const wordsInput = { value: '' };
    const checkbox = { id: 'rule-0', checked: false, dataset: { rule: 'Check commit message for' }, addEventListener() {} };
    const container = {
        html: '',
        set innerHTML(value) {
            this.html = value;
            const directory = value.match(/id="agent-directory"[\s\S]*?value="([^"]*)"/);
            if (directory) directoryInput.value = directory[1];
            const words = value.match(/id="commit-words-0"[\s\S]*?value="([^"]*)"/);
            if (words) wordsInput.value = words[1];
        },
        get innerHTML() { return this.html; },
        querySelector(selector) {
            if (selector === '#agent-directory') return directoryInput;
            if (selector === '[data-commit-words-index="0"]') return wordsInput;
            return null;
        },
        querySelectorAll(selector) {
            if (selector === 'input[type="checkbox"]') return [checkbox];
            if (selector === 'input[type="checkbox"]:checked') return checkbox.checked ? [checkbox] : [];
            return [];
        }
    };
    globalThis.document = {
        querySelectorAll: () => [],
        getElementById(id) {
            if (id === 'wizard-content') return container;
            if (id === 'agent-directory') return directoryInput;
            return { addEventListener: (_type, handler) => handlers.set(id, handler) };
        }
    };
    const app = createApp();
    app.data.configs = { rootPath: 'C:/repo' };
    app.data.availableRulesWithDescriptions = [{ name: 'Check commit message for', description: 'Forbidden words' }];
    const controller = new AgentController(app);
    controller.renderWizardStep();
    directoryInput.value = 'C:/repo/wizard-locks';
    handlers.get('wizard-next-btn')();
    assert.equal(controller.wizardState.currentStep, 2);
    wordsInput.value = 'WIP,,temporary';
    checkbox.checked = true;
    handlers.get('wizard-prev-btn')();
    assert.equal(controller.wizardState.currentStep, 1);
    assert.equal(directoryInput.value, 'C:/repo/wizard-locks');
    handlers.get('wizard-next-btn')();
    assert.equal(controller.wizardState.directory, 'C:/repo/wizard-locks/vc.rules.md');
    assert.equal(wordsInput.value, 'WIP,,temporary');
    wordsInput.value = 'WIP, temporary';
    handlers.get('wizard-next-btn')();
    handlers.get('wizard-next-btn')();
    assert.equal(controller.wizardState.currentStep, 4);
    assert.match(container.innerHTML, /C:\/repo\/wizard-locks\/vc\.rules\.md/);
    assert.doesNotMatch(container.innerHTML, /vc\.rules\.md\/vc\.rules\.md/);
    assert.equal(buildAgentFilePath('C:/repo/vc.rules.md'), 'C:/repo/vc.rules.md');
    assert.equal(buildAgentFilePath('C:\\repo\\vc.rules.md'), 'C:\\repo\\vc.rules.md');
});

test('Full editor renders every individual scoped file card with type icons, including nested files', () => {
    const controller = new AgentController(createApp());
    const files = ['Repository.cs', 'DB/SqlStrings.cs', '<unsafe>/config.json', ...Array.from({ length: 15 }, (_value, index) => `src/File${index}.cs`)];
    const html = controller.renderAgentFileCards(files);
    assert.equal((html.match(/role="listitem"/g) || []).length, files.length);
    assert.match(html, /csharp\.svg/);
    assert.match(html, /Repository\.cs/);
    assert.match(html, /SqlStrings\.cs/);
    assert.match(html, /file-summary-path">DB\//);
    assert.match(html, /&lt;unsafe&gt;/);
    assert.doesNotMatch(html, /<unsafe>|dir-item|more directories/);
});

test('Full editor keeps file cards visible above Rules and the display-name action beside the name', () => {
    const indexSource = readFileSync(path.resolve('VibeRails/wwwroot/index.html'), 'utf8');
    const template = indexSource.slice(indexSource.indexOf('<template id="agent-edit-template">'), indexSource.indexOf('<template id="agent-create-template">'));
    assert.ok(template.indexOf('data-agent-files-list') < template.indexOf('data-agent-rules'));
    assert.doesNotMatch(template, /<details|rules-editor-disclosure/);
    assert.match(template, /rules-editor-name-row[\s\S]*?data-agent-display-name[\s\S]*?data-action="set-custom-agent-name"[\s\S]*?Set display name/);
    assert.doesNotMatch(template, />\s*Rename\s*</);
});

test('Display-name controls and form describe a searchable label without renaming the file', () => {
    const app = createApp();
    const controller = new AgentController(app);
    let title;
    let html;
    controller.openRuleCrudModal = (modalTitle, content) => { title = modalTitle; html = content; return { root: null }; };
    controller.showAgentCustomNameModal({ path: 'C:/repo/vc.rules.md', name: 'vc.rules.md' });
    assert.equal(title, 'Set display name');
    assert.match(html, /DB Rules for NoSQL DB 1/);
    assert.match(html, /friendly, searchable label/);
    assert.match(html, /file stays <code>vc\.rules\.md/);
    assert.match(html, /Save display name/);
    controller.showAgentCustomNameModal({ path: 'C:/repo/vc.rules.md', customName: 'DB Rules for NoSQL DB 1' });
    assert.equal(title, 'Edit display name');
    const appRenderer = Object.create(VibeControlApp.prototype);
    appRenderer.escapeHtml = escapeHtml;
    const item = { agent: { path: 'C:/repo/vc.rules.md' }, index: 0, shortName: 'vc.rules.md', scopePath: '.', relativePath: 'vc.rules.md', ruleCount: 0 };
    assert.match(appRenderer.renderAgentFileItem(item), /Set display name/);
    item.agent.customName = 'DB Rules for NoSQL DB 1';
    item.shortName = item.agent.customName;
    assert.match(appRenderer.renderAgentFileItem(item), /Edit display name/);
    assert.match(appRenderer.renderAgentFileItem(item), /DB Rules for NoSQL DB 1/);
});

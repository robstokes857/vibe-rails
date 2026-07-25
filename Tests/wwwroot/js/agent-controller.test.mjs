import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/agent-controller.js');
const { AgentController } = await import(pathToFileURL(modulePath).href);

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

test('Agent rule cards escape rule text and enforcement values', () => {
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

test('Agent wizard review normalizes enforcement before using it in badge markup', (t) => {
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
    app.getAgentFileViewModel = () => ({ displayName: 'repo/Agent', relativePath: 'AGENTS.md' });
    app.data.agents = [{
        path: 'C:\\repo\\AGENTS.md',
        rules: [
            { text: '<img src=x onerror="alert(1)">', enforcement: 'STOP' },
            { text: 'Tests accompany behavior changes', enforcement: 'bogus" data-owned="yes' }
        ]
    }];

    const controller = new AgentController(app);
    controller.selectedAgentPath = 'C:\\repo\\AGENTS.md';
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
    app.getAgentFileViewModel = () => ({ displayName: 'repo/Agent', relativePath: 'Tests/AGENTS.md' });
    app.data.agents = [{ path: 'C:\\repo\\Tests\\AGENTS.md', rules: [] }];

    const controller = new AgentController(app);
    controller.selectedAgentPath = 'C:\\repo\\Tests\\AGENTS.md';
    const host = createRuleEditorHost();
    controller.renderInlineRuleEditor(createWorkspaceRoot(host));

    assert.match(host.innerHTML, /No rules yet/);
    assert.doesNotMatch(host.innerHTML, /rules-rule-list/);
    // Add rule stays reachable — the empty state is the main path to a first rule.
    assert.match(host.innerHTML, /data-rule-editor-add/);
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

test('Validate Agent renders successful API responses without a missing renderer call', async (t) => {
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

    await controller.validateAgent({ path: 'C:\\repo\\AGENTS.md' });

    assert.equal(section.style.display, 'block');
    assert.match(resultsContainer.innerHTML, /Validation &lt;passed&gt;/);
    assert.match(resultsContainer.innerHTML, /&lt;rule&gt;/);
    assert.match(resultsContainer.innerHTML, /src\/&lt;unsafe&gt;\.js/);
    assert.doesNotMatch(resultsContainer.innerHTML, /<rule>/);
});

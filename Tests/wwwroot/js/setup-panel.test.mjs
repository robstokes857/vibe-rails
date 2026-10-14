import test from 'node:test';
import assert from 'node:assert/strict';
import { buildSetupModel } from '../../../VibeRails/wwwroot/js/modules/setup-panel.js';

const method = (id, extra = {}) => ({ id, label: id, command: `install ${id}`, requires: null, note: null, recommended: false, ...extra });
const tool = (id, group, state, extra = {}) => ({
    id, name: extra.name || id, group, state, why: `${id} why`, docsUrl: 'https://example.test', brand: null,
    path: state === 'missing' ? null : `C:\\bin\\${id}.exe`, version: null, methods: [], ...extra
});

const status = tools => ({ platform: 'windows', checkedUtc: '2026-10-14T12:00:00Z', tools });
const section = (model, key) => model.sections.find(item => item.key === key);
const rows = (model, sectionKey, groupKey) => section(model, sectionKey)?.groups.find(group => group.key === groupKey)?.tools || [];

test('required comes first, with the AI CLIs as an "at least one" group, and optional last', () => {
    const model = buildSetupModel(status([
        tool('node', 'optional', 'missing'),
        tool('git', 'required', 'ready'),
        tool('claude', 'agent', 'missing'),
        tool('pwsh', 'required', 'missing'),
        tool('python', 'optional', 'ready')
    ]));
    assert.deepEqual(model.sections.map(item => item.key), ['required', 'optional']);
    assert.deepEqual(section(model, 'required').groups.map(group => group.key), ['required', 'agent']);
    assert.deepEqual(rows(model, 'required', 'required').map(item => item.id), ['git', 'pwsh']);
    assert.equal(section(model, 'required').groups[1].hint, 'Install at least one.');
    assert.deepEqual(rows(model, 'optional', 'optional').map(item => item.id), ['node', 'python']);
});

test('a machine with every required tool and one agent is complete and raises no alert', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'ready', { name: 'Git' }),
        tool('pwsh', 'required', 'ready', { name: 'PowerShell 7' }),
        tool('claude', 'agent', 'ready', { name: 'Claude Code' }),
        tool('codex', 'agent', 'missing', { name: 'Codex' }),
        tool('node', 'optional', 'missing')
    ]));
    assert.equal(model.complete, true);
    assert.equal(model.alert, null);
    // Once one agent is ready the others stay collapsed behind "Install…".
    assert.equal(rows(model, 'required', 'agent').find(item => item.id === 'codex').expandedByDefault, false);
});

test('missing required tools and no agent produce the app-load alert text', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'missing', { name: 'Git' }),
        tool('pwsh', 'required', 'missing', { name: 'PowerShell 7' }),
        tool('claude', 'agent', 'missing', { name: 'Claude Code' }),
        tool('node', 'optional', 'missing', { name: 'Node.js' })
    ]));
    assert.equal(model.complete, false);
    assert.equal(model.alert, 'Install Git, PowerShell 7 and an AI coding CLI to start using VibeRails.');
    assert.equal(rows(model, 'required', 'required')[1].expandedByDefault, true);
    // With no agent installed, every agent row opens so the user can pick one; optional rows stay shut.
    assert.equal(rows(model, 'required', 'agent')[0].expandedByDefault, true);
    assert.equal(rows(model, 'optional', 'optional')[0].expandedByDefault, false);
});

test('restart-needed counts as installed', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'ready'),
        tool('claude', 'agent', 'restart', { name: 'Claude Code' })
    ]));
    assert.equal(model.complete, true);
    assert.deepEqual(model.restartNeeded.map(item => item.id), ['claude']);
});

test('a method whose prerequisite is missing is blocked by that tool', () => {
    const model = buildSetupModel(status([
        tool('codex', 'agent', 'missing', { methods: [method('native', { recommended: true }), method('npm', { requires: 'node' })] }),
        tool('node', 'optional', 'missing', { name: 'Node.js' })
    ]));
    const codex = rows(model, 'required', 'agent')[0];
    assert.equal(codex.methods[0].blockedBy, null);
    assert.equal(codex.methods[1].blockedBy.name, 'Node.js');
});

test('package managers sit under Optional and only while missing', () => {
    const present = buildSetupModel(status([tool('winget', 'package-manager', 'ready')]));
    assert.equal(present.sections.length, 0);
    const absent = buildSetupModel(status([tool('winget', 'package-manager', 'missing')]));
    assert.deepEqual(rows(absent, 'optional', 'optional').map(item => item.id), ['winget']);
    assert.equal(rows(absent, 'optional', 'optional')[0].expandedByDefault, true);
});

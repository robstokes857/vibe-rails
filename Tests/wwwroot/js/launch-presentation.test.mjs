import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { launchPresentation, libraryScripts } from '../../../VibeRails/wwwroot/js/modules/launch-presentation.js';

const modules = new URL('../../../VibeRails/wwwroot/js/modules/', import.meta.url);
const source = (name) => readFileSync(new URL(name, modules), 'utf8');

test('library scripts keep their registration ID and say which interpreter runs them (VIBE-311)', () => {
    // PythonScriptsController.state: `name` is the registration ID, `fileName` the file.
    const scripts = libraryScripts({ scripts: [
        { name: 'reg_run', fileName: 'run.ps1', displayName: 'Start CP210', status: 'approved' },
        { name: 'reg_draft', fileName: 'draft.py', displayName: 'draft.py', status: 'unapproved' },
        { name: 'reg_edit', fileName: 'edited.sh', status: 'modified' },
        { name: 'legacy.py', status: 'approved' },
        { fileName: 'no-id.sh', status: 'approved' }, null
    ] });

    assert.deepEqual(scripts.map(({ id, name, status, canRun, subtitle }) => [id, name, status, canRun, subtitle]), [
        ['reg_run', 'Start CP210', 'approved', true, 'PowerShell script · run.ps1'],
        ['reg_draft', 'draft.py', 'unapproved', false, 'Python script'],
        ['reg_edit', 'edited.sh', 'modified', false, 'Bash script'],
        ['legacy.py', 'legacy.py', 'approved', true, 'Python script']
    ]);
});

test('only a signed version is runnable, and an unknown library lists nothing', () => {
    for (const status of ['unapproved', 'modified', undefined, 'APPROVED'])
        assert.equal(libraryScripts({ scripts: [{ name: 'a', fileName: 'a.py', status }] })[0].canRun, false, String(status));
    for (const state of [null, undefined, {}, { scripts: null }, { scripts: 'x' }])
        assert.deepEqual(libraryScripts(state), []);
});

test('a library script row uses the same icon as a script Action row', () => {
    const [script] = libraryScripts({ scripts: [{ name: 'reg', fileName: 'run.ps1', status: 'approved' }] });
    const action = launchPresentation({ definitionType: 'action', kind: 'script', name: 'Build' });
    assert.equal(script.logoHtml, action.logoHtml);
    assert.match(script.logoHtml, /fa-code/);
    assert.equal(action.subtitle, 'Script');
});

test('code quality checks are presented like every other Action: nothing filters them out', () => {
    assert.equal(launchPresentation({ definitionType: 'action', kind: 'code-quality', name: 'Gate' }).subtitle, 'Code quality check');
    // VIBE-309's presentation filter is gone; a reintroduced hide has to be a deliberate decision.
    for (const name of ['launch-presentation.js', 'launch-menu.js', 'ai-agents-controller.js',
        'board-card-automations.js', 'launch-lane-editor.js'])
        assert.doesNotMatch(source(name), /isLaunchVisible/, name);
    assert.match(source('launch-lane-editor.js'), /id: 'new:code-quality', name: 'Code quality check', kind: 'code-quality'/);
});

test('Quick actions lists signed library scripts after the saved launches and runs them through the library', () => {
    const menu = source('launch-menu.js');
    assert.match(menu, /libraryScripts\(library\?\.state\)\.filter\(\(script\) => script\.canRun\)/);
    assert.ok(menu.indexOf('...available.map') < menu.indexOf('...scripts.map'), 'scripts follow the agents and Actions');
    assert.match(menu, /library\.run\(script\.id\)/);
});

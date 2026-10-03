import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { findCheckAutomation } from '../../../VibeRails/wwwroot/js/modules/board-lane-agents.js';

// Lane checks are ordinary one-action Automations, and the server allows one Automation name per
// project (POST /api/v1/jobs answers 409 on a duplicate), so the Add form must find a same-name
// Automation whether or not it is enabled.
const check = (id, { name = 'VCA · Review · working-tree', enabled = true, kind = 3, scope = 'working-tree', actions } = {}) =>
    ({ id, name, enabled, actions: actions ?? [{ kind, arguments: [scope] }] });
const wanted = { name: 'VCA · Review · working-tree', kind: 3, scope: 'working-tree' };

test('a same-shape check is reused, enabled or disabled', () => {
    assert.equal(findCheckAutomation([], wanted), null);
    assert.equal(findCheckAutomation([check(1, { name: 'VCA · Review · unpushed', scope: 'unpushed' })], wanted), null);
    for (const enabled of [true, false]) {
        const match = findCheckAutomation([check(4, { enabled })], wanted);
        assert.equal(match.job.id, 4);
        assert.equal(match.reuse, true);
    }
    // The server trims names; letter case is not a second name either.
    assert.equal(findCheckAutomation([check(5, { name: ' vca · review · working-tree ' })], wanted).reuse, true);
});

test('a same-name Automation with another workflow blocks the check instead of POSTing', () => {
    for (const other of [
        check(6, { kind: 2 }),
        check(7, { scope: 'repository' }),
        check(8, { actions: [{ kind: 3, arguments: ['working-tree'] }, { kind: 1, scriptPath: 'a.py' }] }),
        check(9, { actions: [{ kind: 0, environmentId: 2 }] }),
        { id: 10, name: wanted.name, enabled: false }
    ]) {
        const match = findCheckAutomation([other], wanted);
        assert.equal(match.job.id, other.id);
        assert.equal(match.reuse, false, `job ${other.id}`);
    }
});

test('Add check focuses the saved row by its real id and re-enables a disabled match', () => {
    const source = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/board-lane-agents.js', import.meta.url), 'utf8');
    // run() reads a function focus target after the operation, once the new id exists.
    assert.match(source, /content\.querySelector\(typeof focusSelector === 'function' \? focusSelector\(\) : focusSelector\)/);
    const add = source.slice(source.indexOf("if (!event.target.matches('.board-lane-agents-add')) return;"), source.indexOf('const outside = event =>'));
    assert.match(add, /\}, \(\) => Number\.isFinite\(id\) \? `\[data-agent-id="\$\{id\}"\] \[data-agent-action="edit"\]` : '#board-lane-agent-choice'\);/);
    assert.doesNotMatch(add, /\}, `\[data-agent-id="\$\{id\}"\]/, 'no selector built before the id is known');
    assert.match(add, /findCheckAutomation\(jobs, \{ name, kind, scope: checkScope \}\)/);
    assert.match(add, /if \(existing && !existing\.reuse\) \{\s*content\.querySelector\('\[data-agent-error\]'\)\.textContent = `An Automation named/);
    assert.match(add, /else if \(!saved\.enabled\) saved = await updateJob\(saved\.id, \{ enabled: true \}\);/);
    // The re-enable shares the description save's PUT payload.
    assert.match(source, /const saved = await updateJob\(id, \{ description \}\);/);
});

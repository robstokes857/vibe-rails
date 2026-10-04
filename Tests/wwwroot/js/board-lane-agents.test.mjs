import test from 'node:test';
import assert from 'node:assert/strict';
import { prepareLaneAutomation } from '../../../VibeRails/wwwroot/js/modules/board-lane-agent-add.js';
import { matchesCommentFilter } from '../../../VibeRails/wwwroot/js/modules/agent-purpose.js';
import { findCheckAutomation, laneScriptAction } from '../../../VibeRails/wwwroot/js/modules/board-lane-agents.js';

test('lane scripts infer the interpreter and retain argument boundaries without shell parsing', () => {
    for (const [path, runtime] of [['scripts/test.py', 0], ['scripts/Check.PS1', 1], ['scripts/check.sh', 2]]) {
        assert.deepEqual(laneScriptAction(` ${path} `, '--message\r\ntwo words\r\n$(literal); value'), {
            kind: 1, scriptPath: path, scriptRuntime: runtime, arguments: ['--message', 'two words', '$(literal); value']
        });
    }
    assert.deepEqual(laneScriptAction('check.py').arguments, []);
    for (const path of ['', 'check.cmd', 'check.py --flag']) assert.throws(() => laneScriptAction(path), /repository/);
});

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

test('new checks use working changes and creation survives lane-save retries', async () => {
    for (const kind of ['check:2', 'check:3']) {
        const requests = [];
        const draft = { kind };
        const context = { draft, settings: { jobIds: [], jobs: [] }, jobs: [], environments: [],
            column: { name: 'Review' }, projectPath: '/repo', alive: () => true,
            api: async (...args) => { requests.push(args); return { ...args[2], id: 42 }; } };
        assert.equal(await prepareLaneAutomation(context), 42);
        assert.deepEqual(requests[0][2].actions, [{ kind: Number(kind.slice(6)), arguments: ['working-tree'] }]);
        assert.equal(await prepareLaneAutomation(context), 42);
        assert.equal(requests.length, 1);
    }
});

test('disabled matching check is re-enabled without reapproving actions', async () => {
    const job = check(7, { enabled: false });
    const updates = [];
    const id = await prepareLaneAutomation({ draft: { kind: 'check:3' }, settings: { jobIds: [], jobs: [job] }, jobs: [job],
        environments: [], column: { name: 'Review' }, alive: () => true,
        updateJob: async (id, changes) => { updates.push([id, changes]); return { ...job, ...changes }; },
        api: () => assert.fail('No new Automation needed') });
    assert.equal(id, 7);
    assert.deepEqual(updates, [[7, { enabled: true }]]);
});

test('purpose filters preserve attention and distinguish human and unclassified agents', () => {
    const testComment = { author: { kind: 'agent' }, purpose: 'testing' };
    assert.equal(matchesCommentFilter(testComment, 'testing'), true);
    assert.equal(matchesCommentFilter(testComment, 'code_review'), false);
    assert.equal(matchesCommentFilter(testComment, 'human'), false);
    assert.equal(matchesCommentFilter({ author: { kind: 'agent' } }, 'work'), true);
    assert.equal(matchesCommentFilter({ author: { kind: 'user' } }, 'work'), false);
    assert.equal(matchesCommentFilter({ ...testComment, isAttention: true }, 'code_review'), true);
});

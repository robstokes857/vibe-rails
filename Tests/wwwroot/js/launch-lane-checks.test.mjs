import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
    LANE_CHECK_SCOPES, DEFAULT_LANE_CHECK_SCOPE, LANE_BUILT_IN_CHECKS, LANE_CHECKS, LANE_PICKER_PREFERENCES,
    findLaneCheck, laneCheckName, laneChoiceLabel, laneCheckScopeName, laneChoiceKey, laneChoiceDetail,
    laneRowDetail, laneChoices, laneManageGroups
} from '../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js';

const source = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js', import.meta.url), 'utf8');
// getCliBrand reads window for VS Code asset paths; Node has none.
globalThis.window ??= {};

test('lane checks offer every per-entry scope and default to the card’s linked commits (VIBE-271)', () => {
    assert.deepEqual(LANE_CHECK_SCOPES.map(([value]) => value), ['card', 'unpushed', 'working-tree', 'repository']);
    assert.equal(DEFAULT_LANE_CHECK_SCOPE, 'card');
    for (const [, label, help] of LANE_CHECK_SCOPES) assert.ok(label && help, label);
    // Neither check kind may fall back to a hard-coded scope again.
    assert.doesNotMatch(source, /scope: check\.kind === 'vca'/);
    assert.doesNotMatch(source, /'working-tree' : 'card'/);
});

test('a lane-created check is named after what it does and its scope (VIBE-337)', () => {
    assert.equal(laneCheckName('vca', 'card'), 'Rules check · Linked commits');
    assert.equal(laneCheckName('code-quality', 'working-tree'), 'Code quality scan · Working tree');
    assert.equal(laneCheckName('vca', 'unpushed'), 'Rules check · Unpushed commits');
    assert.equal(laneCheckName('code-quality', 'repository'), 'Code quality scan · Repository');
    assert.deepEqual(LANE_BUILT_IN_CHECKS.map(check => [check.name, check.kind, check.key]),
        [['Rules check', 'vca', 'check:vca'], ['Code quality scan', 'code-quality', 'check:code-quality']]);
});

test('picker labels say what a saved check analyzes without repeating its name, old names included', () => {
    assert.equal(laneChoiceLabel({ kind: 'agent', name: 'Review' }), 'Review · Agent');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'Rules check' }), 'Rules check');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'Rules check · Linked commits', check: { scope: 'card' } }), 'Rules check · Linked commits');
    // Checks saved before VIBE-337 keep their names; the kind is not repeated under its new name.
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'VCA check', check: { scope: 'working-tree' } }), 'VCA check · Working tree');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'VCA check · Linked commits', check: { scope: 'card' } }), 'VCA check · Linked commits');
    assert.equal(laneChoiceLabel({ kind: 'code-quality', name: 'Code quality check · Repository', check: { scope: 'repository' } }),
        'Code quality check · Repository');
    assert.equal(laneChoiceLabel({ kind: 'code-quality', name: 'Gate', check: { scope: 'card', failGrade: 'B' } }),
        'Gate · Code quality scan · Linked commits, minimum B');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'Pinned', check: { scope: 'range', baseCommit: 'a', headCommit: 'b' } }),
        'Pinned · Rules check · Commit range');
    assert.equal(laneCheckScopeName(null), '');
});

test('an identical automatic check is reused; a grade, pinned commit, other kind or unavailable one is not', () => {
    const items = [
        { id: 'preset', definitionType: 'preset', kind: 'agent', canRunAutomatically: true },
        { id: 'graded', definitionType: 'action', kind: 'code-quality', canRunAutomatically: true, check: { scope: 'card', failGrade: 'C' } },
        { id: 'vca-tree', definitionType: 'action', kind: 'vca', canRunAutomatically: true, check: { scope: 'working-tree' } },
        { id: 'retired', definitionType: 'action', kind: 'vca', canRunAutomatically: false, check: { scope: 'card' } },
        { id: 'pinned', definitionType: 'action', kind: 'vca', canRunAutomatically: true, check: { scope: 'commit', headCommit: 'abc1234' } },
        { id: 'unknown', definitionType: 'action', kind: 'vca', canRunAutomatically: true }
    ];
    assert.equal(findLaneCheck(items, 'vca', 'card'), undefined);
    assert.equal(findLaneCheck(items, 'code-quality', 'card'), undefined);
    assert.equal(findLaneCheck(items, 'code-quality', 'working-tree'), undefined);
    assert.equal(findLaneCheck(items, 'vca', 'working-tree')?.id, 'vca-tree');
    items.push({ id: 'quality-card', definitionType: 'action', kind: 'code-quality', canRunAutomatically: true, check: { scope: 'card' } });
    assert.equal(findLaneCheck(items, 'code-quality', 'card')?.id, 'quality-card');
});

test('each choice says what it does: description, CLI, script path and interpreter, or the check (VIBE-337)', () => {
    assert.equal(laneChoiceDetail({ definitionType: 'preset', kind: 'agent', cli: 'claude', description: '  Reviews the diff.  ' }), 'Reviews the diff.');
    assert.equal(laneChoiceDetail({ definitionType: 'preset', kind: 'agent', cli: 'codex', description: '' }), 'Starts Codex with its saved prompt.');
    assert.equal(laneChoiceDetail({ definitionType: 'preset', kind: 'agent', cli: 'mystery' }), 'Starts mystery with its saved prompt.');
    assert.equal(laneChoiceDetail({ definitionType: 'action', kind: 'script', script: { path: 'scripts/release.ps1', runtime: 1 } }),
        'Runs scripts/release.ps1 with PowerShell.');
    assert.equal(laneChoiceDetail({ definitionType: 'action', kind: 'script', script: { path: 'a.py', runtime: 0 }, description: 'ignored' }),
        'Runs a.py with Python.');
    assert.equal(laneChoiceDetail({ definitionType: 'action', kind: 'script', script: { path: 'b.sh', runtime: 9 } }), 'Runs b.sh with its interpreter.');
    // An older catalog without the script field still says something useful.
    assert.equal(laneChoiceDetail({ definitionType: 'action', kind: 'script', description: 'Builds it.' }), 'Builds it.');
    assert.equal(laneChoiceDetail({ definitionType: 'action', kind: 'script' }), 'Runs a repository script.');
    for (const kind of ['vca', 'code-quality']) {
        assert.equal(laneChoiceDetail({ kind }), LANE_CHECKS[kind].about);
        assert.equal(laneChoiceDetail({ definitionType: 'action', kind, description: 'mine' }), LANE_CHECKS[kind].about, 'a check says what the kind does');
    }
    assert.match(LANE_CHECKS.vca.about, /vc\.rules\.md/);
    assert.match(LANE_CHECKS.vca.about, /VCA/, 'the old name stays findable');
    assert.match(LANE_CHECKS.vca.about, /never moves the card/);
    assert.match(LANE_CHECKS['code-quality'].about, /A–F/);
    assert.deepEqual([LANE_CHECKS.vca.view, LANE_CHECKS['code-quality'].view], ['dashboard', 'code-quality']);
});

test('run order rows show the kind and what each runs; checks show their scope', () => {
    assert.equal(laneRowDetail({ definitionType: 'preset', kind: 'agent', cli: 'codex' }), 'Agent · Starts Codex with its saved prompt.');
    assert.equal(laneRowDetail({ definitionType: 'action', kind: 'script', script: { path: 'x.sh', runtime: 2 } }), 'Script · Runs x.sh with Bash.');
    assert.equal(laneRowDetail({ definitionType: 'action', kind: 'vca', check: { scope: 'card' } }), 'Rules check · Scope: Linked commits');
    assert.equal(laneRowDetail({ definitionType: 'action', kind: 'code-quality', check: { scope: 'unpushed', failGrade: 'B' } }),
        'Code quality scan · Scope: Unpushed commits, minimum B');
    assert.equal(laneRowDetail({ definitionType: 'action', kind: 'vca' }), 'Rules check');
    assert.equal(laneRowDetail({ definitionType: 'action', kind: 'future', description: 'Does a thing.' }), 'Action · Does a thing.');
});

test('hidden entries leave the picker but stay in Manage list; lane order and other lanes are untouched', () => {
    const items = [
        { id: 'p1', definitionType: 'preset', kind: 'agent', canRunAutomatically: true },
        { id: 'p2', definitionType: 'preset', kind: 'agent', canRunAutomatically: true },
        { id: 'quick', definitionType: 'preset', kind: 'quick-launch', canRunAutomatically: false },
        { id: 's1', definitionType: 'action', kind: 'script', canRunAutomatically: true },
        { id: 'c1', definitionType: 'action', kind: 'vca', canRunAutomatically: true, check: { scope: 'card' } },
        { id: 'f1', definitionType: 'action', kind: 'future', canRunAutomatically: true }
    ];
    const selections = [{ definitionId: 'p2', definitionType: 'preset' }];
    const ids = list => list.map(item => item.id);
    assert.deepEqual(ids(laneChoices(items, selections)), ['p1', 's1', 'c1', 'f1', 'new:vca', 'new:code-quality']);
    const hidden = new Set(['preset:p1', 'check:vca', 'action:c1', 'preset:p2', 'action:gone']);
    assert.deepEqual(ids(laneChoices(items, selections, hidden)), ['s1', 'f1', 'new:code-quality']);
    assert.deepEqual(selections, [{ definitionId: 'p2', definitionType: 'preset' }], 'hiding never edits the lane');
    // Manage list shows everything the picker can offer, whatever this lane runs or hides.
    assert.deepEqual(laneManageGroups(items).map(([name, entries]) => [name, ids(entries)]), [
        ['Agents', ['p1', 'p2']], ['Scripts', ['s1']], ['Checks', ['c1', 'new:vca', 'new:code-quality']], ['Other actions', ['f1']]
    ]);
    assert.deepEqual(laneManageGroups([]).map(([name, entries]) => [name, ids(entries)]), [['Checks', ['new:vca', 'new:code-quality']]]);
    assert.deepEqual(['preset:p1', 'action:c1', 'check:vca'], [items[0], items[4], LANE_BUILT_IN_CHECKS[0]].map(laneChoiceKey));
    const everything = new Set([...items.map(laneChoiceKey), ...LANE_BUILT_IN_CHECKS.map(laneChoiceKey)]);
    assert.deepEqual(laneChoices(items, [], everything), []);
    assert.equal(LANE_PICKER_PREFERENCES, 'lane-picker/preferences');
});

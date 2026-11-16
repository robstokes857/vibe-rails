import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
    LANE_CHECK_SCOPES, DEFAULT_LANE_CHECK_SCOPE, findLaneCheck, laneCheckName, laneChoiceLabel, laneCheckScopeName
} from '../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js';

const source = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js', import.meta.url), 'utf8');

test('lane checks offer every per-entry scope and default to the card’s linked commits (VIBE-271)', () => {
    assert.deepEqual(LANE_CHECK_SCOPES.map(([value]) => value), ['card', 'unpushed', 'working-tree', 'repository']);
    assert.equal(DEFAULT_LANE_CHECK_SCOPE, 'card');
    for (const [, label, help] of LANE_CHECK_SCOPES) assert.ok(label && help, label);
    // Neither check kind may fall back to a hard-coded scope again.
    assert.doesNotMatch(source, /scope: check\.kind === 'vca'/);
    assert.doesNotMatch(source, /'working-tree' : 'card'/);
});

test('a lane-created check is named after its scope', () => {
    assert.equal(laneCheckName('vca', 'card'), 'VCA check · Linked commits');
    assert.equal(laneCheckName('code-quality', 'working-tree'), 'Code quality check · Working tree');
    assert.equal(laneCheckName('vca', 'unpushed'), 'VCA check · Unpushed commits');
    assert.equal(laneCheckName('code-quality', 'repository'), 'Code quality check · Repository');
});

test('picker labels say what a saved check analyzes without repeating its name', () => {
    assert.equal(laneChoiceLabel({ kind: 'agent', name: 'Review' }), 'Review · Agent');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'VCA check' }), 'VCA check');
    // The check an earlier quick-add made under the bare name, still working-tree scoped.
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'VCA check', check: { scope: 'working-tree' } }), 'VCA check · Working tree');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'VCA check · Linked commits', check: { scope: 'card' } }), 'VCA check · Linked commits');
    assert.equal(laneChoiceLabel({ kind: 'code-quality', name: 'Gate', check: { scope: 'card', failGrade: 'B' } }),
        'Gate · Code quality check · Linked commits, minimum B');
    assert.equal(laneChoiceLabel({ kind: 'vca', name: 'Pinned', check: { scope: 'range', baseCommit: 'a', headCommit: 'b' } }),
        'Pinned · VCA check · Commit range');
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

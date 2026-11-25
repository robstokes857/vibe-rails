import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
    LANE_CHECK_SCOPES, DEFAULT_LANE_CHECK_SCOPE, LANE_BUILT_IN_CHECKS, LANE_CHECKS, LANE_PICKER_PREFERENCES,
    findLaneCheck, laneCheckName, laneCheckGrade, laneChoiceLabel, laneCheckScopeName, laneChoiceKey, laneChoiceDetail,
    laneRowDetail, laneChoices, laneManageGroups, laneCheckReused, laneRowGradeEditable, laneRowGradeLabel, laneGradeSavedText
} from '../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js';
import { checkMinimumOptions, CHECK_MINIMUM_HELP, CHECK_MINIMUM_LABEL } from '../../../VibeRails/wwwroot/js/modules/job-checks.js';

const source = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/launch-lane-editor.js', import.meta.url), 'utf8');
const agentsSource = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/ai-agents-controller.js', import.meta.url), 'utf8');
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

test('a new Code quality scan can fail below grade A, B or C, and its name says so (VIBE-335)', () => {
    assert.deepEqual(['A', 'B', 'C'].map(grade => laneCheckGrade('code-quality', grade)), ['A', 'B', 'C']);
    // Only Code quality carries a grade; anything else is feedback only, as the server requires.
    for (const [kind, grade] of [['code-quality', ''], ['code-quality', 'D'], ['code-quality', undefined], ['vca', 'B']])
        assert.equal(laneCheckGrade(kind, grade), '', `${kind} ${grade}`);
    assert.equal(laneCheckName('code-quality', 'card', 'B'), 'Code quality scan · Linked commits, minimum B');
    assert.equal(laneCheckName('code-quality', 'unpushed', ''), 'Code quality scan · Unpushed commits');
    assert.equal(laneCheckName('vca', 'card', 'B'), 'Rules check · Linked commits');
    // The saved name already says the scope and grade, so the picker does not repeat them.
    assert.equal(laneChoiceLabel({ kind: 'code-quality', name: laneCheckName('code-quality', 'repository', 'A'), check: { scope: 'repository', failGrade: 'A' } }),
        'Code quality scan · Repository, minimum A');
    assert.match(source, /checkMinimumOptions\(''\)/, 'a new scan’s minimum passing grade starts at None');
    assert.match(source, /check: failGrade \? \{ scope, failGrade \} : \{ scope \}/, 'feedback only sends no grade');
    assert.doesNotMatch(LANE_CHECKS['code-quality'].about, /set in AI Agents/);
});

test('the minimum is labelled Minimum passing grade, with None first and each option leading with its value (VIBE-274)', () => {
    assert.equal(CHECK_MINIMUM_LABEL, 'Minimum passing grade');
    const options = minimum => [...checkMinimumOptions(minimum).matchAll(/<option value="([^"]*)"( selected)?>([^<]*)<\/option>/g)]
        .map(([, value, selected, text]) => [value, text, Boolean(selected)]);
    assert.deepEqual(options(''), [
        ['', 'None: findings are feedback only', true],
        ['A', 'A: the step fails below grade A', false],
        ['B', 'B: the step fails below grade B', false],
        ['C', 'C: the step fails below grade C', false]
    ]);
    assert.deepEqual(options('B').filter(([, , selected]) => selected).map(([value]) => value), ['B']);
    assert.equal(CHECK_MINIMUM_HELP, 'The grade is the overall Code quality grade of the code the change added, weighted by lines: '
        + 'the same grade Git Guard’s “Code quality minimum” rule checks. Below the minimum the step fails and its report lists what to fix. '
        + 'Explicit file exemptions apply. Either way, the result is saved on the card as a review.');
    // The picker's text points at the control, so the owner can find where the grade is set.
    assert.equal(LANE_CHECKS['code-quality'].about, 'Grades the added code A–F and saves a report on the card with what to fix. '
        + 'Findings are feedback unless you set a minimum passing grade; below it, the step fails.');
    // The lane picker, a run order row and the AI Agents editor share the label and the options.
    assert.doesNotMatch(source, /<label>Findings</);
    assert.match(source, /<label>\$\{CHECK_MINIMUM_LABEL\}<select class="form-select" data-lane-check-minimum /);
    assert.match(source, /\$\{CHECK_MINIMUM_LABEL\}<select class="form-select" data-lane-row-grade [^>]*>\$\{checkMinimumOptions\(item\.check\.failGrade \|\| ''\)\}/);
    assert.match(agentsSource, /select\(CHECK_MINIMUM_LABEL, 'grade', checkMinimumOptions\(saved\?\.check\?\.failGrade \|\| ''\)\)/);
    assert.doesNotMatch(agentsSource, /Minimum grade \(optional\)/, 'the grade is chosen, never typed');
});

test('a run order row edits the grade of a lane-made Code quality scan only (VIBE-274)', () => {
    const scan = (check, more = {}) => ({ id: 'scan', definitionType: 'action', kind: 'code-quality', name: 'Scan', canRunAutomatically: true, check, ...more });
    for (const [scope] of LANE_CHECK_SCOPES) {
        assert.equal(laneRowGradeEditable(scan({ scope })), true, scope);
        assert.equal(laneRowGradeEditable(scan({ scope, failGrade: 'C' })), true, `${scope} C`);
        // A catalog that sends the unset fields as null.
        assert.equal(laneRowGradeEditable(scan({ scope, failGrade: null, baseCommit: null, headCommit: null })), true, `${scope} nulls`);
    }
    // A pinned commit or range is edited in AI Agents, as is a scope the lane picker cannot create.
    assert.equal(laneRowGradeEditable(scan({ scope: 'commit', headCommit: 'abc1234' })), false);
    assert.equal(laneRowGradeEditable(scan({ scope: 'range', baseCommit: 'a', headCommit: 'b' })), false);
    assert.equal(laneRowGradeEditable(scan({ scope: 'card', headCommit: 'abc1234' })), false);
    assert.equal(laneRowGradeEditable(scan({ scope: 'card', baseCommit: 'abc1234' })), false);
    assert.equal(laneRowGradeEditable(scan({ scope: 'future' })), false);
    assert.equal(laneRowGradeEditable(scan(undefined)), false);
    assert.equal(laneRowGradeEditable(scan(null)), false);
    // Only Code quality has a grade; a retired selection has no definition at all.
    assert.equal(laneRowGradeEditable({ definitionType: 'action', kind: 'vca', check: { scope: 'card' } }), false);
    assert.equal(laneRowGradeEditable({ definitionType: 'action', kind: 'script', script: { path: 'a.py' } }), false);
    assert.equal(laneRowGradeEditable({ definitionType: 'preset', kind: 'agent' }), false);
    assert.equal(laneRowGradeEditable(undefined), false);
    assert.equal(laneRowGradeLabel({ name: 'Code quality scan · Linked commits' }), 'Minimum passing grade for Code quality scan · Linked commits');
    assert.equal(laneGradeSavedText('Code quality scan · Linked commits, minimum C', 'C'),
        'Saved. Code quality scan · Linked commits, minimum C fails the step below grade C.');
    assert.equal(laneGradeSavedText('Code quality scan · Linked commits'), 'Saved. Code quality scan · Linked commits: findings are feedback only.');
    assert.equal(laneGradeSavedText('Gate', ''), 'Saved. Gate: findings are feedback only.');
    // The row switches to another check; a saved one is never renamed or edited in place.
    assert.equal(source.match(/api\.save\(/g).length, 1, 'checks are only ever created');
    assert.match(source, /api\.save\('action', null, \{/);
    assert.match(source, /\{ \.\.\.selection, definitionId: target\.id, definitionType: target\.definitionType \}/, 'the row keeps its place and saved data');
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
    // VIBE-335: a chosen grade reuses only a scan with that same grade.
    assert.equal(findLaneCheck(items, 'code-quality', 'card', 'C')?.id, 'graded');
    assert.equal(findLaneCheck(items, 'code-quality', 'card', 'B'), undefined);
    assert.equal(findLaneCheck(items, 'code-quality', 'card', '')?.id, 'quality-card');
    assert.equal(findLaneCheck(items, 'code-quality', 'working-tree', 'C'), undefined);
    // A catalog that sends failGrade: null means feedback only.
    assert.equal(findLaneCheck([{ ...items.at(-1), id: 'null-grade', check: { scope: 'unpushed', failGrade: null } }], 'code-quality', 'unpushed')?.id, 'null-grade');
    assert.equal(findLaneCheck(items, 'vca', 'working-tree', 'C')?.id, 'vca-tree', 'a Rules check never carries a grade');
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
        // Pinned, so no built-in entry stands in for it (a reused check is covered below).
        { id: 'c1', definitionType: 'action', kind: 'vca', canRunAutomatically: true, check: { scope: 'commit', headCommit: 'abc1234' } },
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

test('a saved check the built-in entry reuses is offered once, through that entry; one it cannot make keeps its own', () => {
    const items = [
        // What the lane saved before VIBE-337 renamed the scan: the user saw it beside Code quality scan.
        { id: 'legacy', definitionType: 'action', kind: 'code-quality', name: 'Code quality check · Linked commits', canRunAutomatically: true, check: { scope: 'card' } },
        { id: 'copy', definitionType: 'action', kind: 'code-quality', name: 'Second copy', canRunAutomatically: true, check: { scope: 'card' } },
        { id: 'graded', definitionType: 'action', kind: 'code-quality', name: 'Gate C', canRunAutomatically: true, check: { scope: 'card', failGrade: 'C' } },
        { id: 'rules', definitionType: 'action', kind: 'vca', name: 'VCA check', canRunAutomatically: true, check: { scope: 'working-tree' } },
        { id: 'rules-graded', definitionType: 'action', kind: 'vca', name: 'Odd', canRunAutomatically: true, check: { scope: 'card', failGrade: 'B' } },
        { id: 'pinned', definitionType: 'action', kind: 'vca', name: 'Pinned', canRunAutomatically: true, check: { scope: 'range', baseCommit: 'a', headCommit: 'b' } },
        { id: 'unscoped', definitionType: 'action', kind: 'vca', name: 'Check', canRunAutomatically: true },
        { id: 'retired', definitionType: 'action', kind: 'vca', name: 'Retired', canRunAutomatically: false, check: { scope: 'repository' } },
        { id: 'script', definitionType: 'action', kind: 'script', name: 'Release', canRunAutomatically: true, check: null }
    ];
    const ids = list => list.map(item => item.id);
    assert.deepEqual(ids(items.filter(item => laneCheckReused(items, item))), ['legacy', 'graded', 'rules']);
    // Choosing the built-in entry with those settings adds each one, so none is out of reach.
    assert.equal(findLaneCheck(items, 'code-quality', 'card')?.id, 'legacy');
    assert.equal(findLaneCheck(items, 'code-quality', 'card', 'C')?.id, 'graded');
    assert.equal(findLaneCheck(items, 'vca', 'working-tree')?.id, 'rules');
    assert.deepEqual(ids(laneChoices(items, [])), ['copy', 'rules-graded', 'pinned', 'unscoped', 'script', 'new:vca', 'new:code-quality']);
    assert.deepEqual(laneManageGroups(items).map(([name, entries]) => [name, ids(entries)]), [
        ['Scripts', ['script']], ['Checks', ['copy', 'rules-graded', 'pinned', 'unscoped', 'new:vca', 'new:code-quality']]
    ]);
});

test('the run order saves on every change: no separate order save, unsaved state or discard prompt', () => {
    assert.doesNotMatch(source, /Save lane settings|data-lane-save|data-lane-unsaved|Discard lane changes|confirmDialog/);
    // Tom Select opens on focus; reopening the menu after Add read as a prompt to add a second entry.
    assert.match(source, /focusTomSelectClosed\(picker\)/);
});

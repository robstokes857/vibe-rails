import test from 'node:test';
import assert from 'node:assert/strict';
import { checkGradeBar, reportFiles } from '../../../VibeRails/wwwroot/js/modules/board-check-report.js';

// VIBE-274: the saved check report leads with the files below the check's minimum grade, so a step
// that failed its minimum always shows what to change. Before, only files rated Needs work or At risk
// were listed, and a minimum of B could fail with nothing under "What needs work".

const file = (name, score, rating, extra = {}) => ({ file: name, score, rating, categories: [], ...extra });
const report = { files: [
    file('src/clean.js', 5, 'Clean', { priority: 9 }),
    file('src/okay.js', 25, 'Okay'),
    file('src/poor.js', 40, 'NeedsWork'),
    file('src/failing.js', 60, 'AtRisk')
] };

test('the bar is the minimum grade the check requires, otherwise C', () => {
    assert.equal(checkGradeBar('A'), 'A');
    assert.equal(checkGradeBar('B'), 'B');
    assert.equal(checkGradeBar('C'), 'C');
    for (const none of [null, undefined, '', 'D', 'F', 'b']) assert.equal(checkGradeBar(none), 'C');
});

test('with no minimum, the files graded D or F need work, worst first', () => {
    const files = reportFiles(report, null);
    assert.deepEqual(files.map(entry => [entry.file.file, entry.needsWork, entry.findings.grade]), [
        ['src/failing.js', true, 'F'], ['src/poor.js', true, 'D'], ['src/okay.js', false, 'C'], ['src/clean.js', false, 'A']
    ]);
});

test('a minimum above C also lists the files that hold the grade under it', () => {
    assert.deepEqual(reportFiles(report, 'B').filter(entry => entry.needsWork).map(entry => entry.file.file),
        ['src/failing.js', 'src/poor.js', 'src/okay.js'], 'a C file is below a minimum of B');
    assert.deepEqual(reportFiles(report, 'A').filter(entry => entry.needsWork).map(entry => entry.file.file),
        ['src/failing.js', 'src/poor.js', 'src/okay.js'], 'health 95 is still an A');
    assert.equal(reportFiles({ files: [file('src/b.js', 15, 'Clean')] }, 'A')[0].needsWork, true, 'a B file is below a minimum of A');
});

test('a report without scores falls back to its rating, and malformed reports list nothing', () => {
    const files = reportFiles({ files: [file('a.js', null, 'AtRisk'), file('b.js', undefined, 'Clean'), null, { score: 3 }] }, 'B');
    assert.deepEqual(files.map(entry => [entry.file.file, entry.needsWork]), [['a.js', true], ['b.js', false]]);
    assert.deepEqual(reportFiles(null, 'C'), []);
    assert.deepEqual(reportFiles({ files: 'none' }), []);
});

test('the failure reason keeps its line breaks', async () => {
    // A minimum failed by files the report hides names them in the reason, one file and problem per line.
    const { readFileSync } = await import('node:fs');
    const css = readFileSync(new URL('../../../VibeRails/wwwroot/assets/board/board-check-report.css', import.meta.url), 'utf8');
    assert.match(css, /\.board-check-reason \{[^}]*white-space: pre-wrap;/);
});

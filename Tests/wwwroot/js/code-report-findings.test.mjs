import test from 'node:test';
import assert from 'node:assert/strict';
import { fileFindings, fileMeasurements, atlasQuality } from '../../../VibeRails/wwwroot/js/modules/code-report/findings.js';

// Limits mirror MintLint's ScoringProfile so the expectations read like the scan guide.
const LIMITS = {
    cyclomatic_complexity: [10, 20], cognitive_complexity: [15, 30], npath_complexity: [1000, 100000],
    nesting_depth: [4, 6], parameter_count: [4, 7], halstead_difficulty: [20, 40],
    maintainability_index: [50, 25, true], lines_of_code: [300, 600], method_count: [15, 30],
    field_count: [10, 20], lack_of_cohesion: [2, 4], fan_out: [15, 30], duplication: [0.1, 0.3],
    hard_coded_dependencies: [3, 8], ambient_dependencies: [3, 10]
};
const metric = (name, value, extra = {}) => {
    const [warn, critical, higherIsBetter = false] = LIMITS[name];
    return { name, value, warn, critical, higherIsBetter, score: 0, direction: higherIsBetter ? 'HB' : 'LB', ...extra };
};
const report = (categories, extra = {}) => ({ file: 'src/Payments/PaymentProcessor.cs', score: 62, rating: 'NeedsWork', categories, ...extra });

test('judges each measurement against its own limits, in the direction it reads', () => {
    const levels = fileMeasurements(report([{ name: 'All', weight: 1, metrics: [
        metric('cyclomatic_complexity', 10), metric('cognitive_complexity', 16), metric('nesting_depth', 6),
        metric('maintainability_index', 50), metric('lines_of_code', 299)
    ] }, { name: 'Maintainability', weight: 0.8, metrics: [metric('halstead_difficulty', 39.9)] }]))
        .reduce((result, item) => ({ ...result, [item.name]: item.level }), {});
    assert.deepEqual(levels, {
        cyclomatic_complexity: 'ok', // at the warning limit is not past it
        cognitive_complexity: 'warning',
        nesting_depth: 'critical', // at the critical limit is critical
        maintainability_index: 'ok',
        lines_of_code: 'ok',
        halstead_difficulty: 'warning'
    });
    const health = name => fileMeasurements(report([{ name: 'Maintainability', weight: 0.8, metrics: [metric('maintainability_index', name)] }]))[0].level;
    assert.equal(health(49), 'warning', 'higher-is-better reads below its warning limit as a problem');
    assert.equal(health(25), 'critical');
    assert.equal(health(80), 'ok');
});

test('ranks problems worst first and names where each was measured', () => {
    const findings = fileFindings(report([
        { name: 'Complexity', weight: 1, metrics: [
            metric('cyclomatic_complexity', 31, { source: 'Process', line: 12, snippet: 'void Process() {' }),
            metric('cognitive_complexity', 18, { source: 'Process', line: 12 }),
            metric('npath_complexity', 1_000_000_000, { source: 'Process', line: 12 }),
            metric('nesting_depth', 2, { source: 'Process', line: 12 })
        ] },
        { name: 'Size', weight: 0.7, metrics: [metric('lines_of_code', 420), metric('parameter_count', 9)] },
        { name: 'Duplication', weight: 0.6, metrics: [metric('duplication', 0.25)] }
    ]));
    assert.deepEqual(findings.problems.map(item => item.name), [
        'cyclomatic_complexity', 'npath_complexity', 'parameter_count', // critical; NPath's huge range is capped, then the weight decides
        'duplication', 'lines_of_code', 'cognitive_complexity' // warnings, nearest to critical first
    ]);
    const [first] = findings.problems;
    assert.equal(first.label, 'Cyclomatic complexity');
    assert.equal(first.valueText, '31');
    assert.equal(first.where, 'Process, line 12');
    assert.equal(first.limitText, 'critical at 20');
    assert.equal(first.limitsText, 'Warning above 10 · critical at 20');
    assert.match(first.fix, /^Split it into smaller functions/);
    assert.equal(first.snippet, 'void Process() {');
    const byName = Object.fromEntries(findings.measurements.map(item => [item.name, item]));
    assert.equal(byName.npath_complexity.valueText, '1B (cap)');
    assert.equal(byName.duplication.valueText, '25%');
    assert.equal(byName.duplication.limitText, 'warning above 10%');
    assert.equal(byName.lines_of_code.where, 'Whole file');
    assert.equal(byName.parameter_count.where, 'Top-level code', 'a function metric without a named function came from top-level code');
    assert.deepEqual(findings.within.map(item => item.name), ['nesting_depth']);
    assert.equal(findings.headline, '6 problems, 3 critical');
    assert.equal(findings.hotspot, '3 of the 6 problems are in Process, line 12.');
    assert.equal(findings.ratingLabel, 'Needs work');
    assert.equal(findings.tone, 'warning');
    assert.equal(findings.healthText, '38');
});

test('a composite index lists after the concrete problems it depends on, whatever its level', () => {
    const findings = fileFindings(report([
        { name: 'Maintainability', weight: 0.8, metrics: [
            metric('maintainability_index', 6),
            metric('halstead_difficulty', 28.7, { source: 'overshoot', line: 82 })
        ] },
        { name: 'Size', weight: 0.7, metrics: [metric('lines_of_code', 120), metric('method_count', 31, { source: 'Viewer', line: 4 })] }
    ]));
    assert.deepEqual(findings.problems.map(item => [item.name, item.level]), [
        ['method_count', 'critical'], ['halstead_difficulty', 'warning'], ['maintainability_index', 'critical']
    ]);
    assert.deepEqual(findings.within.map(item => item.name), ['lines_of_code']);
    assert.equal(findings.headline, '3 problems, 2 critical');
    assert.equal(findings.problems[2].limitsText, 'Warning below 50 · critical at 25 · higher is healthier');
});

test('a metric that feeds two categories is listed once, at its heavier weight', () => {
    const items = fileMeasurements(report([
        { name: 'Coupling', weight: 0.8, metrics: [metric('hard_coded_dependencies', 5), metric('fan_out', 2)] },
        { name: 'Testability', weight: 1, metrics: [metric('hard_coded_dependencies', 5), metric('ambient_dependencies', 1)] }
    ]));
    const dependencies = items.filter(item => item.name === 'hard_coded_dependencies');
    assert.equal(dependencies.length, 1);
    assert.equal(dependencies[0].weight, 1);
    assert.deepEqual(dependencies[0].categories, ['Coupling', 'Testability']);
    assert.match(dependencies[0].fix, /constructor or parameters/);
});

test('headlines and hotspots read naturally for every count', () => {
    const headline = metrics => fileFindings(report([{ name: 'Complexity', weight: 1, metrics }])).headline;
    assert.equal(headline([metric('cyclomatic_complexity', 3)]), 'No problems');
    assert.equal(headline([metric('cyclomatic_complexity', 12)]), '1 problem');
    assert.equal(headline([metric('cyclomatic_complexity', 25)]), '1 critical problem');
    assert.equal(headline([metric('cyclomatic_complexity', 25), metric('nesting_depth', 9)]), '2 critical problems');
    assert.equal(headline([metric('cyclomatic_complexity', 12), metric('nesting_depth', 5)]), '2 problems');
    assert.equal(fileFindings(report([])).headline, 'No measurements saved');
    const all = fileFindings(report([{ name: 'Complexity', weight: 1, metrics: [
        metric('cyclomatic_complexity', 25, { source: 'anonymous: test', line: 54 }),
        metric('cognitive_complexity', 40, { source: 'anonymous: test', line: 54 })
    ] }]));
    assert.equal(all.hotspot, 'All 2 problems are in anonymous: test, line 54.');
    const spread = fileFindings(report([{ name: 'Complexity', weight: 1, metrics: [
        metric('cyclomatic_complexity', 25, { source: 'Alpha', line: 1 }),
        metric('cognitive_complexity', 40, { source: 'Beta', line: 9 })
    ] }]));
    assert.equal(spread.hotspot, null, 'one problem per function names no hotspot');
});

test('missing and malformed values never become problems or numbers', () => {
    assert.equal(fileFindings(null), null);
    assert.equal(fileFindings('file'), null);
    const findings = fileFindings({ file: 'a.js', score: null, rating: 'Mystery', categories: [
        null, { metrics: null }, { name: 'Complexity', metrics: [null, { name: 'cyclomatic_complexity', value: '31' },
            { name: 'cognitive_complexity', value: 40 }, { name: 'custom_signal', value: 3, warn: 1, critical: 2 }] }
    ] });
    assert.deepEqual(findings.measurements.map(item => item.name), ['custom_signal', 'cognitive_complexity']);
    assert.equal(findings.measurements[1].level, 'ok', 'without limits nothing is called a problem');
    assert.equal(findings.measurements[1].limitText, 'no limit set');
    assert.equal(findings.measurements[0].label, 'Custom signal');
    assert.equal(findings.measurements[0].level, 'critical');
    assert.equal(findings.measurements[0].fix, null);
    assert.equal(findings.healthText, '—');
    assert.equal(findings.ratingLabel, 'Mystery');
    assert.equal(findings.tone, 'neutral');
});

test('the map inspector gets short display text: rating, headline and up to twelve problems', () => {
    const many = Array.from({ length: 15 }, (_, index) => ({ ...metric('cyclomatic_complexity', 30), name: `signal_${index}` }));
    const quality = atlasQuality(report([
        { name: 'Complexity', weight: 1, metrics: [metric('cyclomatic_complexity', 31, { source: 'Process', line: 12 }), ...many] }
    ], { rating: 'AtRisk' }));
    assert.equal(quality.rating, 'At risk');
    assert.equal(quality.tone, 'danger');
    assert.equal(quality.headline, '16 critical problems');
    assert.equal(quality.findings.length, 12);
    assert.deepEqual(quality.findings[0], { level: 'critical', title: 'Cyclomatic complexity 31', detail: 'Process, line 12 · critical at 20' });

    const clean = atlasQuality(report([{ name: 'Size', weight: 0.7, metrics: [metric('lines_of_code', 40)] }], { rating: 'Clean' }));
    assert.deepEqual(clean, { rating: 'Clean', tone: 'good', headline: 'No problems: every measurement is within its limits.', findings: [] });

    const long = atlasQuality(report([{ name: 'Complexity', weight: 1, metrics: [
        metric('cyclomatic_complexity', 31, { source: 'x'.repeat(500), line: 3 })
    ] }]));
    assert.equal(long.findings[0].detail.length, 300, 'the map rejects longer text, so it is shortened');
    assert.ok(long.findings[0].detail.endsWith('…'));
    assert.equal(atlasQuality(report([{ name: 'Measured', metrics: [{ name: 'lines_of_code', value: 40 }] }])), undefined,
        'with no limits saved there is nothing to judge');
});

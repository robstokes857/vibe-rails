import { formatNumber, healthFromConcern, isNumber } from './vendor/quality/report-model.js';

// What a saved MintLint file report says is wrong with the file. Every measurement is judged
// against its own warning and critical limits (never the 0–100 concern, which reads differently
// for higher-is-better metrics), ranked worst first, and paired with where it was measured, what it
// means and what to change. Plain data in, plain text out: the details panel and the map inspector
// format it, and nothing here touches the DOM.

/** Labels follow the scan guide; `scope` says what a Source/Line pair names. */
const GUIDE = {
    cyclomatic_complexity: { label: 'Cyclomatic complexity', scope: 'function',
        meaning: 'Counts the independent paths through a function: every if, loop, case, && and || adds one.',
        fix: 'Split it into smaller functions that each do one job. Return early instead of nesting else blocks, and replace long if/else or switch chains with a lookup table.' },
    cognitive_complexity: { label: 'Cognitive complexity', scope: 'function',
        meaning: 'Scores how hard a function is to follow: each branch or loop costs more the deeper it is nested.',
        fix: 'Flatten it: use guard clauses, move nested loop and branch bodies into named helpers, and avoid mixing && and || in one condition.' },
    npath_complexity: { label: 'NPath execution paths', scope: 'function',
        meaning: 'Counts the distinct routes through a function. Every independent if in sequence doubles it, so tests cannot cover them all.',
        fix: 'Break the function into steps that each return a result, and remove flags that change what later code in the same function does.' },
    nesting_depth: { label: 'Nesting depth', scope: 'function',
        meaning: 'How many blocks deep the code goes, such as an if inside a loop inside a try.',
        fix: 'Invert conditions and return early, and move the innermost block into its own function.' },
    parameter_count: { label: 'Parameters per function', scope: 'function',
        meaning: 'Long parameter lists are easy to pass in the wrong order and usually mean the function does several jobs.',
        fix: 'Group related values into one options object or record, or split the function so each part takes only what it needs.' },
    halstead_difficulty: { label: 'Halstead difficulty', scope: 'function',
        meaning: 'Measures how dense a function reads: many different operators and the same values used again and again make it hard to follow.',
        fix: 'Name intermediate results, pull repeated expressions into variables or helpers, and split long expressions into steps.' },
    // A composite of the others: it lists after the concrete problems, since fixing them is how it rises.
    maintainability_index: { label: 'Maintainability index', scope: 'file', composite: true,
        meaning: 'A 0 to 100 composite of the size, complexity and vocabulary of the measured code. Higher is healthier.',
        fix: 'It rises when the code gets shorter and its most complex functions get simpler, so fix the other problems in this file first.' },
    lines_of_code: { label: 'Lines of code', scope: 'file',
        meaning: 'Long files mix responsibilities and take longer to review.',
        fix: 'Move each separate responsibility into its own file or module.' },
    method_count: { label: 'Methods per type', scope: 'class',
        meaning: 'A type with this many methods usually has more than one job.',
        fix: 'Split it by responsibility: move each group of related methods into its own type.' },
    field_count: { label: 'Fields per type', scope: 'class',
        meaning: 'Every field is state that any method of the type can change.',
        fix: 'Group fields that change together into small types, or split the type.' },
    lack_of_cohesion: { label: 'Lack of cohesion (LCOM4)', scope: 'class',
        meaning: 'Counts groups of methods that share no fields and never call each other. 1 is cohesive; more means the type does that many unrelated jobs.',
        fix: 'Split the type along those groups: each group of methods and the fields it uses can become its own type.' },
    fan_out: { label: 'Fan-out (modules imported)', scope: 'file',
        meaning: 'Each imported module is another reason for this file to change or break.',
        fix: 'Split the file so each part imports only what it uses, or put the code that coordinates many modules behind one smaller interface.' },
    duplication: { label: 'Duplicated lines', scope: 'file', percent: true,
        meaning: 'Runs of five normalized lines that also appear elsewhere in the scanned files.',
        fix: 'Extract the repeated block into one shared function and call it from each place.' },
    hard_coded_dependencies: { label: 'Hard-coded dependencies', scope: 'file',
        meaning: 'Collaborators created inline, such as new Service(), cannot be swapped out in a test.',
        fix: 'Pass them in through the constructor or parameters instead of creating them inside.' },
    ambient_dependencies: { label: 'Ambient dependencies', scope: 'file',
        meaning: 'Direct calls to the clock, file system, network, environment, randomness or console make results depend on the machine.',
        fix: 'Wrap them behind small interfaces that you pass in, so a test can supply fixed values.' }
};

const RATING_LABELS = { Clean: 'Clean', Okay: 'Okay', NeedsWork: 'Needs work', AtRisk: 'At risk' };
const RATING_TONES = { Clean: 'good', Okay: 'neutral', NeedsWork: 'warning', AtRisk: 'danger' };
const LEVEL_ORDER = { critical: 2, warning: 1, ok: 0 };
const LEVEL_LABELS = { critical: 'Critical', warning: 'Warning', ok: 'Within limits' };
const NPATH_CAP = 1_000_000_000;

const fallbackLabel = name => String(name || 'Measurement').replaceAll('_', ' ').replace(/^./, c => c.toUpperCase());
const text = value => typeof value === 'string' && value.trim() ? value.trim() : null;
const numberOrNull = value => isNumber(value) ? value : null;
const listOf = value => Array.isArray(value) ? value : [];
const lineOf = value => Number.isInteger(value) && value > 0 ? value : null;
const weightOf = category => isNumber(category.weight) ? category.weight : 1;
const isMeasured = metric => Boolean(metric) && typeof metric.name === 'string' && isNumber(metric.value);
const higherIsBetter = metric => metric.higherIsBetter === true || metric.direction === 'HB';
const hasLimits = item => item.warn !== null && item.critical !== null;

function formatMeasure(value, guide, name) {
    if (!isNumber(value)) return '—';
    if (guide?.percent) return `${formatNumber(value * 100, 1)}%`;
    if (name === 'npath_complexity' && value >= NPATH_CAP) return '1B (cap)';
    return formatNumber(value, value >= 100 ? 0 : 1);
}

/** 0 at the warning limit, 1 at the critical limit, beyond 1 past it, whichever way the metric reads. */
function overshoot({ value, warn, critical, higherIsBetter }) {
    // Mirror a higher-is-better scale so both directions measure the same way.
    const [from, to, at] = higherIsBetter ? [-warn, -critical, -value] : [warn, critical, value];
    return to > from ? (at - from) / (to - from) : 0;
}

/** At the critical limit is critical; past the warning limit is a warning; at it is not. */
function levelOf(item) {
    if (!hasLimits(item)) return 'ok';
    const sign = item.higherIsBetter ? -1 : 1;
    if (sign * item.value >= sign * item.critical) return 'critical';
    return sign * item.value > sign * item.warn ? 'warning' : 'ok';
}

function locate(item, guide) {
    // A function metric without a named source came from code outside any function.
    const name = item.source || (guide?.scope === 'function' ? 'Top-level code' : null);
    if (!name) return 'Whole file';
    return item.line ? `${name}, line ${item.line}` : name;
}

/** The short limit for a row ("critical at 20") and the full pair for its details. */
function limitTexts(item, guide) {
    if (!hasLimits(item)) return { limitText: 'no limit set', limitsText: 'No limit set' };
    const warning = `warning ${item.higherIsBetter ? 'below' : 'above'} ${formatMeasure(item.warn, guide)}`;
    const critical = `critical at ${formatMeasure(item.critical, guide)}`;
    const healthier = item.higherIsBetter ? ' · higher is healthier' : '';
    return {
        limitText: { critical, warning }[item.level] || `${warning} · ${critical}`,
        limitsText: `${warning[0].toUpperCase()}${warning.slice(1)} · ${critical}${healthier}`
    };
}

/** One saved metric, judged and described. */
function measurement(metric, category) {
    const guide = GUIDE[metric.name] || {};
    const item = {
        name: metric.name,
        label: guide.label || fallbackLabel(metric.name),
        value: metric.value,
        warn: numberOrNull(metric.warn),
        critical: numberOrNull(metric.critical),
        higherIsBetter: higherIsBetter(metric),
        concern: numberOrNull(metric.score),
        source: text(metric.source),
        line: lineOf(metric.line),
        snippet: text(metric.snippet) && metric.snippet,
        categories: [category.name].filter(text),
        weight: weightOf(category),
        composite: guide.composite === true,
        meaning: guide.meaning || null,
        fix: guide.fix || null
    };
    item.level = levelOf(item);
    return Object.assign(item, limitTexts(item, guide), {
        levelLabel: LEVEL_LABELS[item.level],
        overshoot: overshoot(item),
        valueText: formatMeasure(item.value, guide, item.name),
        where: locate(item, guide)
    });
}

/**
 * Problems first, the actionable ones before a composite that only improves when they do; then
 * critical before warning, worst first. The measurements within their limits follow, nearest first.
 */
function rank(left, right) {
    const first = sortKeys(left), second = sortKeys(right);
    const at = first.findIndex((value, index) => value !== second[index]);
    return at < 0 ? left.label.localeCompare(right.label) : second[at] - first[at];
}

/** Most significant first; a larger key sorts earlier. */
function sortKeys(item) {
    const problem = item.level === 'ok' ? 0 : 1;
    // Overshoot is capped, so a metric with an exponential range (NPath) cannot drown out everything else.
    return [problem, problem && !item.composite ? 1 : 0, LEVEL_ORDER[item.level], Math.min(2, item.overshoot), item.weight, item.concern ?? 0];
}

/**
 * Every measured metric of one saved file report, each judged against its own limits. A metric
 * that feeds two categories (hard-coded dependencies) is listed once.
 */
export function fileMeasurements(file) {
    const byName = new Map();
    for (const category of listOf(file?.categories).filter(Boolean)) {
        for (const metric of listOf(category.metrics).filter(isMeasured)) {
            const known = byName.get(metric.name);
            if (!known) byName.set(metric.name, measurement(metric, category));
            else {
                known.weight = Math.max(known.weight, weightOf(category));
                known.categories.push(...[category.name].filter(text));
            }
        }
    }
    return [...byName.values()].sort(rank);
}

/** The declaration holding most of the problems, when one holds at least two of them. */
function hotspotOf(problems) {
    // A named declaration's "where" is unique to it; the sort is stable, so a tie keeps the worse problem's place.
    const counts = new Map();
    for (const { source, where } of problems) if (source) counts.set(where, (counts.get(where) ?? 0) + 1);
    const [where, count] = [...counts].sort((left, right) => right[1] - left[1])[0] ?? [];
    if (!(count > 1)) return null;
    const share = count === problems.length ? `All ${count}` : `${count} of the ${problems.length}`;
    return `${share} problems are in ${where}.`;
}

/**
 * The verdict for one saved file report: its rating, health, the problems (measurements past a
 * limit) worst first, everything else within limits, and where most of the problems sit when
 * one function or type holds several of them. Null for anything that is not a file report.
 */
export function fileFindings(file) {
    if (!file || typeof file !== 'object') return null;
    const measurements = fileMeasurements(file);
    const problems = measurements.filter(item => item.level !== 'ok');
    const critical = problems.filter(item => item.level === 'critical').length;
    const rating = text(file.rating);
    const health = healthFromConcern(file.score);
    return {
        rating,
        ratingLabel: RATING_LABELS[rating] || rating || 'Not rated',
        tone: RATING_TONES[rating] || 'neutral',
        health,
        healthText: formatNumber(health, 1),
        problems,
        within: measurements.filter(item => item.level === 'ok'),
        measurements,
        critical,
        headline: measurements.length ? headline(problems.length, critical) : 'No measurements saved',
        hotspot: hotspotOf(problems)
    };
}

function headline(problems, critical) {
    if (!problems) return 'No problems';
    if (problems === 1) return critical ? '1 critical problem' : '1 problem';
    if (critical === problems) return `${problems} critical problems`;
    return critical ? `${problems} problems, ${critical} critical` : `${problems} problems`;
}

/**
 * The map inspector's summary for a saved file report (`node.quality`): the rating, a headline
 * and the problems worst first, as short display strings the sandboxed frame renders as text.
 */
export function atlasQuality(file) {
    const findings = fileFindings(file);
    // Nothing can be called within or past a limit when no measurement carries one.
    if (!findings?.measurements.some(hasLimits)) return undefined;
    // The map validates these lengths; a long declaration name is shortened, never allowed to fail the map.
    return {
        rating: clip(findings.ratingLabel, 40),
        tone: findings.tone,
        headline: findings.problems.length ? findings.headline : 'No problems: every measurement is within its limits.',
        findings: findings.problems.slice(0, 12).map(item => ({
            level: item.level,
            title: clip(`${item.label} ${item.valueText}`, 200),
            detail: clip(`${item.where} · ${item.limitText}`, 300)
        }))
    };
}

function clip(value, max) {
    return value.length <= max ? value : `${value.slice(0, max - 1).trimEnd()}…`;
}

export const GIT_PREFLIGHT_STEPS = Object.freeze([
    Object.freeze({ id: 'vca', label: 'VCA rules', description: 'Validate staged changes against repository vc.rules.md rules.' }),
    Object.freeze({ id: 'mintlint', label: 'MintLint', description: 'Grade added staged code and surface maintainability findings.' }),
    Object.freeze({ id: 'automated-workflows', label: 'Automated workflows', description: 'Queue automations set to run before this commit. They do not block Git.' })
]);

const STEP_IDS = new Set(GIT_PREFLIGHT_STEPS.map(step => step.id));
const TERMINAL_STATUSES = new Set(['passed', 'warning', 'blocked', 'skipped', 'error', 'cancelled']);

function asObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value) ? value : {};
}

function normalizeToken(value) {
    return String(value ?? '').trim().toLowerCase().replace(/[\s_]+/g, '-');
}

export function normalizePreflightOutput(value) {
    if (value === null || value === undefined) return '';
    if (typeof value === 'string') return value.replace(/\r\n?/g, '\n').replace(/\0/g, '');
    try {
        return JSON.stringify(value, null, 2);
    } catch {
        return String(value);
    }
}

export function formatPreflightDuration(durationMs) {
    const value = Number(durationMs);
    if (!Number.isFinite(value) || value < 0) return '';
    if (value < 1000) return `${Math.round(value)} ms`;
    return `${(value / 1000).toFixed(value < 10000 ? 1 : 0)} s`;
}

function createStepState(step) {
    return {
        ...step,
        status: 'pending',
        message: step.description,
        output: '',
        details: null,
        durationMs: null,
        blocking: false
    };
}

export function createGitPreflightState() {
    return {
        runId: '',
        sequence: 0,
        status: 'idle',
        message: 'Waiting to inspect the staged commit.',
        startedAt: '',
        finishedAt: '',
        durationMs: null,
        commitAllowed: null,
        blocking: false,
        staged: { count: null, files: [] },
        steps: Object.fromEntries(GIT_PREFLIGHT_STEPS.map(step => [step.id, createStepState(step)]))
    };
}

function normalizeStepId(value) {
    const token = normalizeToken(value);
    if (token === 'automatedworkflows' || token === 'workflows' || token === 'automated-workflow') {
        return 'automated-workflows';
    }
    return STEP_IDS.has(token) ? token : '';
}

function normalizeStatus(value, fallback = 'running') {
    const token = normalizeToken(value);
    if (token === 'pass' || token === 'success' || token === 'complete' || token === 'completed') return 'passed';
    if (token === 'warn') return 'warning';
    if (token === 'failed' || token === 'failure') return 'error';
    if (token === 'canceled') return 'cancelled';
    return ['pending', 'running', ...TERMINAL_STATUSES].includes(token) ? token : fallback;
}

function mergeOutput(current, next) {
    const text = normalizePreflightOutput(next).trimEnd();
    if (!text) return current || '';
    if (!current) return text;
    return `${current.trimEnd()}\n${text}`;
}

function readStagedSummary(event) {
    const details = asObject(event.details);
    const staged = asObject(details.staged);
    const rawFiles = event.stagedFiles ?? details.stagedFiles ?? staged.files ?? details.files;
    const fileItems = Array.isArray(rawFiles)
        ? rawFiles
        : typeof rawFiles === 'string' ? rawFiles.split(/\r?\n/) : [];
    const files = fileItems.map(file => {
            if (typeof file === 'string') return file;
            const item = asObject(file);
            return String(item.path ?? item.file ?? item.name ?? '').trim();
        }).filter(Boolean);
    const rawCount = event.stagedFileCount ?? event.stagedCount ?? details.stagedFileCount ??
        details.stagedCount ?? staged.count;
    const parsedCount = Number(rawCount);

    return {
        count: Number.isFinite(parsedCount) && parsedCount >= 0 ? parsedCount : (files.length ? files.length : null),
        files
    };
}

/**
 * Reduces the server's GitPreflightEvent stream into a stable UI model. Unknown
 * events and future fields are ignored so newer servers remain safe to display.
 */
export function reduceGitPreflightEvent(previousState, rawEvent) {
    const state = previousState || createGitPreflightState();
    const event = asObject(rawEvent);
    const type = normalizeToken(event.type);
    const stepId = normalizeStepId(event.stepId);
    const next = {
        ...state,
        steps: { ...state.steps },
        sequence: Number.isFinite(Number(event.sequence)) ? Number(event.sequence) : state.sequence
    };

    if (type === 'run-started') {
        const fresh = createGitPreflightState();
        return {
            ...fresh,
            runId: String(event.runId ?? ''),
            sequence: next.sequence,
            status: 'running',
            message: String(event.message || 'Inspecting the staged commit.'),
            startedAt: String(event.timestampUtc ?? ''),
            staged: readStagedSummary(event)
        };
    }

    if (type === 'step-started' && stepId) {
        next.status = state.status === 'idle' ? 'running' : state.status;
        next.steps[stepId] = {
            ...state.steps[stepId],
            status: 'running',
            message: String(event.message || `Running ${state.steps[stepId].label}…`),
            output: mergeOutput(state.steps[stepId].output, event.output),
            details: event.details ?? state.steps[stepId].details
        };
        return next;
    }

    if (type === 'step-output' && stepId) {
        next.steps[stepId] = {
            ...state.steps[stepId],
            status: state.steps[stepId].status === 'pending' ? 'running' : state.steps[stepId].status,
            message: String(event.message || state.steps[stepId].message),
            output: mergeOutput(state.steps[stepId].output, event.output ?? event.message),
            details: event.details ?? state.steps[stepId].details
        };
        return next;
    }

    if (type === 'step-finished' && stepId) {
        const status = normalizeStatus(event.status, event.blocking === true ? 'blocked' : 'passed');
        next.steps[stepId] = {
            ...state.steps[stepId],
            status,
            message: String(event.message || `${state.steps[stepId].label} ${status}.`),
            output: mergeOutput(state.steps[stepId].output, event.output),
            details: event.details ?? state.steps[stepId].details,
            durationMs: Number.isFinite(Number(event.durationMs)) ? Number(event.durationMs) : state.steps[stepId].durationMs,
            blocking: event.blocking === true || status === 'blocked'
        };
        return next;
    }

    if (type === 'run-finished') {
        const status = normalizeStatus(event.status, event.commitAllowed === false ? 'blocked' : 'passed');
        next.status = status;
        next.message = String(event.message || (event.commitAllowed === false
            ? 'Commit blocked by Git Guard.'
            : 'Commit checks complete.'));
        next.finishedAt = String(event.timestampUtc ?? '');
        next.durationMs = Number.isFinite(Number(event.durationMs)) ? Number(event.durationMs) : state.durationMs;
        next.commitAllowed = typeof event.commitAllowed === 'boolean'
            ? event.commitAllowed
            : !['blocked', 'error', 'cancelled'].includes(status);
        next.blocking = event.blocking === true || next.commitAllowed === false;
        return next;
    }

    return next;
}

function parseSseBlock(block, onEvent) {
    if (!block.trim()) return;
    let eventName = '';
    const dataLines = [];

    for (const line of block.split('\n')) {
        if (!line || line.startsWith(':')) continue;
        const separator = line.indexOf(':');
        const field = separator < 0 ? line : line.slice(0, separator);
        let value = separator < 0 ? '' : line.slice(separator + 1);
        if (value.startsWith(' ')) value = value.slice(1);
        if (field === 'event') eventName = value;
        if (field === 'data') dataLines.push(value);
    }

    if (!dataLines.length) return;
    const data = dataLines.join('\n');
    let event;
    try {
        event = JSON.parse(data);
    } catch {
        event = { type: eventName || 'message', message: data };
    }
    if (eventName && event && typeof event === 'object' && !event.type) event.type = eventName;
    onEvent(event);
}

/** Incremental SSE parser that tolerates CRLF and arbitrary network chunking. */
export function createSseParser(onEvent) {
    if (typeof onEvent !== 'function') throw new TypeError('onEvent must be a function');
    let buffer = '';

    return {
        push(chunk) {
            buffer += String(chunk ?? '').replace(/\r\n?/g, '\n');
            let boundary = buffer.indexOf('\n\n');
            while (boundary >= 0) {
                parseSseBlock(buffer.slice(0, boundary), onEvent);
                buffer = buffer.slice(boundary + 2);
                boundary = buffer.indexOf('\n\n');
            }
        },
        finish() {
            if (buffer.trim()) parseSseBlock(buffer, onEvent);
            buffer = '';
        }
    };
}

export async function streamGitPreflight({
    url,
    fetchImpl = globalThis.fetch,
    headers = {},
    signal,
    onEvent = () => { }
}) {
    if (typeof fetchImpl !== 'function') throw new TypeError('fetch is unavailable');
    const response = await fetchImpl(url, {
        method: 'POST',
        headers: { Accept: 'text/event-stream', ...headers },
        credentials: 'include',
        cache: 'no-store',
        signal
    });

    if (!response.ok) {
        const error = new Error(`Git preflight failed: ${response.status} ${response.statusText || ''}`.trim());
        error.status = response.status;
        throw error;
    }
    if (!response.body?.getReader) throw new Error('Git preflight returned no event stream.');

    const parser = createSseParser(onEvent);
    const decoder = new TextDecoder();
    const reader = response.body.getReader();
    try {
        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            parser.push(decoder.decode(value, { stream: true }));
        }
        parser.push(decoder.decode());
        parser.finish();
    } finally {
        reader.releaseLock?.();
    }
}

export class GitPreflightRunner {
    constructor(options = {}) {
        this.options = options;
        this.abortController = null;
    }

    get isRunning() {
        return this.abortController !== null;
    }

    async run(onEvent) {
        if (this.isRunning) return { started: false, cancelled: false };
        const abortController = new AbortController();
        this.abortController = abortController;
        try {
            await streamGitPreflight({ ...this.options, signal: abortController.signal, onEvent });
            return { started: true, cancelled: false };
        } catch (error) {
            if (abortController.signal.aborted || error?.name === 'AbortError') {
                return { started: true, cancelled: true };
            }
            throw error;
        } finally {
            if (this.abortController === abortController) this.abortController = null;
        }
    }

    cancel() {
        if (!this.abortController) return false;
        this.abortController.abort();
        return true;
    }
}

export function createAutorunOnce(callback, schedule = queueMicrotask) {
    let consumed = false;
    return () => {
        if (consumed) return false;
        consumed = true;
        schedule(callback);
        return true;
    };
}

export function statusTone(status) {
    const normalized = normalizeStatus(status, 'pending');
    if (normalized === 'passed') return 'success';
    if (normalized === 'warning' || normalized === 'skipped' || normalized === 'cancelled') return 'warning';
    if (normalized === 'blocked' || normalized === 'error') return 'danger';
    return 'neutral';
}

function normalizeCategory(category, fallbackName = '') {
    if (typeof category === 'string') return { name: fallbackName || 'Finding', message: category, status: '' };
    const item = asObject(category);
    return {
        name: String(item.name ?? item.category ?? item.id ?? fallbackName ?? 'Finding') || 'Finding',
        message: String(item.message ?? item.summary ?? item.detail ?? item.value ?? ''),
        status: String(item.status ?? item.grade ?? item.severity ?? '')
    };
}

const MINTLINT_METRIC_LABELS = Object.freeze({
    lines_of_code: 'Lines of code',
    cyclomatic_complexity: 'Cyclomatic complexity',
    cognitive_complexity: 'Cognitive complexity',
    npath_complexity: 'NPath complexity',
    nesting_depth: 'Nesting depth',
    parameter_count: 'Parameter count',
    halstead_difficulty: 'Halstead difficulty',
    maintainability_index: 'Maintainability index',
    lack_of_cohesion: 'Lack of cohesion (LCOM4)',
    fan_out: 'Fan-out',
    duplication: 'Duplicated code',
    hard_coded_dependencies: 'Hard-coded dependencies',
    ambient_dependencies: 'Ambient dependencies',
    method_count: 'Methods per class',
    field_count: 'Fields per class'
});

export function formatMintLintMetricLabel(name) {
    const key = String(name ?? '').trim();
    if (MINTLINT_METRIC_LABELS[key]) return MINTLINT_METRIC_LABELS[key];
    const words = key.replace(/[_-]+/g, ' ').trim();
    return words ? words.charAt(0).toUpperCase() + words.slice(1) : 'Metric';
}

function formatMintLintMetricNumber(name, value) {
    if (!Number.isFinite(value)) return '—';
    if (String(name) === 'duplication') return `${(value * 100).toFixed(1)}%`;
    return Number.isInteger(value) ? String(value) : value.toFixed(1);
}

/** Tone for a 0 (clean) → 100 (severe) concern score, aligned with the rating bands. */
export function mintLintConcernTone(score) {
    const value = Number(score);
    if (!Number.isFinite(value)) return 'neutral';
    if (value >= 75) return 'danger';
    if (value >= 55) return 'warning';
    // One color below the warning band: the legend calls 0–54 healthy, so healthy is
    // always green — no separate "okay" blue tier.
    return 'success';
}

/**
 * Which way a RAW measured value reads: 'LB' lower is better, 'HB' higher is better,
 * 'NA' unknown. Prefers the report's explicit direction; falls back to the legacy
 * higherIsBetter boolean so older payloads still resolve.
 */
export function normalizeMintLintDirection(direction, higherIsBetter) {
    const token = String(direction ?? '').trim().toUpperCase();
    if (token === 'LB' || token === 'HB') return token;
    if (token === 'NA') return 'NA';
    if (higherIsBetter === true) return 'HB';
    if (higherIsBetter === false) return 'LB';
    return 'NA';
}

function normalizeMintLintMetric(rawMetric) {
    const metric = asObject(rawMetric);
    const name = String(metric.name ?? '');
    const line = Number(metric.line);
    return {
        name,
        label: formatMintLintMetricLabel(name),
        value: Number(metric.value),
        score: Number(metric.score),
        warn: Number(metric.warn),
        critical: Number(metric.critical),
        higherIsBetter: metric.higherIsBetter === true,
        direction: normalizeMintLintDirection(metric.direction, metric.higherIsBetter),
        source: metric.source ? String(metric.source) : '',
        line: Number.isFinite(line) && line > 0 ? line : null,
        snippet: metric.snippet ? String(metric.snippet) : ''
    };
}

/**
 * Parses the analyzer's full report (a JSON string when it travels through preflight
 * event details, an object when it comes from /api/v1/code-analyzer) into the shared
 * view model: worst offender per metric across the scan, plus per-file breakdowns
 * ranked by priority (concern × how widely the file is referenced). Returns null when
 * no usable report is present.
 */
function buildMintLintReportModel(rawReport) {
    let report = rawReport;
    if (typeof report === 'string') {
        try {
            report = JSON.parse(report);
        } catch {
            return null;
        }
    }
    if (!isPlainRecord(report) || !Array.isArray(report.files)) return null;

    const files = report.files.map((rawFile, index) => {
        const file = asObject(rawFile);
        const score = Number(file.score);
        const rating = String(file.rating ?? '');
        const referencedBy = Number(file.referencedByCount);
        const priority = Number(file.priority);
        const baseline = file.baselineScore === null || file.baselineScore === undefined
            ? null
            : Number(file.baselineScore);
        const introduced = file.introducedScore === null || file.introducedScore === undefined
            ? null
            : Number(file.introducedScore);
        const categories = (Array.isArray(file.categories) ? file.categories : []).map(rawCategory => {
            const category = asObject(rawCategory);
            return {
                name: String(category.name ?? 'Category'),
                score: Number(category.score),
                weight: Number(category.weight),
                weightedScore: Number(category.weightedScore),
                direction: normalizeMintLintDirection(category.direction),
                metrics: (Array.isArray(category.metrics) ? category.metrics : []).map(normalizeMintLintMetric)
            };
        });
        return {
            path: String(file.file ?? `File ${index + 1}`),
            grade: `${Number.isFinite(score) ? score.toFixed(1) : '—'} ${rating}`.trim(),
            score: Number.isFinite(score) ? score : null,
            rating,
            referencedBy: Number.isFinite(referencedBy) && referencedBy > 0 ? referencedBy : 0,
            priority: Number.isFinite(priority) ? priority : null,
            baseline: baseline !== null && Number.isFinite(baseline) ? baseline : null,
            introduced: introduced !== null && Number.isFinite(introduced) ? introduced : null,
            detailed: true,
            categories
        };
    });

    const worstMetrics = (Array.isArray(report.worstMetrics) ? report.worstMetrics : []).map(rawMetric => {
        const metric = normalizeMintLintMetric(rawMetric);
        const item = asObject(rawMetric);
        return {
            ...metric,
            file: String(item.file ?? ''),
            snippet: item.snippet ? String(item.snippet) : ''
        };
    });

    // Backend-decided fixed roster for the score card. Rows stay in backend order and
    // include unmeasured entries. Averages describe the changeset; the worst measurement
    // rides along. (entry.value/entry.concern are accepted as legacy synonyms.)
    const scorecard = (Array.isArray(report.scorecard) ? report.scorecard : []).map(rawEntry => {
        const entry = asObject(rawEntry);
        const averageValue = Number(entry.averageValue ?? entry.value);
        const averageConcern = Number(entry.averageConcern ?? entry.concern);
        const worstValue = Number(entry.worstValue ?? entry.value);
        const worstConcern = Number(entry.worstConcern ?? entry.concern);
        const fileCount = Number(entry.fileCount);
        const line = Number(entry.line);
        const metricName = String(entry.metricName ?? '');
        return {
            label: String(entry.label ?? 'Metric'),
            measured: entry.measured === true,
            fileCount: Number.isFinite(fileCount) && fileCount > 0 ? fileCount : 0,
            averageValue: Number.isFinite(averageValue) ? averageValue : null,
            averageConcern: Number.isFinite(averageConcern) ? averageConcern : null,
            worstValue: Number.isFinite(worstValue) ? worstValue : null,
            worstConcern: Number.isFinite(worstConcern) ? worstConcern : null,
            direction: normalizeMintLintDirection(entry.direction),
            metricName,
            metricLabel: metricName ? formatMintLintMetricLabel(metricName) : '',
            file: String(entry.file ?? ''),
            source: entry.source ? String(entry.source) : '',
            line: Number.isFinite(line) && line > 0 ? line : null
        };
    });

    // Backend-decided Overview strip: one card per category across the scan.
    const overview = (Array.isArray(report.overview) ? report.overview : []).map(rawCard => {
        const card = asObject(rawCard);
        const worstValue = Number(card.worstMetricValue);
        const worstConcern = Number(card.worstConcern);
        const worstName = String(card.worstMetricName ?? '');
        return {
            name: String(card.category ?? 'Category'),
            concern: Number(card.concern),
            worstConcern: Number.isFinite(worstConcern) ? worstConcern : null,
            direction: normalizeMintLintDirection(card.direction),
            worstMetricName: worstName,
            worstMetricLabel: worstName ? formatMintLintMetricLabel(worstName) : '',
            worstMetricValue: Number.isFinite(worstValue) ? worstValue : null,
            worstMetricDirection: normalizeMintLintDirection(card.worstMetricDirection),
            worstMetricFile: String(card.worstMetricFile ?? '')
        };
    });

    return { detailed: true, files, worstMetrics, overview, scorecard };
}

/**
 * Full view model for a MintLint payload: `{ detailed, files, worstMetrics }`. Legacy
 * payloads (files arrays or the flat worstFiles text) yield `detailed: false` with no
 * worst-metric summary.
 */
export function buildMintLintReportViewModel(details) {
    const payload = asObject(details);
    const reportModel = buildMintLintReportModel(payload.report);
    if (reportModel) return reportModel;
    return { detailed: false, files: buildMintLintDetailModel(details), worstMetrics: [] };
}

/** Normalizes evolving MintLint detail payloads without ever producing markup. */
export function buildMintLintDetailModel(details) {
    const payload = asObject(details);
    const reportModel = buildMintLintReportModel(payload.report);
    if (reportModel) return reportModel.files;

    const rawFiles = Array.isArray(payload.files)
        ? payload.files
        : Array.isArray(payload.results) ? payload.results : [];

    const files = rawFiles.map((rawFile, index) => {
        const file = asObject(rawFile);
        const rawCategories = file.categories ?? file.findings ?? file.grades ?? [];
        let categories = [];
        if (Array.isArray(rawCategories)) {
            categories = rawCategories.map(category => normalizeCategory(category));
        } else if (isPlainRecord(rawCategories)) {
            categories = Object.entries(rawCategories).map(([name, value]) => normalizeCategory(value, name));
        }
        return {
            path: String(file.path ?? file.file ?? file.fileName ?? `File ${index + 1}`),
            grade: String(file.grade ?? file.score ?? file.status ?? ''),
            categories
        };
    });

    if (files.length || typeof payload.worstFiles !== 'string') return files;
    return payload.worstFiles.split(/\r?\n/).filter(Boolean).map((line, index) => {
        const match = line.match(/^(.*):\s+([0-9.]+)\s+(\S+)\s+·\s+(.*)$/);
        if (!match) return { path: line, grade: '', categories: [] };
        const categoryText = match[4].trim();
        const categories = /no notable concerns/i.test(categoryText)
            ? []
            : categoryText.split(/,\s*/).map((entry) => {
                const categoryMatch = entry.match(/^(.*?)\s+([0-9.]+)$/);
                return categoryMatch
                    ? { name: categoryMatch[1], message: 'Concern score', status: categoryMatch[2] }
                    : { name: `Finding ${index + 1}`, message: entry, status: '' };
            });
        return {
            path: match[1],
            grade: `${match[2]} ${match[3]}`,
            categories
        };
    });
}

function isPlainRecord(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

export function setSafeText(element, value) {
    if (element) element.textContent = String(value ?? '');
    return element;
}

function setMeterWidth(element, score) {
    const percent = Math.min(100, Math.max(0, Number(score) || 0));
    // Test doubles have no CSSStyleDeclaration; the width is a number we computed, never markup.
    if (element?.style) element.style.width = `${percent}%`;
}

function formatMintLintThresholds(metric) {
    const comparator = metric.higherIsBetter ? '≤' : '≥';
    return `warn ${comparator} ${formatMintLintMetricNumber(metric.name, metric.warn)} · critical ${comparator} ${formatMintLintMetricNumber(metric.name, metric.critical)}`;
}

function formatMintLintOrigin(metric) {
    const parts = [];
    if (metric.file) parts.push(metric.file);
    if (metric.source) parts.push(metric.source);
    if (metric.line) parts.push(`line ${metric.line}`);
    return parts.join(' · ');
}

/** The top-of-report table: one row per metric, worst measurement across the whole scan. */
function renderMintLintWorstSection(worstMetrics, documentRef) {
    const section = documentRef.createElement('section');
    section.className = 'mintlint-worst';

    const heading = documentRef.createElement('div');
    heading.className = 'mintlint-worst-heading';
    setSafeText(heading, 'Worst offender per metric');
    section.append(heading);

    for (const metric of worstMetrics) {
        const row = documentRef.createElement('details');
        row.className = `mintlint-worst-row mintlint-tone-${mintLintConcernTone(metric.score)}`;

        const summary = documentRef.createElement('summary');
        const label = documentRef.createElement('span');
        label.className = 'mintlint-worst-label';
        setSafeText(label, metric.label);

        const value = documentRef.createElement('span');
        value.className = 'mintlint-worst-value';
        setSafeText(value, formatMintLintMetricNumber(metric.name, metric.value));

        const origin = documentRef.createElement('span');
        origin.className = 'mintlint-worst-origin';
        setSafeText(origin, formatMintLintOrigin(metric));

        const score = documentRef.createElement('span');
        score.className = 'mintlint-worst-score';
        setSafeText(score, Number.isFinite(metric.score) ? metric.score.toFixed(1) : '—');

        summary.append(label, value, origin, score);
        row.append(summary);

        const detail = documentRef.createElement('div');
        detail.className = 'mintlint-worst-detail';
        const range = documentRef.createElement('span');
        range.className = 'mintlint-metric-range';
        setSafeText(range, formatMintLintThresholds(metric));
        detail.append(range);
        if (metric.snippet) {
            const snippet = documentRef.createElement('pre');
            snippet.className = 'mintlint-snippet';
            setSafeText(snippet, metric.snippet);
            detail.append(snippet);
        }
        row.append(detail);
        section.append(row);
    }

    return section;
}

function renderMintLintCategoryBreakdown(fileElement, file, documentRef) {
    let worstWeighted = 0;
    for (const category of file.categories) {
        if (Number.isFinite(category.weightedScore)) {
            worstWeighted = Math.max(worstWeighted, category.weightedScore);
        }
    }

    const list = documentRef.createElement('div');
    list.className = 'mintlint-category-list';

    for (const category of file.categories) {
        const tone = mintLintConcernTone(category.score);
        const section = documentRef.createElement('section');
        section.className = `mintlint-category mintlint-tone-${tone}`;

        const head = documentRef.createElement('div');
        head.className = 'mintlint-category-head';
        const name = documentRef.createElement('span');
        name.className = 'mintlint-category-name';
        setSafeText(name, category.name);
        head.append(name);

        if (worstWeighted > 0 && category.weightedScore === worstWeighted) {
            const driver = documentRef.createElement('span');
            driver.className = 'mintlint-category-driver';
            setSafeText(driver, 'sets file score');
            head.append(driver);
        }

        const math = documentRef.createElement('span');
        math.className = 'mintlint-category-math';
        setSafeText(math, Number.isFinite(category.score)
            ? `${category.score.toFixed(1)} × ${Number.isFinite(category.weight) ? category.weight.toFixed(1) : '1.0'} = ${Number.isFinite(category.weightedScore) ? category.weightedScore.toFixed(1) : '—'}`
            : '—');
        head.append(math);
        section.append(head);

        const meter = documentRef.createElement('div');
        meter.className = 'mintlint-meter';
        const fill = documentRef.createElement('span');
        setMeterWidth(fill, category.score);
        meter.append(fill);
        section.append(meter);

        if (category.metrics.length) {
            const metricList = documentRef.createElement('div');
            metricList.className = 'mintlint-metric-list';
            for (const metric of category.metrics) {
                const row = documentRef.createElement('div');
                row.className = `mintlint-metric mintlint-tone-${mintLintConcernTone(metric.score)}`;

                const label = documentRef.createElement('span');
                label.className = 'mintlint-metric-name';
                setSafeText(label, metric.label);
                if (metric.source) {
                    const source = documentRef.createElement('span');
                    source.className = 'mintlint-metric-source';
                    setSafeText(source, ` · ${metric.source}${metric.line ? ` L${metric.line}` : ''}`);
                    label.append(source);
                }

                const value = documentRef.createElement('span');
                value.className = 'mintlint-metric-value';
                setSafeText(value, formatMintLintMetricNumber(metric.name, metric.value));

                const range = documentRef.createElement('span');
                range.className = 'mintlint-metric-range';
                setSafeText(range, formatMintLintThresholds(metric));

                const score = documentRef.createElement('span');
                score.className = 'mintlint-metric-score';
                setSafeText(score, Number.isFinite(metric.score) ? metric.score.toFixed(1) : '—');

                row.append(label, value, range, score);
                metricList.append(row);
            }
            section.append(metricList);
        }

        list.append(section);
    }

    fileElement.append(list);
}

function renderMintLintFileCard(file, documentRef) {
    const fileElement = documentRef.createElement('details');
    fileElement.className = file.detailed
        ? 'git-preflight-mint-file mintlint-file'
        : 'git-preflight-mint-file';
    const summary = documentRef.createElement('summary');
    const path = documentRef.createElement('span');
    path.className = 'git-preflight-mint-path';
    setSafeText(path, file.path);
    summary.append(path);

    if (file.detailed) {
        const impact = documentRef.createElement('span');
        impact.className = 'mintlint-file-impact';
        const impactParts = [
            file.referencedBy > 0
                ? `used by ${file.referencedBy} file${file.referencedBy === 1 ? '' : 's'}`
                : 'no other files reference it'
        ];
        if (file.baseline !== null && file.introduced !== null) {
            if (file.introduced > 0) {
                impactParts.push(`+${file.introduced.toFixed(1)} added by this change (was ${file.baseline.toFixed(1)})`);
            } else if (file.introduced < 0) {
                impactParts.push(`improved by ${Math.abs(file.introduced).toFixed(1)} (was ${file.baseline.toFixed(1)})`);
            } else {
                impactParts.push(`all pre-existing (was ${file.baseline.toFixed(1)})`);
            }
        } else if (file.baseline === null) {
            impactParts.push('score covers added code only');
        }
        if (Number.isFinite(file.priority) && file.priority !== null) {
            impactParts.push(`priority ${file.priority.toFixed(1)}`);
        }
        setSafeText(impact, impactParts.join(' · '));
        summary.append(impact);
    }

    const grade = documentRef.createElement('span');
    grade.className = file.detailed
        ? `git-preflight-mint-grade mintlint-tone-${mintLintConcernTone(file.score)}`
        : 'git-preflight-mint-grade';
    setSafeText(grade, file.grade || 'Details');
    summary.append(grade);
    fileElement.append(summary);

    if (file.detailed) {
        renderMintLintCategoryBreakdown(fileElement, file, documentRef);
        return fileElement;
    }

    const categoryList = documentRef.createElement('dl');
    categoryList.className = 'git-preflight-mint-categories';
    for (const category of file.categories) {
        const term = documentRef.createElement('dt');
        setSafeText(term, category.name);
        const description = documentRef.createElement('dd');
        setSafeText(description, [category.status, category.message].filter(Boolean).join(' · '));
        categoryList.append(term, description);
    }
    if (file.categories.length) fileElement.append(categoryList);
    return fileElement;
}

export function renderMintLintDetails(container, details, documentRef = globalThis.document) {
    if (!container || !documentRef?.createElement) return 0;
    const model = buildMintLintReportViewModel(details);
    container.replaceChildren?.();

    if (model.detailed && model.worstMetrics.length) {
        container.append(renderMintLintWorstSection(model.worstMetrics, documentRef));
    }

    if (!model.files.length) return 0;

    if (!model.detailed) {
        for (const file of model.files) {
            container.append(renderMintLintFileCard(file, documentRef));
        }
        return model.files.length;
    }

    // The per-file list stays collapsed; the worst-offender table above is the headline.
    const disclosure = documentRef.createElement('details');
    disclosure.className = 'mintlint-files';
    const summary = documentRef.createElement('summary');
    const title = documentRef.createElement('span');
    title.className = 'mintlint-files-title';
    setSafeText(title, 'Per-file breakdown');
    const count = documentRef.createElement('span');
    count.className = 'mintlint-files-count';
    setSafeText(count, `${model.files.length} file${model.files.length === 1 ? '' : 's'} · ranked by concern × usage`);
    summary.append(title, count);
    disclosure.append(summary);

    const list = documentRef.createElement('div');
    list.className = 'mintlint-files-list';
    for (const file of model.files) {
        list.append(renderMintLintFileCard(file, documentRef));
    }
    disclosure.append(list);
    container.append(disclosure);
    return model.files.length;
}

export function formatPreflightEventForConsole(rawEvent) {
    const event = asObject(rawEvent);
    const type = normalizeToken(event.type);
    const stepId = normalizeStepId(event.stepId);
    const step = GIT_PREFLIGHT_STEPS.find(candidate => candidate.id === stepId);
    const output = normalizePreflightOutput(event.output).trimEnd();
    const message = String(event.message || '').trim();

    if (type === 'step-output') return output || message;
    if (type === 'step-started') return `[run] ${step?.label || stepId || 'Step'}${message ? ` — ${message}` : ''}`;
    if (type === 'step-finished') {
        const status = normalizeStatus(event.status, 'passed');
        return `[${status}] ${step?.label || stepId || 'Step'}${message ? ` — ${message}` : ''}${output ? `\n${output}` : ''}`;
    }
    if (type === 'run-started') return message || 'Git Guard preflight started.';
    if (type === 'run-finished') return `[${normalizeStatus(event.status, 'passed')}] ${message || 'Git Guard preflight finished.'}`;
    return output || message;
}

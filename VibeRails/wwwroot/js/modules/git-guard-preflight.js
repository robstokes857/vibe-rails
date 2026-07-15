export const GIT_PREFLIGHT_STEPS = Object.freeze([
    Object.freeze({ id: 'vca', label: 'VCA rules', description: 'Validate staged changes against repository AGENTS.md rules.' }),
    Object.freeze({ id: 'mintlint', label: 'MintLint', description: 'Grade staged files and surface maintainability findings.' }),
    Object.freeze({ id: 'automated-workflows', label: 'Automated workflows', description: 'Run repository-defined commit safeguards.' })
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

/** Normalizes evolving MintLint detail payloads without ever producing markup. */
export function buildMintLintDetailModel(details) {
    const payload = asObject(details);
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

export function renderMintLintDetails(container, details, documentRef = globalThis.document) {
    if (!container || !documentRef?.createElement) return 0;
    const files = buildMintLintDetailModel(details);
    container.replaceChildren?.();

    for (const file of files) {
        const fileElement = documentRef.createElement('details');
        fileElement.className = 'git-preflight-mint-file';
        const summary = documentRef.createElement('summary');
        const path = documentRef.createElement('span');
        path.className = 'git-preflight-mint-path';
        setSafeText(path, file.path);
        const grade = documentRef.createElement('span');
        grade.className = 'git-preflight-mint-grade';
        setSafeText(grade, file.grade || 'Details');
        summary.append(path, grade);
        fileElement.append(summary);

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
        container.append(fileElement);
    }
    return files.length;
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

import { escapeHtml, confirmDialog, isConfirmDialogOpen } from './utils.js';
import { VcaConsole } from './vca-console.js';
import { createSseParser } from './git-guard-preflight.js';

// Mirrors EnvironmentStepPhase in the backend.
export const STEP_PHASE = Object.freeze({
    PRE_LAUNCH: 0,
    POST_EXIT: 1,
    // Never runs on its own — only when the Initial Message references it via {{step:<id>}},
    // hidden and captured, with the output substituted into the prompt.
    MANUAL: 2
});

// Mirrors EnvironmentStepRoutes.MaxStepsPerEnvironment and EnvironmentStep's timeout bounds.
export const MAX_STEPS = 20;
export const DEFAULT_TIMEOUT_SECONDS = 600;
export const MIN_TIMEOUT_SECONDS = 1;
export const MAX_TIMEOUT_SECONDS = 3600;

const SECTIONS = Object.freeze([
    {
        phase: STEP_PHASE.PRE_LAUNCH,
        title: 'Before launch',
        // Said plainly because it is the one thing about steps that is not obvious.
        note: 'Runs in its own terminal window, one at a time, before the CLI starts. If a step fails, the launch is aborted.'
    },
    {
        phase: STEP_PHASE.POST_EXIT,
        title: 'After it exits',
        note: 'Runs when the terminal closes — for a Worker that is when the agent finishes; for a tab it is when you close the tab.'
    },
    {
        phase: STEP_PHASE.MANUAL,
        title: 'Only when referenced',
        note: 'Never runs on its own. Reference it from the Initial Message with {{step:…}} (use "Insert step output") and it runs hidden at launch, with its output pasted into the message.'
    }
]);

let clientIdCounter = 0;
const nextClientId = () => `step-${++clientIdCounter}`;

// The durable identity, generated client-side and round-tripped through every save —
// {{step:<id>}} references in the Initial Message stay valid because this never changes.
// (clientId above is only a per-render DOM key and is never persisted.)
export function newStepId() {
    if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID();
    // Ancient-webview fallback; format matches Guid.ToString() so the server keeps it.
    return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, ch => {
        const r = Math.floor(Math.random() * 16);
        return (ch === 'x' ? r : ((r & 0x3) | 0x8)).toString(16);
    });
}

function clampTimeout(value) {
    const parsed = Number(value);
    if (!Number.isFinite(parsed)) return DEFAULT_TIMEOUT_SECONDS;
    return Math.min(MAX_TIMEOUT_SECONDS, Math.max(MIN_TIMEOUT_SECONDS, Math.round(parsed)));
}

function normalizePhase(value) {
    const parsed = Number(value);
    if (parsed === STEP_PHASE.POST_EXIT || parsed === STEP_PHASE.MANUAL) return parsed;
    return STEP_PHASE.PRE_LAUNCH;
}

/**
 * Wire steps (or an already-normalized list) into the editor's working shape. Ordering is
 * phase-then-position, which is the order the backend runs them in and therefore the only order
 * worth showing.
 */
export function normalizeSteps(rawSteps) {
    if (!Array.isArray(rawSteps)) return [];

    return rawSteps
        .filter(step => step && typeof step === 'object')
        .map((step, index) => ({
            clientId: step.clientId || nextClientId(),
            // The server GUID; a step that arrives without one (older row shape) gets a fresh
            // identity here so a later save always round-trips something stable.
            id: typeof step.id === 'string' && step.id ? step.id : newStepId(),
            phase: normalizePhase(step.phase),
            // Position is a server concern; keep the incoming value only to sort by.
            position: Number.isFinite(Number(step.position)) ? Number(step.position) : index,
            name: String(step.name ?? ''),
            command: String(step.command ?? ''),
            startMinimized: Boolean(step.startMinimized),
            timeoutSeconds: clampTimeout(step.timeoutSeconds ?? DEFAULT_TIMEOUT_SECONDS),
            enabled: step.enabled !== false
        }))
        .sort((a, b) => (a.phase - b.phase) || (a.position - b.position))
        .map(({ position, ...step }) => step);
}

/**
 * The wire form. Position is implied by array order and deliberately never sent — the server
 * stamps it, per phase, from the order here.
 */
export function serializeSteps(steps) {
    if (!Array.isArray(steps)) return [];

    const ordered = [
        ...steps.filter(step => normalizePhase(step?.phase) === STEP_PHASE.PRE_LAUNCH),
        ...steps.filter(step => normalizePhase(step?.phase) === STEP_PHASE.POST_EXIT),
        ...steps.filter(step => normalizePhase(step?.phase) === STEP_PHASE.MANUAL)
    ];

    return ordered
        .filter(step => String(step?.command ?? '').trim().length > 0)
        .map(step => ({
            id: typeof step.id === 'string' && step.id ? step.id : newStepId(),
            phase: normalizePhase(step.phase),
            name: String(step.name ?? '').trim(),
            command: String(step.command ?? ''),
            startMinimized: Boolean(step.startMinimized),
            timeoutSeconds: clampTimeout(step.timeoutSeconds),
            enabled: step.enabled !== false
        }));
}

/** "2 before · 1 after · 1 referenced", or "None yet" when there is nothing to summarize. */
export function summarizeSteps(steps) {
    const list = Array.isArray(steps) ? steps : [];
    const before = list.filter(step => normalizePhase(step?.phase) === STEP_PHASE.PRE_LAUNCH).length;
    const referenced = list.filter(step => normalizePhase(step?.phase) === STEP_PHASE.MANUAL).length;
    const after = list.length - before - referenced;
    if (!before && !after && !referenced) return 'None yet';

    const parts = [];
    if (before) parts.push(`${before} before`);
    if (after) parts.push(`${after} after`);
    if (referenced) parts.push(`${referenced} referenced`);
    return parts.join(' · ');
}

/** The summary button that opens this editor from the environment form. */
export function renderStepsSummaryButton(steps) {
    return `
        <div class="mb-3">
            <label class="form-label">Steps</label>
            <button type="button" class="btn btn-outline-secondary w-100 env-steps-summary-button"
                    data-env-steps-open aria-haspopup="dialog">
                <span class="env-steps-summary-label">
                    <i class="fa-solid fa-list-check" aria-hidden="true"></i>
                    Steps
                </span>
                <span class="env-steps-summary-count" data-env-steps-summary>${escapeHtml(summarizeSteps(steps))}</span>
            </button>
            <small class="form-text text-muted d-block">Shell commands run before this launches, after it exits, or only when the Initial Message references their output.</small>
        </div>`;
}

/** Exported for the Initial Message field's step-reference caption and insert picker. */
export function stepDisplayName(step) {
    const name = String(step?.name ?? '').trim();
    if (name) return name;

    const firstLine = String(step?.command ?? '')
        .split('\n')
        .map(line => line.trim())
        .find(line => line.length > 0);
    return firstLine || 'Untitled step';
}

function createStep(phase) {
    return {
        clientId: nextClientId(),
        id: newStepId(),
        phase: normalizePhase(phase),
        name: '',
        command: '',
        startMinimized: false,
        timeoutSeconds: DEFAULT_TIMEOUT_SECONDS,
        enabled: true
    };
}

/**
 * Runs one step's command through the streaming test endpoint. Exported so it can be exercised
 * against a fake fetch without a DOM.
 */
export async function streamStepTest({
    url,
    command,
    workingDirectory = null,
    timeoutSeconds = DEFAULT_TIMEOUT_SECONDS,
    headers = {},
    fetchImpl = globalThis.fetch,
    signal,
    onEvent = () => { }
}) {
    if (typeof fetchImpl !== 'function') throw new TypeError('fetch is unavailable');

    const response = await fetchImpl(url, {
        method: 'POST',
        headers: { Accept: 'text/event-stream', 'Content-Type': 'application/json', ...headers },
        credentials: 'include',
        cache: 'no-store',
        body: JSON.stringify({ command, workingDirectory, timeoutSeconds: clampTimeout(timeoutSeconds) }),
        signal
    });

    if (!response.ok) {
        const error = new Error(`Step test failed: ${response.status} ${response.statusText || ''}`.trim());
        error.status = response.status;
        throw error;
    }
    if (!response.body?.getReader) throw new Error('The step test returned no event stream.');

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

/**
 * Opens the steps editor as a NESTED modal layer rather than a second app.showModal: showModal
 * rebuilds #modal-container's innerHTML wholesale, which would destroy the environment form
 * underneath. Same approach as the LLM picker's customization modal — own layer, `inert` +
 * aria-hidden on the existing children, focus trapped inside.
 *
 * @returns {{ close: () => void }} handle, mostly for tests and for teardown from the caller.
 */
export function openStepsEditor(app, { steps = [], workingDirectory = null, onSave = null } = {}) {
    const host = typeof document !== 'undefined' ? document.getElementById('modal-container') : null;
    if (!host) return { close: () => { } };

    const layer = document.createElement('div');
    layer.className = 'llm-picker-modal-layer env-steps-modal-layer';
    layer.innerHTML = `
        <div class="modal fade show d-block env-steps-modal" tabindex="-1"
             role="dialog" aria-modal="true" aria-labelledby="env-steps-modal-title">
            <div class="modal-dialog modal-lg modal-dialog-scrollable">
                <div class="modal-content">
                    <div class="modal-header">
                        <div>
                            <h5 class="modal-title" id="env-steps-modal-title">Steps</h5>
                            <p class="text-muted small mb-0">Shell commands run in their own terminal window, one at a time.</p>
                        </div>
                        <button type="button" class="btn-close" data-env-steps-action="cancel"
                                aria-label="Close the steps editor"></button>
                    </div>
                    <form data-env-steps-form>
                        <div class="modal-body" data-env-steps-body></div>
                        <div class="modal-footer">
                            <div class="alert alert-danger mb-0 me-auto d-none" role="alert" data-env-steps-error></div>
                            <button type="button" class="btn btn-secondary" data-env-steps-action="cancel">Cancel</button>
                            <button type="submit" class="btn btn-primary" data-env-steps-action="save">Done</button>
                        </div>
                    </form>
                </div>
            </div>
        </div>
        <div class="modal-backdrop fade show env-steps-modal-backdrop"></div>`;

    const underlying = Array.from(host.children).map(element => ({
        element,
        inert: Boolean(element.inert),
        ariaHidden: element.getAttribute('aria-hidden')
    }));
    underlying.forEach(({ element }) => {
        element.inert = true;
        element.setAttribute('aria-hidden', 'true');
    });
    host.appendChild(layer);

    const state = {
        layer,
        host,
        steps: normalizeSteps(steps),
        workingDirectory,
        underlying,
        draggingId: null,
        activeTest: null,
        keydownHandler: null,
        observer: null,
        disposed: false
    };

    function abortActiveTest() {
        state.activeTest?.controller.abort();
        state.activeTest = null;
    }

    function dispose({ restoreFocus = true } = {}) {
        if (state.disposed) return;
        state.disposed = true;
        abortActiveTest();
        state.observer?.disconnect();
        if (state.keydownHandler) document.removeEventListener('keydown', state.keydownHandler, true);
        state.underlying.forEach(({ element, inert, ariaHidden }) => {
            if (!element.isConnected) return;
            element.inert = inert;
            if (ariaHidden == null) element.removeAttribute('aria-hidden');
            else element.setAttribute('aria-hidden', ariaHidden);
        });
        if (restoreFocus) {
            requestAnimationFrame(() => host.querySelector('[data-env-steps-open]')?.focus());
        }
    }

    function close({ restoreFocus = true } = {}) {
        if (state.disposed) return;
        layer.remove();
        dispose({ restoreFocus });
    }

    function showError(message) {
        const error = layer.querySelector('[data-env-steps-error]');
        if (!error) return;
        error.textContent = message || '';
        error.classList.toggle('d-none', !message);
    }

    function stepsFor(phase) {
        return state.steps.filter(step => step.phase === phase);
    }

    function renderRow(step, index, count) {
        const label = escapeHtml(stepDisplayName(step));
        const id = escapeHtml(step.clientId);
        // Referenced steps run hidden and captured, so a window-minimize switch would be a
        // dead control on their rows.
        const showMinimized = step.phase !== STEP_PHASE.MANUAL;
        return `
            <div class="env-step-row${step.enabled ? '' : ' is-disabled'}" data-env-step-id="${id}" data-tone="neutral">
                <div class="env-step-row-head">
                    <span class="env-step-drag-handle" draggable="true" role="button" tabindex="0"
                          aria-label="Drag ${label} to reorder" title="Drag to reorder">
                        <i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>
                    </span>
                    <input type="text" class="form-control form-control-sm env-step-name"
                           data-env-step-field="name" value="${escapeHtml(step.name)}"
                           placeholder="Step name (optional)" aria-label="Name for ${label}">
                    <div class="env-step-row-buttons" role="group" aria-label="Actions for ${label}">
                        <button type="button" class="btn btn-sm btn-outline-secondary"
                                data-env-step-move="up" aria-label="Move ${label} up" ${index === 0 ? 'disabled' : ''}>
                            <i class="fa-solid fa-arrow-up" aria-hidden="true"></i>
                        </button>
                        <button type="button" class="btn btn-sm btn-outline-secondary"
                                data-env-step-move="down" aria-label="Move ${label} down" ${index === count - 1 ? 'disabled' : ''}>
                            <i class="fa-solid fa-arrow-down" aria-hidden="true"></i>
                        </button>
                        <button type="button" class="btn btn-sm btn-outline-secondary"
                                data-env-step-action="test" aria-label="Test ${label}">Test</button>
                        <button type="button" class="btn btn-sm btn-outline-danger"
                                data-env-step-action="delete" aria-label="Delete ${label}">
                            <i class="fa-solid fa-trash" aria-hidden="true"></i>
                        </button>
                    </div>
                </div>
                <textarea class="form-control env-step-command" data-env-step-field="command" rows="3"
                          spellcheck="false" placeholder="npm install"
                          aria-label="Command for ${label}">${escapeHtml(step.command)}</textarea>
                <div class="env-step-row-options">
                    <div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" role="switch"
                               id="env-step-enabled-${id}" data-env-step-field="enabled" ${step.enabled ? 'checked' : ''}>
                        <label class="form-check-label" for="env-step-enabled-${id}">Enabled</label>
                    </div>
                    ${showMinimized ? `<div class="form-check form-switch">
                        <input class="form-check-input" type="checkbox" role="switch"
                               id="env-step-minimized-${id}" data-env-step-field="startMinimized" ${step.startMinimized ? 'checked' : ''}>
                        <label class="form-check-label" for="env-step-minimized-${id}">Start minimized</label>
                    </div>` : ''}
                    <label class="env-step-timeout">
                        <span>Timeout</span>
                        <input type="number" class="form-control form-control-sm" data-env-step-field="timeoutSeconds"
                               min="${MIN_TIMEOUT_SECONDS}" max="${MAX_TIMEOUT_SECONDS}" step="1" value="${step.timeoutSeconds}">
                        <span>s</span>
                    </label>
                </div>
                <section class="vca-console-card env-step-console d-none" data-vca-console data-tone="neutral"
                         aria-busy="false" aria-label="Test output for ${label}">
                    <div class="env-step-console-head">
                        <span class="vca-console-state" data-vca-console-state>Ready</span>
                        <small data-vca-console-meta>Not run yet</small>
                        <span class="rules-check-running" data-vca-console-spinner hidden aria-hidden="true"></span>
                    </div>
                    <div class="vca-console-shell">
                        <pre class="vca-console-output" data-vca-console-output role="log"></pre>
                    </div>
                </section>
            </div>`;
    }

    function renderSection(section) {
        const sectionSteps = stepsFor(section.phase);
        const rows = sectionSteps.length === 0
            ? '<p class="env-steps-empty text-muted small mb-0">No steps yet.</p>'
            : sectionSteps.map((step, index) => renderRow(step, index, sectionSteps.length)).join('');

        return `
            <section class="env-steps-section" data-env-steps-section="${section.phase}">
                <div class="env-steps-section-head">
                    <h6 class="mb-0">${escapeHtml(section.title)}</h6>
                    <button type="button" class="btn btn-sm btn-outline-primary"
                            data-env-steps-add="${section.phase}">
                        <i class="fa-solid fa-plus" aria-hidden="true"></i> Add step
                    </button>
                </div>
                <p class="env-steps-section-note text-muted small">${escapeHtml(section.note)}</p>
                <div class="env-steps-list">${rows}</div>
            </section>`;
    }

    function render(focusTarget = null) {
        if (state.disposed) return;
        // A structural re-render replaces every row element, which would orphan a streaming test.
        abortActiveTest();

        const body = layer.querySelector('[data-env-steps-body]');
        if (!body) return;
        body.innerHTML = SECTIONS.map(renderSection).join('');
        bindRows();

        if (focusTarget) {
            requestAnimationFrame(() => layer
                .querySelector(`[data-env-step-id="${CSS.escape(focusTarget.clientId)}"] ${focusTarget.selector}`)
                ?.focus());
        }
    }

    function findStep(clientId) {
        return state.steps.find(step => step.clientId === clientId) || null;
    }

    function moveStep(clientId, direction) {
        const step = findStep(clientId);
        if (!step) return;

        const siblings = stepsFor(step.phase);
        const currentIndex = siblings.indexOf(step);
        const targetIndex = direction === 'up' ? currentIndex - 1 : currentIndex + 1;
        if (targetIndex < 0 || targetIndex >= siblings.length) return;

        const from = state.steps.indexOf(step);
        const to = state.steps.indexOf(siblings[targetIndex]);
        [state.steps[from], state.steps[to]] = [state.steps[to], state.steps[from]];
        render({ clientId, selector: `[data-env-step-move="${direction}"]` });
    }

    function dropStep(sourceId, targetId, after) {
        if (!sourceId || sourceId === targetId) return;
        const source = findStep(sourceId);
        const target = findStep(targetId);
        // Dragging between sections would silently change when a step runs, so phases don't mix.
        if (!source || !target || source.phase !== target.phase) return;

        state.steps.splice(state.steps.indexOf(source), 1);
        const targetIndex = state.steps.indexOf(target);
        state.steps.splice(targetIndex + (after ? 1 : 0), 0, source);
        render();
    }

    async function deleteStep(clientId) {
        const step = findStep(clientId);
        if (!step) return;

        // No window.confirm: the VS Code webview silently returns false, so it would read as
        // "delete does nothing". Enforced by a sweep test over every first-party module.
        const confirmed = await confirmDialog({
            title: 'Delete this step?',
            message: `"${stepDisplayName(step)}" is removed from this environment when you save.`,
            confirmLabel: 'Delete',
            danger: true
        });
        if (!confirmed || state.disposed) return;

        state.steps = state.steps.filter(candidate => candidate.clientId !== clientId);
        render();
    }

    function addStep(phase) {
        if (state.steps.length >= MAX_STEPS) {
            showError(`An environment can have at most ${MAX_STEPS} steps.`);
            return;
        }
        showError('');

        const step = createStep(phase);
        // Appended to the end of its own phase so a new step lands where the user is looking.
        const lastOfPhase = stepsFor(step.phase).at(-1);
        const insertAt = lastOfPhase ? state.steps.indexOf(lastOfPhase) + 1 : state.steps.length;
        state.steps.splice(insertAt, 0, step);
        render({ clientId: step.clientId, selector: '[data-env-step-field="command"]' });
    }

    async function testStep(clientId, row) {
        const step = findStep(clientId);
        if (!step || state.activeTest) return;

        const consoleRoot = row.querySelector('.env-step-console');
        if (!consoleRoot) return;
        consoleRoot.classList.remove('d-none');

        const consoleView = new VcaConsole(consoleRoot, {
            defaultMessage: '',
            runningMessage: 'Running…'
        });

        if (!String(step.command).trim()) {
            consoleView.fail('This step has no command yet.');
            return;
        }

        const controller = new AbortController();
        state.activeTest = { clientId, controller };
        consoleView.begin(stepDisplayName(step));

        const baseUrl = globalThis.window?.__viberails_API_BASE__ || '';
        const tabToken = app?.getSessionStorageValue?.('viberails_tab')
            ?? globalThis.sessionStorage?.getItem?.('viberails_tab');

        try {
            let sawOutput = false;
            await streamStepTest({
                url: `${baseUrl}/api/v1/environments/steps/test`,
                command: step.command,
                workingDirectory: state.workingDirectory,
                timeoutSeconds: step.timeoutSeconds,
                headers: tabToken ? { viberails_tab: tabToken } : {},
                signal: controller.signal,
                onEvent: event => {
                    const type = String(event?.type || '').toLowerCase();
                    if (type === 'line') {
                        sawOutput = true;
                        consoleView.writeLine(event.isError ? `[stderr] ${event.line ?? ''}` : String(event.line ?? ''));
                        return;
                    }
                    if (type === 'error') {
                        consoleView.fail(event.message || 'The step could not be run.');
                        return;
                    }
                    if (type !== 'done') return;

                    const exitCode = Number(event.exitCode);
                    const ok = exitCode === 0;
                    if (!sawOutput) consoleView.writeLine('(no output)');
                    consoleView.finishStream({
                        tone: ok ? 'success' : 'danger',
                        state: ok ? 'Passed' : 'Failed',
                        meta: [
                            Number.isFinite(exitCode) ? `Exit code ${exitCode}` : null,
                            Number.isFinite(Number(event.durationMs)) ? `${Math.round(Number(event.durationMs))} ms` : null
                        ].filter(Boolean).join(' · ')
                    });
                }
            });
        } catch (error) {
            if (!controller.signal.aborted && error?.name !== 'AbortError') {
                consoleView.fail(error?.message || 'The step could not be run.');
            }
        } finally {
            if (state.activeTest?.controller === controller) state.activeTest = null;
        }
    }

    function bindRows() {
        layer.querySelectorAll('[data-env-steps-add]').forEach(button => {
            button.addEventListener('click', () => addStep(Number(button.dataset.envStepsAdd)));
        });

        layer.querySelectorAll('.env-step-row').forEach(row => {
            const clientId = row.dataset.envStepId;

            row.querySelectorAll('[data-env-step-field]').forEach(input => {
                const field = input.dataset.envStepField;
                const eventName = input.type === 'checkbox' ? 'change' : 'input';
                input.addEventListener(eventName, () => {
                    const step = findStep(clientId);
                    if (!step) return;

                    if (field === 'enabled' || field === 'startMinimized') {
                        step[field] = Boolean(input.checked);
                        if (field === 'enabled') row.classList.toggle('is-disabled', !step.enabled);
                        return;
                    }
                    if (field === 'timeoutSeconds') {
                        step.timeoutSeconds = clampTimeout(input.value);
                        return;
                    }
                    // Text fields are written straight through without a re-render, so the caret
                    // and focus survive typing.
                    step[field] = input.value;
                });
            });

            // Clamp only on blur: rewriting the value mid-keystroke makes "60" impossible to type.
            row.querySelector('[data-env-step-field="timeoutSeconds"]')?.addEventListener('blur', event => {
                const step = findStep(clientId);
                if (!step) return;
                step.timeoutSeconds = clampTimeout(event.target.value);
                event.target.value = step.timeoutSeconds;
            });

            row.querySelectorAll('[data-env-step-move]').forEach(button => {
                button.addEventListener('click', () => moveStep(clientId, button.dataset.envStepMove));
            });
            row.querySelector('[data-env-step-action="delete"]')
                ?.addEventListener('click', () => void deleteStep(clientId));
            row.querySelector('[data-env-step-action="test"]')
                ?.addEventListener('click', () => void testStep(clientId, row));

            const handle = row.querySelector('.env-step-drag-handle');
            handle?.addEventListener('keydown', event => {
                if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
                event.preventDefault();
                moveStep(clientId, event.key === 'ArrowUp' ? 'up' : 'down');
            });
            handle?.addEventListener('dragstart', event => {
                state.draggingId = clientId;
                row.classList.add('is-dragging');
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', clientId);
            });
            handle?.addEventListener('dragend', () => {
                state.draggingId = null;
                layer.querySelectorAll('.is-dragging, .is-drag-target')
                    .forEach(element => element.classList.remove('is-dragging', 'is-drag-target'));
            });
            row.addEventListener('dragover', event => {
                const source = findStep(state.draggingId);
                const target = findStep(clientId);
                if (!source || !target || source.phase !== target.phase || source.clientId === target.clientId) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                row.classList.add('is-drag-target');
            });
            row.addEventListener('dragleave', () => row.classList.remove('is-drag-target'));
            row.addEventListener('drop', event => {
                event.preventDefault();
                row.classList.remove('is-drag-target');
                const sourceId = state.draggingId || event.dataTransfer.getData('text/plain');
                const after = event.clientY > row.getBoundingClientRect().top + row.offsetHeight / 2;
                dropStep(sourceId, clientId, after);
            });
        });
    }

    layer.querySelectorAll('[data-env-steps-action="cancel"]')
        .forEach(button => button.addEventListener('click', () => close()));

    layer.querySelector('[data-env-steps-form]')?.addEventListener('submit', event => {
        event.preventDefault();
        const emptyCommand = state.steps.find(step => !String(step.command).trim());
        if (emptyCommand) {
            showError('Every step needs a command. Fill it in or delete the step.');
            return;
        }
        showError('');
        const saved = state.steps.map(step => ({ ...step }));
        close();
        onSave?.(saved);
    });

    state.keydownHandler = event => {
        if (state.disposed) return;
        // A confirmDialog overlay owns Escape while it is up (see utils.js).
        if (isConfirmDialogOpen()) return;
        if (event.key === 'Escape') {
            event.preventDefault();
            event.stopImmediatePropagation();
            close();
            return;
        }
        if (event.key === 'Tab') trapFocus(event, layer);
    };
    document.addEventListener('keydown', state.keydownHandler, true);

    state.observer = new MutationObserver(() => {
        if (!layer.isConnected) dispose({ restoreFocus: false });
    });
    state.observer.observe(host, { childList: true });

    render();
    requestAnimationFrame(() => layer.querySelector('.env-steps-modal')?.focus());

    return { close, getSteps: () => state.steps.map(step => ({ ...step })) };
}

function trapFocus(event, layer) {
    const focusable = Array.from(layer.querySelectorAll(
        'a[href], button:not([disabled]), textarea:not([disabled]), input:not([disabled]), select:not([disabled]), [tabindex]:not([tabindex="-1"])'
    )).filter(element => element.offsetParent !== null || element === document.activeElement);
    if (focusable.length === 0) return;

    const first = focusable[0];
    const last = focusable[focusable.length - 1];
    if (event.shiftKey && document.activeElement === first) {
        event.preventDefault();
        last.focus();
    } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault();
        first.focus();
    }
}

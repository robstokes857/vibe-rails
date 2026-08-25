import { escapeHtml, isConfirmDialogOpen } from './utils.js';

const API = '/api/v1/python-scripts';
const STORAGE_PREFIX = 'viberails.pythonRun.';
const MAX_EXTRAS = 24;

/**
 * The little run window: a signed script's inputs go in, its exit code, output and return
 * value come back, and nothing spawns a terminal. This is the small-space surface — the
 * PTY tab ("Run in terminal") stays one click away for scripts that need typing or Ctrl+C.
 *
 * Inputs come from two places. A script exposed to MCP has already declared its parameters
 * (name, type, required, default, positional-or-flag); those render as typed fields whose
 * shape is locked, because that declaration is the script's real interface. Anything else
 * — and any extra argument on a script that declares nothing — is a free argument row.
 * Whatever you typed is remembered per script, so re-running is one click.
 *
 * The command line under the inputs is not a decoration: it is literally the payload. The
 * tokens it prints are the argv array that gets posted, so a preview can never drift from
 * what runs.
 */

/** A run "worked" only when it exited 0 without hitting the timeout. */
export function isPythonRunOk(run) {
    return Boolean(run) && run.exitCode === 0 && !run.timedOut;
}

/** stdout then stderr of one run, as the row drawer and this window both show it. */
export function formatPythonRunOutput(run) {
    const parts = [];
    if (run?.standardOutput?.trim()) parts.push(run.standardOutput.trimEnd());
    if (run?.standardError?.trim()) parts.push(`[stderr]\n${run.standardError.trimEnd()}`);
    return parts.join('\n\n') || '(no output)';
}

/** True for a JSON scalar the run window would rather call output than a return value. */
function isStructuredJson(text) {
    const trimmed = String(text || '').trim();
    return trimmed.startsWith('{') || trimmed.startsWith('[');
}

/**
 * The script's return value, re-indented for reading. Mirrors the server's convention
 * (PythonScriptService.ExtractReturnJson) so a client that never saw `returnJson` — an
 * older backend — still shows the object a script printed on its last line.
 */
export function prettyJson(text) {
    if (!isStructuredJson(text)) return null;
    try {
        return JSON.stringify(JSON.parse(String(text).trim()), null, 2);
    } catch {
        return null;
    }
}

/** The return value of a finished run, whether the backend extracted it or not. */
export function returnValueOf(run) {
    const fromServer = prettyJson(run?.returnJson);
    if (fromServer) return fromServer;
    const stdout = String(run?.standardOutput || '').trim();
    if (!stdout) return null;
    return prettyJson(stdout) || prettyJson(stdout.split(/\r?\n/).pop());
}

/** A declared parameter's value as one argv token, or null when it has nothing to say. */
function parameterValue(parameter, values) {
    const supplied = values?.[parameter.name];
    const hasSupplied = supplied !== undefined && supplied !== null && String(supplied).trim() !== '';
    if (hasSupplied) return String(supplied).trim();
    if (parameter.defaultValue !== null && parameter.defaultValue !== undefined) {
        return String(parameter.defaultValue);
    }
    return null;
}

function isTypeValid(type, value) {
    if (type === 'integer') return /^-?\d+$/.test(value);
    if (type === 'number') return Number.isFinite(Number(value));
    if (type === 'boolean') return value === 'true' || value === 'false';
    return true;
}

/**
 * The argv the run posts, built the way PythonScriptMcpService.BuildArguments builds it for
 * an agent: declared parameters first (positional values in order, then named options), then
 * the free argument rows. Returns { argv, error } — error is the first thing a human has to
 * fix, phrased for them.
 */
export function resolveArgv({ parameters = [], values = {}, extras = [] } = {}) {
    const positional = [];
    const options = [];

    for (const parameter of parameters) {
        const value = parameterValue(parameter, values);
        if (value === null) {
            if (parameter.required) {
                return { argv: [], error: `${parameter.name} needs a value.` };
            }
            continue;
        }
        if (!isTypeValid(parameter.type, value)) {
            const expected = parameter.type === 'boolean' ? 'true or false' : `a ${parameter.type}`;
            return { argv: [], error: `${parameter.name} must be ${expected}.` };
        }

        if (parameter.argumentMode === 'positional') {
            positional.push(value);
        } else if (parameter.type === 'boolean') {
            // A false boolean option is the absence of its flag, exactly as the MCP path
            // sends it — passing "--verbose false" would reach Python as a stray value.
            if (value === 'true') options.push(parameter.flag);
        } else {
            options.push(parameter.flag, value);
        }
    }

    const free = [];
    for (const extra of extras) {
        const flag = String(extra?.flag || '').trim();
        const value = String(extra?.value ?? '').trim();
        if (flag) free.push(flag);
        if (value) free.push(value);
    }

    return { argv: [...positional, ...options, ...free], error: null };
}

/** Shell-style quoting, for display only: it shows where one argument ends. */
export function quoteForDisplay(token) {
    const text = String(token ?? '');
    return /[\s"']/.test(text) ? `"${text.replace(/"/g, '\\"')}"` : text;
}

export function readRemembered(storage, name) {
    try {
        const raw = storage?.getItem?.(STORAGE_PREFIX + name);
        const parsed = raw ? JSON.parse(raw) : null;
        if (!parsed || typeof parsed !== 'object') return null;
        return {
            values: parsed.values && typeof parsed.values === 'object' ? parsed.values : {},
            extras: Array.isArray(parsed.extras) ? parsed.extras.slice(0, MAX_EXTRAS) : [],
            stdin: typeof parsed.stdin === 'string' ? parsed.stdin : ''
        };
    } catch {
        return null;
    }
}

export class PythonRunWindow {
    constructor(app, scripts) {
        this.app = app;
        this.scripts = scripts;
        this.layer = null;
        this.name = null;
        this.parameters = [];
        this.values = {};
        this.extras = [];
        this.stdin = '';
        this.lastRun = null;
        this.running = false;
        // Identity of the run that owns the window right now. A run that outlived its window
        // (closed mid-run) still records its result, but must not paint over or re-enable a
        // window that has since been reopened for something else.
        this._runToken = null;
        this._resolve = null;
        this._onKeydown = null;
    }

    get isOpen() {
        return Boolean(this.layer);
    }

    /**
     * Opens the window for one script and resolves with the last run's result when it closes
     * (null if nothing ran). A script that declares no inputs and remembers none runs straight
     * away — "call the script in a little window" should not cost a second click.
     */
    open(name) {
        if (!name) return Promise.resolve(null);
        if (this.layer) this.close();

        const configuration = this.scripts?.mcpConfigurationByScript?.(name) || null;
        this.name = name;
        this.parameters = Array.isArray(configuration?.parameters) ? configuration.parameters : [];
        const remembered = readRemembered(globalThis.localStorage, name);
        this.values = remembered?.values || {};
        this.extras = remembered?.extras || [];
        this.stdin = remembered?.stdin || '';
        this.lastRun = null;
        // NOT `false`. A run started from an earlier window can still be in flight, and
        // clearing the flag here is what used to let a second interpreter start for the same
        // script. The in-flight run still owns this window and will clear it when it lands.
        this.running = this.scripts?.runningNames?.has(name) === true;

        this._mount();
        this._paintRunState();

        const takesNothing = !this.running
            && this.parameters.length === 0 && this.extras.length === 0 && !this.stdin;
        if (takesNothing) {
            void this.execute();
        } else {
            requestAnimationFrame(() => {
                this.layer?.querySelector('[data-run-input]')?.focus();
            });
        }

        return new Promise((resolve) => { this._resolve = resolve; });
    }

    close() {
        if (!this.layer) return;
        this._remember();
        if (this._onKeydown) document.removeEventListener('keydown', this._onKeydown, true);
        this._onKeydown = null;
        if (this.scripts?.modal?.layer === this.layer) this.scripts.modal = null;
        this.layer.remove();
        this.layer = null;
        const resolve = this._resolve;
        this._resolve = null;
        resolve?.(this.lastRun);
    }

    // --- run ---

    /** Posts the resolved argv and stdin, then paints the result. */
    async execute() {
        if (this.running || !this.name) return null;
        if (this.scripts?.runningNames?.has(this.name)) {
            this._showProblem(`${this.name} is already running.`);
            return null;
        }
        const { argv, error } = resolveArgv(this);
        if (error) {
            this._showProblem(error);
            return null;
        }
        this._showProblem('');
        this._remember();

        const name = this.name;
        const token = {};
        this._runToken = token;
        this.running = true;
        // The previous result goes now, not when the new one lands: a stale "exit 0" sitting
        // under a failed re-run reads as if the failure were the old run's.
        this.lastRun = null;
        this.scripts?.markRunning?.(name);
        this._paintRunState();

        let result = null;
        try {
            result = await this.app.apiCall(`${API}/run`, 'POST',
                { name, arguments: argv, standardInput: this.stdin || null },
                { showLoading: false, preferErrorResponseMessage: true });
            // Unconditional: the row's "Last run" drawer and the workbench's output panel are
            // where the result belongs even when nobody is looking at the window any more.
            this.scripts?.recordRun?.(name, result);
        } catch (problem) {
            if (this._runToken === token) this._showProblem(problem?.message || `Could not run ${name}.`);
        } finally {
            this.scripts?.clearRunning?.(name);
            // Window state is only ours to touch while we are still the current run. Reopening
            // the window for the same script leaves the token alone, so a run that survived its
            // window still lands in it; opening a different script replaces the token, and this
            // run then finishes quietly.
            if (this._runToken === token) {
                this._runToken = null;
                this.running = false;
                if (this.name === name) this.lastRun = result;
                this._paintRunState();
                this._paintResult();
            }
        }
        return result;
    }

    /**
     * Hands this script to the PTY tab instead, for runs that need typing or Ctrl+C. The
     * captured run has to be finished first — the controller refuses to launch over one, and
     * closing before that refusal is what made this button look dead.
     */
    async runInTerminal() {
        const name = this.name;
        if (!name) return;
        if (this.running || this.scripts?.runningNames?.has(name)) {
            this._showProblem(`${name} is still running. Wait for it to finish first.`);
            return;
        }
        this.close();
        await this.scripts?.runInTerminal?.(name);
    }

    // --- markup ---

    _mount() {
        const host = document.getElementById('modal-container') || document.body;
        const layer = document.createElement('div');
        layer.className = 'llm-picker-modal-layer';
        layer.innerHTML = this._shell();
        host.appendChild(layer);
        this.layer = layer;

        layer.addEventListener('click', (event) => this._onClick(event));
        layer.addEventListener('input', (event) => this._onInput(event));
        layer.addEventListener('change', (event) => this._onInput(event));
        layer.querySelector('[data-run-form]')?.addEventListener('submit', (event) => {
            event.preventDefault();
            void this.execute();
        });

        this._onKeydown = (event) => {
            if (isConfirmDialogOpen() || !this.layer) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                this.close();
                return;
            }
            // Ctrl/⌘+Enter runs from anywhere in the window, including the stdin box
            // where a bare Enter has to stay a newline.
            if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
                event.preventDefault();
                void this.execute();
            }
        };
        document.addEventListener('keydown', this._onKeydown, true);

        if (this.scripts) this.scripts.modal = { layer, close: () => this.close() };
        this._paintFields();
    }

    _shell() {
        const name = escapeHtml(this.name || '');
        return `
            <div class="modal fade show d-block vb-run-window" tabindex="-1" role="dialog"
                 aria-modal="true" aria-labelledby="vb-run-window-title">
                <div class="modal-dialog modal-dialog-centered">
                    <form class="modal-content" data-run-form autocomplete="off">
                        <div class="modal-header vb-run-header">
                            <div class="vb-run-identity">
                                <i class="fa-brands fa-python" aria-hidden="true"></i>
                                <h5 class="modal-title" id="vb-run-window-title">Run <span>${name}</span></h5>
                            </div>
                            <button type="button" class="btn-close" data-run-action="close" aria-label="Close"></button>
                        </div>

                        <div class="modal-body vb-run-body">
                            <div class="alert alert-danger vb-run-problem d-none" role="alert" data-run-problem></div>

                            <section class="vb-run-section" aria-label="Inputs">
                                <div class="vb-run-section-head">
                                    <h6>Inputs</h6>
                                    <button class="btn btn-sm btn-outline-secondary" type="button" data-run-action="add-extra">
                                        <i class="fa-solid fa-plus me-1" aria-hidden="true"></i>Add argument
                                    </button>
                                </div>
                                <div class="vb-run-fields" data-run-fields></div>
                            </section>

                            <details class="vb-run-stdin" data-run-stdin-wrap ${this.stdin ? 'open' : ''}>
                                <summary>Standard input</summary>
                                <textarea class="form-control" rows="3" data-run-stdin
                                          placeholder="Text piped to the script's stdin"
                                          spellcheck="false">${escapeHtml(this.stdin)}</textarea>
                            </details>

                            <section class="vb-run-result" data-run-result aria-live="polite"></section>
                        </div>

                        <!-- Pinned between the scrolling body and the footer: this is what the
                             Run button beside it is about to execute, so it must never scroll
                             away behind a long list of inputs. -->
                        <section class="vb-run-command-strip" aria-label="Command">
                            <div class="vb-run-section-head"><h6>Command</h6></div>
                            <div class="vb-run-command" data-run-command role="status"></div>
                        </section>

                        <div class="modal-footer vb-run-footer">
                            <button class="btn btn-sm btn-link vb-run-terminal-link" type="button" data-run-action="terminal"
                                    data-run-terminal title="Run this script in a terminal tab instead — for scripts that ask questions or need Ctrl+C">
                                <i class="fa-solid fa-terminal me-1" aria-hidden="true"></i>Run in terminal
                            </button>
                            <button type="button" class="btn btn-secondary" data-run-action="close">Close</button>
                            <button type="submit" class="btn btn-primary" data-run-submit>
                                <i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run
                            </button>
                        </div>
                    </form>
                </div>
            </div>
            <div class="modal-backdrop fade show"></div>`;
    }

    _renderFields() {
        const declared = this.parameters.map((parameter, index) =>
            this._renderDeclaredField(parameter, index)).join('');
        const extras = this.extras.map((extra, index) => this._renderExtraRow(extra, index)).join('');
        if (!declared && !extras) {
            return `<p class="vb-run-empty">${escapeHtml(this.name || 'This script')} takes no arguments. Add one to pass a value through <code>sys.argv</code>.</p>`;
        }
        return declared + extras;
    }

    _renderDeclaredField(parameter, index) {
        const name = escapeHtml(parameter.name || '');
        const value = this.values?.[parameter.name] ?? '';
        const shape = parameter.argumentMode === 'positional'
            ? `#${this.parameters.filter((other, position) =>
                other.argumentMode === 'positional' && position <= index).length}`
            : parameter.flag || '';
        const control = parameter.type === 'boolean'
            ? `<select class="form-select form-select-sm" data-run-input data-run-param="${name}">
                    <option value="" ${value === '' ? 'selected' : ''}>—</option>
                    <option value="true" ${String(value) === 'true' ? 'selected' : ''}>true</option>
                    <option value="false" ${String(value) === 'false' ? 'selected' : ''}>false</option>
               </select>`
            // Always type=text: a number input would swallow the leading "-" of a negative
            // value mid-typing and silently report "" for anything it dislikes.
            : `<input class="form-control form-control-sm" type="text"
                      inputmode="${parameter.type === 'integer' || parameter.type === 'number' ? 'numeric' : 'text'}"
                      data-run-input data-run-param="${name}" value="${escapeHtml(String(value))}"
                      placeholder="${escapeHtml(parameter.defaultValue ?? '')}" spellcheck="false">`;

        return `
            <div class="vb-run-field${parameter.required ? ' is-required' : ''}">
                <div class="vb-run-field-label">
                    <label>${name}${parameter.required ? '<span class="vb-run-required" title="Required">*</span>' : ''}</label>
                    <span class="vb-run-shape" title="${parameter.argumentMode === 'positional' ? 'Positional argument' : 'Named option'}">${escapeHtml(shape)}</span>
                </div>
                ${control}
                ${parameter.description ? `<p class="vb-run-field-hint">${escapeHtml(parameter.description)}</p>` : ''}
            </div>`;
    }

    _renderExtraRow(extra, index) {
        return `
            <div class="vb-run-field vb-run-field-extra" data-run-extra="${index}">
                <div class="vb-run-field-label">
                    <label>Argument</label>
                    <button class="vb-run-remove" type="button" data-run-action="remove-extra" data-index="${index}"
                            aria-label="Remove argument"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
                </div>
                <div class="vb-run-extra-inputs">
                    <input class="form-control form-control-sm vb-run-extra-flag" data-run-input data-run-extra-flag="${index}"
                           value="${escapeHtml(extra?.flag || '')}" placeholder="--flag" spellcheck="false"
                           aria-label="Flag (optional)">
                    <input class="form-control form-control-sm" data-run-input data-run-extra-value="${index}"
                           value="${escapeHtml(extra?.value || '')}" placeholder="value" spellcheck="false"
                           aria-label="Value">
                </div>
            </div>`;
    }

    // --- painting ---

    /** Rebuilds the input rows. Structural only — it drops focus, so never call it on input. */
    _paintFields() {
        const fields = this.layer?.querySelector('[data-run-fields]');
        if (fields) fields.innerHTML = this._renderFields();
        this._paintCommandLine();
    }

    _paintCommandLine() {
        const box = this.layer?.querySelector('[data-run-command]');
        if (!box) return;
        const { argv, error } = resolveArgv(this);
        const tokens = argv.map((token) => {
            const looksLikeFlag = /^-{1,2}[^\d\s]/.test(token);
            return `<span class="${looksLikeFlag ? 'vb-run-flag' : 'vb-run-value'}">${escapeHtml(quoteForDisplay(token))}</span>`;
        }).join(' ');
        box.innerHTML = `<span class="vb-run-exec">python</span> <span class="vb-run-script">${escapeHtml(this.name || '')}</span>${tokens ? ' ' + tokens : ''}`;
        box.classList.toggle('is-incomplete', Boolean(error));
        this._paintTerminalLink(argv.length > 0 || Boolean(this.stdin));
    }

    /**
     * The PTY escape hatch posts a script name and nothing else — /run/interactive deliberately
     * accepts no command text from the browser — so it does NOT run the command line above.
     * Say so on the button rather than letting the two disagree in silence.
     */
    _paintTerminalLink(hasInput) {
        const link = this.layer?.querySelector('[data-run-terminal]');
        if (!link) return;
        const note = link.querySelector('[data-run-terminal-note]');
        if (hasInput && !note) {
            link.insertAdjacentHTML('beforeend',
                '<span class="vb-run-terminal-note" data-run-terminal-note>· without these inputs</span>');
        } else if (!hasInput && note) {
            note.remove();
        }
        link.title = hasInput
            ? 'Run this script in a terminal tab instead — for scripts that ask questions or need '
                + 'Ctrl+C. The terminal run takes no arguments or stdin, so it ignores the inputs above.'
            : 'Run this script in a terminal tab instead — for scripts that ask questions or need Ctrl+C';
    }

    _paintRunState() {
        const submit = this.layer?.querySelector('[data-run-submit]');
        if (!submit) return;
        submit.disabled = this.running;
        submit.innerHTML = this.running
            ? '<span class="spinner-border spinner-border-sm" aria-hidden="true"></span> Running…'
            : '<i class="fa-solid fa-play me-1" aria-hidden="true"></i>Run';
        const result = this.layer?.querySelector('[data-run-result]');
        if (this.running && result) {
            result.innerHTML = '<div class="vb-run-status is-waiting"><span class="vb-run-dot"></span>Running…</div>';
        }
    }

    _paintResult() {
        const host = this.layer?.querySelector('[data-run-result]');
        const run = this.lastRun;
        if (!host) return;
        if (!run) {
            host.innerHTML = '';
            return;
        }

        const ok = isPythonRunOk(run);
        const returned = returnValueOf(run);
        const output = formatPythonRunOutput(run);
        const started = run.startedUtc ? new Date(run.startedUtc) : null;
        const facts = [
            run.timedOut ? 'timed out' : `exit ${run.exitCode}`,
            `${Math.round(run.durationMs)} ms`,
            started && !Number.isNaN(started.valueOf()) ? started.toLocaleTimeString() : ''
        ].filter(Boolean).join(' · ');

        host.innerHTML = `
            <div class="vb-run-status ${ok ? 'is-ok' : 'is-bad'}" data-run-status>
                <span class="vb-run-dot"></span>${escapeHtml(facts)}
            </div>
            ${returned ? `
            <div class="vb-run-panel">
                <div class="vb-run-panel-head">
                    <h6>Returned</h6>
                    <button class="btn btn-sm btn-outline-secondary" type="button" data-run-action="copy-return">
                        <i class="fa-regular fa-copy me-1" aria-hidden="true"></i>Copy
                    </button>
                </div>
                <pre class="vb-run-json" data-run-return>${this._tintJson(returned)}</pre>
            </div>` : ''}
            <div class="vb-run-panel">
                <div class="vb-run-panel-head"><h6>Output</h6></div>
                <pre class="vb-run-output">${escapeHtml(output)}</pre>
                ${!returned && output === '(no output)' ? `
                <p class="vb-run-field-hint">Print a JSON object on the last line to return a value.</p>` : ''}
            </div>`;

        // A window opened over a long input list is still scrolled to the inputs; the
        // result is the news, so bring it into view rather than leaving it below the fold.
        this.layer?.querySelector('[data-run-status]')?.scrollIntoView?.(
            { block: 'nearest', behavior: 'smooth' });
    }

    /** Minimal JSON tinting: keys, strings, numbers and literals. Input is already escaped. */
    _tintJson(json) {
        return escapeHtml(json).replace(
            /("(?:\\.|[^"\\])*")(\s*:)?|\b(true|false|null)\b|(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g,
            (match, string, colon, literal, number) => {
                if (string) {
                    return colon
                        ? `<span class="vb-json-key">${string}</span>${colon}`
                        : `<span class="vb-json-string">${string}</span>`;
                }
                if (literal) return `<span class="vb-json-literal">${literal}</span>`;
                if (number) return `<span class="vb-json-number">${number}</span>`;
                return match;
            });
    }

    _showProblem(message) {
        const box = this.layer?.querySelector('[data-run-problem]');
        if (!box) return;
        box.textContent = message || '';
        box.classList.toggle('d-none', !message);
    }

    // --- events ---

    _onClick(event) {
        const button = event.target.closest('[data-run-action]');
        if (!button) return;
        const action = button.dataset.runAction;
        if (action === 'close') return this.close();
        if (action === 'terminal') return void this.runInTerminal();
        if (action === 'copy-return') return void this._copyReturn(button);
        if (action === 'add-extra') {
            if (this.extras.length >= MAX_EXTRAS) return;
            this.extras.push({ flag: '', value: '' });
            this._paintFields();
            const rows = this.layer?.querySelectorAll('[data-run-extra]');
            rows?.[rows.length - 1]?.querySelector('input')?.focus();
            return;
        }
        if (action === 'remove-extra') {
            this.extras.splice(Number(button.dataset.index), 1);
            this._paintFields();
        }
    }

    _onInput(event) {
        const target = event.target;
        if (target?.dataset?.runParam !== undefined && target.dataset.runParam !== '') {
            this.values[target.dataset.runParam] = target.value;
            return void this._paintCommandLine();
        }
        if (target?.dataset?.runExtraFlag !== undefined) {
            const extra = this.extras[Number(target.dataset.runExtraFlag)];
            if (extra) extra.flag = target.value;
            return void this._paintCommandLine();
        }
        if (target?.dataset?.runExtraValue !== undefined) {
            const extra = this.extras[Number(target.dataset.runExtraValue)];
            if (extra) extra.value = target.value;
            return void this._paintCommandLine();
        }
        if (target?.matches?.('[data-run-stdin]')) {
            this.stdin = target.value;
        }
    }

    async _copyReturn(button) {
        const text = this.layer?.querySelector('[data-run-return]')?.textContent || '';
        try {
            await navigator.clipboard.writeText(text);
            const original = button.innerHTML;
            button.innerHTML = '<i class="fa-solid fa-check me-1" aria-hidden="true"></i>Copied';
            setTimeout(() => { button.innerHTML = original; }, 1200);
        } catch {
            this.app?.showError?.('Could not copy the return value.');
        }
    }

    _remember() {
        if (!this.name) return;
        try {
            globalThis.localStorage?.setItem(STORAGE_PREFIX + this.name, JSON.stringify({
                values: this.values,
                extras: this.extras,
                stdin: this.stdin
            }));
        } catch { /* private mode, quota — remembering inputs is a convenience */ }
    }
}

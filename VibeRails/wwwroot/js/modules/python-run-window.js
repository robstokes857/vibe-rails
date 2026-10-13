import { escapeHtml, isConfirmDialogOpen } from './utils.js';
import { scriptRuntimeFor } from './script-runtimes.js';
import { VibeTerminal } from './vibe-terminal.js';

// Retained for the existing captured-run API's row/workbench summaries.
export function isPythonRunOk(run) {
    return Boolean(run) && run.exitCode === 0 && !run.timedOut && !run.cancelled;
}
export function pythonRunStatus(run) {
    return run?.cancelled ? 'Stop requested' : `exit ${run?.exitCode}${run?.timedOut ? ' (timed out)' : ''}`;
}
export function formatPythonRunOutput(run) {
    const parts = [];
    if (run?.standardOutput?.trim()) parts.push(run.standardOutput.trimEnd());
    if (run?.standardError?.trim()) parts.push(`[stderr]\n${run.standardError.trimEnd()}`);
    return parts.join('\n\n') || '(no output)';
}

/** A private xterm window. Input goes straight to its PTY, with no remembered fields or recording. */
export class PythonRunWindow {
    constructor(app, scripts) {
        this.app = app;
        this.scripts = scripts;
        this.layer = null;
        this.name = null;
        this.running = false;
        this.lastRun = null;
        this.terminal = null;
        this._runToken = null;
        this._resolve = null;
    }

    get isOpen() { return Boolean(this.layer); }

    open(name) {
        if (!name) return Promise.resolve(null);
        this.close();
        this.name = name;
        this.lastRun = null;
        this._previousFocus = document.activeElement;
        const closed = new Promise(resolve => { this._resolve = resolve; });
        try {
            this._mount();
            void this.execute();
        } catch (error) {
            this.app.showError(error?.message || 'Could not open the script terminal.');
            this.close();
        }
        return closed;
    }

    close() {
        if (!this.layer) return;
        if (this._runToken?.awaitingPin && this.scripts?.modal?.layer !== this.layer) {
            this.scripts?._closeModal?.();
            if (!this.layer) return;
        }
        const token = this._runToken;
        this._runToken = null;
        if (token) {
            token.socket?.close();
            this.scripts?.clearRunning?.(token.name);
        }
        this.running = false;
        this._input?.();
        this._input = null;
        this.terminal?.dispose();
        this.terminal = null;
        document.removeEventListener('keydown', this._onKeydown, true);
        window.removeEventListener('pagehide', this._onPageHide);
        this._dockButton?.remove();
        this._dockButton = null;
        this.layer.remove();
        this.layer = null;
        this._previousFocus?.focus?.();
        const resolve = this._resolve;
        this._resolve = null;
        resolve?.(this.lastRun);
    }

    minimize() {
        if (!this.layer) return;
        this.layer.hidden = true;
        let dock = document.getElementById('script-run-dock');
        if (!dock) {
            dock = document.createElement('div');
            dock.id = 'script-run-dock';
            dock.setAttribute('aria-label', 'Running scripts');
            document.body.appendChild(dock);
        }
        if (!this._dockButton) {
            this._dockButton = document.createElement('button');
            this._dockButton.type = 'button';
            this._dockButton.className = 'vb-run-dock-button';
            this._dockButton.addEventListener('click', () => this.show());
            dock.appendChild(this._dockButton);
        }
        this._updateDock();
        this._previousFocus?.focus?.();
    }

    show() {
        if (!this.layer) return;
        for (const other of this.scripts?.runWindows?.values?.() || []) {
            if (other !== this && other.layer && !other.layer.hidden) other.minimize();
        }
        this.layer.hidden = false;
        this._dockButton?.remove();
        this._dockButton = null;
        this.terminal?.fit();
        this.terminal?.focus();
    }

    _updateDock() {
        if (this._dockButton) {
            const label = this.scripts?.displayName?.(this.name) || this.name;
            this._dockButton.textContent = `${label} · ${this._status || 'Running'}`;
            this._dockButton.title = 'Restore script terminal';
            this._dockButton.dataset.tone = this._tone || 'waiting';
        }
    }

    async execute() {
        if (this.running || !this.layer || !this.name) return;
        const token = { name: this.name, awaitingPin: true, socket: null, ready: false, started: Date.now() };
        this._runToken = token;
        this.running = true;
        this.lastRun = null;
        this.scripts?.markRunning?.(token.name);
        this.terminal?.reset();
        this._paintState('Starting…', 'waiting');
        try {
            let pin = await this.scripts?.requestRunPin?.(token.name, { isCurrent: () => this._runToken === token });
            token.awaitingPin = false;
            if (this._runToken !== token) return;
            if (pin === null) return this._finish(token, 'Run cancelled', 'neutral');
            const base = window.__viberails_API_BASE__ || window.location.origin;
            const url = `${base.replace(/^http/, 'ws')}/api/v1/python-scripts/run/terminal/ws`;
            const tab = sessionStorage.getItem('viberails_tab')?.replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
            const socket = new WebSocket(url, tab ? [tab] : []);
            token.socket = socket;
            socket.binaryType = 'arraybuffer';
            socket.onopen = () => {
                if (this._runToken !== token) { pin = undefined; socket.close(); return; }
                this.terminal?.fit();
                socket.send(JSON.stringify({ name: token.name, pin, cols: this._cols(), rows: this._rows() }));
                pin = undefined;
                socket.onopen = null;
            };
            socket.onmessage = event => {
                if (this._runToken !== token) return;
                if (typeof event.data !== 'string') {
                    this.terminal?.write(new Uint8Array(event.data));
                } else if (event.data === 'ready') {
                    token.ready = true;
                    this._paintState('Running', 'waiting');
                    this.terminal?.focus();
                } else if (event.data.startsWith('exit:')) {
                    const exitCode = Number(event.data.slice(5));
                    if (!Number.isInteger(exitCode)) return;
                    this.lastRun = { exitCode, durationMs: Date.now() - token.started };
                    this._finish(token, exitCode === 0 ? 'Finished · exit 0' : `Failed · exit ${exitCode}`,
                        exitCode === 0 ? 'success' : 'danger');
                } else if (event.data === 'stopped') {
                    this.lastRun = { cancelled: true, exitCode: null };
                    this._finish(token, 'Stopped', 'neutral');
                } else if (event.data.startsWith('error:')) {
                    this._finish(token, event.data.slice(6), 'danger');
                }
            };
            socket.onclose = () => {
                pin = undefined;
                socket.onopen = null;
                if (this._runToken === token)
                    this._finish(token, 'Connection closed. Run again to start a new script.', 'danger');
            };
            socket.onerror = () => {
                pin = undefined;
                this._finish(token, 'Could not connect to the script terminal.', 'danger');
            };
        } catch (error) {
            this._finish(token, error?.message || 'Could not start the script.', 'danger');
        }
    }

    _finish(token, message, tone) {
        if (this._runToken !== token) return;
        this._runToken = null;
        this.running = false;
        token.socket?.close();
        this.scripts?.clearRunning?.(token.name);
        this._paintState(message, tone);
    }

    canStop(name = this.name) { return name === this.name && this.running; }

    stop(name = this.name) {
        if (!this.canStop(name)) return;
        const token = this._runToken;
        if (token?.ready && token.socket?.readyState === WebSocket.OPEN) {
            token.ready = false;
            this._paintState('Stopping…', 'waiting');
            const stop = this.layer?.querySelector('[data-run-action="stop"]');
            if (stop) stop.disabled = true;
            token.socket.send('stop');
        } else {
            if (token?.awaitingPin) this.scripts?._closeModal?.();
            this._finish(token, 'Run cancelled', 'neutral');
        }
    }

    _cols() { return Math.min(400, Math.max(10, this.terminal?.cols || 80)); }
    _rows() { return Math.min(150, Math.max(5, this.terminal?.rows || 24)); }

    _sendInput(data) {
        const token = this._runToken;
        if (!token?.ready || token.socket?.readyState !== WebSocket.OPEN) return;
        // Binary frames cannot be mistaken for control messages, even when a password
        // or pasted line happens to begin with a reserved protocol prefix.
        const bytes = new TextEncoder().encode(data);
        for (let offset = 0; offset < bytes.length; offset += 64 * 1024)
            token.socket.send(bytes.subarray(offset, offset + 64 * 1024));
    }

    _mount() {
        const layer = document.createElement('div');
        layer.className = 'vb-script-terminal-layer';
        layer.innerHTML = this._shell();
        document.body.appendChild(layer);
        this.layer = layer;
        layer.addEventListener('click', event => {
            const action = event.target.closest('[data-run-action]')?.dataset.runAction;
            if (action === 'close') this.close();
            if (action === 'minimize') this.minimize();
            if (action === 'stop') this.stop();
            if (action === 'run') void this.execute();
        });
        this.terminal = new VibeTerminal({ outputEl: layer.querySelector('[data-run-terminal]'),
            cols: 80, rows: 24, scrollback: 2000, disableStdin: true });
        this._input = this.terminal.onData(data => this._sendInput(data));
        this.terminal.onFitChange = () => {
            const token = this._runToken;
            if (token?.ready && token.socket?.readyState === WebSocket.OPEN)
                token.socket.send(`__resize__:${this._cols()},${this._rows()}`);
        };
        this.terminal.startResizeHandling();
        this.terminal.fit();
        this._onKeydown = event => {
            if (isConfirmDialogOpen() || this.scripts?.modal || !layer.contains(event.target)) return;
            // Escape remains available to the program; this floating window does not trap focus.
            if (event.key === 'Escape' && !event.target.closest('.xterm')) {
                event.preventDefault(); event.stopImmediatePropagation(); this.minimize();
            }
        };
        this._onPageHide = () => this.close();
        document.addEventListener('keydown', this._onKeydown, true);
        window.addEventListener('pagehide', this._onPageHide);
        this.show();
        layer.querySelector('[data-run-action="close"]')?.focus();
    }

    _shell() {
        const script = this.scripts?.scriptByName?.(this.name);
        const runtime = scriptRuntimeFor(script?.fileName || this.name);
        const label = escapeHtml(this.scripts?.displayName?.(this.name) || this.name);
        return `<section class="vb-run-window" role="dialog" aria-modal="false"
                    aria-labelledby="vb-run-window-title-${escapeHtml(this.name)}" aria-describedby="vb-run-privacy-${escapeHtml(this.name)}">
            <div class="vb-run-titlebar">
                <i class="fa-solid fa-terminal" aria-hidden="true"></i>
                <h5 id="vb-run-window-title-${escapeHtml(this.name)}">${label}</h5>
                <span class="vb-run-runtime">${escapeHtml(runtime.label)}</span>
                <div class="vb-run-window-controls">
                    <button type="button" data-run-action="run" aria-label="Run again" title="Run again" hidden><i class="fa-solid fa-rotate-right" aria-hidden="true"></i></button>
                    <button type="button" data-run-action="stop" aria-label="Stop" title="Stop script"><i class="fa-solid fa-stop" aria-hidden="true"></i></button>
                    <button type="button" data-run-action="minimize" aria-label="Minimize" title="Minimize — keep running"><i class="fa-solid fa-minus" aria-hidden="true"></i></button>
                    <button type="button" data-run-action="close" aria-label="Close" title="Close and stop script"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
                </div>
            </div>
            <div class="vb-run-terminal" data-run-terminal></div>
            <div class="vb-run-bottom-bar">
                <span data-run-status role="status" aria-live="polite">Starting…</span>
                <span id="vb-run-privacy-${escapeHtml(this.name)}">Input isn’t saved · Close stops the script</span>
            </div>
        </section>`;
    }

    _paintState(message, tone) {
        this._status = message;
        this._tone = tone;
        this._updateDock();
        const status = this.layer?.querySelector('[data-run-status]');
        if (status) { status.textContent = message; status.dataset.tone = tone; }
        const stop = this.layer?.querySelector('[data-run-action="stop"]');
        if (stop) { stop.hidden = !this.running; stop.disabled = false; }
        const run = this.layer?.querySelector('[data-run-action="run"]');
        if (run) run.hidden = this.running;
        if (this.terminal?.terminal) this.terminal.terminal.options.disableStdin = !this._runToken?.ready;
    }
}

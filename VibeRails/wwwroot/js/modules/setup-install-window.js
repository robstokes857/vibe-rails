import { escapeHtml, isConfirmDialogOpen } from './utils.js';
import { VibeTerminal } from './vibe-terminal.js';

/**
 * A floating xterm window for one Setup install. The server picks the command from its catalog
 * by tool and method ID; the command shown in the window chrome is the catalog text, never
 * written into the terminal itself. Same protocol and styling as the script run window.
 */
export class SetupInstallWindow {
    constructor(app, { onFinished } = {}) {
        this.app = app;
        this.onFinished = onFinished;
        this.layer = null;
        this.terminal = null;
        this.running = false;
        this._token = null;
        // The latest run, kept after it finishes: its re-check may only paint while it still owns the window.
        this._run = null;
        this._input = null;
        this._dockButton = null;
    }

    get isOpen() { return Boolean(this.layer); }

    open(tool, method) {
        this.close();
        this.tool = tool;
        this.method = method;
        this._previousFocus = document.activeElement;
        try {
            this._mount();
            this._execute();
        } catch (error) {
            this.app.showError(error?.message || 'Could not open the installer.');
            this.close();
        }
    }

    close() {
        if (!this.layer) return;
        const token = this._token;
        this._token = null;
        this._run = null;
        token?.socket?.close();
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
            this._dockButton.title = 'Restore installer';
            this._dockButton.addEventListener('click', () => this.show());
            dock.appendChild(this._dockButton);
        }
        this._updateDock();
        this._previousFocus?.focus?.();
    }

    show() {
        if (!this.layer) return;
        this.layer.hidden = false;
        this._dockButton?.remove();
        this._dockButton = null;
        this.terminal?.fit();
        this.terminal?.focus();
    }

    stop() {
        const token = this._token;
        if (!this.running || !token) return;
        if (token.ready && token.socket?.readyState === WebSocket.OPEN) {
            token.ready = false;
            this._paintState('Stopping…', 'waiting');
            token.socket.send('stop');
        } else {
            this._finish(token, 'Install cancelled', 'neutral', null);
        }
    }

    _execute() {
        const token = { socket: null, ready: false };
        this._token = token;
        this._run = token;
        this.running = true;
        this._paintState('Starting…', 'waiting');
        const base = window.__viberails_API_BASE__ || window.location.origin;
        const url = `${base.replace(/^http/, 'ws')}/api/v1/setup/install/ws`;
        const tab = sessionStorage.getItem('viberails_tab')?.replace(/\+/g, '-').replace(/\//g, '_').replace(/=/g, '');
        const socket = new WebSocket(url, tab ? [tab] : []);
        token.socket = socket;
        socket.binaryType = 'arraybuffer';
        socket.onopen = () => {
            if (this._token !== token) { socket.close(); return; }
            this.terminal?.fit();
            socket.send(JSON.stringify({ tool: this.tool.id, method: this.method.id, cols: this._cols(), rows: this._rows() }));
            socket.onopen = null;
        };
        socket.onmessage = event => {
            if (this._token !== token) return;
            if (typeof event.data !== 'string') {
                this.terminal?.write(new Uint8Array(event.data));
            } else if (event.data === 'ready') {
                token.ready = true;
                this._paintState('Installing…', 'waiting');
                this.terminal?.focus();
            } else if (event.data.startsWith('exit:')) {
                const exitCode = Number(event.data.slice(5));
                if (!Number.isInteger(exitCode)) return;
                this._finish(token, exitCode === 0 ? 'Finished · checking…' : `Exited with code ${exitCode} · checking…`,
                    exitCode === 0 ? 'success' : 'danger', exitCode);
            } else if (event.data === 'stopped') {
                this._finish(token, 'Stopped', 'neutral', null);
            } else if (event.data.startsWith('error:')) {
                this._finish(token, event.data.slice(6), 'danger', null);
            }
        };
        socket.onclose = () => {
            if (this._token === token) this._finish(token, 'Connection closed.', 'danger', null);
        };
        socket.onerror = () => this._finish(token, 'Could not connect to the installer.', 'danger', null);
    }

    async _finish(token, message, tone, exitCode) {
        if (this._token !== token) return;
        this._token = null;
        this.running = false;
        token.socket?.close();
        this._paintState(message, tone);
        if (exitCode === null) return;
        // Installers report "already installed" and reboot-pending as non-zero, so the
        // re-check, not the exit code, decides what the page shows.
        const tool = this.tool;
        const status = await this.onFinished?.(tool, exitCode);
        // The window is free while the re-check runs; a newer install there owns the status line.
        if (!this.layer || this._run !== token) return;
        const state = status?.tools?.find(item => item.id === tool.id)?.state;
        if (state === 'ready') this._paintState(`${tool.name} is installed and ready.`, 'success');
        else if (state === 'restart') this._paintState(`${tool.name} is installed. Restart VibeRails so new terminals can find it.`, 'success');
        else this._paintState(`${message.replace(' · checking…', '')} · ${tool.name} still isn’t found.`, 'danger');
    }

    _cols() { return Math.min(400, Math.max(10, this.terminal?.cols || 80)); }
    _rows() { return Math.min(150, Math.max(5, this.terminal?.rows || 24)); }

    _sendInput(data) {
        const token = this._token;
        if (!token?.ready || token.socket?.readyState !== WebSocket.OPEN) return;
        // Binary frames carry keystrokes (winget agreements, sudo passwords) so they can
        // never be mistaken for a control message.
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
        });
        this.terminal = new VibeTerminal({ outputEl: layer.querySelector('[data-run-terminal]'),
            cols: 80, rows: 24, scrollback: 2000, disableStdin: true });
        this._input = this.terminal.onData(data => this._sendInput(data));
        this.terminal.onFitChange = () => {
            const token = this._token;
            if (token?.ready && token.socket?.readyState === WebSocket.OPEN)
                token.socket.send(`__resize__:${this._cols()},${this._rows()}`);
        };
        this.terminal.startResizeHandling();
        this.terminal.fit();
        this._onKeydown = event => {
            if (isConfirmDialogOpen() || !layer.contains(event.target)) return;
            if (event.key === 'Escape' && !event.target.closest('.xterm')) {
                event.preventDefault(); event.stopImmediatePropagation(); this.minimize();
            }
        };
        this._onPageHide = () => this.close();
        document.addEventListener('keydown', this._onKeydown, true);
        window.addEventListener('pagehide', this._onPageHide);
        this.show();
    }

    _shell() {
        const name = escapeHtml(this.tool.name);
        return `<section class="vb-run-window vb-setup-install-window" role="dialog" aria-modal="false" aria-labelledby="vb-setup-install-title">
            <div class="vb-run-titlebar">
                <i class="fa-solid fa-download" aria-hidden="true"></i>
                <h5 id="vb-setup-install-title">Installing ${name}</h5>
                <span class="vb-run-runtime">${escapeHtml(this.method.label)}</span>
                <div class="vb-run-window-controls">
                    <button type="button" data-run-action="stop" aria-label="Stop" title="Stop installer"><i class="fa-solid fa-stop" aria-hidden="true"></i></button>
                    <button type="button" data-run-action="minimize" aria-label="Minimize" title="Minimize — keep installing"><i class="fa-solid fa-minus" aria-hidden="true"></i></button>
                    <button type="button" data-run-action="close" aria-label="Close" title="Close and stop installer"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
                </div>
            </div>
            <div class="vb-setup-install-command" title="The exact command this window runs"><span aria-hidden="true">›</span><code>${escapeHtml(this.method.command)}</code></div>
            <div class="vb-run-terminal" data-run-terminal></div>
            <div class="vb-run-bottom-bar">
                <span data-run-status role="status" aria-live="polite">Starting…</span>
                <span>Answer any prompts here · Close stops the installer</span>
            </div>
        </section>`;
    }

    _updateDock() {
        if (!this._dockButton) return;
        this._dockButton.textContent = `${this.tool?.name || 'Installer'} · ${this._status || 'Installing'}`;
        this._dockButton.dataset.tone = this._tone || 'waiting';
    }

    _paintState(message, tone) {
        this._status = message;
        this._tone = tone;
        this._updateDock();
        const status = this.layer?.querySelector('[data-run-status]');
        if (status) { status.textContent = message; status.dataset.tone = tone; }
        const stop = this.layer?.querySelector('[data-run-action="stop"]');
        if (stop) { stop.hidden = !this.running; stop.disabled = false; }
        if (this.terminal?.terminal) this.terminal.terminal.options.disableStdin = !this._token?.ready;
    }
}

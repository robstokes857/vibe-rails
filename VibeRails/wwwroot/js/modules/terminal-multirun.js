import { populateLlmSelectionSelect, parseLlmSelection } from './utils.js';

const DEFAULT_LEFT_VALUE = 'base:claude';
const DEFAULT_RIGHT_VALUE = 'base:codex';

export function renderTerminalMultiRunModalHtml() {
    return `
        <div class="vb-multirun-modal">
            <p class="vb-multirun-help text-muted small mb-3">
                Open two terminal tabs with the same prompt.
            </p>
            <div class="row g-3">
                <div class="col-md-6">
                    <label class="form-label" for="vb-multirun-cli-1">First tab</label>
                    <select id="vb-multirun-cli-1" class="form-select"></select>
                </div>
                <div class="col-md-6">
                    <label class="form-label" for="vb-multirun-cli-2">Second tab</label>
                    <select id="vb-multirun-cli-2" class="form-select"></select>
                </div>
            </div>
            <div class="mt-3">
                <label class="form-label" for="vb-multirun-input">Initial prompt</label>
                <textarea id="vb-multirun-input" class="form-control font-monospace" rows="6"
                    placeholder="Type or paste a prompt to send to both tabs..." spellcheck="false"></textarea>               
            </div>
            <div class="vb-multirun-actions mt-3 d-flex justify-content-end gap-2">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="button" class="btn btn-primary" id="vb-multirun-run-btn">Run</button>
            </div>
        </div>
    `;
}

/**
 * Multi Run controller. Opens two new terminal tabs back-to-back with the
 * same initial prompt, each backed by a (potentially different) base CLI.
 * Lets the user compare how different CLIs respond to the same input.
 *
 * Today only base CLIs are selectable. See terminal-multirun.md for the
 * planned "include custom sandboxes" extension and its prerequisite.
 */
export class TerminalMultiRun {
    constructor(manager) {
        this.manager = manager;
        this.app = manager.app;
        this._cli1Select = null;
        this._cli2Select = null;
        this._tom1 = null;
        this._tom2 = null;
        this._input = null;
        this._runBtn = null;
        this._handleRun = this._handleRun.bind(this);
        this._handleKeydown = this._handleKeydown.bind(this);
    }

    show() {
        this.app.showModal('Multi Run', renderTerminalMultiRunModalHtml());
        this._mount();
    }

    _mount() {
        const container = document.getElementById('modal-container');
        if (!container) return;

        this._cli1Select = container.querySelector('#vb-multirun-cli-1');
        this._cli2Select = container.querySelector('#vb-multirun-cli-2');
        this._input = container.querySelector('#vb-multirun-input');
        this._runBtn = container.querySelector('#vb-multirun-run-btn');

        this._tom1 = this._buildBaseCliSelect(this._cli1Select, DEFAULT_LEFT_VALUE);
        this._tom2 = this._buildBaseCliSelect(this._cli2Select, DEFAULT_RIGHT_VALUE);

        this._runBtn?.addEventListener('click', this._handleRun);
        this._input?.addEventListener('keydown', this._handleKeydown);
        this._input?.focus();
    }

    _buildBaseCliSelect(selectEl, defaultValue) {
        if (!selectEl) return null;
        // Empty environments list → only base CLIs render in the dropdown.
        // When sandboxes are tied to a CLI/env (see terminal-multirun.md), the
        // sandbox list goes here in a custom group.
        populateLlmSelectionSelect(selectEl, [], {
            placeholder: null,
            includeGroups: false,
            includeDefaultSuffix: false,
            selectedValue: defaultValue,
            enhance: true
        });
        return selectEl.tomselect || null;
    }

    _handleKeydown(event) {
        if (event.key === 'Enter' && (event.metaKey || event.ctrlKey)) {
            event.preventDefault();
            this._handleRun();
        }
    }

    async _handleRun() {
        const sel1 = this._tom1?.getValue?.() || this._cli1Select?.value || '';
        const sel2 = this._tom2?.getValue?.() || this._cli2Select?.value || '';
        const prompt = (this._input?.value || '').trim();

        if (!sel1 || !sel2) {
            this.app.showError('Pick a CLI for both tabs.');
            return;
        }
        if (!prompt) {
            this.app.showError('Enter an initial prompt.');
            this._input?.focus();
            return;
        }

        const parsed1 = parseLlmSelection(sel1);
        const parsed2 = parseLlmSelection(sel2);
        if (!parsed1.cli || !parsed2.cli) {
            this.app.showError('Invalid CLI selection.');
            return;
        }

        this._runBtn.disabled = true;
        try {
            // Sequential is intentional. createAndActivateTab activates the new
            // tab synchronously (deactivating the previous), so parallel calls
            // would race on activation. Doing them in order leaves the user on
            // the second tab — which they selected as "second", so the focus
            // matches expectation.
            await this._launchTab(sel1, prompt);
            await this._launchTab(sel2, prompt);
            this.app.closeModal();
        } catch (error) {
            this.app.showError(`Failed to launch Multi Run: ${error.message}`);
            if (this._runBtn) this._runBtn.disabled = false;
        }
    }

    async _launchTab(selection, initialPrompt) {
        const parsed = parseLlmSelection(selection);
        const tab = await this.manager.createAndActivateTab({ selection });
        if (!tab) {
            throw new Error(`Could not create tab for ${parsed.cli}`);
        }

        const body = { cli: parsed.cli, initialPrompt };
        const started = await tab.instance.startSession(body);
        if (!started) {
            throw new Error(`Could not start session for ${parsed.cli}`);
        }

        const meta = this.manager.getSelectionMeta(tab.state.selection);
        tab.state.title = `${meta.displayName} Terminal`;
        this.manager.saveTabTitle(tab.state.id, tab.state.title);
        this.manager.updateUi();
    }
}

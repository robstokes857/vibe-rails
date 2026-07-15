import { VcaConsole } from './vca-console.js';
import {
    GIT_PREFLIGHT_STEPS,
    GitPreflightRunner,
    createAutorunOnce,
    createGitPreflightState,
    formatPreflightDuration,
    formatPreflightEventForConsole,
    reduceGitPreflightEvent,
    renderMintLintDetails,
    setSafeText,
    statusTone
} from './git-guard-preflight.js';

function normalizeHookStateToken(value) {
    return String(value ?? '').trim().toLowerCase().replace(/[^a-z0-9]+/g, '');
}

function isObject(value) {
    return value !== null && typeof value === 'object' && !Array.isArray(value);
}

function normalizeHookDetail(detail, { fallbackInstalled = false, inGitRepo = true } = {}) {
    if (!inGitRepo) {
        return {
            tone: 'neutral',
            label: 'Unavailable',
            message: 'Open a git repository to inspect this hook.',
            installed: false,
            current: false,
            needsRepair: false
        };
    }

    if (!isObject(detail)) {
        return fallbackInstalled
            ? {
                tone: 'success',
                label: 'Installed',
                message: 'Detailed hook health is not reported by this server.',
                installed: true,
                current: null,
                needsRepair: false
            }
            : {
                tone: 'neutral',
                label: 'Not installed',
                message: 'Install Git Guard to add this hook.',
                installed: false,
                current: false,
                needsRepair: false
            };
    }

    const token = normalizeHookStateToken(detail.state);
    const repairStates = new Set(['needsrepair', 'repair', 'outdated', 'stale', 'modified', 'mismatch', 'mismatched', 'broken', 'invalid', 'partial']);
    const currentStates = new Set(['current', 'healthy', 'installed', 'ready', 'managed']);
    const missingStates = new Set(['missing', 'absent', 'notinstalled', 'uninstalled']);
    const installed = detail.installed === true || detail.current === true || currentStates.has(token) || repairStates.has(token);
    const needsRepair = repairStates.has(token) || (installed && detail.current === false);
    const current = !needsRepair && (detail.current === true || currentStates.has(token));
    const missing = missingStates.has(token) || detail.installed === false || (!installed && detail.current === false);

    let tone = 'neutral';
    let label = 'Unknown';
    let fallbackMessage = 'Hook status could not be determined.';

    if (needsRepair) {
        tone = 'warning';
        label = 'Needs repair';
        fallbackMessage = 'The hook exists but does not match the current VibeRails hook.';
    } else if (current || installed) {
        tone = 'success';
        label = current ? 'Current' : 'Installed';
        fallbackMessage = current ? 'Installed and up to date.' : 'Hook is installed.';
    } else if (missing) {
        label = 'Not installed';
        fallbackMessage = 'Install Git Guard to add this hook.';
    }

    return {
        tone,
        label,
        message: String(detail.message || fallbackMessage),
        installed,
        current,
        needsRepair
    };
}

/**
 * Converts both the current detailed hook response and the original three-field
 * response into one honest UI model. Exported so the compatibility behavior can
 * stay covered without a browser DOM.
 */
export function normalizeHookStatus(status = {}, { isError = false } = {}) {
    const payload = isObject(status) ? status : {};
    const inGitRepo = payload.inGitRepo !== false;
    const reportedInstalled = payload.isInstalled === true;
    const stateToken = normalizeHookStateToken(payload.state);
    const explicitRepair = payload.needsRepair === true ||
        ['needsrepair', 'repair', 'outdated', 'degraded', 'partial', 'broken'].includes(stateToken);

    const preCommit = normalizeHookDetail(payload.preCommit, {
        fallbackInstalled: reportedInstalled,
        inGitRepo
    });
    const commitMessage = normalizeHookDetail(payload.commitMessage, {
        fallbackInstalled: reportedInstalled,
        inGitRepo
    });
    const needsRepair = inGitRepo && (explicitRepair || preCommit.needsRepair || commitMessage.needsRepair);
    const isInstalled = inGitRepo && (reportedInstalled || (preCommit.current === true && commitMessage.current === true));
    const anyHookInstalled = preCommit.installed || commitMessage.installed;

    if (isError) {
        return {
            tone: 'danger',
            badge: 'Unavailable',
            title: 'Hook status unavailable',
            message: String(payload.message || 'VibeRails could not inspect the repository hooks.'),
            inGitRepo: false,
            isInstalled: false,
            needsRepair: false,
            installLabel: 'Install Hooks',
            installDisabled: true,
            uninstallDisabled: true,
            repositoryPath: '',
            hooksPath: '',
            autoInstall: null,
            preCommit,
            commitMessage
        };
    }

    let tone = 'neutral';
    let badge = 'Not installed';
    let title = 'Git Guard is off';
    let fallbackMessage = 'Install both hooks to run VCA checks whenever git commit runs.';

    if (!inGitRepo) {
        tone = 'warning';
        badge = 'No repository';
        title = 'Git Guard needs a Git repository';
        fallbackMessage = 'Open a Git repository to install or inspect VCA hooks.';
    } else if (needsRepair) {
        tone = 'warning';
        badge = 'Repair needed';
        title = 'Git Guard needs attention';
        fallbackMessage = 'One or more hooks are missing, outdated, or no longer managed by VibeRails.';
    } else if (isInstalled) {
        tone = 'success';
        badge = 'Protected';
        title = 'Git Guard is on';
        fallbackMessage = 'VCA checks will run automatically during git commit.';
    }

    return {
        tone,
        badge,
        title,
        message: String(payload.message || fallbackMessage),
        inGitRepo,
        isInstalled,
        needsRepair,
        installLabel: needsRepair ? 'Repair Hooks' : isInstalled ? 'Hooks Installed' : 'Install Hooks',
        installDisabled: !inGitRepo || (isInstalled && !needsRepair),
        uninstallDisabled: !inGitRepo || !(reportedInstalled || anyHookInstalled || needsRepair),
        repositoryPath: String(payload.repositoryPath || ''),
        hooksPath: String(payload.hooksPath || ''),
        autoInstall: typeof payload.autoInstallEnabled === 'boolean'
            ? (payload.autoInstallEnabled ? 'Auto-install enabled' : 'Manual installation')
            : null,
        preCommit,
        commitMessage
    };
}

export class RuleController {
    constructor(app) {
        this.app = app;
        this.viewRoot = null;
        this.vcaConsole = null;
        this.hookStatus = null;
        this.preflightState = createGitPreflightState();
        this.preflightRunner = null;
        this.focusedMode = false;
        this.autorunFocusedPreflight = createAutorunOnce(() => this.runGitPreflight());
    }

    loadCheckViolations() {
        return this.loadGitGuard({ focused: false });
    }

    loadFocusedGitGuard() {
        return this.loadGitGuard({ focused: true });
    }

    loadGitGuard({ focused = false } = {}) {
        this.unload();
        const content = document.getElementById('app-content');
        if (!content) return;

        this.focusedMode = focused;
        this.preflightState = createGitPreflightState();
        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('check-violations-template');
        const root = fragment.querySelector('[data-view="check-violations"]');

        if (root) {
            this.viewRoot = root;
            root.dataset.mode = focused ? 'focused' : 'dashboard';
            // Back is handled once by app.js's global delegated action listener.
            this.app.bindAction(root, '[data-action="run-hook-preview"]', () => this.runHookPreview());
            this.app.bindAction(root, '[data-action="run-git-preflight"]', () => this.runGitPreflight());
            this.app.bindAction(root, '[data-action="cancel-git-preflight"]', () => this.cancelGitPreflight());
            this.app.bindAction(root, '[data-action="exit-git-guard"]', () => this.exitToDashboard());
            this.app.bindAction(root, '[data-action="install-hooks"]', () => this.installHooks());
            this.app.bindAction(root, '[data-action="uninstall-hooks"]', () => this.uninstallHooks());
            this.app.bindAction(root, '[data-action="copy-hook-output"]', () => this.copyHookOutput());
            this.app.bindAction(root, '[data-action="clear-hook-output"]', () => this.clearHookOutput());
            this.vcaConsole = new VcaConsole(root.querySelector('[data-vca-console]'));
        }

        content.appendChild(fragment);
        this.renderPreflightState();
        this.refreshHookStatus();
        if (focused) this.autorunFocusedPreflight();
    }

    unload() {
        this.preflightRunner?.cancel();
        this.preflightRunner = null;
        this.viewRoot = null;
        this.vcaConsole = null;
    }

    exitToDashboard() {
        try {
            const url = new URL(window.location.href);
            url.pathname = '/';
            url.searchParams.delete('view');
            window.history.replaceState({}, document.title, url.toString());
        } catch {
            // Navigation still works when an embedded host does not expose History.
        }
        this.app.navigate('dashboard', {}, { resetStack: true });
    }

    async refreshHookStatus() {
        this.setHookStatusLoading(true);

        try {
            const status = await this.app.apiCall('/api/v1/hooks/status', 'GET', null, { showLoading: false });
            this.renderHookStatus(status);
        } catch (error) {
            this.renderHookStatus({
                inGitRepo: false,
                isInstalled: false,
                message: error?.message || 'Unable to check hook status'
            }, true);
        }
    }

    async installHooks() {
        const button = this.query('[data-action="install-hooks"]');
        this.setButtonBusy(button, true, this.hookStatus?.needsRepair ? 'Repairing…' : 'Installing…');
        this.setHookActionButtonsDisabled(true);

        try {
            const response = await this.app.apiCall('/api/v1/hooks/install', 'POST', null, { showLoading: false });
            const succeeded = response?.success === true;
            const responseMessage = response?.message || 'Git hook setup finished.';
            this.app.showToast(
                succeeded ? (this.hookStatus?.needsRepair ? 'Hooks Repaired' : 'Hooks Installed') : 'Hook Install Failed',
                responseMessage,
                succeeded ? 'success' : 'error');
            this.vcaConsole?.report(`${succeeded ? '[pass]' : '[error]'} ${responseMessage}`, {
                tone: succeeded ? 'success' : 'danger',
                state: succeeded ? 'Setup complete' : 'Setup failed',
                meta: responseMessage
            });
            await this.refreshHookStatus();
        } catch (error) {
            this.app.showError(`Failed to install hooks: ${error.message}`);
            this.vcaConsole?.report(`[error] Failed to install hooks: ${error.message}`, {
                tone: 'danger',
                state: 'Setup failed',
                meta: 'Hook installation failed'
            });
            await this.refreshHookStatus();
        } finally {
            this.setButtonBusy(button, false);
        }
    }

    async uninstallHooks() {
        const button = this.query('[data-action="uninstall-hooks"]');
        this.setButtonBusy(button, true, 'Removing…');
        this.setHookActionButtonsDisabled(true);

        try {
            const response = await this.app.apiCall('/api/v1/hooks', 'DELETE', null, { showLoading: false });
            const succeeded = response?.success === true;
            const responseMessage = response?.message || 'Git hook removal finished.';
            this.app.showToast(
                succeeded ? 'Hooks Removed' : 'Hook Removal Failed',
                responseMessage,
                succeeded ? 'info' : 'error');
            this.vcaConsole?.report(`${succeeded ? '[pass]' : '[error]'} ${responseMessage}`, {
                tone: succeeded ? 'success' : 'danger',
                state: succeeded ? 'Hooks removed' : 'Removal failed',
                meta: responseMessage
            });
            await this.refreshHookStatus();
        } catch (error) {
            this.app.showError(`Failed to remove hooks: ${error.message}`);
            this.vcaConsole?.report(`[error] Failed to remove hooks: ${error.message}`, {
                tone: 'danger',
                state: 'Removal failed',
                meta: 'Hook removal failed'
            });
            await this.refreshHookStatus();
        } finally {
            this.setButtonBusy(button, false);
        }
    }

    renderHookStatus(status, isError = false) {
        const viewModel = normalizeHookStatus(status, { isError });
        this.hookStatus = viewModel;

        const health = this.query('[data-hook-health]');
        const badge = this.query('[data-hook-status-badge]');
        const installButton = this.query('[data-action="install-hooks"]');
        const uninstallButton = this.query('[data-action="uninstall-hooks"]');
        const previewButton = this.query('[data-action="run-hook-preview"]');

        if (health) {
            health.setAttribute('aria-busy', 'false');
            health.dataset.tone = viewModel.tone;
        }
        if (badge) {
            badge.dataset.tone = viewModel.tone;
            badge.textContent = viewModel.badge;
        }
        this.setText('[data-hook-status-title]', viewModel.title);
        this.setText('[data-hook-status-message]', viewModel.message);
        this.renderHookDetail('pre-commit', viewModel.preCommit);
        this.renderHookDetail('commit-message', viewModel.commitMessage);
        this.renderOptionalValue('[data-hook-repository-row]', '[data-hook-repository-path]', viewModel.repositoryPath);
        this.renderOptionalValue('[data-hook-path-row]', '[data-hook-path]', viewModel.hooksPath);
        this.renderOptionalValue('[data-hook-auto-install-row]', '[data-hook-auto-install]', viewModel.autoInstall);

        if (installButton) {
            installButton.dataset.idleLabel = viewModel.installLabel;
            if (installButton.getAttribute('aria-busy') !== 'true') {
                this.setButtonLabel(installButton, viewModel.installLabel);
            }
            this.setButtonDisabled(installButton, viewModel.installDisabled);
        }
        this.setButtonDisabled(uninstallButton, viewModel.uninstallDisabled);
        this.setButtonDisabled(previewButton, !viewModel.inGitRepo || isError);
        if (this.preflightRunner?.isRunning) this.setHookMutationButtonsDisabled(true);
    }

    setHookStatusLoading(isLoading) {
        const health = this.query('[data-hook-health]');
        const badge = this.query('[data-hook-status-badge]');

        if (!isLoading) return;

        if (health) {
            health.setAttribute('aria-busy', 'true');
            health.dataset.tone = 'neutral';
        }
        if (badge) {
            badge.dataset.tone = 'neutral';
            badge.textContent = 'Checking';
        }
        this.setText('[data-hook-status-title]', 'Checking Git Guard');
        this.setText('[data-hook-status-message]', 'Inspecting the repository hook files…');
        this.renderHookDetail('pre-commit', { tone: 'neutral', label: 'Checking', message: 'Inspecting hook…' });
        this.renderHookDetail('commit-message', { tone: 'neutral', label: 'Checking', message: 'Inspecting hook…' });
        this.setHookActionButtonsDisabled(true);
    }

    setHookActionButtonsDisabled(disabled) {
        this.viewRoot?.querySelectorAll('[data-action="install-hooks"], [data-action="uninstall-hooks"], [data-action="run-hook-preview"]')
            .forEach(button => {
                this.setButtonDisabled(button, disabled);
            });
    }

    async runHookPreview() {
        const button = this.query('[data-action="run-hook-preview"]');
        this.setButtonBusy(button, true, 'Running Hook Check…');
        this.setHookMutationButtonsDisabled(true);
        this.setConsoleUtilityButtonsDisabled(true);
        this.vcaConsole?.begin();
        try {
            const response = await this.app.apiCall('/api/v1/hooks/preview', 'POST', null, { showLoading: false });
            const result = this.vcaConsole?.complete(response);
            const tone = result?.tone || (response?.success === false ? 'danger' : 'success');
            const title = tone === 'danger'
                ? 'Hook Check Failed'
                : tone === 'warning'
                    ? 'Hook Check Warning'
                    : 'Hook Check Complete';
            this.app.showToast(
                title,
                result?.meta || response?.status || 'Hook check finished.',
                tone === 'danger' ? 'error' : tone === 'warning' ? 'warning' : 'success');
        } catch (error) {
            this.vcaConsole?.fail(error);
            this.app.showError(`Hook check failed: ${error.message}`);
        } finally {
            this.setButtonBusy(button, false);
            this.setHookMutationButtonsDisabled(false);
            this.setConsoleUtilityButtonsDisabled(false);
        }
    }

    async runGitPreflight() {
        if (this.preflightRunner?.isRunning) return false;

        const baseUrl = window.__viberails_API_BASE__ || '';
        const tabToken = this.app.getSessionStorageValue?.('viberails_tab') ??
            globalThis.sessionStorage?.getItem?.('viberails_tab');
        this.preflightState = createGitPreflightState();
        this.preflightState = {
            ...this.preflightState,
            status: 'running',
            message: 'Opening the Git Guard event stream…'
        };
        this.renderPreflightState();
        this.setHookMutationButtonsDisabled(true);
        this.setConsoleUtilityButtonsDisabled(true);
        this.vcaConsole?.begin('git guard preflight');

        this.preflightRunner = new GitPreflightRunner({
            url: `${baseUrl}/api/v1/git/preflight/stream`,
            headers: tabToken ? { viberails_tab: tabToken } : {}
        });

        try {
            const result = await this.preflightRunner.run(event => this.applyPreflightEvent(event));
            if (result.cancelled && this.preflightState.status === 'running') {
                this.applyPreflightEvent({
                    runId: this.preflightState.runId,
                    sequence: this.preflightState.sequence + 1,
                    type: 'run_finished',
                    status: 'cancelled',
                    message: 'Git preflight cancelled.',
                    blocking: false,
                    commitAllowed: false
                });
            } else if (this.preflightState.status === 'running') {
                this.applyPreflightEvent({
                    runId: this.preflightState.runId,
                    sequence: this.preflightState.sequence + 1,
                    type: 'run_finished',
                    status: 'error',
                    message: 'The event stream ended before Git Guard reported a result.',
                    blocking: true,
                    commitAllowed: false
                });
            }
            return true;
        } catch (error) {
            if (error?.status === 401 && !window.__viberails_VSCODE__) {
                window.location.href = `${baseUrl}/auth/bootstrap`;
            }
            this.applyPreflightEvent({
                runId: this.preflightState.runId,
                sequence: this.preflightState.sequence + 1,
                type: 'run_finished',
                status: 'error',
                message: error?.message || 'Git preflight failed.',
                blocking: true,
                commitAllowed: false
            });
            return false;
        } finally {
            this.renderPreflightState();
            this.setHookMutationButtonsDisabled(false);
            this.setConsoleUtilityButtonsDisabled(false);
        }
    }

    cancelGitPreflight() {
        const cancelled = this.preflightRunner?.cancel() === true;
        if (cancelled) {
            this.preflightState = { ...this.preflightState, message: 'Cancelling Git preflight…' };
            this.renderPreflightState();
        }
        return cancelled;
    }

    applyPreflightEvent(event) {
        this.preflightState = reduceGitPreflightEvent(this.preflightState, event);
        const line = formatPreflightEventForConsole(event);
        if (line) this.vcaConsole?.writeLine(line);
        this.renderPreflightState();

        if (String(event?.type || '').toLowerCase() === 'run_finished') {
            const tone = statusTone(this.preflightState.status);
            const metaDuration = formatPreflightDuration(this.preflightState.durationMs);
            this.vcaConsole?.finishStream({
                tone,
                state: this.preflightState.commitAllowed === true
                    ? 'Commit allowed'
                    : this.preflightState.status === 'cancelled' ? 'Cancelled' : 'Commit blocked',
                meta: [this.preflightState.message, metaDuration].filter(Boolean).join(' · ')
            });
        }
    }

    renderPreflightState() {
        if (!this.viewRoot) return;
        const state = this.preflightState;
        const running = state.status === 'running';
        const runButton = this.query('[data-action="run-git-preflight"]');
        const cancelButton = this.query('[data-action="cancel-git-preflight"]');

        if (runButton) {
            runButton.disabled = running;
            this.setTextWithin(runButton, '[data-button-label]', running ? 'Running…' : 'Run Again');
        }
        if (cancelButton) cancelButton.disabled = !running;

        const runState = this.query('[data-preflight-run-state]');
        if (runState) {
            runState.dataset.tone = statusTone(state.status);
            runState.textContent = this.formatStatusLabel(state.status);
        }
        this.setText('[data-preflight-message]', state.message);

        const stagedCount = state.staged.count;
        this.setText('[data-preflight-staged-count]', stagedCount === null
            ? 'Waiting for staged snapshot'
            : `${stagedCount} staged file${stagedCount === 1 ? '' : 's'}`);
        this.renderStagedFiles(state.staged.files);

        for (const stepDefinition of GIT_PREFLIGHT_STEPS) {
            const step = state.steps[stepDefinition.id];
            const card = this.query(`[data-preflight-step="${stepDefinition.id}"]`);
            if (!card) continue;
            card.dataset.status = step.status;
            card.dataset.tone = statusTone(step.status);
            const badge = card.querySelector('[data-preflight-step-status]');
            if (badge) {
                badge.dataset.tone = statusTone(step.status);
                badge.textContent = this.formatStatusLabel(step.status);
            }
            setSafeText(card.querySelector('[data-preflight-step-message]'), step.message);
            const duration = formatPreflightDuration(step.durationMs);
            const durationElement = card.querySelector('[data-preflight-step-duration]');
            if (durationElement) {
                durationElement.hidden = !duration;
                durationElement.textContent = duration;
            }
            const output = card.querySelector('[data-preflight-step-output]');
            if (output) {
                output.hidden = !step.output;
                output.textContent = step.output;
            }

            if (stepDefinition.id === 'mintlint') {
                const detailsContainer = card.querySelector('[data-mintlint-details]');
                const disclosure = card.querySelector('[data-mintlint-disclosure]');
                const fileCount = renderMintLintDetails(detailsContainer, step.details);
                if (disclosure) disclosure.hidden = fileCount === 0;
            }
        }

        this.renderFinalPreflightResult(state);
    }

    renderStagedFiles(files) {
        const container = this.query('[data-preflight-staged-files]');
        if (!container) return;
        container.replaceChildren();
        for (const file of files) {
            const item = document.createElement('li');
            item.textContent = file;
            container.append(item);
        }
        container.hidden = files.length === 0;
    }

    renderFinalPreflightResult(state) {
        const result = this.query('[data-preflight-result]');
        if (!result) return;
        const complete = state.status !== 'idle' && state.status !== 'running';
        result.dataset.tone = statusTone(state.status);
        result.setAttribute('aria-busy', String(state.status === 'running'));
        this.setText('[data-preflight-result-title]', !complete
            ? (state.status === 'running' ? 'Checking this commit…' : 'Commit decision pending')
            : state.commitAllowed === true ? 'Commit allowed' : state.status === 'cancelled' ? 'Check cancelled' : 'Commit blocked');
        this.setText('[data-preflight-result-message]', complete
            ? state.message
            : state.status === 'running'
                ? 'Git Guard is running every configured safeguard.'
                : 'Run the preflight to decide whether Git may create this commit.');
        const decision = this.query('[data-preflight-decision]');
        if (decision) {
            decision.dataset.tone = statusTone(state.status);
            decision.textContent = !complete ? (state.status === 'running' ? 'In progress' : 'Not checked')
                : state.commitAllowed === true ? 'Allowed' : state.status === 'cancelled' ? 'Cancelled' : 'Blocked';
        }
    }

    formatStatusLabel(status) {
        const value = String(status || 'pending').replace(/[-_]+/g, ' ');
        return value.charAt(0).toUpperCase() + value.slice(1);
    }

    setTextWithin(root, selector, value) {
        const element = root?.querySelector(selector);
        if (element) element.textContent = value;
    }

    // Kept as a compatibility alias for callers that used the former method name.
    runVCAValidation() {
        return this.runHookPreview();
    }

    async copyHookOutput() {
        const copied = await this.vcaConsole?.copy();
        this.app.showToast(
            copied ? 'Console Copied' : 'Copy Unavailable',
            copied ? 'Hook output copied to the clipboard.' : 'Select the console text and copy it manually.',
            copied ? 'success' : 'warning');
    }

    clearHookOutput() {
        this.vcaConsole?.clear();
    }

    setConsoleUtilityButtonsDisabled(disabled) {
        this.viewRoot?.querySelectorAll('[data-action="copy-hook-output"], [data-action="clear-hook-output"]')
            .forEach(button => {
                button.disabled = Boolean(disabled);
            });
    }

    setHookMutationButtonsDisabled(disabled) {
        const installButton = this.query('[data-action="install-hooks"]');
        const uninstallButton = this.query('[data-action="uninstall-hooks"]');
        this.setButtonDisabled(
            installButton,
            disabled || !this.hookStatus || this.hookStatus.installDisabled);
        this.setButtonDisabled(
            uninstallButton,
            disabled || !this.hookStatus || this.hookStatus.uninstallDisabled);
    }

    renderHookDetail(key, detail) {
        const badge = this.query(`[data-hook-detail="${key}"] [data-hook-detail-badge]`);
        const message = this.query(`[data-hook-detail="${key}"] [data-hook-detail-message]`);
        if (badge) {
            badge.dataset.tone = detail?.tone || 'neutral';
            badge.textContent = detail?.label || 'Unknown';
        }
        if (message) message.textContent = detail?.message || '';
    }

    renderOptionalValue(rowSelector, valueSelector, value) {
        const row = this.query(rowSelector);
        const valueElement = this.query(valueSelector);
        if (row) row.hidden = !value;
        if (valueElement) valueElement.textContent = value || '';
    }

    setButtonBusy(button, isBusy, busyLabel = 'Working…') {
        if (!button) return;
        const spinner = button.querySelector('[data-button-spinner]');
        const icon = button.querySelector('[data-button-icon]');
        if (isBusy && button.getAttribute('aria-busy') !== 'true') {
            button.dataset.restingDisabled = String(button.disabled);
        }
        button.setAttribute('aria-busy', String(Boolean(isBusy)));
        button.disabled = isBusy || button.dataset.restingDisabled === 'true';
        if (spinner) spinner.hidden = !isBusy;
        if (icon) icon.hidden = Boolean(isBusy);
        this.setButtonLabel(button, isBusy ? busyLabel : (button.dataset.idleLabel || 'Run Hook Check'));
    }

    setButtonDisabled(button, disabled) {
        if (!button) return;
        button.dataset.restingDisabled = String(Boolean(disabled));
        button.disabled = button.getAttribute('aria-busy') === 'true' || Boolean(disabled);
    }

    setButtonLabel(button, label) {
        const labelElement = button?.querySelector('[data-button-label]');
        if (labelElement) labelElement.textContent = label;
    }

    setText(selector, value) {
        const element = this.query(selector);
        if (element) element.textContent = value || '';
    }

    query(selector) {
        return this.viewRoot?.querySelector(selector) || null;
    }

    loadActiveRules() {
        const content = document.getElementById('app-content');
        if (!content) return;

        content.innerHTML = '';
        const fragment = this.app.cloneTemplate('active-rules-template');
        const root = fragment.querySelector('[data-view="active-rules"]');

        if (root) {
            this.app.bindAction(root, '[data-action="go-back"]', () => this.app.goBack());
            const container = root.querySelector('[data-active-rules]');
            if (container) {
                container.innerHTML = this.renderActiveRulesTree(container);
            }
        }

        content.appendChild(fragment);
    }

    renderActiveRulesTree(container) {
        // Show actual rules from agents
        if (!this.app.data.agents || this.app.data.agents.length === 0) {
            return '<p class="text-muted text-center">No agent files found. Create an AGENTS.md to define rules.</p>';
        }

        const html = this.app.data.agents.map((agent, idx) => {
            const displayName = agent.customName || agent.name;

            return `
            <div class="card mb-3">
                <div class="card-header" style="cursor: pointer;" data-active-rule-agent="${idx}">
                    <strong>${this.app.escapeHtml(displayName)}</strong>
                    <small class="text-muted ms-2">${agent.ruleCount} rule(s)</small>
                    <span class="text-muted ms-2">&rarr;</span>
                </div>
                <div class="card-body">
                    ${agent.rules && agent.rules.length > 0 ? `
                        <ul class="list-unstyled mb-0">
                            ${agent.rules.map(rule => {
                                const badgeClass = rule.enforcement === 'STOP' ? 'bg-danger' :
                                                   rule.enforcement === 'COMMIT' ? 'bg-warning' :
                                                   rule.enforcement === 'WARN' ? 'bg-info' : 'bg-secondary';
                                return `
                                    <li class="mb-2">
                                        <span class="badge ${badgeClass}">${rule.enforcement}</span>
                                        <span class="ms-2">${this.app.escapeHtml(rule.text)}</span>
                                    </li>
                                `;
                            }).join('')}
                        </ul>
                    ` : '<p class="text-muted mb-0">No rules defined</p>'}
                </div>
            </div>
        `;
        }).join('');

        // Bind click handlers after rendering (CSP-safe)
        if (container) {
            setTimeout(() => {
                container.querySelectorAll('[data-active-rule-agent]').forEach(el => {
                    const idx = parseInt(el.dataset.activeRuleAgent);
                    const agent = this.app.data.agents[idx];
                    if (agent) {
                        el.addEventListener('click', () => this.app.navigate('agent-edit', agent));
                    }
                });
            }, 0);
        }

        return html;
    }
}

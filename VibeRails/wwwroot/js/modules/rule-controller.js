import { VcaConsole, copyVcaConsoleText } from './vca-console.js';
import {
    disposeCodeAnalyzerDashboard,
    renderCodeAnalyzerDashboard
} from './code-analyzer-dashboard.js';
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

function formatAnalyzerRating(value) {
    const rating = String(value || '').trim();
    if (!rating) return 'No staged code';
    return rating
        .replace(/([a-z])([A-Z])/g, '$1 $2')
        .replace(/[-_]+/g, ' ')
        .replace(/^./, character => character.toUpperCase());
}

export function buildCodeAnalyzerSummary(response = {}) {
    const rawScore = Number(response?.healthScore);
    const hasScore = Number.isFinite(rawScore);
    const score = hasScore ? Math.min(100, Math.max(0, rawScore)) : null;
    const analyzedFileCount = Math.max(0, Number.parseInt(response?.analyzedFileCount, 10) || 0);
    const skippedFileCount = Math.max(0, Number.parseInt(response?.skippedFileCount, 10) || 0);
    const tone = score === null
        ? 'neutral'
        : score >= 70 ? 'success' : score >= 45 ? 'warning' : 'danger';

    return {
        score,
        scoreLabel: score === null ? '—' : Number.isInteger(score) ? String(score) : score.toFixed(1),
        rating: formatAnalyzerRating(score === null ? '' : response?.rating),
        analyzedFileCount,
        skippedFileCount,
        tone
    };
}

function normalizeVcaFinding(finding = {}) {
    const status = String(finding?.status || '').trim().toLowerCase();
    const presentation = {
        blocked: { label: 'STOP', tone: 'danger' },
        acknowledgment_required: { label: 'COMMIT', tone: 'warning' },
        warning: { label: 'WARN', tone: 'warning' },
        deferred: { label: 'LATER', tone: 'info' }
    }[status] || { label: String(finding?.enforcement || 'INFO').toUpperCase(), tone: 'info' };

    return {
        status,
        label: presentation.label,
        tone: presentation.tone,
        enforcement: String(finding?.enforcement || presentation.label).toUpperCase(),
        rule: String(finding?.rule || 'VCA rule'),
        reason: String(finding?.reason || 'The rule did not pass.'),
        sourcePath: String(finding?.sourcePath || ''),
        guidance: String(finding?.guidance || 'Review this finding before committing.'),
        acknowledgment: finding?.acknowledgment ? String(finding.acknowledgment) : null
    };
}

export function buildVcaExplanationViewModel(response = {}) {
    const validation = isObject(response?.validation) ? response.validation : {};
    const findings = Array.isArray(validation.findings)
        ? validation.findings.map(normalizeVcaFinding)
        : [];
    const count = (key, status) => {
        const supplied = Number(validation[key]);
        return Number.isFinite(supplied)
            ? Math.max(0, Math.trunc(supplied))
            : findings.filter(finding => finding.status === status).length;
    };
    const stopCount = count('stopCount', 'blocked');
    const commitCount = count('commitCount', 'acknowledgment_required');
    const warningCount = count('warningCount', 'warning');
    const deferredCount = count('deferredCount', 'deferred');
    const stagedFileCount = Math.max(0, Number.parseInt(validation.stagedFileCount, 10) || 0);
    const applicableRuleCount = Math.max(0, Number.parseInt(validation.applicableRuleCount, 10) || 0);
    let outcome = String(validation.outcome || '').trim().toLowerCase();
    if (!outcome) {
        outcome = response?.success === false ? 'error' : 'passed';
    }

    let tone = 'success';
    let icon = 'fa-circle-check';
    let title = 'Staged changes satisfy your VCA rules';
    let message = applicableRuleCount > 0
        ? `${stagedFileCount} staged file${stagedFileCount === 1 ? '' : 's'} passed ${applicableRuleCount} applicable rule${applicableRuleCount === 1 ? '' : 's'}.`
        : 'No VCA violations were found in the staged changes.';

    if (outcome === 'blocked') {
        tone = 'danger';
        icon = 'fa-circle-xmark';
        title = `Commit blocked — ${stopCount || findings.length} issue${(stopCount || findings.length) === 1 ? '' : 's'} must be fixed`;
        message = 'STOP rules cannot be bypassed. Fix the items below, stage the corrections, and run validation again.';
    } else if (outcome === 'attention') {
        tone = 'warning';
        icon = 'fa-triangle-exclamation';
        if (commitCount > 0) {
            title = `${commitCount} commit acknowledgment${commitCount === 1 ? '' : 's'} required`;
            message = warningCount > 0
                ? `The commit-message hook will require the listed acknowledgment${commitCount === 1 ? '' : 's'}; ${warningCount} additional warning${warningCount === 1 ? '' : 's'} will not block.`
                : 'Fix each item, or include its exact token and a meaningful reason in the final commit message.';
        } else {
            title = `${warningCount || findings.length} warning${(warningCount || findings.length) === 1 ? '' : 's'} to review`;
            message = 'These findings will not block the commit, but they may point to work worth addressing.';
        }
    } else if (outcome === 'empty') {
        tone = 'info';
        icon = 'fa-circle-info';
        title = 'Nothing staged to validate';
        message = 'Stage the files you want to commit, then run validation again.';
    } else if (outcome === 'error') {
        tone = 'danger';
        icon = 'fa-circle-exclamation';
        title = 'Validation could not finish';
        message = 'The validator itself failed; this does not necessarily mean your code violated a rule. Review the raw details below.';
    } else if (deferredCount > 0) {
        tone = 'info';
        icon = 'fa-clock';
        title = 'Pre-commit rules passed';
        message = `${deferredCount} check${deferredCount === 1 ? '' : 's'} will run later against the final commit message.`;
    }

    const stats = [
        { value: stagedFileCount, label: 'Staged' },
        { value: applicableRuleCount, label: 'Rules' },
        { value: findings.length, label: 'Findings' }
    ];
    const fixBriefLines = [
        `VCA VALIDATION: ${title}`,
        message,
        ...findings.flatMap((finding, index) => [
            '',
            `${index + 1}. [${finding.label}] ${finding.rule}`,
            finding.sourcePath ? `Rule source: ${finding.sourcePath}` : null,
            `Why it failed: ${finding.reason}`,
            `What to do next: ${finding.guidance}`,
            finding.acknowledgment ? `Acknowledgment: ${finding.acknowledgment} Reason: <explain why this is acceptable>` : null
        ].filter(Boolean))
    ];

    return {
        outcome,
        tone,
        icon,
        title,
        message,
        stats,
        findings,
        actionable: outcome === 'blocked' || outcome === 'attention' || outcome === 'error',
        fixBrief: fixBriefLines.join('\n')
    };
}

export class RuleController {
    constructor(app) {
        this.app = app;
        this.viewRoot = null;
        this.vcaConsole = null;
        this.codeAnalyzerConsole = null;
        this.lastVcaFixBrief = '';
        this.hookStatus = null;
        this.preflightState = createGitPreflightState();
        this.preflightRunner = null;
        this.focusedMode = false;
    }

    loadCheckViolations() {
        return this.loadGitGuard({ focused: false });
    }

    loadFocusedGitGuard() {
        return this.loadGitGuard({ focused: true });
    }

    attachRulesOverview(root) {
        this.unload();
        this.focusedMode = false;
        this.viewRoot = root;
        this.bindHookControls(root);
        void this.runRulesOverviewChecks(root);
    }

    async runRulesOverviewChecks(root) {
        await this.refreshHookStatus();
        if (this.viewRoot !== root || !this.hookStatus?.inGitRepo) return false;
        await Promise.all([
            this.runHookPreview(),
            this.runCodeAnalyzer()
        ]);
        return true;
    }

    bindHookControls(root) {
        this.app.bindAction(root, '[data-action="run-hook-preview"]', () => this.runHookPreview());
        this.app.bindAction(root, '[data-action="toggle-hooks"]', () => this.toggleHooks());
        this.app.bindAction(root, '[data-action="install-hooks"]', () => this.installHooks());
        this.app.bindAction(root, '[data-action="uninstall-hooks"]', () => this.confirmUninstallHooks());
        this.app.bindAction(root, '[data-action="copy-hook-output"]', () => this.copyHookOutput());
        this.app.bindAction(root, '[data-action="clear-hook-output"]', () => this.clearHookOutput());
        this.app.bindAction(root, '[data-action="run-code-analyzer"]', () => this.runCodeAnalyzer());
        this.app.bindAction(root, '[data-action="copy-code-analyzer-output"]', () => this.copyCodeAnalyzerOutput());
        this.app.bindAction(root, '[data-action="clear-code-analyzer-output"]', () => this.clearCodeAnalyzerOutput());
        this.app.bindAction(root, '[data-action="open-fix-terminal"]', () => this.openFixTerminal());
        this.app.bindAction(root, '[data-action="copy-fix-brief"]', () => this.copyVcaFixBrief());
        this.vcaConsole = new VcaConsole(root.querySelector('[data-vca-console]'));
        this.codeAnalyzerConsole = new VcaConsole(root.querySelector('[data-code-analyzer-console]'), {
            defaultMessage: 'Code metrics ready.\nChange a supported source file, then run the scan.',
            runningMessage: 'Analyzing working-tree source changes…',
            failureMessage: 'The code metrics scan could not be completed.',
            failureMeta: 'Code metrics scan failed'
        });
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
            this.app.bindAction(root, '[data-action="run-git-preflight"]', () => this.runGitPreflight());
            this.app.bindAction(root, '[data-action="cancel-git-preflight"]', () => this.cancelGitPreflight());
            this.app.bindAction(root, '[data-action="exit-git-guard"]', () => this.exitToDashboard());
            this.bindHookControls(root);
        }

        content.appendChild(fragment);
        this.renderPreflightState();
        this.refreshHookStatus();
        if (focused && root) {
            // The once-gate belongs to this rendered view, not the controller lifetime. Capturing
            // the root also prevents an autorun queued by a view that was immediately replaced.
            const autorunFocusedPreflight = createAutorunOnce(() => {
                if (this.viewRoot === root && this.focusedMode) this.runGitPreflight();
            });
            autorunFocusedPreflight();
        }
    }

    unload() {
        this.preflightRunner?.cancel();
        this.preflightRunner = null;
        disposeCodeAnalyzerDashboard(this.viewRoot?.querySelector?.('[data-code-analyzer-report]'));
        this.viewRoot = null;
        this.vcaConsole = null;
        this.codeAnalyzerConsole = null;
        this.lastVcaFixBrief = '';
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
        const button = this.query('[data-action="install-hooks"]') || this.query('[data-action="toggle-hooks"]');
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
        const button = this.query('[data-action="uninstall-hooks"]') || this.query('[data-action="toggle-hooks"]');
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

    async toggleHooks() {
        if (!this.hookStatus?.inGitRepo) return;
        if (this.hookStatus.isInstalled && !this.hookStatus.needsRepair) {
            this.confirmUninstallHooks();
            return;
        }
        await this.installHooks();
    }

    confirmUninstallHooks() {
        this.app.showModal('Keep Git Guard enabled?', `
            <div class="git-guard-uninstall-warning">
                <div class="git-guard-uninstall-warning-icon" aria-hidden="true">
                    <i class="fa-solid fa-shield-halved"></i>
                </div>
                <div>
                    <h5>Are you sure you want to remove this protection?</h5>
                    <p>Git Guard runs VCA and commit-message checks before Git creates a commit. Turning it off removes those automatic safeguards, so violations can reach the repository without being checked.</p>
                    <p class="mb-0"><strong>We recommend leaving Git Guard enabled.</strong> You can still run either check manually from this page.</p>
                </div>
            </div>
            <div class="d-flex flex-wrap gap-2 justify-content-end mt-4">
                <button type="button" class="btn btn-outline-danger" data-action="confirm-uninstall-hooks">
                    Uninstall anyway
                </button>
                <button type="button" class="btn btn-primary" data-action="close-modal">
                    <i class="fa-solid fa-shield-halved me-1" aria-hidden="true"></i>
                    Keep Git Guard
                </button>
            </div>
        `);

        document.getElementById('modal-container')
            ?.querySelector('[data-action="confirm-uninstall-hooks"]')
            ?.addEventListener('click', async () => {
                this.app.closeModal();
                await this.uninstallHooks();
            }, { once: true });
    }

    renderHookStatus(status, isError = false) {
        const viewModel = normalizeHookStatus(status, { isError });
        this.hookStatus = viewModel;

        const health = this.query('[data-hook-health]');
        const badge = this.query('[data-hook-status-badge]');
        const installButton = this.query('[data-action="install-hooks"]');
        const uninstallButton = this.query('[data-action="uninstall-hooks"]');
        const toggleButton = this.query('[data-action="toggle-hooks"]');
        const previewButton = this.query('[data-action="run-hook-preview"]');
        const analyzerButton = this.query('[data-action="run-code-analyzer"]');

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
        const hookDetails = this.query('[data-hook-details]');
        if (hookDetails) {
            hookDetails.hidden = !(viewModel.repositoryPath || viewModel.hooksPath || viewModel.autoInstall);
        }

        if (installButton) {
            installButton.dataset.idleLabel = viewModel.installLabel;
            if (installButton.getAttribute('aria-busy') !== 'true') {
                this.setButtonLabel(installButton, viewModel.installLabel);
            }
            this.setButtonDisabled(installButton, viewModel.installDisabled);
        }
        this.setButtonDisabled(uninstallButton, viewModel.uninstallDisabled);
        if (toggleButton) {
            const isOn = viewModel.isInstalled && !viewModel.needsRepair;
            const toggleLabel = isOn
                ? 'Turn Git Guard off'
                : viewModel.needsRepair ? 'Repair Git Guard' : 'Turn Git Guard on';
            toggleButton.setAttribute('aria-checked', String(isOn));
            toggleButton.dataset.tone = viewModel.tone;
            toggleButton.dataset.idleLabel = toggleLabel;
            if (toggleButton.getAttribute('aria-busy') !== 'true') {
                this.setButtonLabel(toggleButton, toggleLabel);
            }
            this.setButtonDisabled(toggleButton, !viewModel.inGitRepo || isError);
        }
        this.setButtonDisabled(previewButton, !viewModel.inGitRepo || isError);
        this.setButtonDisabled(analyzerButton, !viewModel.inGitRepo || isError);
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
        this.viewRoot?.querySelectorAll('[data-action="toggle-hooks"], [data-action="install-hooks"], [data-action="uninstall-hooks"], [data-action="run-hook-preview"], [data-action="run-code-analyzer"]')
            .forEach(button => {
                this.setButtonDisabled(button, disabled);
            });
    }

    async runHookPreview() {
        const button = this.query('[data-action="run-hook-preview"]');
        this.setButtonBusy(button, true, 'Refreshing…');
        this.setHookMutationButtonsDisabled(true);
        this.setConsoleUtilityButtonsDisabled(true);
        this.vcaConsole?.begin();
        this.renderVcaExplanationLoading();
        try {
            const response = await this.app.apiCall('/api/v1/hooks/preview', 'POST', null, { showLoading: false });
            this.vcaConsole?.complete(response);
            this.renderVcaExplanation(response);
        } catch (error) {
            this.vcaConsole?.fail(error);
            this.renderVcaExplanation({ success: false, status: 'error' });
        } finally {
            this.setButtonBusy(button, false);
            this.setHookMutationButtonsDisabled(false);
            this.setConsoleUtilityButtonsDisabled(false);
        }
    }

    async runCodeAnalyzer() {
        const button = this.query('[data-action="run-code-analyzer"]');
        this.setButtonBusy(button, true, 'Refreshing…');
        this.setCodeAnalyzerUtilityButtonsDisabled(true);
        this.codeAnalyzerConsole?.begin('code metrics');

        try {
            const fullScan = this.query('[data-code-analyzer-full-scan]')?.checked === true;
            const response = await this.app.apiCall(
                `/api/v1/code-analyzer${fullScan ? '?fullScan=true' : ''}`,
                'POST', null, { showLoading: false });
            this.codeAnalyzerConsole?.complete(response);
            this.renderCodeAnalyzerSummary(response);
        } catch (error) {
            this.codeAnalyzerConsole?.fail(error);
            this.renderCodeAnalyzerSummary(null);
        } finally {
            this.setButtonBusy(button, false);
            this.setCodeAnalyzerUtilityButtonsDisabled(false);
        }
    }

    renderCodeAnalyzerSummary(response) {
        const empty = this.query('[data-code-analyzer-empty]');
        const reportContainer = this.query('[data-code-analyzer-report]');
        if (!response) {
            disposeCodeAnalyzerDashboard(reportContainer);
            if (reportContainer) reportContainer.hidden = true;
            if (empty) empty.hidden = false;
            return;
        }

        if (reportContainer) {
            const fileCount = renderCodeAnalyzerDashboard(reportContainer, response);
            reportContainer.hidden = fileCount === 0;
            if (empty) {
                empty.hidden = fileCount > 0;
                const title = empty.querySelector('strong');
                const message = empty.querySelector('p');
                if (title) title.textContent = fileCount > 0 ? '' : 'No changed source files to analyze';
                if (message) {
                    message.textContent = fileCount > 0
                        ? ''
                        : 'Change a supported source file, then run the scan again.';
                }
            }
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
        if (this.vcaConsole?.clear()) {
            const explanation = this.query('[data-vca-explanation]');
            if (explanation) explanation.hidden = true;
            this.lastVcaFixBrief = '';
        }
    }

    renderVcaExplanationLoading() {
        const explanation = this.query('[data-vca-explanation]');
        const verdict = this.query('[data-vca-explanation-verdict]');
        if (!explanation) return;
        explanation.hidden = false;
        if (verdict) verdict.dataset.tone = 'neutral';
        this.setText('[data-vca-explanation-title]', 'Checking staged changes…');
        this.setText('[data-vca-explanation-message]', 'Matching each staged file against the applicable rules.');
        this.setVcaExplanationIcon('fa-spinner fa-spin');
        this.query('[data-vca-explanation-stats]')?.replaceChildren();
        this.query('[data-vca-finding-list]')?.replaceChildren();
        const actions = this.query('[data-vca-explanation-actions]');
        if (actions) actions.hidden = true;
    }

    renderVcaExplanation(response) {
        const explanation = this.query('[data-vca-explanation]');
        if (!explanation) return null;
        const model = buildVcaExplanationViewModel(response);
        this.lastVcaFixBrief = model.fixBrief;
        explanation.hidden = false;

        const verdict = this.query('[data-vca-explanation-verdict]');
        if (verdict) verdict.dataset.tone = model.tone;
        this.setVcaExplanationIcon(model.icon);
        this.setText('[data-vca-explanation-title]', model.title);
        this.setText('[data-vca-explanation-message]', model.message);

        const stats = this.query('[data-vca-explanation-stats]');
        if (stats) {
            stats.replaceChildren(...model.stats.map(stat => {
                const item = document.createElement('span');
                item.className = 'vca-explanation-stat';
                const value = document.createElement('strong');
                value.textContent = String(stat.value);
                const label = document.createElement('span');
                label.textContent = stat.label;
                item.append(value, label);
                return item;
            }));
        }

        const list = this.query('[data-vca-finding-list]');
        if (list) {
            list.replaceChildren(...model.findings.map(finding => this.createVcaFindingElement(finding)));
        }

        const actions = this.query('[data-vca-explanation-actions]');
        if (actions) actions.hidden = !model.actionable;
        const terminalButton = this.query('[data-action="open-fix-terminal"]');
        if (terminalButton) terminalButton.hidden = !this.findRulesTerminal();
        return model;
    }

    createVcaFindingElement(finding) {
        const article = document.createElement('article');
        article.className = 'vca-finding';
        article.dataset.tone = finding.tone;

        const badge = document.createElement('span');
        badge.className = 'vca-finding-badge';
        badge.textContent = finding.label;

        const content = document.createElement('div');
        const title = document.createElement('h4');
        title.textContent = finding.rule;
        content.appendChild(title);
        if (finding.sourcePath) {
            const source = document.createElement('span');
            source.className = 'vca-finding-source';
            source.textContent = `Rule defined in ${finding.sourcePath}`;
            content.appendChild(source);
        }

        const details = document.createElement('dl');
        details.className = 'vca-finding-detail';
        [['Why it failed', finding.reason], ['What to do next', finding.guidance]].forEach(([label, value]) => {
            const term = document.createElement('dt');
            term.textContent = label;
            const description = document.createElement('dd');
            description.textContent = value;
            details.append(term, description);
        });
        content.appendChild(details);

        if (finding.acknowledgment) {
            const token = document.createElement('code');
            token.className = 'vca-finding-token';
            token.textContent = `${finding.acknowledgment} Reason: <explain why this is acceptable>`;
            content.appendChild(token);
        }

        article.append(badge, content);
        return article;
    }

    setVcaExplanationIcon(iconClass) {
        const icon = this.query('[data-vca-explanation-verdict] .vca-explanation-icon i');
        if (icon) icon.className = `fa-solid ${iconClass}`;
    }

    findRulesTerminal() {
        // The terminal station lives inside the Rules view itself; the dashboard-level
        // lookup remains as a fallback for any host that still mounts it as a sibling.
        return this.viewRoot?.querySelector('[data-terminal-section]')
            || this.viewRoot?.closest('[data-dashboard]')?.querySelector('[data-terminal-section]')
            || null;
    }

    openFixTerminal() {
        const terminal = this.findRulesTerminal();
        if (!terminal) return;
        terminal.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    async copyVcaFixBrief() {
        const copied = await copyVcaConsoleText(this.lastVcaFixBrief);
        this.app.showToast(
            copied ? 'Fix Brief Copied' : 'Copy Unavailable',
            copied ? 'Paste the brief into the agent terminal below.' : 'Select the findings and copy them manually.',
            copied ? 'success' : 'warning');
    }

    async copyCodeAnalyzerOutput() {
        const copied = await this.codeAnalyzerConsole?.copy();
        this.app.showToast(
            copied ? 'Results Copied' : 'Copy Unavailable',
            copied ? 'Scan summary copied to the clipboard.' : 'Select the result text and copy it manually.',
            copied ? 'success' : 'warning');
    }

    clearCodeAnalyzerOutput() {
        if (this.codeAnalyzerConsole?.clear()) {
            const report = this.query('[data-code-analyzer-report]');
            disposeCodeAnalyzerDashboard(report);
            if (report) report.hidden = true;
            const empty = this.query('[data-code-analyzer-empty]');
            if (empty) empty.hidden = false;
        }
    }

    setConsoleUtilityButtonsDisabled(disabled) {
        this.viewRoot?.querySelectorAll('[data-action="copy-hook-output"], [data-action="clear-hook-output"]')
            .forEach(button => {
                button.disabled = Boolean(disabled);
            });
    }

    setCodeAnalyzerUtilityButtonsDisabled(disabled) {
        this.viewRoot?.querySelectorAll('[data-action="copy-code-analyzer-output"], [data-action="clear-code-analyzer-output"]')
            .forEach(button => {
                button.disabled = Boolean(disabled);
            });
    }

    setHookMutationButtonsDisabled(disabled) {
        const installButton = this.query('[data-action="install-hooks"]');
        const uninstallButton = this.query('[data-action="uninstall-hooks"]');
        const toggleButton = this.query('[data-action="toggle-hooks"]');
        this.setButtonDisabled(
            installButton,
            disabled || !this.hookStatus || this.hookStatus.installDisabled);
        this.setButtonDisabled(
            uninstallButton,
            disabled || !this.hookStatus || this.hookStatus.uninstallDisabled);
        this.setButtonDisabled(
            toggleButton,
            disabled || !this.hookStatus || !this.hookStatus.inGitRepo);
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

}

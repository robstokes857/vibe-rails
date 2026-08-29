import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const consoleModule = path.resolve('VibeRails/wwwroot/js/modules/vca-console.js');
const controllerModule = path.resolve('VibeRails/wwwroot/js/modules/rule-controller.js');

const {
    VcaConsole,
    buildVcaPreviewViewModel,
    copyVcaConsoleText,
    formatVcaDuration,
    normalizeVcaConsoleText
} = await import(pathToFileURL(consoleModule).href);
const {
    RuleController,
    buildCodeAnalyzerSummary,
    buildProjectHealthFixPrompt,
    buildVcaExplanationViewModel,
    normalizeHookStatus
} = await import(pathToFileURL(controllerModule).href);

test('detailed current hook status produces a protected health model', () => {
    const model = normalizeHookStatus({
        inGitRepo: true,
        isInstalled: true,
        needsRepair: false,
        state: 'Healthy',
        repositoryPath: 'C:\\source\\project',
        hooksPath: 'C:\\source\\project\\.git\\hooks',
        autoInstallEnabled: true,
        preCommit: {
            state: 'Current',
            installed: true,
            current: true,
            message: 'Pre-commit is current.'
        },
        commitMessage: {
            state: 'Current',
            installed: true,
            current: true,
            message: 'Commit message is current.'
        },
        postCommit: {
            state: 'Current',
            installed: true,
            current: true,
            message: 'Post-commit is current.'
        }
    });

    assert.equal(model.badge, 'Protected');
    assert.equal(model.tone, 'success');
    assert.equal(model.installDisabled, true);
    assert.equal(model.uninstallDisabled, false);
    assert.equal(model.showRepairRemoval, false);
    assert.equal(model.preCommit.label, 'Current');
    assert.equal(model.commitMessage.label, 'Current');
    assert.equal(model.postCommit.label, 'Current');
    assert.equal(model.autoInstall, 'Auto-install enabled');
});

test('partial or stale hooks expose Repair Hooks instead of claiming protection', () => {
    const model = normalizeHookStatus({
        inGitRepo: true,
        isInstalled: false,
        needsRepair: true,
        state: 'NeedsRepair',
        preCommit: {
            state: 'Outdated',
            installed: true,
            current: false
        },
        commitMessage: {
            state: 'Missing',
            installed: false,
            current: false
        },
        postCommit: {
            state: 'Missing',
            installed: false,
            current: false
        }
    });

    assert.equal(model.badge, 'Repair needed');
    assert.equal(model.installLabel, 'Repair Hooks');
    assert.equal(model.installDisabled, false);
    assert.equal(model.uninstallDisabled, false);
    assert.equal(model.showRepairRemoval, true);
    assert.equal(model.preCommit.label, 'Needs repair');
    assert.equal(model.commitMessage.label, 'Not installed');
    assert.equal(model.postCommit.label, 'Not installed');
});

test('original minimal status payload remains useful without inventing detailed health', () => {
    const model = normalizeHookStatus({
        inGitRepo: true,
        isInstalled: true,
        message: null
    });

    assert.equal(model.badge, 'Protected');
    assert.equal(model.preCommit.label, 'Installed');
    assert.equal(model.postCommit.label, 'Installed');
    assert.match(model.preCommit.message, /not reported/i);
    assert.equal(model.repositoryPath, '');
    assert.equal(model.autoInstall, null);
});

test('no-repository state disables all hook mutations', () => {
    const model = normalizeHookStatus({
        inGitRepo: false,
        isInstalled: false,
        message: 'Not in a git repository'
    });

    assert.equal(model.badge, 'No repository');
    assert.equal(model.installDisabled, true);
    assert.equal(model.uninstallDisabled, true);
    assert.equal(model.showRepairRemoval, false);
    assert.equal(model.preCommit.label, 'Unavailable');
});

test('Project health renders zero-valued rule inventory counts', () => {
    const nodes = new Map([
        ['[data-rule-file-count]', { textContent: 'stale' }],
        ['[data-rule-count]', { textContent: 'stale' }],
        ['[data-stop-rule-count]', { textContent: 'stale' }]
    ]);
    const controller = new RuleController({ data: { agents: [] } });
    controller.viewRoot = { querySelector: selector => nodes.get(selector) || null };

    controller.renderRuleInventorySummary();

    assert.equal(nodes.get('[data-rule-file-count]').textContent, 0);
    assert.equal(nodes.get('[data-rule-count]').textContent, 0);
    assert.equal(nodes.get('[data-stop-rule-count]').textContent, 0);
});

test('Project health includes a direct remove action for a broken Git Guard', async () => {
    const html = await readFile('VibeRails/wwwroot/index.html', 'utf8');
    const settingStart = html.indexOf('<section class="project-health-guard"');
    const settingEnd = html.indexOf('</section>', settingStart);
    const setting = html.slice(settingStart, settingEnd);

    assert.ok(settingStart >= 0);
    assert.ok(settingEnd > settingStart);
    assert.match(setting, /data-action="uninstall-hooks"/);
    assert.match(setting, /data-repair-only/);
    assert.match(setting, />Remove</);
});

test('Rules overview automatically starts validation and analysis together', async () => {
    const controller = new RuleController({});
    const root = {};
    const calls = [];
    controller.viewRoot = root;
    controller.refreshHookStatus = async () => {
        controller.hookStatus = { inGitRepo: true };
    };
    controller.runHookPreview = async () => calls.push('validation');
    controller.runCodeAnalyzer = async () => calls.push('analysis');

    assert.equal(await controller.runRulesOverviewChecks(root), true);
    assert.deepEqual(calls.sort(), ['analysis', 'validation']);
});

test('Rules overview restores its MintLint result instead of rescanning on return', async () => {
    const controller = new RuleController({});
    const root = {};
    const calls = [];
    const cachedResponse = { healthScore: 90 };
    controller.viewRoot = root;
    controller.codeAnalyzerCache = {
        repositoryPath: 'C:\\source\\project',
        response: cachedResponse,
        ignoredFiles: [],
        unpushed: false
    };
    controller.refreshHookStatus = async () => {
        controller.hookStatus = { inGitRepo: true, repositoryPath: 'C:\\source\\project' };
    };
    controller.runHookPreview = async () => calls.push('validation');
    controller.runCodeAnalyzer = async () => calls.push('analysis');
    controller.renderCodeAnalyzerSummary = response => calls.push(response === cachedResponse ? 'restored' : 'wrong-response');

    assert.equal(await controller.runRulesOverviewChecks(root), true);
    assert.deepEqual(calls.sort(), ['restored', 'validation']);
});

test('focused Git Guard autoruns again when the view is reopened', async () => {
    const root = { dataset: {} };
    const fragment = { querySelector: () => root };
    const content = {
        innerHTML: '',
        appendChild() { }
    };
    const originalDocument = globalThis.document;
    globalThis.document = { getElementById: () => content };

    try {
        const controller = new RuleController({
            bindAction() { },
            cloneTemplate: () => fragment
        });
        let runs = 0;
        controller.bindHookControls = () => { };
        controller.renderPreflightState = () => { };
        controller.refreshHookStatus = () => { };
        controller.runGitPreflight = () => { runs += 1; };

        controller.loadFocusedGitGuard();
        await Promise.resolve();
        controller.loadCheckViolations();
        controller.loadFocusedGitGuard();
        await Promise.resolve();

        assert.equal(runs, 2);
    } finally {
        globalThis.document = originalDocument;
    }
});

test('turning off an installed Git Guard requires confirmation', async () => {
    const controller = new RuleController({});
    let confirmationCount = 0;
    let uninstallCount = 0;
    controller.hookStatus = { inGitRepo: true, isInstalled: true, needsRepair: false };
    controller.confirmUninstallHooks = () => { confirmationCount += 1; };
    controller.uninstallHooks = async () => { uninstallCount += 1; };

    await controller.toggleHooks();
    assert.equal(confirmationCount, 1);
    assert.equal(uninstallCount, 0);
});

test('code analyzer converts concern output into a bounded health summary', () => {
    assert.deepEqual(buildCodeAnalyzerSummary({
        healthScore: 82.5,
        rating: 'NeedsWork',
        analyzedFileCount: 4,
        skippedFileCount: 2
    }), {
        score: 82.5,
        scoreLabel: '82.5',
        rating: 'Needs Work',
        analyzedFileCount: 4,
        skippedFileCount: 2,
        tone: 'success'
    });

    const empty = buildCodeAnalyzerSummary({ analyzedFileCount: 0, skippedFileCount: 3 });
    assert.equal(empty.score, null);
    assert.equal(empty.scoreLabel, '—');
    assert.equal(empty.rating, 'No staged code');
    assert.equal(empty.tone, 'neutral');

    // An explicit null is what the backend actually sends when nothing was analyzed;
    // Number(null) is 0, so this must not read as a zero score.
    const nulled = buildCodeAnalyzerSummary({
        healthScore: null,
        rating: null,
        analyzedFileCount: 0,
        skippedFileCount: 3
    });
    assert.equal(nulled.score, null);
    assert.equal(nulled.scoreLabel, '—');
    assert.equal(nulled.tone, 'neutral');
});

test('VCA STOP findings explain why the commit is blocked and how to fix it', () => {
    const model = buildVcaExplanationViewModel({
        success: false,
        validation: {
            outcome: 'blocked',
            stagedFileCount: 2,
            applicableRuleCount: 3,
            stopCount: 1,
            findings: [{
                status: 'blocked',
                enforcement: 'STOP',
                rule: 'Tests must accompany behavior changes',
                reason: 'src/widget.cs changed without a matching test file.',
                sourcePath: 'vc.rules.md',
                guidance: 'Add or update the relevant test, then stage it.'
            }]
        }
    });

    assert.equal(model.tone, 'danger');
    assert.match(model.title, /commit blocked/i);
    assert.match(model.message, /cannot be bypassed/i);
    assert.equal(model.findings[0].label, 'STOP');
    assert.match(model.findings[0].reason, /without a matching test/i);
    assert.match(model.fixBrief, /what to do next: add or update/i);
    assert.deepEqual(model.stats.map(stat => stat.value), [2, 3, 1]);
});

test('VCA action queue puts blockers before acknowledgments and warnings', () => {
    const model = buildVcaExplanationViewModel({
        success: false,
        validation: {
            outcome: 'blocked',
            findings: [
                { status: 'warning', enforcement: 'WARN', rule: 'Warning' },
                { status: 'acknowledgment_required', enforcement: 'COMMIT', rule: 'Acknowledge' },
                { status: 'blocked', enforcement: 'STOP', rule: 'Blocker' }
            ]
        }
    });

    assert.deepEqual(model.findings.map(finding => finding.rule), ['Blocker', 'Acknowledge', 'Warning']);
});

test('Fix rules opens a fresh managed terminal with an auto-submitted VCA brief', () => {
    const launches = [];
    const controller = new RuleController({
        terminalController: {
            launchInFocus(options) { launches.push(options); }
        },
        data: { configs: { rootPath: 'C:\\source\\project' } }
    });
    controller.lastVcaFixBrief = 'VCA VALIDATION: Commit blocked\n1. [STOP] Add tests';

    controller.openFixTerminal();

    assert.equal(launches.length, 1);
    assert.equal(launches[0].cli, 'claude');
    assert.equal(launches[0].forceNewTab, true);
    assert.equal(launches[0].workingDirectory, 'C:\\source\\project');
    assert.match(launches[0].initialPrompt, /RULES/);
    assert.match(launches[0].initialPrompt, /Add tests/);
    assert.match(launches[0].initialPrompt, /Do not weaken, delete, or bypass a rule/i);
});

test('Project health uses the first enabled managed LLM from the shared picker order', () => {
    const launches = [];
    const controller = new RuleController({
        llmPickerController: {
            getEnabledItems(context) {
                assert.equal(context, 'multi-run');
                return [{ cli: 'codex', key: 'base:codex' }, { cli: 'claude', key: 'base:claude' }];
            }
        },
        terminalController: { launchInFocus(options) { launches.push(options); } },
        data: { configs: { rootPath: 'C:\\source\\project' } },
        showToast() {}
    });

    assert.equal(controller.launchProjectHealthFix('quality'), true);
    assert.equal(launches[0].cli, 'codex');
});

test('Project health does not launch an agent outside a Git repository', () => {
    const launches = [];
    const toasts = [];
    const controller = new RuleController({
        terminalController: { launchInFocus(options) { launches.push(options); } },
        data: { configs: {} },
        showToast(...args) { toasts.push(args); }
    });
    controller.hookStatus = { inGitRepo: false };

    assert.equal(controller.launchProjectHealthFix('all'), false);
    assert.equal(launches.length, 0);
    assert.match(toasts[0][1], /Open a local Git repository/i);
});

test('project-health prompts stay useful when one or both scans have no cached result', () => {
    const quality = buildProjectHealthFixPrompt('quality');
    assert.match(quality, /CODE QUALITY/);
    assert.match(quality, /No saved Code quality scan is available/);
    assert.doesNotMatch(quality, /\nRULES\n/);

    const both = buildProjectHealthFixPrompt('all', {
        vcaFixBrief: 'VCA VALIDATION: 1 STOP finding'
    });
    assert.match(both, /\nRULES\n/);
    assert.match(both, /VCA VALIDATION: 1 STOP finding/);
    assert.match(both, /\nCODE QUALITY\n/);
    assert.match(both, /validate_vca checks the staged snapshot/);

    const withScan = buildProjectHealthFixPrompt('quality', {
        codeAnalyzerResponse: {
            success: true,
            healthScore: 64,
            rating: 'NeedsWork',
            analyzedFileCount: 1,
            output: 'Top issue: cyclomatic complexity in src/Widget.cs',
            report: { score: 36, rating: 'NeedsWork', files: [], overview: [], scorecard: [] }
        }
    });
    assert.match(withScan, /grade D, health 64\/100/);
    assert.match(withScan, /Top issue: cyclomatic complexity in src\/Widget\.cs/);
});

test('VCA COMMIT and WARN findings clearly distinguish acknowledgement from advice', () => {
    const model = buildVcaExplanationViewModel({
        success: true,
        validation: {
            outcome: 'attention',
            commitCount: 1,
            warningCount: 1,
            findings: [{
                status: 'acknowledgment_required',
                rule: 'Document public API changes',
                reason: 'The public contract changed without documentation.',
                guidance: 'Update the API documentation or acknowledge the exception.',
                acknowledgment: '[VCA-ACK:docs]'
            }, {
                status: 'warning',
                rule: 'Keep methods focused',
                reason: 'One method is longer than the preferred limit.',
                guidance: 'Consider extracting a helper.'
            }]
        }
    });

    assert.equal(model.tone, 'warning');
    assert.match(model.title, /acknowledgment required/i);
    assert.match(model.message, /will not block/i);
    assert.equal(model.findings[0].label, 'COMMIT');
    assert.equal(model.findings[1].label, 'WARN');
    assert.match(model.fixBrief, /\[VCA-ACK:docs\] Reason:/);
});

test('VCA pass and empty states are calm, explicit results', () => {
    const passed = buildVcaExplanationViewModel({
        success: true,
        validation: { outcome: 'passed', stagedFileCount: 4, applicableRuleCount: 7, findings: [] }
    });
    const empty = buildVcaExplanationViewModel({
        success: true,
        validation: { outcome: 'empty', stagedFileCount: 0, findings: [] }
    });

    assert.equal(passed.tone, 'success');
    assert.match(passed.title, /satisfy your VCA rules/i);
    assert.equal(passed.actionable, false);
    assert.equal(empty.tone, 'info');
    assert.match(empty.title, /no uncommitted changes/i);
    assert.equal(empty.actionable, false);
});

test('VCA explanatory data remains plain text even when a rule contains markup', () => {
    const model = buildVcaExplanationViewModel({
        validation: {
            outcome: 'blocked',
            findings: [{
                status: 'blocked',
                rule: '<img src=x onerror=alert(1)>',
                reason: '<script>unsafe()</script>',
                guidance: 'Use textContent only.'
            }]
        }
    });

    assert.equal(model.findings[0].rule, '<img src=x onerror=alert(1)>');
    assert.match(model.fixBrief, /<script>unsafe\(\)<\/script>/);
});

test('console helpers normalize output and summarize preview completion', () => {
    assert.equal(normalizeVcaConsoleText('one\r\ntwo\rthree\0'), 'one\ntwo\nthree');
    assert.equal(formatVcaDuration(42.4), '42 ms');
    assert.equal(formatVcaDuration(1250), '1.3 s');

    const result = buildVcaPreviewViewModel({
        success: true,
        exitCode: 0,
        status: 'Passed',
        title: 'VCA preview',
        output: 'PASS: safe <markup>\r\n',
        durationMs: 1250
    });

    assert.equal(result.tone, 'success');
    assert.equal(result.output, 'PASS: safe <markup>');
    assert.equal(result.meta, 'Finished in 1.3 s · Exit code 0');
});

test('non-zero preview exits are rendered as failures even if status text is absent', () => {
    const result = buildVcaPreviewViewModel({
        success: false,
        exitCode: 3,
        output: '[block] commit blocked'
    });

    assert.equal(result.tone, 'danger');
    assert.equal(result.status, 'Failed');
});

test('commit-level warnings stay visibly distinct from a clean pass', () => {
    const result = buildVcaPreviewViewModel({
        success: true,
        exitCode: 0,
        status: 'warning',
        output: '[warn] Commit acknowledgments are required.'
    });

    assert.equal(result.tone, 'warning');
    assert.equal(result.status, 'warning');
});

function fakeElement(textContent = '') {
    return {
        textContent,
        dataset: {},
        attributes: new Map(),
        classList: {
            values: new Set(),
            toggle(name, force) {
                if (force) this.values.add(name);
                else this.values.delete(name);
            }
        },
        setAttribute(name, value) {
            this.attributes.set(name, String(value));
        },
        getAttribute(name) {
            return this.attributes.get(name) ?? null;
        },
        scrollTop: 0,
        scrollHeight: 120
    };
}

test('VcaConsole writes untrusted hook output as text and exposes busy state', () => {
    const output = fakeElement('');
    const state = fakeElement('');
    const meta = fakeElement('');
    const spinner = { hidden: true };
    const root = fakeElement('');
    root.querySelector = (selector) => ({
        '[data-vca-console-output]': output,
        '[data-vca-console-state]': state,
        '[data-vca-console-meta]': meta,
        '[data-vca-console-spinner]': spinner
    })[selector] || null;

    const hookConsole = new VcaConsole(root);
    hookConsole.begin();
    assert.equal(root.attributes.get('aria-busy'), 'true');
    assert.equal(spinner.hidden, false);
    assert.match(output.textContent, /Starting VCA hook check/);
    assert.equal(hookConsole.clear(), false, 'clear must not contradict an active busy state');
    assert.match(output.textContent, /Starting VCA hook check/);

    hookConsole.complete({
        success: true,
        exitCode: 0,
        status: 'Passed',
        output: '<img src=x onerror=alert(1)>'
    });

    assert.equal(output.textContent, '<img src=x onerror=alert(1)>');
    assert.equal(root.attributes.get('aria-busy'), 'false');
    assert.equal(spinner.hidden, true);
    assert.equal(root.dataset.tone, 'success');
    assert.equal(state.textContent, 'Passed');

    hookConsole.report('[error] setup failed', {
        tone: 'danger',
        state: 'Setup failed',
        meta: 'Hook files were not changed'
    });
    assert.equal(root.dataset.tone, 'danger');
    assert.equal(state.textContent, 'Setup failed');
    assert.equal(meta.textContent, 'Hook files were not changed');
    assert.match(output.textContent, /\[error\] setup failed$/);

    assert.equal(hookConsole.clear(), true);
    assert.equal(root.dataset.tone, 'neutral');
    assert.equal(state.textContent, 'Ready');
});

test('copy helper uses an injected clipboard and preserves plain text', async () => {
    const writes = [];
    const copied = await copyVcaConsoleText('line 1\r\nline 2', {
        clipboard: { writeText: async (value) => writes.push(value) }
    });

    assert.equal(copied, true);
    assert.deepEqual(writes, ['line 1\nline 2']);
});

test('button progress preserves the resting disabled state produced by a status refresh', () => {
    const label = { textContent: '' };
    const spinner = { hidden: true };
    const icon = { hidden: false };
    const attributes = new Map();
    const button = {
        dataset: { idleLabel: 'Install Hooks' },
        disabled: false,
        getAttribute: (name) => attributes.get(name) ?? null,
        setAttribute: (name, value) => attributes.set(name, String(value)),
        querySelector: (selector) => ({
            '[data-button-label]': label,
            '[data-button-spinner]': spinner,
            '[data-button-icon]': icon
        })[selector] || null
    };
    const controller = new RuleController({});

    controller.setButtonBusy(button, true, 'Installing…');
    assert.equal(button.disabled, true);
    assert.equal(attributes.get('aria-busy'), 'true');
    assert.equal(label.textContent, 'Installing…');
    assert.equal(spinner.hidden, false);
    assert.equal(icon.hidden, true);

    // A completed status request says the installed button should now rest disabled.
    controller.setButtonDisabled(button, true);
    controller.setButtonBusy(button, false);
    assert.equal(button.disabled, true);
    assert.equal(attributes.get('aria-busy'), 'false');
    assert.equal(label.textContent, 'Install Hooks');
    assert.equal(spinner.hidden, true);
    assert.equal(icon.hidden, false);
});

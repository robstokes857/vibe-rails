import { agentPurposeOptions } from './agent-purpose.js';
import { switchReviewerDefaults, routingEditorMarkup, mountRoutingEditor } from './reviewer-routing.js';
import { getEnabledLlmItems, mountLlmPicker, setLlmPickerValue } from './pickers/llm-picker.js';
import { normalizeSteps, openStepsEditor, renderStepsSummaryButton, serializeSteps, summarizeSteps } from './environment-steps.js';

/** A form-scoped lookup, also accepting Document and the lightweight unit-test documents. */
export function environmentField(root, id) {
    return root.getElementById ? root.getElementById(id) : root.querySelector(`#${id}`);
}

// EnvironmentRoutes rejects a name LlmParser resolves to a built-in CLI (any casing, and any
// number, because enum values parse too) and EnvironmentNameValidator's Windows device names.
const RESERVED_WORKER_NAMES = new Set([
    'codex', 'claude', 'antigravity', 'copilot', 'shell', 'opencode', 'glm52', 'grok46', 'glm53',
    'deepseekv4pro', 'kimik3', 'grok', 'deepseek-v4-pro', 'kimi-k3', 'con', 'prn', 'aux', 'nul',
    ...[1, 2, 3, 4, 5, 6, 7, 8, 9].flatMap(digit => [`com${digit}`, `lpt${digit}`])
]);

/** Derive an internal identifier without restricting the Automation's display name. */
export function automationWorkerName(name, environments = []) {
    let base = String(name || '').normalize('NFKD').replace(/[^A-Za-z0-9_\- ]/g, '')
        .replace(/^[^A-Za-z0-9]+/, '').trim().slice(0, 64).trim() || 'Automation';
    if (/^\d+$/.test(base) || RESERVED_WORKER_NAMES.has(base.toLowerCase())) base = `${base.slice(0, 57)} Worker`;
    const used = new Set(environments.map(environment => environment.name.toLowerCase()));
    let candidate = base;
    for (let suffix = 2; used.has(candidate.toLowerCase()); suffix++) {
        const tail = ` ${suffix}`;
        candidate = base.slice(0, 64 - tail.length).trimEnd() + tail;
    }
    return candidate;
}

/**
 * Mount the existing Environment controls without owning a modal or submit button.
 * Provider catalogs, normalization, argument builders and settings APIs stay on the controller.
 * Nothing is written until save(); dispose() only tears down UI resources.
 */
export function mountEnvironmentEditor(controller, host, {
    env = null, cliSettings = {}, initialName = '', automationWorker = false, getName = null
} = {}) {
    const app = controller.app;
    const escape = value => app.escapeHtml(value ?? '');
    const isEdit = Boolean(env);
    const workerOnly = automationWorker || Boolean(env?.automationWorker);
    const initialCli = env?.cli || getEnabledLlmItems(app, 'environment-provider')[0]?.cli || 'claude';
    const workspaceMode = env?.workspaceMode || 0;
    const initialSteps = normalizeSteps(env?.steps);
    let editedSteps = null;
    let stepsEditor = null;
    let disposed = false;
    let savedEnvironment = env;
    let pendingSave = null;
    // Remember successful writes separately: a settings/job failure must not POST a second Worker.
    // An existing Worker starts from its loaded form (set below), so a save that changed none of
    // its controls writes nothing; an Automation rename must not rebuild the Worker's arguments.
    let lastEnvironmentPayload = null;
    let lastSettingsPayload = null;
    const drafts = new Map([[initialCli, cliSettings]]);
    let currentCli = initialCli;

    host.classList.add('environment-editor');
    host.innerHTML = `
        ${!isEdit && !getName ? `<div class="mb-3"><label class="form-label" for="env-name">${workerOnly ? 'Worker Name' : 'Environment / Worker Name'}</label><input class="form-control" id="env-name" required maxlength="64" value="${escape(initialName)}"></div>` : ''}
        <div class="env-editor-presets" ${isEdit || getName ? 'hidden' : ''}><span>Start with</span><div class="env-preset-actions">
            <button type="button" class="btn btn-sm btn-outline-secondary" data-code-review-preset>Code review</button>
            <button type="button" class="btn btn-sm btn-outline-secondary" data-switch-reviewer-preset>Switch reviewer</button>
        </div></div>
        <div class="env-primary-options" data-primary-options>
            <div class="env-provider-field"><label class="form-label" for="env-cli">LLM</label>${isEdit
                ? `<input class="form-control" id="env-cli" value="${escape(env.cli)}" disabled>`
                : '<select class="form-select" id="env-cli" required></select>'}</div>
        </div>
        ${controller.renderInitialMessageField(initialCli, escape(cliSettings.initialMessage ?? cliSettings.prompt ?? ''))}
        <section class="env-secondary-options" aria-label="Run settings">
            <div class="env-secondary-content">
                <div class="mb-3 env-purpose-group"><label class="form-label" for="env-purpose">Work type <span class="text-muted">(optional)</span></label>
                    <select id="env-purpose" class="form-select">${agentPurposeOptions(env?.purpose)}</select>
                    <small class="form-text text-muted">Code review also saves a report on the originating card.</small>
                </div>
                <div data-reviewer-policy ${env?.purpose === 'code_review' ? '' : 'hidden'}>
                    <label class="form-label" for="env-reviewer-mode">Reviewer selection</label>
                    <select id="env-reviewer-mode" class="form-select mb-2"><option value="fixed">Fixed provider</option><option value="switch" ${env?.reviewerRouting?.mode === 'switch' ? 'selected' : ''}>Switch reviewer</option></select>
                    <div data-reviewer-routing ${env?.reviewerRouting?.mode === 'switch' ? '' : 'hidden'}>${routingEditorMarkup()}</div>
                </div>
                ${app.data.isInGit ? `<div class="mb-3 env-workspace-group"><label class="form-label" for="env-workspace-mode">Workspace</label><select class="form-select" id="env-workspace-mode">
                    <option value="0" ${workspaceMode === 0 ? 'selected' : ''}>Project directory</option>
                    ${!workerOnly || workspaceMode === 1 ? `<option value="1" ${workspaceMode === 1 ? 'selected' : ''}>Its own clone — reused each launch</option>` : ''}
                    <option value="2" ${workspaceMode === 2 ? 'selected' : ''}>Clean Git checkout every run</option>
                </select><small class="form-text text-muted">A fresh checkout starts from the current branch's last commit. Uncommitted and ignored files are not copied.</small></div>` : ''}
                ${!workerOnly ? `<div class="mb-3 form-check form-switch"><input class="form-check-input" type="checkbox" id="env-hidden" ${env?.hidden ? 'checked' : ''}><label class="form-check-label" for="env-hidden">Hide from launch pickers</label></div>` : ''}
                <div data-cli-settings-slot></div>
                <div class="mb-3" data-custom-args-group><label class="form-label" for="env-custom-args">Extra CLI arguments</label><input class="form-control" id="env-custom-args" value="${escape(env?.customArgs || '')}"></div>
                ${renderStepsSummaryButton(initialSteps)}
            </div>
        </section>`;
    const get = id => environmentField(host, id);
    const message = get('env-initial-message');
    message.rows = 3;
    const context = document.createElement('div');
    context.className = 'env-context-options';
    for (const selector of ['.env-purpose-group', '.env-workspace-group', '[data-reviewer-policy]']) {
        const field = host.querySelector(selector);
        if (field) context.append(field);
    }
    message.parentElement.before(context);
    const slot = host.querySelector('[data-cli-settings-slot]');
    const primary = host.querySelector('[data-primary-options]');
    const routingHost = host.querySelector('[data-reviewer-routing]');
    const routingEditor = mountRoutingEditor(app, routingHost, env?.reviewerRouting || switchReviewerDefaults());
    const cliSelect = get('env-cli');
    const renderSettings = (cli, settings) => {
        primary.querySelectorAll('[data-primary-cli-field]').forEach(field => field.remove());
        slot.innerHTML = controller.buildCliSettingsHtml(cli, settings);
        // Move the actual controls (including their values), never copies of their catalogs/logic.
        for (const selector of ['[id$="-model"]', '[id$="-effort"]', '[id$="-yolo"], #claude-dangerously-skip-permissions, #copilot-permission-preset']) {
            const input = slot.querySelector(selector);
            const group = input?.closest('.mb-3');
            if (!group) continue;
            group.dataset.primaryCliField = '';
            if (input.type === 'checkbox') group.classList.add('env-primary-permissions');
            const label = group.querySelector('label');
            if (label) label.htmlFor = input.id;
            const help = group.querySelector('small');
            if (help) {
                input.title = help.textContent.trim();
                help.textContent = input.id.endsWith('-model') ? 'Uses this model for each run.'
                    : input.id.endsWith('-effort') ? 'How much reasoning to use.' : 'YOLO skips permission prompts.';
            }
            primary.append(group);
        }
        slot.querySelectorAll(':scope > hr, :scope > h6').forEach(element => element.remove());
        controller.bindCliSettingsInteractions(cli, host);
        host.querySelector('[data-custom-args-group]').hidden = controller.usesManagedCustomArgs(cli);
        get('env-initial-message').placeholder = getName ? 'Describe what should happen each time this runs.' : controller.initialMessagePlaceholder(cli);
        host.querySelector('[data-initial-message-cli]').textContent = controller.cliDisplayName(cli);
    };
    const pickerDisposer = isEdit ? null : mountLlmPicker(app, cliSelect, {
        context: 'environment-provider', placeholder: null, selectedValue: initialCli, includeGroups: false
    });
    renderSettings(initialCli, cliSettings);
    cliSelect.addEventListener('change', () => {
        if (savedEnvironment) return;
        drafts.set(currentCli, controller.extractCliSettingsPayload(currentCli, host));
        currentCli = cliSelect.value;
        renderSettings(currentCli, drafts.get(currentCli) || {});
    });
    const reviewerMode = get('env-reviewer-mode');
    reviewerMode.addEventListener('change', () => { routingHost.hidden = reviewerMode.value !== 'switch'; });
    get('env-purpose').addEventListener('change', event => {
        host.querySelector('[data-reviewer-policy]').hidden = event.target.value !== 'code_review';
        if (!savedEnvironment && event.target.value === 'code_review') {
            if (!get('env-initial-message').value.trim()) get('env-initial-message').value = 'Review the intended changes for correctness, regressions and missing validation. Establish scope from the card handoff and actual checkout. Save the review on the originating card, then follow its workflow instructions.';
        }
    });
    for (const [selector, mode, name] of [['[data-code-review-preset]', 'fixed', 'Code review'], ['[data-switch-reviewer-preset]', 'switch', 'Switch reviewer']]) {
        host.querySelector(selector).addEventListener('click', event => {
            if (!savedEnvironment) {
                setLlmPickerValue(app, cliSelect, 'codex');
                cliSelect.dispatchEvent(new Event('change', { bubbles: true }));
            }
            get('env-purpose').value = 'code_review';
            get('env-purpose').dispatchEvent(new Event('change', { bubbles: true }));
            reviewerMode.value = mode;
            reviewerMode.dispatchEvent(new Event('change'));
            if (get('env-name') && !get('env-name').value.trim()) get('env-name').value = name;
        });
    }
    const refreshRefs = controller.bindInitialMessageField(() => editedSteps ?? initialSteps, host);
    host.querySelector('[data-env-steps-open]').addEventListener('click', event => {
        stepsEditor = openStepsEditor(app, {
            triggerElement: event.currentTarget,
            steps: editedSteps ?? initialSteps,
            workingDirectory: env?.workspacePath || null,
            onSave: steps => {
                editedSteps = steps;
                stepsEditor = null;
                host.querySelector('[data-env-steps-summary]').textContent = summarizeSteps(steps);
                refreshRefs();
            }
        });
    });

    const read = () => {
        const cli = savedEnvironment?.cli || cliSelect.value;
        const settings = controller.extractCliSettingsPayload(cli, host);
        const purpose = get('env-purpose').value;
        const payload = {
            ...controller.buildEnvironmentSavePayload(cli, settings, host), purpose,
            reviewerRouting: purpose === 'code_review' && reviewerMode.value === 'switch'
                ? routingEditor.read() : { ...switchReviewerDefaults(), mode: 'fixed' },
            ...(get('env-hidden') ? { hidden: get('env-hidden').checked } : {}),
            ...(get('env-workspace-mode') ? { workspaceMode: Number(get('env-workspace-mode').value) } : {}),
            ...(editedSteps !== null ? { steps: serializeSteps(editedSteps) } : {})
        };
        return { cli, settings, payload, name: savedEnvironment?.name || getName?.() || get('env-name')?.value.trim() || '' };
    };
    if (isEdit) {
        const loaded = read();
        lastEnvironmentPayload = JSON.stringify(loaded.payload);
        lastSettingsPayload = JSON.stringify(loaded.settings);
    }
    const save = async () => {
        const draft = read();
        const payloadKey = JSON.stringify(draft.payload);
        if (!savedEnvironment) {
            savedEnvironment = await app.apiCall('/api/v1/environments', 'POST', {
                name: draft.name, cli: draft.cli, ...draft.payload,
                ...(workerOnly ? { automationWorker: true } : {})
            });
            // The POST returns the new record. Keep it before the settings write can fail.
            lastEnvironmentPayload = payloadKey;
            cliSelect.disabled = true;
            cliSelect.tomselect?.disable();
            if (get('env-name')) get('env-name').disabled = true;
        } else if (lastEnvironmentPayload !== payloadKey) {
            savedEnvironment = await app.apiCall(`/api/v1/environments/${encodeURIComponent(savedEnvironment.name)}`, 'PUT', draft.payload);
            lastEnvironmentPayload = payloadKey;
        }
        const settingsKey = JSON.stringify(draft.settings);
        if (lastSettingsPayload !== settingsKey) {
            await controller.saveCliSettings(draft.cli, savedEnvironment.name, draft.settings);
            lastSettingsPayload = settingsKey;
        }
        return savedEnvironment;
    };
    return {
        element: host, read,
        save() {
            if (disposed) return Promise.reject(new Error('The editor was closed.'));
            if (!pendingSave) pendingSave = save().finally(() => { pendingSave = null; });
            return pendingSave;
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            pickerDisposer?.();
            routingEditor.dispose();
            stepsEditor?.close({ restoreFocus: false });
            host.querySelector('.env-step-output-menu')?._vbCloseMenu?.();
        }
    };
}

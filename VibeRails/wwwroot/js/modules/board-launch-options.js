import { escapeHtml } from './utils.js';
import { codexModelSupportsSpeed, codexModelSupportsEffort, normalizeCodexModelEffort, normalizeLlmModel, renderLlmModelOptions } from './llm-model-catalog.js';

const pinnedModels = Object.freeze({
    'glm-5.2': 'zai/glm-5.2', 'glm-5.3': 'zai-coding-plan/glm-5.3',
    'deepseek-v4-pro': 'deepseek/deepseek-v4-pro', 'kimi-k3': 'moonshotai/kimi-k3'
});
const openCodeClis = ['opencode', 'glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3'];
const pendingDefaults = new WeakMap();

/** Wait for the current editors, including a selection changed during an earlier request. */
export function waitForBoardLaunchDefaults(root) {
    const pending = [...(root?.querySelectorAll?.('[data-board-launch-options]') || [])]
        .map(element => pendingDefaults.get(element)).filter(Boolean);
    return pending.length ? Promise.all(pending).then(() => waitForBoardLaunchDefaults(root)) : null;
}
const efforts = Object.freeze({
    claude: ['low', 'medium', 'high', 'xhigh', 'max'],
    codex: ['minimal', 'low', 'medium', 'high', 'xhigh', 'max', 'ultra'],
    antigravity: ['low', 'medium', 'high'],
    copilot: ['none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'],
    grok: ['low', 'medium', 'high', 'xhigh']
});

function baseCli(selection) {
    if (typeof selection !== 'string' || !selection.startsWith('base:')) return null;
    const cli = selection.slice(5).toLowerCase();
    return cli === 'grok-4.6' ? 'grok' : cli;
}

export function normalizeBoardLaunchOptions(selection, options = {}) {
    const cli = baseCli(selection);
    if (!cli) return null;
    const model = pinnedModels[cli] ? '' : normalizeLlmModel(cli, options?.model);
    let effort = (efforts[cli] || []).includes(options?.effort) ? options.effort : '';
    if (cli === 'codex') effort = normalizeCodexModelEffort(model, effort);
    return {
        model,
        effort,
        mode: startModes(cli).length ? String(options?.mode || '') : '',
        yolo: options?.yolo === true,
        speed: cli === 'codex' && codexModelSupportsSpeed(model, options?.speed) ? options.speed : ''
    };
}

// Every startup mode here becomes a real CLI flag (--permission-mode / --mode / --agent).
// Codex is absent deliberately: its /plan is a TUI command with no launch flag, and the
// handshake that used to type it into the running TUI was removed 2026-09-15.
function startModes(cli) {
    if (cli === 'codex') return [];
    if (cli === 'copilot') return ['interactive', 'plan', 'autopilot'];
    if (cli === 'antigravity') return ['accept-edits', 'plan'];
    return openCodeClis.includes(cli) ? ['build', 'plan'] : ['plan'];
}

export function renderBoardLaunchOptions(selection, options = {}, { idPrefix = 'board-launch' } = {}) {
    const cli = baseCli(selection);
    if (!cli) return '';
    const values = normalizeBoardLaunchOptions(selection, options);
    const modes = startModes(cli);
    const optionTags = (list, selected) => [['', 'Default'], ...list.map(value => [value, value.charAt(0).toUpperCase() + value.slice(1)])]
        .map(([value, label]) => `<option value="${escapeHtml(value)}" ${value === selected ? 'selected' : ''}>${escapeHtml(label)}</option>`).join('');
    const modelHtml = pinnedModels[cli]
        ? `<input class="form-control form-control-sm" value="${escapeHtml(pinnedModels[cli])}" disabled aria-label="Fixed model">`
        : `<select class="form-select form-select-sm" data-board-launch-model aria-label="Model">${renderLlmModelOptions(cli, values.model)}</select>`;
    return `<div class="board-launch-options mt-2" data-board-launch-options>
        <label class="form-label">Model</label>${modelHtml}
        ${cli === 'codex' ? `<label class="form-label mt-2">Speed</label><select class="form-select form-select-sm" data-board-launch-speed aria-label="Speed">
            <option value="" ${!values.speed ? 'selected' : ''}>Default</option>
            ${['fast', 'ultrafast'].map(speed => `<option value="${speed}" ${values.speed === speed ? 'selected' : ''} ${codexModelSupportsSpeed(values.model, speed) ? '' : 'disabled'}>${speed === 'fast' ? 'Fast' : 'Ultrafast'}</option>`).join('')}
        </select>` : ''}
        ${efforts[cli] ? `<label class="form-label mt-2">Effort</label><select class="form-select form-select-sm" data-board-launch-effort aria-label="Effort">${optionTags(efforts[cli], values.effort)}</select>` : ''}
        ${modes.length ? `<label class="form-label mt-2">Start mode</label><select class="form-select form-select-sm" data-board-launch-mode aria-label="Start mode">${optionTags(modes, values.mode)}</select>` : ''}
        <div class="form-check mt-2">
            <input class="form-check-input" type="checkbox" id="${escapeHtml(idPrefix)}-yolo" data-board-launch-yolo ${values.yolo ? 'checked' : ''}>
            <label class="form-check-label" for="${escapeHtml(idPrefix)}-yolo">YOLO mode</label>
        </div>
        <button type="button" class="btn btn-link btn-sm px-0 mt-2" data-board-launch-default>
            <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><rect x="3" y="11" width="18" height="11" rx="2"></rect><path d="M7 11V7a5 5 0 0 1 10 0v4"></path></svg>
            Make Default For LLM
        </button>
    </div>`;
}

export function readBoardLaunchOptions(container, selection) {
    return normalizeBoardLaunchOptions(selection, {
        model: container.querySelector('[data-board-launch-model]')?.value || '',
        effort: container.querySelector('[data-board-launch-effort]')?.value || '',
        mode: container.querySelector('[data-board-launch-mode]')?.value || '',
        yolo: Boolean(container.querySelector('[data-board-launch-yolo]')?.checked),
        speed: container.querySelector('[data-board-launch-speed]')?.value || ''
    });
}

export function bindBoardLaunchOptions(container, selection, { app, useDefaults = false } = {}) {
    const model = container.querySelector('[data-board-launch-model]');
    const effort = container.querySelector('[data-board-launch-effort]');
    const speed = container.querySelector('[data-board-launch-speed]');
    const synchronize = () => {
        if (baseCli(selection) !== 'codex') return;
        for (const level of efforts.codex) {
            const option = effort?.querySelector(`option[value="${level}"]`);
            if (option) option.disabled = !codexModelSupportsEffort(model?.value, level);
        }
        if (effort) effort.value = normalizeCodexModelEffort(model?.value, effort.value);
        if (speed) {
            for (const tier of ['fast', 'ultrafast']) {
                const option = speed.querySelector(`option[value="${tier}"]`);
                if (option) option.disabled = !codexModelSupportsSpeed(model?.value, tier);
            }
            if (!codexModelSupportsSpeed(model?.value, speed.value)) speed.value = '';
        }
    };
    model?.addEventListener('change', synchronize);
    synchronize();
    const cli = baseCli(selection);
    const button = app && container.querySelector('[data-board-launch-default]');
    const abort = new AbortController();
    let disposed = false;
    let edited = false;
    const alive = () => !disposed && container.isConnected !== false;
    const markEdited = () => { edited = true; abort.abort(); };
    const endpoint = `/api/v1/llm-picker/defaults/${encodeURIComponent(cli || '')}`;
    const saveDefault = async () => {
        if (!alive() || button.disabled) return;
        edited = true;
        button.disabled = true;
        try {
            await app.apiCall(endpoint, 'PUT', readBoardLaunchOptions(container, selection), { showLoading: false, preferErrorResponseMessage: true });
            if (alive()) app.showToast('LLM defaults', 'Default options saved for this LLM.', 'success');
        } catch (error) {
            if (alive()) app.showToast('LLM defaults', error?.message || 'Default options could not be saved.', 'error');
        } finally { if (alive()) button.disabled = false; }
    };
    if (app && cli) {
        container.addEventListener('input', markEdited);
        container.addEventListener('change', markEdited);
        button?.addEventListener('click', saveDefault);
        if (useDefaults) {
            const optionsRoot = container.querySelector('[data-board-launch-options]') || container;
            const loading = app.apiCall(endpoint, 'GET', null, { showLoading: false, signal: abort.signal })
                .then(options => {
                    if (!alive() || edited) return;
                    const values = normalizeBoardLaunchOptions(selection, options);
                    if (model) model.innerHTML = renderLlmModelOptions(cli, values.model);
                    for (const key of ['model', 'effort', 'mode', 'speed']) {
                        const input = container.querySelector(`[data-board-launch-${key}]`);
                        if (input) input.value = values[key];
                    }
                    const yolo = container.querySelector('[data-board-launch-yolo]');
                    if (yolo) yolo.checked = values.yolo;
                    synchronize();
                    container.dispatchEvent(new Event('change', { bubbles: true }));
                })
                .catch(error => {
                    if (alive() && !edited && error?.name !== 'AbortError')
                        app.showToast('LLM defaults', error?.message || 'Default options could not be loaded.', 'error');
                })
                .finally(() => {
                    if (pendingDefaults.get(optionsRoot) === loading) pendingDefaults.delete(optionsRoot);
                });
            pendingDefaults.set(optionsRoot, loading);
        }
    }
    return () => {
        disposed = true;
        abort.abort();
        model?.removeEventListener('change', synchronize);
        if (app && cli) {
            container.removeEventListener('input', markEdited);
            container.removeEventListener('change', markEdited);
            button?.removeEventListener('click', saveDefault);
        }
    };
}

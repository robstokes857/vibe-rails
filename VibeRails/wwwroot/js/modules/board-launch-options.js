import { escapeHtml } from './utils.js';
import { renderLlmModelOptions } from './llm-model-catalog.js';

const pinnedModels = Object.freeze({
    'glm-5.2': 'zai/glm-5.2', 'glm-5.3': 'zai-coding-plan/glm-5.3',
    'deepseek-v4-pro': 'deepseek/deepseek-v4-pro', 'kimi-k3': 'moonshotai/kimi-k3'
});
const openCodeClis = ['opencode', 'glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3'];
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
    const model = pinnedModels[cli] ? '' : String(options?.model || '').trim();
    let effort = (efforts[cli] || []).includes(options?.effort) ? options.effort : '';
    if (cli === 'codex' && model.toLowerCase() === 'gpt-5.5' && effort === 'max') effort = 'xhigh';
    return { model, effort, mode: startModes(cli).length ? String(options?.mode || '') : '' };
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

export function renderBoardLaunchOptions(selection, options = {}) {
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
        ${efforts[cli] ? `<label class="form-label mt-2">Effort</label><select class="form-select form-select-sm" data-board-launch-effort aria-label="Effort">${optionTags(efforts[cli], values.effort)}</select>` : ''}
        ${modes.length ? `<label class="form-label mt-2">Start mode</label><select class="form-select form-select-sm" data-board-launch-mode aria-label="Start mode">${optionTags(modes, values.mode)}</select>` : ''}
    </div>`;
}

export function readBoardLaunchOptions(container, selection) {
    return normalizeBoardLaunchOptions(selection, {
        model: container.querySelector('[data-board-launch-model]')?.value || '',
        effort: container.querySelector('[data-board-launch-effort]')?.value || '',
        mode: container.querySelector('[data-board-launch-mode]')?.value || ''
    });
}

export function bindBoardLaunchOptions(container, selection) {
    const model = container.querySelector('[data-board-launch-model]');
    const effort = container.querySelector('[data-board-launch-effort]');
    const synchronize = () => {
        if (baseCli(selection) !== 'codex' || !effort) return;
        const max = effort.querySelector('option[value="max"]');
        const unsupported = model?.value.toLowerCase() === 'gpt-5.5';
        if (max) max.disabled = unsupported;
        if (unsupported && effort.value === 'max') effort.value = 'xhigh';
    };
    model?.addEventListener('change', synchronize);
    synchronize();
    return () => model?.removeEventListener('change', synchronize);
}

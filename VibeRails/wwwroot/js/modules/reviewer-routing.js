import { escapeHtml as esc } from './utils.js';
import { mountLlmPicker } from './pickers/llm-picker.js';
import { renderLlmModelOptions } from './llm-model-catalog.js';

export function switchReviewerDefaults() {
    return { mode: 'switch', mappings: [
        { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } },
        { sourceProvider: 'codex', reviewer: { selection: 'base:claude' } }
    ], fallback: { selection: 'base:codex' } };
}

export function routingSummary(r) {
    if (!r) return '';
    const source = r.source?.provider ? `${r.source.provider} coded this` : `Coding source: ${r.source?.kind || 'unknown'}`;
    return `${source} → ${r.reviewer || r.provider} will review${r.overridden ? ' (override)' : r.usedFallback ? ' (configured fallback)' : ''}${r.model ? ` · ${r.model}` : ''}`;
}

export function reviewerTargetMarkup() {
    return '<select data-reviewer-target aria-label="Reviewer provider or environment"></select><div data-reviewer-model></div>';
}

/** Shared provider/environment picker plus the existing provider model catalog. */
export function mountReviewerTarget(app, host, target, onChange = () => {}) {
    const picker = host.querySelector('[data-reviewer-target]');
    const model = host.querySelector('[data-reviewer-model]');
    let options = { ...(target?.options || {}) };
    const disposePicker = mountLlmPicker(app, picker, { context: 'sandbox', selectedValue: target?.selection || 'base:codex', placeholder: 'Choose reviewer', selectedFallback: value => ({ value, label: `Unavailable selection (${value})` }) });
    function renderModel() {
        const selection = picker.value || target?.selection || 'base:codex';
        const cli = selection.startsWith('base:') ? selection.slice(5) : null;
        model.innerHTML = cli ? `<label class="form-label small">Model<select class="form-select form-select-sm" data-reviewer-model-value aria-label="Reviewer model">${renderLlmModelOptions(cli, options.model || '')}</select></label>`
            : '<small class="text-muted">Uses the saved environment’s model and settings.</small>';
    }
    const change = () => { options = {}; renderModel(); onChange(); };
    picker.addEventListener('change', change);
    model.addEventListener('change', onChange);
    renderModel();
    return {
        read: () => ({ selection: picker.value, ...(picker.value?.startsWith('base:') ? { options: { ...options, model: model.querySelector('[data-reviewer-model-value]')?.value || '' } } : {}) }),
        dispose() { disposePicker?.(); picker.removeEventListener('change', change); model.removeEventListener('change', onChange); }
    };
}

export function routingEditorMarkup() {
    return `<div data-reviewer-mappings></div><button type="button" class="btn btn-sm btn-link" data-reviewer-add>Add provider mapping</button>
        <label class="form-label d-block mt-2">Fallback for unknown, mixed, human or unmapped coding sources</label>
        <div data-reviewer-fallback>${reviewerTargetMarkup()}</div>
        <p class="text-muted small">Mappings may use the same provider. The selected reviewer supplies model and permission settings; a Switch Worker supplies its initial message. Put step functions and their prompt references in the reviewer environments. Review runs keep their selected provider and scope. This preset reviews the stated project checkout, including when a selected environment normally creates a clone.</p>`;
}

export function mountRoutingEditor(app, host, initial = switchReviewerDefaults(), onChange = () => {}) {
    if (!host) return { read: () => initial, dispose() {} };
    const rows = [];
    const mappings = host.querySelector('[data-reviewer-mappings]');
    function add(mapping = { sourceProvider: '', reviewer: { selection: 'base:codex' } }) {
        const row = document.createElement('div');
        row.className = 'border rounded p-2 mb-2';
        row.innerHTML = `<label class="form-label">Coding provider<input class="form-control form-control-sm" data-reviewer-source value="${esc(mapping.sourceProvider)}" placeholder="claude, codex, opencode…" aria-label="Coding provider"></label>
            <span class="d-block small mb-1">Reviewer</span><div data-reviewer-choice>${reviewerTargetMarkup()}</div>
            <button type="button" class="btn btn-sm btn-link" data-reviewer-remove>Remove mapping</button>`;
        mappings.append(row);
        const target = mountReviewerTarget(app, row.querySelector('[data-reviewer-choice]'), mapping.reviewer, onChange);
        const entry = { row, target };
        rows.push(entry);
        row.querySelector('[data-reviewer-source]').addEventListener('input', onChange);
        row.querySelector('[data-reviewer-remove]').addEventListener('click', () => {
            target.dispose(); rows.splice(rows.indexOf(entry), 1); row.remove(); onChange();
        });
    }
    for (const mapping of initial.mappings || []) add(mapping);
    const fallback = mountReviewerTarget(app, host.querySelector('[data-reviewer-fallback]'), initial.fallback, onChange);
    const addButton = host.querySelector('[data-reviewer-add]');
    const addClick = () => { if (rows.length < 20) { add(); onChange(); } };
    addButton.addEventListener('click', addClick);
    return {
        read: () => ({ mode: 'switch', mappings: rows.map(({ row, target }) => ({ sourceProvider: row.querySelector('[data-reviewer-source]').value.trim().toLowerCase(), reviewer: target.read() })), fallback: fallback.read() }),
        dispose() { rows.forEach(r => r.target.dispose()); fallback.dispose(); addButton.removeEventListener('click', addClick); }
    };
}

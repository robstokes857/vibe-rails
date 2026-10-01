import { escapeHtml as esc } from './utils.js';

export const isCheck = kind => Number(kind) === 2 || Number(kind) === 3;
export const checkName = kind => Number(kind) === 2 ? 'Code quality' : 'VCA';
export function validCheckScope(args) {
    return Array.isArray(args) && ((args.length === 1 && ['working-tree', 'unpushed', 'repository'].includes(args[0]))
        || (args.length === 3 && args[0] === 'range' && args.slice(1).every(sha => /^[a-f0-9]{40}$/i.test(sha))));
}
export function checkFields(action) {
    const args = action.arguments || ['unpushed'];
    return `<div class="job-action-body">
        <label class="form-label">Scope
            <select class="form-select" data-action-field="checkScope">
                ${[['unpushed', 'Unpushed commits (merge base → HEAD)'], ['working-tree', 'Working-tree changes (excludes commits)'],
        ['range', 'Selected commit / range (base → head)'], ['repository', 'Repository at HEAD (all committed files)']]
        .map(([value, label]) => `<option value="${value}"${args[0] === value ? ' selected' : ''}>${label}</option>`).join('')}
            </select>
        </label>
        ${args[0] === 'range' ? `<label class="form-label">Base commit SHA<input class="form-control" data-action-field="checkBase" value="${esc(args[1] || '')}" maxlength="40"></label>
            <label class="form-label">Head commit SHA<input class="form-control" data-action-field="checkHead" value="${esc(args[2] || '')}" maxlength="40"></label>
            <p class="form-text">For one commit, use its parent as the base. Both SHAs must be full length and the base must be an ancestor. Linked card commits never select a range automatically.</p>` : ''}
        <p class="form-text">Run inexpensive checks before the review Worker. Results are advisory evidence for the reviewer; they never move a card. Empty or unsupported analysis is marked skipped.</p>
    </div>`;
}

import { escapeHtml as esc } from './utils.js';

export const isCheck = kind => Number(kind) === 2 || Number(kind) === 3;
export const checkName = kind => Number(kind) === 2 ? 'Code quality' : 'VCA';
export function validCheckScope(args) {
    return Array.isArray(args) && ((args.length === 1 && ['card', 'working-tree', 'unpushed', 'repository'].includes(args[0]))
        || (args.length === 2 && args[0] === 'commit' && /^[a-f0-9]{7,40}$/i.test(args[1]))
        || (args.length === 3 && args[0] === 'range' && args.slice(1).every(sha => /^[a-f0-9]{40}$/i.test(sha))));
}
export function checkFields(action) {
    const args = action.arguments || [Number(action.kind) === 2 ? 'card' : 'unpushed'];
    return `<div class="job-action-body">
        <label class="form-label">Scope
            <select class="form-select" data-action-field="checkScope">
                ${[['card', 'Linked commits on the card'], ['commit', 'One commit'], ['unpushed', 'Unpushed commits (merge base → HEAD)'], ['working-tree', 'Working-tree changes (excludes commits)'],
        ['range', 'Commit range (base → head)'], ['repository', 'Repository at HEAD (all committed files)']]
        .map(([value, label]) => `<option value="${value}"${args[0] === value ? ' selected' : ''}>${label}</option>`).join('')}
            </select>
        </label>
        ${args[0] === 'card' ? '<p class="form-text">Checks code from the card’s linked commits. With no linked code, the result is Needs review; research spikes are skipped.</p>' : ''}
        ${args[0] === 'commit' ? `<label class="form-label">Commit SHA<input class="form-control" data-action-field="checkCommit" value="${esc(args[1] || '')}" minlength="7" maxlength="40"></label>
            <p class="form-text">Enter a 7–40 character commit SHA. Only that commit’s changes are checked, including an initial commit.</p>` : ''}
        ${args[0] === 'range' ? `<label class="form-label">Base commit SHA<input class="form-control" data-action-field="checkBase" value="${esc(args[1] || '')}" maxlength="40"></label>
            <label class="form-label">Head commit SHA<input class="form-control" data-action-field="checkHead" value="${esc(args[2] || '')}" maxlength="40"></label>
            <p class="form-text">Both SHAs must be full length and the base must be an ancestor. Use One commit to check a commit without entering its parent.</p>` : ''}
        <p class="form-text">Run inexpensive checks before the review Worker. Results are advisory evidence for the reviewer; they never move a card. Empty or unsupported analysis is marked skipped.</p>
    </div>`;
}

import { escapeHtml as esc } from './utils.js';

export const isCheck = kind => Number(kind) === 2 || Number(kind) === 3;
export const checkName = kind => Number(kind) === 2 ? 'Code quality' : 'VCA';
/** The minimum grades a Code quality action can require; the same ones Git Guard's rule accepts. */
export const CHECK_MINIMUM_GRADES = Object.freeze(['A', 'B', 'C']);

/**
 * A Code quality action may end with `minimum A|B|C` (VIBE-278): below that grade the step fails.
 * Everything before it is the scope vector. Without it, findings are feedback only.
 */
export function splitCheckArguments(args) {
    const list = Array.isArray(args) ? args : [];
    return list.length >= 3 && list[list.length - 2] === 'minimum'
        ? { scope: list.slice(0, -2), minimum: String(list[list.length - 1]) }
        : { scope: list, minimum: '' };
}
export const joinCheckArguments = (scope, minimum) => minimum ? [...scope, 'minimum', minimum] : [...scope];

export function validCheckScope(args, kind = 2) {
    if (!Array.isArray(args)) return false;
    const { scope, minimum } = splitCheckArguments(args);
    if (minimum && (Number(kind) !== 2 || !CHECK_MINIMUM_GRADES.includes(minimum))) return false;
    return (scope.length === 1 && ['card', 'working-tree', 'unpushed', 'repository'].includes(scope[0]))
        || (scope.length === 2 && scope[0] === 'commit' && /^[a-f0-9]{7,40}$/i.test(scope[1]))
        || (scope.length === 3 && scope[0] === 'range' && scope.slice(1).every(sha => /^[a-f0-9]{40}$/i.test(sha)));
}

/** The choice between feedback only and failing the step below a grade, for both editors. */
export function checkMinimumOptions(minimum) {
    return [['', 'Post as feedback only; never fail the step'], ...CHECK_MINIMUM_GRADES.map(grade => [grade, `Fail the step below grade ${grade}`])]
        .map(([value, label]) => `<option value="${value}"${minimum === value ? ' selected' : ''}>${label}</option>`).join('');
}
export const CHECK_MINIMUM_HELP = 'The grade is the overall Code quality grade of the added code, the same one Git Guard’s “Code quality minimum” rule checks. Explicit file exemptions apply. Either way, the result is saved on the card as a review.';

export function checkFields(action, actionId = '') {
    const quality = Number(action.kind) === 2;
    const { scope, minimum } = splitCheckArguments(action.arguments || [quality ? 'card' : 'unpushed']);
    const args = scope;
    const helpId = `job-check-minimum-help-${esc(actionId)}`;
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
        ${quality ? `<label class="form-label">Findings
            <select class="form-select" data-action-field="checkMinimum" aria-describedby="${helpId}">${checkMinimumOptions(minimum)}</select>
        </label>
        <p class="form-text" id="${helpId}">${CHECK_MINIMUM_HELP} With no linked code, a minimum grade fails the step.</p>` : ''}
        <p class="form-text">Run inexpensive checks before the review Worker. Results never move a card. ${quality ? 'Findings stay feedback for the reviewer unless you set a minimum grade.' : 'VCA results are feedback for the reviewer.'} Empty or unsupported analysis is marked skipped.</p>
    </div>`;
}

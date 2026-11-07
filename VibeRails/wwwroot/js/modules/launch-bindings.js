import { escapeHtml } from './utils.js';

export const bindingSection = (
    project
) => `<section class="board-side-section launch-bindings">
    <h3 class="board-discussion-title">Automatic runs</h3>
    <p class="board-editor-muted">Execution project: <strong>${escapeHtml(project || 'Current project')}</strong></p>
    <p class="board-editor-muted">Schedules run while Vibe Rails is open. Board lane runs are configured on the Board.</p>
    <div data-binding-list></div>
    <button type="button" class="btn btn-sm btn-outline-secondary" data-binding-add>Add schedule or event</button>
    <p class="board-editor-muted" data-binding-hint></p>
</section>`;

export function mountBindings(root, initial, { project, eligible }) {
    const bindings = initial.map((item) => ({
        ...item,
        trigger: { ...item.trigger }
    }));
    const host = root.querySelector('[data-binding-list]');
    const add = root.querySelector('[data-binding-add]');
    const abort = new AbortController();
    const isSchedule = (item) => Number(item.trigger.kind) === 0;
    const render = () => {
        host.innerHTML = bindings
            .map(
                (
                    item,
                    index
                ) => `<fieldset data-binding-index="${index}" class="launch-binding">
            <legend class="visually-hidden">Automatic run ${index + 1}</legend>
            <label class="card-editor-switch-row"><span>Enabled</span><input type="checkbox" class="card-editor-switch" role="switch" data-binding-field="enabled" ${item.enabled ? 'checked' : ''}></label>
            <label>Trigger<select class="form-select" data-binding-field="kind" aria-label="Trigger">
                ${[
                    [0, 'Schedule'],
                    [4, 'Before commit'],
                    [2, 'After commit']
                ]
                    .map(
                        ([value, label]) =>
                            `<option value="${value}"${value === Number(item.trigger.kind) ? ' selected' : ''}>${label}</option>`
                    )
                    .join('')}
            </select></label>
            <div ${isSchedule(item) ? '' : 'hidden'} data-binding-schedule>
                <label>Frequency<select class="form-select" data-binding-field="scheduleKind" aria-label="Frequency">
                    ${[
                        [0, 'Interval'],
                        [1, 'Daily'],
                        [2, 'Weekly']
                    ]
                        .map(
                            ([value, label]) =>
                                `<option value="${value}"${value === Number(item.trigger.scheduleKind || 0) ? ' selected' : ''}>${label}</option>`
                        )
                        .join('')}
                </select></label>
                <label ${Number(item.trigger.scheduleKind || 0) === 0 ? '' : 'hidden'} data-binding-interval>Every (minutes)<input type="number" class="form-control" min="1" data-binding-field="intervalMinutes" value="${Number(item.trigger.intervalMinutes) || 60}"></label>
                <div ${Number(item.trigger.scheduleKind || 0) === 0 ? 'hidden' : ''} data-binding-clock>
                    <label>Local time<input class="form-control" type="time" data-binding-field="localTime" value="${escapeHtml(item.trigger.localTime || '09:00')}"></label>
                    <label>Time zone<input class="form-control" data-binding-field="timeZoneId" value="${escapeHtml(item.trigger.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone)}"></label>
                </div>
                <fieldset ${Number(item.trigger.scheduleKind) === 2 ? '' : 'hidden'} data-binding-days><legend>Days</legend>
                    ${['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'].map((day, bit) => `<label class="me-2"><input type="checkbox" data-binding-day="${1 << bit}"${(item.trigger.daysOfWeekMask || 0) & (1 << bit) ? ' checked' : ''}> ${day}</label>`).join('')}
                </fieldset>
            </div>
            <p class="board-editor-muted">${escapeHtml(item.projectPath || project)}</p>
        </fieldset>`
            )
            .join('');
        updateEligibility();
    };
    function updateEligibility() {
        const allowed = eligible();
        add.disabled = !allowed;
        root.querySelector('[data-binding-hint]').textContent = allowed
            ? 'Changes apply when you save.'
            : 'Quick Launches and disabled definitions cannot run automatically. Existing bindings remain saved but will not dispatch.';
    }
    host.addEventListener(
        'input',
        (event) => {
            const field = event.target.dataset.bindingField,
                row = event.target.closest('[data-binding-index]');
            if (!row) return;
            const item = bindings[Number(row.dataset.bindingIndex)];
            if (event.target.hasAttribute('data-binding-day')) {
                item.trigger.daysOfWeekMask = [
                    ...row.querySelectorAll('[data-binding-day]:checked')
                ].reduce(
                    (mask, node) => mask | Number(node.dataset.bindingDay),
                    0
                );
            } else if (field === 'enabled') item.enabled = event.target.checked;
            else if (field)
                item.trigger[field] = [
                    'kind',
                    'scheduleKind',
                    'intervalMinutes'
                ].includes(field)
                    ? Number(event.target.value)
                    : event.target.value;
            if (field === 'kind' || field === 'scheduleKind') {
                row.querySelector('[data-binding-schedule]').hidden =
                    !isSchedule(item);
                row.querySelector('[data-binding-interval]').hidden =
                    Number(item.trigger.scheduleKind) !== 0;
                row.querySelector('[data-binding-clock]').hidden =
                    Number(item.trigger.scheduleKind) === 0;
                row.querySelector('[data-binding-days]').hidden =
                    Number(item.trigger.scheduleKind) !== 2;
            }
        },
        { signal: abort.signal }
    );
    add.addEventListener(
        'click',
        () => {
            bindings.push({
                enabled: true,
                revision: 0,
                trigger: {
                    kind: 0,
                    scheduleKind: 0,
                    intervalMinutes: 60,
                    localTime: '09:00',
                    timeZoneId:
                        Intl.DateTimeFormat().resolvedOptions().timeZone,
                    daysOfWeekMask: 62
                }
            });
            render();
            add.dispatchEvent(new Event('change', { bubbles: true }));
        },
        { signal: abort.signal }
    );
    render();
    return { bindings, updateEligibility, dispose: () => abort.abort() };
}

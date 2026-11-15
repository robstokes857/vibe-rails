import { escapeHtml } from './utils.js';

export function ruleChoices(agents, project) {
    const prefix = String(project).replaceAll('\\', '/').replace(/\/$/, '') + '/';
    const choices = new Map();
    for (const file of agents || []) {
        const path = String(file.path).replaceAll('\\', '/');
        const sourcePath = path.toLowerCase().startsWith(prefix.toLowerCase()) ? path.slice(prefix.length) : path;
        for (const rule of file.rules || []) {
            const value = JSON.stringify({ sourcePath, ruleText: rule.text });
            choices.set(value, { value, label: `${sourcePath} · ${rule.text} (${rule.enforcement})` });
        }
    }
    return [...choices.values()];
}

export function bindingTrigger(trigger) {
    const kind = Number(trigger.kind);
    if (kind !== 0) return { kind };
    const scheduleKind = Number(trigger.scheduleKind || 0);
    return scheduleKind === 0
        ? { kind, scheduleKind, intervalMinutes: Number(trigger.intervalMinutes) || 60 }
        : { kind, scheduleKind, localTime: trigger.localTime || '09:00', timeZoneId: trigger.timeZoneId || Intl.DateTimeFormat().resolvedOptions().timeZone,
            ...(scheduleKind === 2 ? { daysOfWeekMask: trigger.daysOfWeekMask || 0 } : {}) };
}

export const bindingSection = (
    project
) => `<section class="board-side-section launch-bindings">
    <h3 class="board-discussion-title">Automatic runs</h3>
    <p class="board-editor-muted">Execution project: <strong>${escapeHtml(project || 'Current project')}</strong></p>
    <p class="board-editor-muted">Runs execute while VibeRails is open. Rule responses queue after violations during real commits, even when STOP blocks the commit. Previews never launch them.</p>
    <div data-binding-list></div>
    <button type="button" class="btn btn-sm btn-outline-secondary" data-binding-add>Create schedule or event</button>
    <p class="board-editor-muted" data-binding-hint></p>
</section>`;

export function mountBindings(root, initial, { project, eligible, ruleEligible = () => true, loadRules }) {
    const bindings = initial.map((item) => ({
        ...item,
        trigger: { ...item.trigger }
    }));
    const host = root.querySelector('[data-binding-list]');
    const add = root.querySelector('[data-binding-add]');
    const abort = new AbortController();
    let rules = [], ruleError = '', rulesLoaded = false;
    const ruleOptions = (item) => {
        const selected = item.rule ? JSON.stringify(item.rule) : '';
        const options = [...rules];
        if (selected && !options.some(option => option.value === selected))
            options.unshift({ value: selected, label: `${item.rule.sourcePath} · ${item.rule.ruleText} (${rulesLoaded ? 'not found in current rules' : 'saved selection'})` });
        return '<option value="">Select a repository rule</option>' + options.map(option =>
            `<option value="${escapeHtml(option.value)}"${option.value === selected ? ' selected' : ''}>${escapeHtml(option.label)}</option>`).join('');
    };
    const refreshRules = async () => {
        try {
            const response = await loadRules?.(abort.signal);
            if (abort.signal.aborted) return;
            rules = ruleChoices(response?.agents, project);
            rulesLoaded = true;
            ruleError = rules.length ? '' : 'No repository rules found. Add rules on the Rules page, then refresh.';
        } catch (error) {
            if (abort.signal.aborted) return;
            ruleError = 'Could not load rules. Refresh to try again; saved selections are kept.';
        }
        host.querySelectorAll('[data-binding-rule]').forEach(select => {
            select.innerHTML = ruleOptions(bindings[Number(select.closest('[data-binding-index]').dataset.bindingIndex)]);
        });
        host.querySelectorAll('[data-rule-load-status]').forEach(node => { node.textContent = ruleError; });
    };
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
                    [2, 'After commit'],
                    [6, 'Rule violation']
                ]
                    .map(
                        ([value, label]) =>
                            `<option value="${value}"${value === Number(item.trigger.kind) ? ' selected' : ''}>${label}</option>`
                    )
                    .join('')}
            </select></label>
            <div ${Number(item.trigger.kind) === 6 ? '' : 'hidden'} data-binding-rule-panel>
                <label>Repository rule<select class="form-select" data-binding-rule aria-label="Repository rule" aria-describedby="rule-binding-help-${index}">${ruleOptions(item)}</select></label>
                <p class="board-editor-muted text-break" data-binding-rule-summary>${item.rule ? `${escapeHtml(item.rule.sourcePath)}<br>${escapeHtml(item.rule.ruleText)}` : ''}</p>
                <p class="board-editor-muted" id="rule-binding-help-${index}">The declaring file and exact rule text identify this rule. Editing either requires updating this selection. Enforcement stays unchanged. Requires an AI Agent or script Action.</p>
                <p class="board-editor-muted" role="status" data-rule-load-status>${escapeHtml(ruleError)}</p>
                <button type="button" class="btn btn-sm btn-secondary" data-rule-refresh>Refresh rules</button>
            </div>
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
        host.querySelectorAll('[data-binding-field="kind"] option[value="6"]').forEach(option => { option.disabled = !ruleEligible(); });
        add.disabled = !allowed;
        root.querySelector('[data-binding-hint]').textContent = allowed
            ? 'Changes apply when you save.'
            : 'This definition cannot run automatically here. Check its prompt, Enabled setting and Projects selection. Existing bindings remain saved but will not dispatch.';
    }
    host.addEventListener(
        'input',
        (event) => {
            const field = event.target.dataset.bindingField,
                row = event.target.closest('[data-binding-index]');
            if (!row) return;
            const item = bindings[Number(row.dataset.bindingIndex)];
            if (event.target.hasAttribute('data-binding-rule')) {
                item.rule = event.target.value ? JSON.parse(event.target.value) : null;
                row.querySelector('[data-binding-rule-summary]').textContent = item.rule ? `${item.rule.sourcePath} · ${item.rule.ruleText}` : '';
            } else if (event.target.hasAttribute('data-binding-day')) {
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
                row.querySelector('[data-binding-rule-panel]').hidden = Number(item.trigger.kind) !== 6;
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
    host.addEventListener('click', event => {
        if (event.target.closest('[data-rule-refresh]')) void refreshRules();
    }, { signal: abort.signal });
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
    void refreshRules();
    return { bindings, updateEligibility, validate: () => {
        if (bindings.some(item => item.enabled && Number(item.trigger.kind) === 6 && !item.rule))
            return 'Select a repository rule for each enabled rule response.';
        if (!ruleEligible() && bindings.some(item => item.enabled && Number(item.trigger.kind) === 6))
            return 'Rule responses require an AI Agent or script Action.';
        return '';
    }, dispose: () => abort.abort() };
}

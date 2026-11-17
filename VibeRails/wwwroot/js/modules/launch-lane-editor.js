import { escapeHtml, confirmDialog, enhanceSelectWithTomSelect, getCliBrand } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { launchPresentation } from './launch-presentation.js';
import { boardBrandLogo } from './board-brand.js';
import { checkMinimumOptions, CHECK_MINIMUM_GRADES, CHECK_MINIMUM_HELP } from './job-checks.js';

/**
 * Scopes a new lane check can use (VIBE-271). Each one makes sense on every card entry; a pinned
 * commit or range does not, so those stay in the AI Agents editor. Linked commits is the default:
 * card work is committed before it reaches a later lane, so a working-tree check sees none of it.
 */
export const LANE_CHECK_SCOPES = Object.freeze([
    ['card', 'Linked commits on the card', 'Checks the code in the card’s linked commits. With no linked code the result is Needs review and a Code quality check fails its step; research spikes are skipped.'],
    ['unpushed', 'Unpushed commits (merge base → HEAD)', 'Checks every commit on the branch that its upstream lacks, including other cards’ commits. A branch with no upstream fails visibly.'],
    ['working-tree', 'Working-tree changes (excludes commits)', 'Checks uncommitted and untracked files in the checkout, including other sessions’ edits. Committed card work is not included.'],
    ['repository', 'Repository at HEAD (all committed files)', 'Checks every committed file at HEAD. Uncommitted files are excluded.']
]);
export const DEFAULT_LANE_CHECK_SCOPE = 'card';
const SCOPE_NAMES = { card: 'Linked commits', commit: 'One commit', unpushed: 'Unpushed commits', 'working-tree': 'Working tree', range: 'Commit range', repository: 'Repository' };
/**
 * Kind names as the lane shows them. Nobody knew what “VCA” meant, so that check reads as a Rules check,
 * and Code quality as a scan (VIBE-337). Checks saved under the older names keep them (laneChoiceLabel).
 */
const KIND_LABELS = { agent: 'Agent', script: 'Script', vca: 'Rules check', 'code-quality': 'Code quality scan' };
const LEGACY_KIND_LABELS = { vca: 'VCA check', 'code-quality': 'Code quality check' };
const RUNTIME_LABELS = ['Python', 'PowerShell', 'Bash'];
/** The per-user Manage list: one stored row per hidden entry, like Quick actions (VIBE-337). */
export const LANE_PICKER_PREFERENCES = 'lane-picker/preferences';
let mounted = 0;

/** What each check kind does, and the page that explains it (VIBE-337). */
export const LANE_CHECKS = Object.freeze({
    vca: Object.freeze({
        about: 'Checks the card’s code against this repository’s rules in vc.rules.md (formerly “VCA check”). WARN, COMMIT and STOP violations are reported as feedback; it never moves the card.',
        view: 'dashboard',
        link: 'Open Rules'
    }),
    'code-quality': Object.freeze({
        about: 'Grades the added code A–F and saves a report on the card. Findings are feedback unless the scan has a minimum grade; below it, the step fails.',
        view: 'code-quality',
        link: 'Open Quality'
    })
});

/** Always offered, whatever checks exist: the scope is chosen below the picker (VIBE-271). */
export const LANE_BUILT_IN_CHECKS = Object.freeze([
    Object.freeze({ id: 'new:vca', key: 'check:vca', name: 'Rules check', kind: 'vca' }),
    Object.freeze({ id: 'new:code-quality', key: 'check:code-quality', name: 'Code quality scan', kind: 'code-quality' })
]);

/** What a saved check analyzes, in words; '' when the catalog did not say. */
export function laneCheckScopeName(check) {
    if (!check?.scope) return '';
    const scope = SCOPE_NAMES[check.scope] || check.scope;
    return check.failGrade ? `${scope}, minimum ${check.failGrade}` : scope;
}

/** A new check's failing grade: A, B or C on a Code quality scan, otherwise '' for feedback only (VIBE-335). */
export const laneCheckGrade = (kind, grade) => kind === 'code-quality' && CHECK_MINIMUM_GRADES.includes(grade) ? grade : '';

/** The name a lane-created check is saved under, so the lane list says what it checks and any minimum grade. */
export const laneCheckName = (kind, scope, failGrade = '') =>
    `${KIND_LABELS[kind]} · ${laneCheckScopeName({ scope, failGrade: laneCheckGrade(kind, failGrade) })}`;

/** Picker text: the name, then the kind and a check's scope unless the name already says them. */
export function laneChoiceLabel(item) {
    const kind = KIND_LABELS[item.kind] || item.kind;
    const names = [kind, LEGACY_KIND_LABELS[item.kind]].filter(Boolean);
    const parts = [item.name];
    if (!names.some(name => item.name === name || item.name.startsWith(`${name} · `))) parts.push(kind);
    const scope = laneCheckScopeName(item.check);
    if (scope && !item.name.endsWith(scope)) parts.push(scope);
    return parts.join(' · ');
}

/** An existing automatic check with exactly this kind, scope and minimum grade ('' for none), and no pinned commits. */
export const findLaneCheck = (items, kind, scope, failGrade = '') => items.find(item => item.definitionType === 'action'
    && item.kind === kind && item.canRunAutomatically && item.check?.scope === scope
    && (item.check.failGrade || '') === laneCheckGrade(kind, failGrade) && !item.check.baseCommit && !item.check.headCommit);

/** The stable Manage list key: a saved definition's type and id, or check:<kind> for a built-in new check. */
export const laneChoiceKey = item => item.key || `${item.definitionType}:${item.id}`;

/** One sentence of what an entry does: the picker's second line and its “What this does” text. */
export function laneChoiceDetail(item) {
    const check = LANE_CHECKS[item?.kind];
    if (check) return check.about;
    if (item?.kind === 'script') {
        const path = item.script?.path;
        if (path) return `Runs ${path} with ${RUNTIME_LABELS[item.script.runtime] || 'its interpreter'}.`;
        return item.description?.trim() || 'Runs a repository script.';
    }
    const description = item?.description?.trim();
    if (description) return description;
    return item?.definitionType === 'preset'
        ? `Starts ${item.cli ? getCliBrand(item.cli).label : 'its CLI'} with its saved prompt.`
        : 'Runs this Action.';
}

/** A run order row's second line: the kind, then what it runs; a check's scope instead of its description. */
export function laneRowDetail(item) {
    const kind = KIND_LABELS[item.kind] || (item.definitionType === 'preset' ? 'Agent' : 'Action');
    if (LANE_CHECKS[item.kind]) {
        const scope = laneCheckScopeName(item.check);
        return scope ? `${kind} · Scope: ${scope}` : kind;
    }
    return `${kind} · ${laneChoiceDetail(item)}`;
}

/** What the picker offers: runnable definitions not in this lane yet, then the built-in checks, minus hidden ones. */
export function laneChoices(items, selections, hidden = new Set()) {
    return [
        ...items.filter(item => item.canRunAutomatically && !selections.some(selection => selection.definitionId === item.id)),
        ...LANE_BUILT_IN_CHECKS
    ].filter(item => !hidden.has(laneChoiceKey(item)));
}

/** Every entry the picker can offer, hidden or not and whatever this lane runs, in Manage list groups. */
export function laneManageGroups(items) {
    const offered = [...items.filter(item => item.canRunAutomatically), ...LANE_BUILT_IN_CHECKS];
    const action = item => item.definitionType === 'action' && !LANE_CHECKS[item.kind];
    return [
        ['Agents', offered.filter(item => item.definitionType === 'preset')],
        ['Scripts', offered.filter(item => action(item) && item.kind === 'script')],
        ['Checks', offered.filter(item => LANE_CHECKS[item.kind])],
        ['Other actions', offered.filter(item => action(item) && item.kind !== 'script')]
    ].filter(([, entries]) => entries.length);
}

const choiceLogo = item => LANE_CHECKS[item.kind] ? boardBrandLogo(null, '') : launchPresentation(item).logoHtml;

/** Ordered definitions, shared by lane settings and the lane popup. */
export function mountLaunchLane(app, host, columnId, onSaved = () => {}, { beforeSave, saveRoot = host } = {}) {
    const api = new LaunchApi(app),
        abort = new AbortController();
    let selections = [],
        revision = 0,
        items = [],
        busy = false;
    let dirty = false,
        confirming = false;
    let picker = null,
        choiceItems = new Map();
    // Manage list (VIBE-337): what this user hides from the picker. Never the lane, never a definition.
    let hidden = new Set(),
        preferencesFailed = false,
        manageOpen = false;
    const id = ++mounted;
    const scopeHelpId = `launch-lane-check-help-${id}`,
        minimumHelpId = `launch-lane-check-minimum-help-${id}`,
        pickerId = `launch-lane-choice-${id}`,
        manageId = `launch-lane-manage-${id}`,
        stagingId = `launch-lane-staging-${id}`;
    const scopeHelp = scope => `${LANE_CHECK_SCOPES.find(([value]) => value === scope)?.[2] || ''} A pinned commit or range can be set on the check in AI Agents.`;
    const current = () => !abort.signal.aborted && host.isConnected;
    const beforeClose = (retry) => {
        if (busy) {
            app.showToast(
                'Board',
                'Wait for lane settings to finish saving.',
                'info'
            );
            return false;
        }
        if (!dirty) return true;
        if (!confirming) {
            confirming = true;
            void confirmDialog({
                title: 'Discard lane changes?',
                message: 'Your unsaved lane changes will be lost.',
                confirmLabel: 'Discard',
                danger: true
            }).then((ok) => {
                confirming = false;
                if (ok && current()) {
                    dirty = false;
                    retry?.();
                }
            });
        }
        return false;
    };
    const rows = () => selections
        .map((selection, index) => {
            const item = items.find(
                (item) =>
                    item.id === selection.definitionId &&
                    item.definitionType === selection.definitionType
            );
            const name = escapeHtml(item?.name || 'Unavailable / retired selection');
            const detail = item ? `<span class="launch-lane-row-detail">${escapeHtml(laneRowDetail(item))}</span>` : '';
            return `<div class="launch-lane-row" data-lane-index="${index}"><div class="launch-lane-title"><span class="launch-lane-number" aria-hidden="true">${index + 1}</span><span class="launch-lane-row-text"><strong>${name}</strong>${detail}</span></div>${!item?.canRunAutomatically ? '<p class="text-warning">Unavailable for automatic runs. Remove or replace this selection.</p>' : ''}<div class="launch-run-actions"><button class="btn btn-sm btn-secondary" data-lane-move="-1"${index === 0 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} up"><i class="fa-solid fa-arrow-up" aria-hidden="true"></i> Up</button><button class="btn btn-sm btn-secondary" data-lane-move="1"${index === selections.length - 1 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} down"><i class="fa-solid fa-arrow-down" aria-hidden="true"></i> Down</button><button class="btn btn-sm btn-outline-danger" data-lane-remove>Remove</button></div></div>`;
        })
        .join('') || '<p class="launch-lane-empty">No runs yet. Add an agent, script or check below.</p>';
    const render = () => {
        picker?.destroy();
        picker = null;
        host.classList.add('launch-lane-editor');
        host.innerHTML = `<h3>Run order</h3><div class="launch-lane-list" data-lane-selection-list>${rows()}</div>
            <div class="launch-lane-add">
                <div class="launch-lane-add-heading"><label for="${pickerId}">Add to this lane</label><button type="button" class="btn btn-link btn-sm" data-lane-manage aria-expanded="false" aria-controls="${manageId}">Manage list</button></div>
                <section class="launch-lane-manage" id="${manageId}" data-lane-manage-list aria-label="Manage list" hidden></section>
                <div class="launch-lane-picker" data-lane-picker-slot></div>
                <div class="launch-lane-about" data-lane-about hidden>
                    <p class="launch-lane-about-text"><strong>What this does:</strong> <span data-lane-about-text></span> <a class="launch-lane-about-link" href="?view=dashboard" data-lane-about-link hidden></a></p>
                    <div class="launch-lane-check" data-lane-check-options hidden><label>Check scope<select class="form-select" data-lane-check-scope aria-describedby="${scopeHelpId}">${LANE_CHECK_SCOPES
        .map(([value, label]) => `<option value="${value}"${value === DEFAULT_LANE_CHECK_SCOPE ? ' selected' : ''}>${label}</option>`)
        .join('')}</select></label><p class="form-text" id="${scopeHelpId}" data-lane-check-help>${escapeHtml(scopeHelp(DEFAULT_LANE_CHECK_SCOPE))}</p>
                        <div class="launch-lane-check" data-lane-check-minimum-options hidden><label>Findings<select class="form-select" data-lane-check-minimum aria-describedby="${minimumHelpId}">${checkMinimumOptions('')}</select></label><p class="form-text" id="${minimumHelpId}">${escapeHtml(CHECK_MINIMUM_HELP)}</p></div></div>
                </div>
                <div class="launch-lane-stage"><button type="button" class="btn btn-secondary" data-lane-add disabled aria-describedby="${stagingId}">Add to run order</button><p class="form-text" id="${stagingId}">Add puts it in the run order above. Nothing runs until you save lane settings, and then only when a card enters this lane.</p></div>
            </div><div><a href="?view=ai-agents" class="btn btn-link btn-sm px-0" data-lane-manage-agents>Create an agent in AI Agents</a></div><div class="launch-lane-footer"><p class="launch-lane-unsaved" data-lane-unsaved role="status"></p><button class="btn btn-primary" data-lane-save>Save lane settings</button></div><p data-lane-error role="alert"></p>`;
        renderPicker();
        if (manageOpen) setManage(true, false);
        syncDirty();
    };
    /** The searchable picker alone, so a Manage list switch never resets the run order or its own focus. */
    const renderPicker = (keep = '') => {
        picker?.destroy();
        picker = null;
        const slot = host.querySelector('[data-lane-picker-slot]');
        const choices = laneChoices(items, selections, hidden);
        choiceItems = new Map(choices.map(item => [item.id, item]));
        if (!choices.length) {
            slot.innerHTML = '<div class="launch-lane-empty launch-lane-picker-empty" data-lane-picker-empty><p>Nothing to add: every remaining entry is hidden from this list.</p><button type="button" class="btn btn-secondary btn-sm" data-lane-manage-show>Show hidden entries</button></div>';
            choose('');
            return;
        }
        slot.innerHTML = `<select id="${pickerId}" class="form-select" data-lane-add-choice><option value="">Choose an agent, script or check…</option>${choices
            .map(item => `<option value="${escapeHtml(item.id)}"${item.id === keep ? ' selected' : ''}>${escapeHtml(laneChoiceLabel(item))}</option>`)
            .join('')}</select>`;
        const select = slot.querySelector('select');
        // Each option says what it does on a second line; the selected value stays one line (VIBE-337).
        const renderChoice = (detailed) => (data, escape) => {
            const item = choiceItems.get(data.value);
            if (!item) return `<div>${escape(data.text)}</div>`;
            const detail = detailed ? `<span class="launch-lane-choice-detail">${escape(laneChoiceDetail(item))}</span>` : '';
            return `<div class="launch-lane-choice"><span class="launch-lane-choice-icon" aria-hidden="true">${choiceLogo(item)}</span><span class="launch-lane-choice-text"><span class="launch-lane-choice-label">${escape(data.text)}</span>${detail}</span></div>`;
        };
        picker = enhanceSelectWithTomSelect(select, {
            placeholder: 'Choose an agent, script or check…',
            searchPlaceholder: 'Search agents, scripts and checks…',
            emptyMessage: 'No matching agents, scripts or checks.',
            render: { option: renderChoice(true), item: renderChoice(false) }
        });
        picker?.control.setAttribute('aria-label', 'Add to this lane');
        picker?.dropdown.classList.add('launch-lane-dropdown');
        choose(select.value);
    };
    /** Shows what the chosen entry does, its page for a check, the scope field for a new check and Findings for a new scan. */
    const choose = (value) => {
        const item = choiceItems.get(value);
        host.querySelector('[data-lane-add]').disabled = busy || !item;
        const about = host.querySelector('[data-lane-about]');
        about.hidden = !item;
        const check = item && LANE_CHECKS[item.kind];
        const link = about.querySelector('[data-lane-about-link]');
        link.hidden = !check;
        if (item) about.querySelector('[data-lane-about-text]').textContent = laneChoiceDetail(item);
        if (check) {
            link.textContent = check.link;
            link.href = `?view=${check.view}`;
            link.dataset.laneOpenView = check.view;
        }
        const builtIn = LANE_BUILT_IN_CHECKS.find(check => check.id === value);
        host.querySelector('[data-lane-check-options]').hidden = !builtIn;
        // Only Code quality can fail its step below a grade; a Rules check is always feedback (VIBE-335).
        host.querySelector('[data-lane-check-minimum-options]').hidden = builtIn?.kind !== 'code-quality';
    };
    const syncDirty = () => {
        const note = host.querySelector('[data-lane-unsaved]');
        if (note) note.textContent = dirty ? 'Unsaved changes. Save lane settings to apply them.' : '';
    };
    const renderManage = () => {
        const panel = host.querySelector('[data-lane-manage-list]');
        panel.innerHTML = `<p class="launch-lane-manage-help">Choose what <strong>Add to this lane</strong> offers on this computer. Changes save immediately. Hiding an entry does not disable it, remove it from lanes that already run it, or change Quick actions and AI Agents.</p>${preferencesFailed ? '<p class="launch-lane-manage-note">Your saved choices could not be loaded, so every entry is shown.</p>' : ''}${laneManageGroups(items)
            .map(([group, entries]) => `<fieldset><legend>${group}</legend>${entries
                .map(item => {
                    const key = laneChoiceKey(item), label = escapeHtml(laneChoiceLabel(item));
                    return `<label class="launch-lane-manage-row"><span class="launch-lane-choice-icon" aria-hidden="true">${choiceLogo(item)}</span><span class="launch-lane-choice-text"><span class="launch-lane-choice-label">${label}</span><span class="launch-lane-choice-detail">${escapeHtml(laneChoiceDetail(item))}</span></span><input type="checkbox" role="switch" class="card-editor-switch" data-lane-manage-key="${escapeHtml(key)}" data-lane-manage-name="${label}" aria-label="Show ${label} in this list"${hidden.has(key) ? '' : ' checked'}></label>`;
                })
                .join('')}</fieldset>`)
            .join('')}<p class="launch-lane-manage-status" role="status" data-lane-manage-status></p><p class="launch-lane-manage-error" role="alert" data-lane-manage-error></p><div class="launch-lane-manage-footer"><button type="button" class="btn btn-secondary btn-sm" data-lane-manage-done>Done</button></div>`;
    };
    const setManage = (open, focus = 'toggle') => {
        manageOpen = open;
        const panel = host.querySelector('[data-lane-manage-list]'),
            toggle = host.querySelector('[data-lane-manage]');
        if (open) renderManage();
        else panel.replaceChildren();
        panel.hidden = !open;
        toggle.setAttribute('aria-expanded', String(open));
        if (focus === 'toggle') toggle.focus();
        else if (focus === 'panel') panel.querySelector('input:not(:disabled)')?.focus();
    };
    const saveVisibility = async (input) => {
        const key = input.dataset.laneManageKey,
            hide = !input.checked,
            name = input.dataset.laneManageName;
        const status = host.querySelector('[data-lane-manage-status]'),
            failure = host.querySelector('[data-lane-manage-error]');
        // Disabling a focused switch drops focus; keyboard users get it back when the save ends.
        const focused = document.activeElement === input;
        input.disabled = true;
        status.textContent = 'Saving…';
        failure.textContent = '';
        try {
            // Not aborted when the panel closes: an accepted write is a saved choice.
            await api.call(LANE_PICKER_PREFERENCES, 'PUT', { key, hidden: hide });
            if (hide) hidden.add(key);
            else hidden.delete(key);
            if (!current()) return;
            status.textContent = `Saved. ${name} is ${hide ? 'hidden from' : 'shown in'} this list.`;
            renderPicker(host.querySelector('[data-lane-add-choice]')?.value || '');
        } catch (cause) {
            if (!current()) return;
            input.checked = hide; // Back to how it was: a switch is checked while its entry shows.
            status.textContent = '';
            failure.textContent = cause?.message || 'Could not save this choice. Try again.';
        } finally {
            input.disabled = false;
            if (focused && input.isConnected) input.focus({ preventScroll: true });
        }
    };
    const error = (failure) => {
        if (!current()) return;
        host.querySelector('[data-lane-error]').textContent = failure.message;
        if (!host.querySelector('[data-lane-reload]')) {
            const reload = document.createElement('button');
            reload.className = 'btn btn-outline-secondary';
            reload.textContent = 'Reload latest';
            reload.setAttribute('data-lane-reload', '');
            host.append(reload);
        }
    };
    const load = async () => {
        try {
            const [lane, catalog, preferences] = await Promise.all([
                api.call(
                    `board/columns/${encodeURIComponent(columnId)}/launches`,
                    'GET',
                    null,
                    { signal: abort.signal }
                ),
                api.catalog({ signal: abort.signal, includeAll: true }),
                // A preference failure shows every entry rather than blocking the lane.
                api.call(LANE_PICKER_PREFERENCES, 'GET', null, { signal: abort.signal }).catch(() => null)
            ]);
            if (!current()) return;
            selections = lane.selections.map((item) => ({ ...item }));
            revision = lane.revision;
            items = catalog.items;
            hidden = new Set(Array.isArray(preferences?.hiddenKeys) ? preferences.hiddenKeys : []);
            preferencesFailed = !preferences;
            dirty = false;
            render();
        } catch (failure) {
            if (current()) {
                picker?.destroy();
                picker = null;
                host.innerHTML = `<p role="alert">${escapeHtml(failure.message)}</p><button class="btn btn-outline-secondary" data-lane-reload>Reload</button>`;
            }
        }
    };
    host.addEventListener(
        'change',
        (event) => {
            if (event.target.hasAttribute('data-lane-manage-key')) {
                void saveVisibility(event.target);
                return;
            }
            if (event.target.hasAttribute('data-lane-add-choice')) {
                choose(event.target.value);
                return;
            }
            if (event.target.hasAttribute('data-lane-check-scope'))
                host.querySelector('[data-lane-check-help]').textContent = scopeHelp(event.target.value);
        },
        { signal: abort.signal }
    );
    host.addEventListener(
        'click',
        async (event) => {
            if (event.target.closest('[data-lane-manage-agents]')) {
                event.preventDefault();
                app.navigate('ai-agents');
                return;
            }
            const pageLink = event.target.closest('[data-lane-open-view]');
            if (pageLink) {
                // Guarded navigation: unsaved lane changes still ask before leaving.
                event.preventDefault();
                const view = pageLink.dataset.laneOpenView;
                app.navigate(view, {}, { resetStack: view === 'dashboard' });
                return;
            }
            const button = event.target.closest('button');
            if (!button || busy) return;
            if (button.hasAttribute('data-lane-manage')) {
                setManage(!manageOpen);
                return;
            }
            if (button.hasAttribute('data-lane-manage-show')) {
                setManage(true, 'panel');
                return;
            }
            if (button.hasAttribute('data-lane-manage-done')) {
                setManage(false);
                return;
            }
            if (button.hasAttribute('data-lane-reload')) {
                if (beforeClose(() => void load())) void load();
                return;
            }
            if (button.hasAttribute('data-lane-add')) {
                const value = host.querySelector('[data-lane-add-choice]')?.value || '';
                let item = items.find(item => item.id === value);
                const check = LANE_BUILT_IN_CHECKS.find(check => check.id === value);
                const scope = check && host.querySelector('[data-lane-check-scope]').value;
                const failGrade = check ? laneCheckGrade(check.kind, host.querySelector('[data-lane-check-minimum]').value) : '';
                // Reuse an identical check, minimum grade included, instead of adding another copy to the catalog.
                if (check) item = findLaneCheck(items, check.kind, scope, failGrade);
                if (check && item && selections.some(selection => selection.definitionId === item.id)) {
                    host.querySelector('[data-lane-error]').textContent = `This lane already runs “${item.name}”.`;
                    return;
                }
                if (check && !item) {
                    const unlock = lock();
                    try {
                        const saved = await api.save('action', null, {
                            name: laneCheckName(check.kind, scope, failGrade), kind: check.kind, enabled: true, expectedRevision: 0,
                            check: failGrade ? { scope, failGrade } : { scope }, script: null
                        });
                        if (!current()) return;
                        item = { ...saved, definitionType: 'action', canRunAutomatically: true };
                        items.push(item);
                    } catch (failure) {
                        error(failure);
                        return;
                    } finally {
                        unlock();
                    }
                }
                if (item) {
                    selections.push({
                        definitionId: item.id,
                        definitionType: item.definitionType
                    });
                    dirty = true;
                }
                render();
                (picker || host.querySelector('[data-lane-manage-show]'))?.focus();
                return;
            }
            const row = button.closest('[data-lane-index]');
            if (row) {
                dirty = true;
                const index = Number(row.dataset.laneIndex);
                let focusIndex = index;
                if (button.hasAttribute('data-lane-remove'))
                    selections.splice(index, 1);
                else {
                    focusIndex = index + Number(button.dataset.laneMove);
                    [selections[index], selections[focusIndex]] = [
                        selections[focusIndex],
                        selections[index]
                    ];
                }
                render();
                const target = host.querySelector(
                    `[data-lane-index="${Math.min(focusIndex, selections.length - 1)}"]`
                );
                (
                    target?.querySelector(
                        `button[data-lane-move="${button.dataset.laneMove}"]:not(:disabled)`
                    ) ||
                    target?.querySelector('button:not(:disabled)') ||
                    picker?.control || host.querySelector('[data-lane-manage]')
                )?.focus();
                return;
            }
            if (button.hasAttribute('data-lane-save')) {
                const unlock = lock(saveRoot);
                try {
                    // The full lane dialog also owns the name, colour and default destination.
                    // Save those before reporting success from this shared settings action.
                    if (await beforeSave?.() === false) return;
                    const saved = await api.call(
                        `board/columns/${encodeURIComponent(columnId)}/launches`,
                        'PUT',
                        { selections, revision }
                    );
                    if (current()) {
                        revision = saved.revision;
                        dirty = false;
                        syncDirty();
                        host.querySelector('[data-lane-error]').textContent =
                            '';
                        host.querySelector('[data-lane-reload]')?.remove();
                        onSaved(saved);
                        app.showToast(
                            'Board',
                            'Lane settings saved.',
                            'success'
                        );
                    }
                } catch (failure) {
                    error(failure);
                } finally {
                    unlock();
                }
            }
        },
        { signal: abort.signal }
    );
    const removeGuard = app.registerNavigationGuard?.(({ retry }) =>
        beforeClose(retry)
    );
    const dispose = () => {
        abort.abort();
        picker?.destroy();
        removeGuard?.();
    };
    dispose.beforeClose = beforeClose;
    void load();
    return dispose;

    function lock(root = host) {
        busy = true;
        const controls = [...root.querySelectorAll('input,select,button')].map(node => [node, node.disabled]);
        controls.forEach(([node]) => { node.disabled = true; });
        picker?.disable();
        return () => {
            busy = false;
            if (!current()) return;
            controls.forEach(([node, disabled]) => { node.disabled = disabled; });
            picker?.enable();
        };
    }
}

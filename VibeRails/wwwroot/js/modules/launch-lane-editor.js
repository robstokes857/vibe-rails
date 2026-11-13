import { escapeHtml, confirmDialog, enhanceSelectWithTomSelect } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { launchPresentation } from './launch-presentation.js';
import { boardBrandLogo } from './board-brand.js';

/** Ordered definitions, shared by lane settings and the lane popup. */
export function mountLaunchLane(app, host, columnId, onSaved = () => {}) {
    const api = new LaunchApi(app),
        abort = new AbortController();
    let selections = [],
        revision = 0,
        items = [],
        busy = false;
    let dirty = false,
        confirming = false;
    let picker = null;
    const builtInChecks = [
        { id: 'new:vca', name: 'VCA check', kind: 'vca' },
        { id: 'new:code-quality', name: 'Code quality check', kind: 'code-quality' }
    ];
    const labelForKind = { agent: 'Agent', script: 'Script', vca: 'VCA check', 'code-quality': 'Code quality check' };
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
    const render = () => {
        picker?.destroy();
        picker = null;
        host.classList.add('launch-lane-editor');
        const choices = items.filter(item => item.canRunAutomatically && !selections.some(selection => selection.definitionId === item.id));
        const checks = builtInChecks.filter(check => !items.some(item => item.kind === check.kind && item.canRunAutomatically));
        const choiceItems = new Map([...choices, ...checks].map(item => [item.id, item]));
        const renderChoice = (data, escape) => {
            const item = choiceItems.get(data.value);
            if (!item) return `<div>${escape(data.text)}</div>`;
            const logo = item.kind === 'vca' || item.kind === 'code-quality'
                ? boardBrandLogo(null, '') : launchPresentation(item).logoHtml;
            return `<div class="launch-lane-choice"><span class="launch-lane-choice-icon" aria-hidden="true">${logo}</span><span class="launch-lane-choice-label">${escape(data.text)}</span></div>`;
        };
        host.innerHTML = `<h3>Run order</h3><div class="launch-lane-list" data-lane-selection-list>${selections
            .map((selection, index) => {
                const item = items.find(
                    (item) =>
                        item.id === selection.definitionId &&
                        item.definitionType === selection.definitionType
                );
                return `<div class="launch-lane-row" data-lane-index="${index}"><div class="launch-lane-title"><span class="launch-lane-number" aria-hidden="true">${index + 1}</span><strong>${escapeHtml(item?.name || 'Unavailable / retired selection')}</strong></div>${!item?.canRunAutomatically ? '<p class="text-warning">Unavailable for automatic runs. Remove or replace this selection.</p>' : ''}<div class="launch-run-actions"><button class="btn btn-sm btn-secondary" data-lane-move="-1"${index === 0 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} up"><i class="fa-solid fa-arrow-up" aria-hidden="true"></i> Up</button><button class="btn btn-sm btn-secondary" data-lane-move="1"${index === selections.length - 1 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} down"><i class="fa-solid fa-arrow-down" aria-hidden="true"></i> Down</button><button class="btn btn-sm btn-outline-danger" data-lane-remove>Remove</button></div></div>`;
            })
            .join(
                ''
            ) || '<p class="launch-lane-empty">No runs yet. Add an agent, script or check below.</p>'}</div><div class="launch-lane-add"><label>Add to this lane<select class="form-select" data-lane-add-choice><option value="">Choose an agent, script or check…</option>${[...choices, ...checks]
            .map(
                (item) =>
                    `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)}${item.name === labelForKind[item.kind] ? '' : ` · ${escapeHtml(labelForKind[item.kind] || item.kind)}`}</option>`
            )
            .join(
                ''
            )}</select></label><button class="btn btn-secondary" data-lane-add disabled>Add</button></div><div><a href="?view=ai-agents" class="btn btn-link btn-sm px-0" data-lane-manage-agents>Create an agent in AI Agents</a></div><div class="launch-lane-footer"><button class="btn btn-primary" data-lane-save>Save lane settings</button></div><p data-lane-error role="alert"></p>`;
        picker = enhanceSelectWithTomSelect(host.querySelector('[data-lane-add-choice]'), {
            placeholder: 'Choose an agent, script or check…',
            searchPlaceholder: 'Search agents, scripts and checks…',
            emptyMessage: 'No matching agents, scripts or checks.',
            render: { option: renderChoice, item: renderChoice }
        });
        picker?.control.setAttribute('aria-label', 'Add to this lane');
        picker?.dropdown.classList.add('launch-lane-dropdown');
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
            const [lane, catalog] = await Promise.all([
                api.call(
                    `board/columns/${encodeURIComponent(columnId)}/launches`,
                    'GET',
                    null,
                    { signal: abort.signal }
                ),
                api.catalog({ signal: abort.signal, includeAll: true })
            ]);
            if (!current()) return;
            selections = lane.selections.map((item) => ({ ...item }));
            revision = lane.revision;
            items = catalog.items;
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
            if (event.target.hasAttribute('data-lane-add-choice')) {
                host.querySelector('[data-lane-add]').disabled = busy || !event.target.value;
                return;
            }
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
            const button = event.target.closest('button');
            if (!button || busy) return;
            if (button.hasAttribute('data-lane-reload')) {
                if (beforeClose(() => void load())) void load();
                return;
            }
            if (button.hasAttribute('data-lane-add')) {
                const value = host.querySelector('[data-lane-add-choice]').value;
                let item = items.find(item => item.id === value);
                const check = builtInChecks.find(check => check.id === value);
                if (check) {
                    const unlock = lock();
                    try {
                        const saved = await api.save('action', null, {
                            name: check.name, kind: check.kind, enabled: true, expectedRevision: 0,
                            check: { scope: check.kind === 'vca' ? 'working-tree' : 'card' }, script: null
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
                picker?.focus();
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
                    picker?.control || host.querySelector('[data-lane-add-choice]')
                )?.focus();
                return;
            }
            if (button.hasAttribute('data-lane-save')) {
                const unlock = lock();
                try {
                    const saved = await api.call(
                        `board/columns/${encodeURIComponent(columnId)}/launches`,
                        'PUT',
                        { selections, revision }
                    );
                    if (current()) {
                        revision = saved.revision;
                        dirty = false;
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

    function lock() {
        busy = true;
        const controls = [...host.querySelectorAll('input,select,button')].map(node => [node, node.disabled]);
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

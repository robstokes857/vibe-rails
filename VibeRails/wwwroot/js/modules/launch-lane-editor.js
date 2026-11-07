import { escapeHtml, confirmDialog } from './utils.js';
import { LaunchApi } from './launch-api.js';

/** Ordered definitions and explicit gates, shared by lane settings and the lane popup. */
export function mountLaunchLane(app, host, columnId, onSaved = () => {}) {
    const api = new LaunchApi(app),
        abort = new AbortController();
    let selections = [],
        revision = 0,
        items = [],
        busy = false;
    let dirty = false,
        confirming = false;
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
                message: 'Your unsaved run order and gates will be lost.',
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
        host.innerHTML = `<p class="board-editor-muted">Runs in order on lane entry. Each run waits for the preceding process and its required result.</p><div data-lane-selection-list>${selections
            .map((selection, index) => {
                const item = items.find(
                    (item) =>
                        item.id === selection.definitionId &&
                        item.definitionType === selection.definitionType
                );
                const gates = [
                    ['process', 'Process succeeds'],
                    ['result', 'Explicit result passes']
                ];
                if (selection.definitionType === 'preset')
                    gates.push(['review', 'Saved review passes']);
                return `<div class="launch-lane-row" data-lane-index="${index}"><strong>${escapeHtml(item?.name || 'Unavailable / retired selection')}</strong>${!item?.canRunAutomatically ? '<p class="text-warning">Unavailable for automatic runs. Remove or replace this selection.</p>' : ''}<label>Continue after<select class="form-select" data-lane-gate>${gates.map(([value, label]) => `<option value="${value}"${selection.gate === value ? ' selected' : ''}>${label}</option>`).join('')}</select></label><div class="launch-run-actions"><button class="btn btn-sm btn-outline-secondary" data-lane-move="-1"${index === 0 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} up">Up</button><button class="btn btn-sm btn-outline-secondary" data-lane-move="1"${index === selections.length - 1 ? ' disabled' : ''} aria-label="Move ${escapeHtml(item?.name || 'entry')} down">Down</button><button class="btn btn-sm btn-outline-danger" data-lane-remove>Remove</button></div></div>`;
            })
            .join(
                ''
            )}</div><label>Add Agent or Action<select class="form-select" data-lane-add-choice><option value="">Choose…</option>${items
            .filter(
                (item) =>
                    item.canRunAutomatically &&
                    !selections.some(
                        (selection) => selection.definitionId === item.id
                    )
            )
            .map(
                (item) =>
                    `<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} (${escapeHtml(item.kind)})</option>`
            )
            .join(
                ''
            )}</select></label><div class="launch-run-actions"><button class="btn btn-outline-secondary" data-lane-add>Add</button><button class="btn btn-primary" data-lane-save>Save lane settings</button></div><p data-lane-error role="alert"></p>`;
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
                api.catalog({ signal: abort.signal })
            ]);
            if (!current()) return;
            selections = lane.selections.map((item) => ({ ...item }));
            revision = lane.revision;
            items = catalog.items;
            render();
        } catch (failure) {
            if (current())
                host.innerHTML = `<p role="alert">${escapeHtml(failure.message)}</p><button class="btn btn-outline-secondary" data-lane-reload>Reload</button>`;
        }
    };
    host.addEventListener(
        'change',
        (event) => {
            if (!busy && event.target.hasAttribute('data-lane-gate'))
                selections[
                    Number(
                        event.target.closest('[data-lane-index]').dataset
                            .laneIndex
                    )
                ].gate = event.target.value;
        },
        { signal: abort.signal }
    );
    host.addEventListener(
        'click',
        async (event) => {
            const button = event.target.closest('button');
            if (!button || busy) return;
            if (button.hasAttribute('data-lane-reload')) {
                if (beforeClose(() => void load())) void load();
                return;
            }
            if (button.hasAttribute('data-lane-add')) {
                const item = items.find(
                    (item) =>
                        item.id ===
                        host.querySelector('[data-lane-add-choice]').value
                );
                if (item) {
                    selections.push({
                        definitionId: item.id,
                        definitionType: item.definitionType,
                        gate: 'result'
                    });
                    dirty = true;
                }
                render();
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
                    host.querySelector('[data-lane-add-choice]')
                )?.focus();
                return;
            }
            if (button.hasAttribute('data-lane-save')) {
                busy = true;
                const controls = [
                    ...host.querySelectorAll('input,select,button')
                ].map((node) => [node, node.disabled]);
                controls.forEach(([node]) => {
                    node.disabled = true;
                });
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
                    busy = false;
                    if (current())
                        controls.forEach(([node, disabled]) => {
                            node.disabled = disabled;
                        });
                }
            }
        },
        { signal: abort.signal }
    );
    host.addEventListener(
        'change',
        () => {
            if (!busy) dirty = true;
        },
        { signal: abort.signal }
    );
    const removeGuard = app.registerNavigationGuard?.(({ retry }) =>
        beforeClose(retry)
    );
    const dispose = () => {
        abort.abort();
        removeGuard?.();
    };
    dispose.beforeClose = beforeClose;
    void load();
    return dispose;
}

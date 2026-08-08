import { buildLlmSelectionValue, populateLlmSelectionItemsSelect } from '../utils.js';

/**
 * Worker picker facade for the Automation editor.
 *
 * Workers are environments flagged `automationWorker` (created from the
 * Automation editor). This picker sources them from `app.data.environments`,
 * NOT from the LLM-picker preferences catalog — workers are excluded there so
 * they never surface in launch pickers. `hidden` has no effect here: every
 * Worker is always listed. There is no "Customize LLM list" footer because
 * this is not a launch picker.
 *
 * The caller owns refresh: re-mount after an environments fetch (the select is
 * safe to re-mount in place). Returns a disposer.
 */
export function mountWorkerPicker(app, selectEl, options = {}) {
    if (!selectEl) return () => {};

    const {
        selectedValue = '',
        selectedFallback = null,
        searchPlaceholder = 'Search Workers...'
    } = options;

    const environments = Array.isArray(app.data.environments) ? app.data.environments : [];
    const items = environments
        .filter((environment) => environment?.automationWorker)
        .map((environment) => workerOptionItem(environment))
        .filter(Boolean);

    const normalizedSelectedValue = String(selectedValue || '');
    if (normalizedSelectedValue && !items.some((item) => item.value === normalizedSelectedValue)) {
        // Automations created before the Worker flag existed can point at a regular
        // custom environment. Resolve it by id so the selection keeps its real name;
        // fall back to the caller's item (e.g. "… (missing)") for a deleted one.
        const selectedId = Number.parseInt(normalizedSelectedValue.split(':')[1], 10);
        const existing = environments.find((environment) =>
            Number.parseInt(environment?.id, 10) === selectedId);
        const resolved = existing ? workerOptionItem(existing) : null;
        if (resolved) {
            items.push(resolved);
        } else if (selectedFallback && String(selectedFallback.value || '') === normalizedSelectedValue) {
            items.push({
                group: null,
                value: normalizedSelectedValue,
                label: selectedFallback.label || 'Missing Worker',
                cli: selectedFallback.cli || '',
                environmentId: selectedFallback.environmentId ?? null,
                environmentName: selectedFallback.environmentName || null,
                kind: 'environment'
            });
        }
    }

    const placeholder = options.placeholder
        || (items.length > 0 ? 'Select a Worker...' : 'Add a Worker to continue...');

    populateLlmSelectionItemsSelect(selectEl, items, {
        placeholder,
        selectedValue: normalizedSelectedValue,
        searchable: true,
        searchPlaceholder,
        emptyMessage: 'No Workers yet.'
    });

    return () => {
        if (selectEl.tomselect && typeof selectEl.tomselect.destroy === 'function') {
            try { selectEl.tomselect.destroy(); } catch { /* already detached */ }
        }
    };
}

function workerOptionItem(environment) {
    const cli = String(environment?.cli || '').toLowerCase();
    const environmentId = Number.parseInt(environment?.id, 10);
    if (!cli || !Number.isFinite(environmentId)) return null;
    const environmentName = (environment?.name || '').toString().trim();
    return {
        group: null,
        value: buildLlmSelectionValue(cli, environmentId),
        label: `${environmentName || `Worker ${environmentId}`} (${cli})`,
        cli,
        environmentId,
        environmentName: environmentName || null,
        kind: 'environment'
    };
}

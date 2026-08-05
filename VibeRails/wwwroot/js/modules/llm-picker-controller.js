import {
    BASE_LLM_CHOICES,
    SHELL_LLM_CHOICE,
    escapeHtml,
    getCliBrand,
    populateLlmSelectionItemsSelect
} from './utils.js';

const PREFERENCES_ENDPOINT = '/api/v1/llm-picker/preferences';
const BASE_GROUP = 'Base CLIs';
const ENVIRONMENT_GROUP = 'Custom Environments';
const CUSTOMIZABLE_CONTEXTS = new Set(['terminal', 'sandbox', 'automation', 'multi-run']);

const CONTEXT_MATCHERS = Object.freeze({
    terminal: () => true,
    sandbox: (item) => item.kind === 'environment' || (item.kind === 'base' && item.cli !== 'shell'),
    automation: (item) => item.kind === 'environment',
    'multi-run': (item) => item.kind === 'base' && item.cli !== 'shell',
    'environment-provider': (item) => item.kind === 'base' && item.cli !== 'shell'
});

/**
 * Owns the resolved launch-picker catalog, mounted Tom Select instances, and
 * the global customization modal. utils.js remains the low-level renderer.
 */
export class LlmPickerController {
    constructor(app) {
        this.app = app;
        this.catalog = null;
        this.usingFallbackCatalog = false;
        this.loadPromise = null;
        this.lastEnvironmentsFingerprint = null;
        this.mountedPickers = new Set();
        this.pickerBySelect = new WeakMap();
        this.modalState = null;
    }

    async loadPreferences({ force = false, refreshMounted = true } = {}) {
        if (!force && this.catalog) return this.catalog;
        if (!force && this.loadPromise) return this.loadPromise;

        const promise = this.app.apiCall(PREFERENCES_ENDPOINT, 'GET', null, { showLoading: false })
            .then((response) => {
                this.catalog = this._normalizeCatalog(response?.items);
                this.usingFallbackCatalog = false;
                if (refreshMounted) this.refreshAll();
                return this.catalog;
            })
            .catch((error) => {
                console.error('Failed to load LLM picker preferences; using defaults.', error);
                if (!this.catalog || force) {
                    this.catalog = this._buildFallbackCatalog();
                    this.usingFallbackCatalog = true;
                }
                if (refreshMounted) this.refreshAll();
                return this.catalog;
            })
            .finally(() => {
                if (this.loadPromise === promise) this.loadPromise = null;
            });

        this.loadPromise = promise;
        return promise;
    }

    /**
     * Re-resolve the catalog after an environments fetch, but only when the
     * picker-relevant environment data actually changed. Environments are
     * re-fetched on every poll/refresh; without this gate each one would cost a
     * redundant preferences request plus a Tom Select re-render of every
     * mounted picker.
     */
    async refreshFromEnvironments(environments) {
        const fingerprint = (environments || [])
            .map((environment) => [
                environment?.id,
                String(environment?.cli || '').toLowerCase(),
                environment?.name || '',
                environment?.hidden ? 1 : 0
            ].join(':'))
            .join('|');
        if (fingerprint === this.lastEnvironmentsFingerprint
            && this.catalog
            && !this.usingFallbackCatalog) {
            return this.catalog;
        }
        this.lastEnvironmentsFingerprint = fingerprint;
        return this.loadPreferences({ force: true });
    }

    mount(selectEl, configuration = {}) {
        if (!selectEl) return () => {};

        this.pickerBySelect.get(selectEl)?.dispose?.();
        const context = configuration.context || 'terminal';
        if (!CONTEXT_MATCHERS[context]) {
            throw new Error(`Unknown LLM picker context: ${context}`);
        }

        const record = {
            selectEl,
            configuration: {
                placeholder: 'Select LLM...',
                searchable: true,
                searchPlaceholder: context === 'automation'
                    ? 'Search Environments...'
                    : 'Search LLMs...',
                includeGroups: context !== 'multi-run' && context !== 'environment-provider',
                includeDefaultSuffix: context === 'terminal',
                plainCliValues: context === 'environment-provider',
                selectedValue: '',
                selectedFallback: null,
                ...configuration,
                context
            },
            disposed: false,
            everConnected: selectEl.isConnected,
            renderedSelectedValue: '',
            preservedSelectionValue: null,
            changeHandler: null,
            dispose: null
        };

        record.dispose = () => {
            if (record.disposed) return;
            record.disposed = true;
            this.mountedPickers.delete(record);
            if (this.pickerBySelect.get(selectEl) === record) {
                this.pickerBySelect.delete(selectEl);
            }
            if (record.changeHandler) selectEl.removeEventListener('change', record.changeHandler);
            if (selectEl.tomselect && typeof selectEl.tomselect.destroy === 'function') {
                try { selectEl.tomselect.destroy(); } catch { /* already detached */ }
            }
        };

        this.mountedPickers.add(record);
        this.pickerBySelect.set(selectEl, record);
        record.changeHandler = () => {
            const value = String(selectEl.tomselect?.getValue?.() || selectEl.value || '');
            if (record.preservedSelectionValue && value !== record.preservedSelectionValue) {
                record.configuration.selectedValue = value;
                requestAnimationFrame(() => {
                    if (!record.disposed) this._renderPicker(record, { useConfiguredSelection: true });
                });
            }
        };
        selectEl.addEventListener('change', record.changeHandler);
        this._renderPicker(record, { useConfiguredSelection: true });
        return record.dispose;
    }

    setValue(selectEl, value) {
        const record = this.pickerBySelect.get(selectEl);
        const normalizedValue = String(value || '');
        if (!record || record.disposed) {
            selectEl?.tomselect?.setValue?.(normalizedValue, true);
            if (selectEl && !selectEl.tomselect) selectEl.value = normalizedValue;
            return;
        }

        const currentValue = String(selectEl.tomselect?.getValue?.() || selectEl.value || '');
        if (currentValue === normalizedValue && record.renderedSelectedValue === normalizedValue) return;
        record.configuration.selectedValue = normalizedValue;
        this._renderPicker(record, { useConfiguredSelection: true });
    }

    refreshAll() {
        for (const record of Array.from(this.mountedPickers)) {
            if (record.disposed) {
                this.mountedPickers.delete(record);
                continue;
            }

            if (record.everConnected && !record.selectEl.isConnected) {
                record.dispose();
                continue;
            }

            record.everConnected ||= record.selectEl.isConnected;
            this._renderPicker(record);
        }
    }

    getEnabledItems(context) {
        return this._contextCatalog(context)
            .filter((item) => context === 'environment-provider' || item.enabled)
            .map((item) => ({ ...item }));
    }

    _renderPicker(record, { useConfiguredSelection = false } = {}) {
        const { selectEl, configuration } = record;
        const previousTomSelect = selectEl.tomselect;
        const selectedValue = useConfiguredSelection
            ? String(configuration.selectedValue || '')
            : String(previousTomSelect?.getValue?.() || selectEl.value || '');
        const searchValue = previousTomSelect?.control_input?.value || '';
        const wasOpen = Boolean(previousTomSelect?.isOpen);
        const optionItems = this._buildOptionItems(configuration, selectedValue);
        const selectedCatalogItem = this._contextCatalog(configuration.context)
            .find((item) => this._selectionValue(item, configuration.plainCliValues) === selectedValue);
        record.preservedSelectionValue = selectedValue && (
            (selectedCatalogItem && !selectedCatalogItem.enabled && configuration.context !== 'environment-provider')
            || (!selectedCatalogItem && optionItems.some((item) => item.value === selectedValue))
        ) ? selectedValue : null;

        populateLlmSelectionItemsSelect(selectEl, optionItems, {
            placeholder: configuration.placeholder,
            selectedValue,
            searchable: configuration.searchable,
            searchPlaceholder: configuration.searchPlaceholder,
            emptyMessage: CUSTOMIZABLE_CONTEXTS.has(configuration.context)
                ? 'No launch targets are currently visible.'
                : 'No matching LLMs.',
            onCustomize: CUSTOMIZABLE_CONTEXTS.has(configuration.context)
                ? ({ triggerElement }) => this.openCustomizationModal({
                    originSelect: selectEl,
                    triggerElement
                })
                : null
        });

        const nextTomSelect = selectEl.tomselect;
        if (nextTomSelect && searchValue) {
            nextTomSelect.setTextboxValue?.(searchValue);
            nextTomSelect.refreshOptions?.(false);
        }
        if (nextTomSelect && wasOpen) {
            requestAnimationFrame(() => {
                if (selectEl.isConnected && selectEl.tomselect === nextTomSelect) {
                    nextTomSelect.open();
                }
            });
        }
        record.renderedSelectedValue = selectedValue;
    }

    _buildOptionItems(configuration, selectedValue) {
        const { context, includeGroups, includeDefaultSuffix, plainCliValues } = configuration;
        const allContextItems = this._contextCatalog(context);
        const visibleItems = allContextItems.filter((item) =>
            context === 'environment-provider'
            || item.enabled
            || this._selectionValue(item, plainCliValues) === selectedValue
        );
        const optionItems = visibleItems.map((item) => {
            const value = this._selectionValue(item, plainCliValues);
            let label = item.label;
            if (item.kind === 'base' && includeDefaultSuffix && item.cli !== 'shell') {
                label = `${label} (default)`;
            }
            if (!item.enabled && value === selectedValue && context !== 'environment-provider') {
                label = `${label} (hidden)`;
            }
            return {
                group: includeGroups ? item.group : null,
                value,
                label,
                cli: item.cli,
                environmentId: item.environmentId,
                environmentName: item.kind === 'environment'
                    ? item.label.replace(/ \([^)]*\)$/, '')
                    : null,
                kind: item.kind
            };
        });

        const alreadyIncluded = optionItems.some((item) => item.value === selectedValue);
        const fallback = typeof configuration.selectedFallback === 'function'
            ? configuration.selectedFallback(selectedValue)
            : configuration.selectedFallback;
        if (selectedValue && !alreadyIncluded && fallback?.value === selectedValue) {
            optionItems.push({
                group: includeGroups ? (fallback.group || ENVIRONMENT_GROUP) : null,
                value: fallback.value,
                label: fallback.label || 'Missing Environment',
                cli: fallback.cli || '',
                environmentId: fallback.environmentId ?? null,
                environmentName: fallback.environmentName || null,
                kind: fallback.kind || 'environment'
            });
        }

        return optionItems;
    }

    _selectionValue(item, plainCliValues) {
        return plainCliValues ? item.cli : item.key;
    }

    _contextCatalog(context) {
        const matcher = CONTEXT_MATCHERS[context];
        if (!matcher) return [];
        const catalog = this.usingFallbackCatalog
            ? this._buildFallbackCatalog()
            : (this.catalog || this._buildFallbackCatalog());
        return catalog.filter(matcher);
    }

    _normalizeCatalog(items) {
        if (!Array.isArray(items)) {
            throw new Error('The LLM picker preference response did not contain an item list.');
        }

        return items
            .map((item) => ({
                key: String(item?.key || ''),
                kind: item?.kind === 'environment' ? 'environment' : 'base',
                group: String(item?.group || ''),
                label: String(item?.label || ''),
                cli: String(item?.cli || '').toLowerCase(),
                environmentId: item?.environmentId == null
                    ? null
                    : Number.parseInt(item.environmentId, 10),
                enabled: Boolean(item?.enabled),
                order: Number.parseInt(item?.order, 10) || 0
            }))
            .filter((item) => item.key && item.label && item.cli)
            .sort((left, right) => {
                const groupDifference = this._groupRank(left.group) - this._groupRank(right.group);
                return groupDifference || left.order - right.order;
            });
    }

    _buildFallbackCatalog() {
        const baseItems = [...BASE_LLM_CHOICES, SHELL_LLM_CHOICE].map((item, order) => ({
            key: `base:${item.cli}`,
            kind: 'base',
            group: BASE_GROUP,
            label: item.label,
            cli: item.cli,
            environmentId: null,
            enabled: true,
            order
        }));
        const environmentItems = (this.app.data.environments || [])
            .map((environment, order) => {
                const cli = String(environment?.cli || '').toLowerCase();
                const environmentId = Number.parseInt(environment?.id, 10);
                if (!cli || !Number.isFinite(environmentId)) return null;
                return {
                    key: `env:${environmentId}:${cli}`,
                    kind: 'environment',
                    group: ENVIRONMENT_GROUP,
                    label: `${environment.name || `Env ${environmentId}`} (${cli})`,
                    cli,
                    environmentId,
                    enabled: !environment.hidden,
                    order
                };
            })
            .filter(Boolean);
        return [...baseItems, ...environmentItems];
    }

    _groupRank(group) {
        return group === BASE_GROUP ? 0 : 1;
    }

    openCustomizationModal({ originSelect = null, triggerElement = null } = {}) {
        this._closeCustomizationModal({ restoreFocus: false });
        const host = document.getElementById('modal-container');
        if (!host) return;

        const items = (this.usingFallbackCatalog
            ? this._buildFallbackCatalog()
            : (this.catalog || this._buildFallbackCatalog()))
            .map((item) => ({ ...item }));
        const layer = document.createElement('div');
        layer.className = 'llm-picker-modal-layer';
        layer.innerHTML = `
            <div class="modal fade show d-block llm-picker-customization-modal" tabindex="-1"
                 role="dialog" aria-modal="true" aria-labelledby="llm-picker-modal-title">
                <div class="modal-dialog modal-lg modal-dialog-scrollable">
                    <div class="modal-content">
                        <div class="modal-header">
                            <div>
                                <h5 class="modal-title" id="llm-picker-modal-title">Customize LLM list</h5>
                                <p class="text-muted small mb-0">Choose what appears in launch pickers and arrange each section.</p>
                            </div>
                            <button type="button" class="btn-close" data-llm-picker-action="cancel"
                                    aria-label="Close LLM list customization"></button>
                        </div>
                        <form data-llm-picker-form>
                            <div class="modal-body">
                                <div class="llm-picker-modal-section">
                                    <h6>Base CLIs</h6>
                                    <div data-llm-picker-group="${BASE_GROUP}"></div>
                                </div>
                                <div class="llm-picker-modal-section mt-4">
                                    <h6>Custom Environments</h6>
                                    <div data-llm-picker-group="${ENVIRONMENT_GROUP}"></div>
                                </div>
                                <div class="alert alert-danger mt-3 mb-0 d-none" role="alert"
                                     data-llm-picker-error></div>
                            </div>
                            <div class="modal-footer llm-picker-modal-actions">
                                <button type="button" class="btn btn-outline-secondary me-auto"
                                        data-llm-picker-action="reset">Reset to defaults</button>
                                <button type="button" class="btn btn-secondary"
                                        data-llm-picker-action="cancel">Cancel</button>
                                <button type="submit" class="btn btn-primary"
                                        data-llm-picker-action="save">Save</button>
                            </div>
                        </form>
                    </div>
                </div>
            </div>
            <div class="modal-backdrop fade show llm-picker-customization-backdrop"></div>`;

        const underlying = Array.from(host.children).map((element) => ({
            element,
            inert: Boolean(element.inert),
            ariaHidden: element.getAttribute('aria-hidden')
        }));
        underlying.forEach(({ element }) => {
            element.inert = true;
            element.setAttribute('aria-hidden', 'true');
        });
        host.appendChild(layer);

        const state = {
            layer,
            host,
            items,
            dirty: false,
            pending: false,
            draggingKey: null,
            originSelect,
            triggerElement,
            underlying,
            observer: null,
            keydownHandler: null,
            disposed: false
        };
        this.modalState = state;
        this._renderModalGroups();
        this._bindModalActions();

        state.observer = new MutationObserver(() => {
            if (!layer.isConnected) this._disposeModalState(state, { restoreFocus: true });
        });
        state.observer.observe(host, { childList: true });

        requestAnimationFrame(() => layer.querySelector('.llm-picker-customization-modal')?.focus());
    }

    _renderModalGroups(focusTarget = null) {
        const state = this.modalState;
        if (!state || state.disposed) return;

        for (const group of [BASE_GROUP, ENVIRONMENT_GROUP]) {
            const container = state.layer.querySelector(`[data-llm-picker-group="${group}"]`);
            if (!container) continue;
            const groupItems = state.items.filter((item) => item.group === group);
            container.innerHTML = groupItems.length === 0
                ? '<p class="llm-picker-group-empty text-muted small mb-0">No custom Environments yet.</p>'
                : groupItems.map((item, index) => this._renderModalRow(item, index, groupItems.length)).join('');
        }

        this._bindModalRows();
        if (focusTarget) {
            requestAnimationFrame(() => state.layer
                .querySelector(`[data-llm-picker-key="${CSS.escape(focusTarget.key)}"] [data-llm-picker-move="${focusTarget.direction}"]`)
                ?.focus());
        }
    }

    _renderModalRow(item, index, count) {
        const brand = getCliBrand(item.cli);
        const logoStyle = brand.logoFilter
            ? ` style="filter: ${escapeHtml(brand.logoFilter)};"`
            : '';
        const logo = brand.logo
            ? `<img class="llm-picker-row-logo" src="${escapeHtml(brand.logo)}" alt=""${logoStyle}>`
            : '<span class="llm-picker-row-logo llm-picker-row-logo-fallback"><i class="fa-solid fa-terminal" aria-hidden="true"></i></span>';
        const key = escapeHtml(item.key);
        const label = escapeHtml(item.label);
        return `
            <div class="llm-picker-modal-row${item.enabled ? '' : ' is-hidden'}"
                 data-llm-picker-key="${key}" data-llm-picker-group-name="${escapeHtml(item.group)}">
                <span class="llm-picker-drag-handle" draggable="true" role="button" tabindex="0"
                      aria-label="Drag ${label} to reorder" title="Drag to reorder">
                    <i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>
                </span>
                ${logo}
                <span class="llm-picker-row-label">${label}</span>
                <div class="form-check form-switch llm-picker-visibility-switch">
                    <input class="form-check-input" type="checkbox" role="switch"
                           id="llm-picker-visible-${escapeHtml(item.key.replace(/[^a-zA-Z0-9_-]/g, '-'))}"
                           data-llm-picker-enabled ${item.enabled ? 'checked' : ''}
                           aria-label="Show ${label} in launch pickers">
                </div>
                <div class="llm-picker-move-buttons" role="group" aria-label="Move ${label}">
                    <button type="button" class="btn btn-sm btn-outline-secondary"
                            data-llm-picker-move="up" aria-label="Move ${label} up" ${index === 0 ? 'disabled' : ''}>
                        <i class="fa-solid fa-arrow-up" aria-hidden="true"></i>
                    </button>
                    <button type="button" class="btn btn-sm btn-outline-secondary"
                            data-llm-picker-move="down" aria-label="Move ${label} down" ${index === count - 1 ? 'disabled' : ''}>
                        <i class="fa-solid fa-arrow-down" aria-hidden="true"></i>
                    </button>
                </div>
            </div>`;
    }

    _bindModalActions() {
        const state = this.modalState;
        if (!state) return;
        state.layer.querySelectorAll('[data-llm-picker-action="cancel"]')
            .forEach((button) => button.addEventListener('click', () => this._closeCustomizationModal()));
        state.layer.querySelector('[data-llm-picker-action="reset"]')
            ?.addEventListener('click', () => void this._resetPreferences());
        state.layer.querySelector('[data-llm-picker-form]')
            ?.addEventListener('submit', (event) => {
                event.preventDefault();
                void this._savePreferences();
            });

        state.keydownHandler = (event) => {
            if (this.modalState !== state) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                this._closeCustomizationModal();
                return;
            }
            if (event.key === 'Tab') this._trapModalFocus(event, state.layer);
        };
        document.addEventListener('keydown', state.keydownHandler, true);
    }

    _bindModalRows() {
        const state = this.modalState;
        if (!state) return;
        state.layer.querySelectorAll('.llm-picker-modal-row').forEach((row) => {
            const key = row.dataset.llmPickerKey;
            row.querySelector('[data-llm-picker-enabled]')?.addEventListener('change', (event) => {
                const item = state.items.find((candidate) => candidate.key === key);
                if (!item) return;
                item.enabled = Boolean(event.target.checked);
                row.classList.toggle('is-hidden', !item.enabled);
                state.dirty = true;
            });
            row.querySelectorAll('[data-llm-picker-move]').forEach((button) => {
                button.addEventListener('click', () => {
                    this._moveModalItem(key, button.dataset.llmPickerMove);
                });
            });

            const handle = row.querySelector('.llm-picker-drag-handle');
            handle?.addEventListener('keydown', (event) => {
                if (event.key !== 'ArrowUp' && event.key !== 'ArrowDown') return;
                event.preventDefault();
                this._moveModalItem(key, event.key === 'ArrowUp' ? 'up' : 'down');
            });
            handle?.addEventListener('dragstart', (event) => {
                state.draggingKey = key;
                row.classList.add('is-dragging');
                event.dataTransfer.effectAllowed = 'move';
                event.dataTransfer.setData('text/plain', key);
            });
            handle?.addEventListener('dragend', () => {
                state.draggingKey = null;
                state.layer.querySelectorAll('.is-dragging, .is-drag-target')
                    .forEach((element) => element.classList.remove('is-dragging', 'is-drag-target'));
            });
            row.addEventListener('dragover', (event) => {
                const source = state.items.find((item) => item.key === state.draggingKey);
                const target = state.items.find((item) => item.key === key);
                if (!source || !target || source.group !== target.group || source.key === target.key) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = 'move';
                row.classList.add('is-drag-target');
            });
            row.addEventListener('dragleave', () => row.classList.remove('is-drag-target'));
            row.addEventListener('drop', (event) => {
                event.preventDefault();
                row.classList.remove('is-drag-target');
                const sourceKey = state.draggingKey || event.dataTransfer.getData('text/plain');
                const after = event.clientY > row.getBoundingClientRect().top + row.offsetHeight / 2;
                this._dropModalItem(sourceKey, key, after);
            });
        });
    }

    _moveModalItem(key, direction) {
        const state = this.modalState;
        const item = state?.items.find((candidate) => candidate.key === key);
        if (!state || !item) return;
        const groupItems = state.items.filter((candidate) => candidate.group === item.group);
        const currentIndex = groupItems.findIndex((candidate) => candidate.key === key);
        const targetIndex = direction === 'up' ? currentIndex - 1 : currentIndex + 1;
        if (targetIndex < 0 || targetIndex >= groupItems.length) return;
        const target = groupItems[targetIndex];
        const itemIndex = state.items.findIndex((candidate) => candidate.key === item.key);
        const swapIndex = state.items.findIndex((candidate) => candidate.key === target.key);
        [state.items[itemIndex], state.items[swapIndex]] = [state.items[swapIndex], state.items[itemIndex]];
        state.dirty = true;
        this._renderModalGroups({ key, direction });
    }

    _dropModalItem(sourceKey, targetKey, after) {
        const state = this.modalState;
        if (!state || !sourceKey || sourceKey === targetKey) return;
        const source = state.items.find((item) => item.key === sourceKey);
        const target = state.items.find((item) => item.key === targetKey);
        if (!source || !target || source.group !== target.group) return;

        const sourceIndex = state.items.findIndex((item) => item.key === sourceKey);
        state.items.splice(sourceIndex, 1);
        const targetIndex = state.items.findIndex((item) => item.key === targetKey);
        state.items.splice(targetIndex + (after ? 1 : 0), 0, source);
        state.dirty = true;
        this._renderModalGroups();
    }

    async _savePreferences() {
        const state = this.modalState;
        if (!state || state.pending) return;
        this._setModalPending(true);
        this._showModalError('');
        try {
            const response = await this.app.apiCall(
                PREFERENCES_ENDPOINT,
                'PUT',
                { items: this._snapshotModalItems() },
                { showLoading: false });
            this.catalog = this._normalizeCatalog(response?.items);
            this.usingFallbackCatalog = false;
            this.refreshAll();
            this._closeCustomizationModal();
            this.app.showToast('LLM list updated', 'Launch picker preferences were saved.', 'success');
        } catch (error) {
            this._showModalError(error?.message || 'Could not save LLM picker preferences.');
            this._setModalPending(false);
        }
    }

    async _resetPreferences() {
        const state = this.modalState;
        if (!state || state.pending) return;
        if (state.dirty && !window.confirm('Discard these unsaved changes and reset the LLM list to defaults?')) {
            return;
        }
        this._setModalPending(true);
        this._showModalError('');
        try {
            const response = await this.app.apiCall(
                PREFERENCES_ENDPOINT,
                'DELETE',
                null,
                { showLoading: false });
            this.catalog = this._normalizeCatalog(response?.items);
            this.usingFallbackCatalog = false;
            this.refreshAll();
            this._closeCustomizationModal();
            this.app.showToast('LLM list reset', 'All launch targets are visible in the default order.', 'success');
        } catch (error) {
            this._showModalError(error?.message || 'Could not reset LLM picker preferences.');
            this._setModalPending(false);
        }
    }

    _snapshotModalItems() {
        const state = this.modalState;
        if (!state) return [];
        const groupOrder = new Map([[BASE_GROUP, 0], [ENVIRONMENT_GROUP, 0]]);
        return state.items.map((item) => {
            const order = groupOrder.get(item.group) ?? 0;
            groupOrder.set(item.group, order + 1);
            return {
                key: item.key,
                kind: item.kind,
                group: item.group,
                label: item.label,
                cli: item.cli,
                environmentId: item.environmentId,
                enabled: item.enabled,
                order
            };
        });
    }

    _setModalPending(pending) {
        const state = this.modalState;
        if (!state) return;
        state.pending = pending;
        state.layer.querySelectorAll('button, input').forEach((control) => {
            control.disabled = pending;
        });
        state.layer.querySelectorAll('.llm-picker-drag-handle').forEach((handle) => {
            handle.draggable = !pending;
            handle.setAttribute('aria-disabled', String(pending));
        });
        const save = state.layer.querySelector('[data-llm-picker-action="save"]');
        if (save) save.textContent = pending ? 'Saving…' : 'Save';
        if (!pending) this._renderModalGroups();
    }

    _showModalError(message) {
        const error = this.modalState?.layer.querySelector('[data-llm-picker-error]');
        if (!error) return;
        error.textContent = message;
        error.classList.toggle('d-none', !message);
    }

    _closeCustomizationModal({ restoreFocus = true } = {}) {
        const state = this.modalState;
        if (!state) return;
        state.layer.remove();
        this._disposeModalState(state, { restoreFocus });
    }

    _disposeModalState(state, { restoreFocus }) {
        if (!state || state.disposed) return;
        state.disposed = true;
        state.observer?.disconnect();
        if (state.keydownHandler) document.removeEventListener('keydown', state.keydownHandler, true);
        state.underlying.forEach(({ element, inert, ariaHidden }) => {
            if (!element.isConnected) return;
            element.inert = inert;
            if (ariaHidden == null) element.removeAttribute('aria-hidden');
            else element.setAttribute('aria-hidden', ariaHidden);
        });
        if (this.modalState === state) this.modalState = null;

        if (restoreFocus) {
            requestAnimationFrame(() => {
                const tomSelect = state.originSelect?.tomselect;
                if (tomSelect) {
                    tomSelect.close();
                    // dropdown_input moves Tom Select's text input into the body-level
                    // dropdown. Its public focus() therefore targets a hidden input and
                    // schedules an open-on-focus pass after this modal closes. Focus the
                    // visible focus node under Tom Select's guard, then synchronize the
                    // instance state without reopening the menu.
                    // ignoreFocus/isFocused are Tom Select PRIVATE state, pinned to the
                    // vendored v2.4.3 bundle — see the note on mountLlmPickerFooter in
                    // utils.js before upgrading it.
                    const focusNode = tomSelect.focus_node || tomSelect.control;
                    tomSelect.ignoreFocus = true;
                    focusNode?.focus?.({ preventScroll: true });
                    tomSelect.close();
                    // Tom Select may already have queued an open-on-focus callback from
                    // the footer interaction. Run once after that queue drains, while the
                    // guard is still held, so the visible combobox wins final focus.
                    setTimeout(() => {
                        focusNode?.focus?.({ preventScroll: true });
                        tomSelect.close();
                        tomSelect.ignoreFocus = false;
                        tomSelect.isFocused = true;
                        tomSelect.refreshState?.();
                    }, 0);
                    return;
                }
                const target = (state.originSelect?.isConnected ? state.originSelect : null)
                    || (state.triggerElement?.isConnected ? state.triggerElement : null);
                target?.focus?.();
            });
        }
    }

    _trapModalFocus(event, layer) {
        const focusable = Array.from(layer.querySelectorAll(
            'button:not([disabled]), input:not([disabled]), [tabindex]:not([tabindex="-1"])'))
            .filter((element) => !element.closest('[inert]'));
        if (focusable.length === 0) return;
        const first = focusable[0];
        const last = focusable[focusable.length - 1];
        if (event.shiftKey && document.activeElement === first) {
            event.preventDefault();
            last.focus();
        } else if (!event.shiftKey && document.activeElement === last) {
            event.preventDefault();
            first.focus();
        }
    }
}

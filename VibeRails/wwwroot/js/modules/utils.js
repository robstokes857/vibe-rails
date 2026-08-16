export function getLlmName(llmEnum) {
    const names = {
        0: 'Unknown',
        1: 'Codex',
        2: 'Claude',
        3: 'Antigravity',
        4: 'Copilot',
        6: 'OpenCode',
        7: 'GLM 5.2',
        8: 'Grok 4.6'
    };
    return names[llmEnum] || 'Unknown';
}

export function getProjectNameFromPath(path) {
    if (!path) return 'Unknown Project';
    // Handle both Windows and Unix paths
    const parts = path.replace(/\\/g, '/').split('/').filter(p => p);
    return parts[parts.length - 1] || 'Unknown Project';
}

export function formatDuration(totalSeconds) {
    if (totalSeconds < 60) return `${totalSeconds}s`;
    const hours = Math.floor(totalSeconds / 3600);
    const minutes = Math.floor((totalSeconds % 3600) / 60);
    if (hours > 0) return minutes > 0 ? `${hours}h ${minutes}m` : `${hours}h`;
    return `${minutes}m`;
}

export function formatRelativeTime(dateString) {
    if (!dateString) return '';
    const date = new Date(dateString);
    const now = new Date();
    const diffMs = now - date;
    const diffMins = Math.floor(diffMs / 60000);
    const diffHours = Math.floor(diffMs / 3600000);
    const diffDays = Math.floor(diffMs / 86400000);

    if (diffMins < 1) return 'Just now';
    if (diffMins < 60) return `${diffMins} minute${diffMins !== 1 ? 's' : ''} ago`;
    if (diffHours < 24) return `${diffHours} hour${diffHours !== 1 ? 's' : ''} ago`;
    if (diffDays < 7) return `${diffDays} day${diffDays !== 1 ? 's' : ''} ago`;
    return date.toLocaleDateString();
}

export function getCliBrand(cli) {
    const key = (cli || '').toLowerCase();

    // Helper to get asset path (works in both browser and VS Code webview)
    const getAssetPath = (relativePath) => {
        if (window.__viberails_ASSETS_BASE__) {
            return `${window.__viberails_ASSETS_BASE__}/${relativePath}`;
        }
        return relativePath;
    };

    const brands = {
        codex: {
            label: 'Codex',
            logo: getAssetPath('assets/img/openai.svg'),
            className: 'badge-cli-codex',
            accentColor: '#70a597',
            logoFilter: 'brightness(0) invert(1)'
        },
        chatgpt: {
            label: 'ChatGPT',
            logo: getAssetPath('assets/img/openai.svg'),
            className: 'badge-cli-codex',
            accentColor: '#70a597',
            logoFilter: 'brightness(0) invert(1)'
        },
        openai: {
            label: 'OpenAI',
            logo: getAssetPath('assets/img/openai.svg'),
            className: 'badge-cli-codex',
            accentColor: '#70a597',
            logoFilter: 'brightness(0) invert(1)'
        },
        claude: {
            label: 'Claude',
            logo: getAssetPath('assets/img/claude-color.svg'),
            className: 'badge-cli-claude',
            accentColor: '#c47055'
        },
        antigravity: {
            label: 'Antigravity',
            logo: getAssetPath('assets/img/agy.png'),
            className: 'badge-cli-antigravity',
            accentColor: '#4d9de0'
        },
        copilot: {
            label: 'Copilot',
            logo: getAssetPath('assets/img/copilot.svg'),
            className: 'badge-cli-copilot',
            accentColor: '#ab7df8'
        },
        opencode: {
            label: 'OpenCode',
            logo: getAssetPath('assets/img/opencode.svg'),
            className: 'badge-cli-opencode',
            // Warm silver drawn from the OpenCode logo (#F1ECEC off-white /
            // #4B4646 dark — the brand is monochrome). The previous amber
            // (#e0a530) read as an ugly yellow on the tab strip and collided
            // with the WAITING state's fixed yellow #f9e2af.
            accentColor: '#c9c6c6'
        },
        'glm-5.2': {
            label: 'GLM 5.2',
            logo: getAssetPath('assets/img/z-ai.svg'),
            className: 'badge-cli-glm',
            // Brand blue pulled from the z-ai.svg logo (#1F63EC appears
            // throughout the SVG's style block).
            accentColor: '#1F63EC'
        },
        'grok-4.6': {
            label: 'Grok 4.6',
            logo: getAssetPath('assets/img/grok.svg'),
            className: 'badge-cli-grok',
            // Cool off-white so the mark reads on the dark tab strip without
            // colliding with OpenCode's warmer silver.
            accentColor: '#e8e8e8'
        },
        shell: {
            label: 'Terminal',
            logo: getAssetPath('assets/img/terminal.svg'),
            className: 'badge-cli-shell',
            accentColor: '#3fb950',
            logoFilter: 'brightness(0) invert(1)'
        }
    };

    return brands[key] || { label: cli || 'Unknown', logo: '', className: '', accentColor: null };
}

export const BASE_LLM_CHOICES = Object.freeze([
    { cli: 'claude', label: 'Claude' },
    { cli: 'codex', label: 'Codex' },
    { cli: 'glm-5.2', label: 'GLM 5.2' },
    { cli: 'grok-4.6', label: 'Grok 4.6' },
    { cli: 'opencode', label: 'OpenCode' },
    { cli: 'copilot', label: 'Copilot' },
    { cli: 'antigravity', label: 'Antigravity' }
]);

// Plain shell — a no-agent terminal. Kept out of BASE_LLM_CHOICES so it only surfaces
// where explicitly opted in (the in-app terminal tab picker), not in multi-run /
// chat-history / sandbox pickers where a bare shell has no meaning.
export const SHELL_LLM_CHOICE = Object.freeze({ cli: 'shell', label: 'Terminal' });

function normalizeCliValue(value) {
    return (value || '').toString().trim().toLowerCase();
}

function formatCliDisplayName(cli) {
    if (!cli) return '';
    return cli.charAt(0).toUpperCase() + cli.slice(1);
}

export function buildLlmSelectionValue(cli, environmentId = null) {
    const normalizedCli = normalizeCliValue(cli);
    if (!normalizedCli) return '';

    const normalizedEnvironmentId = Number.parseInt(environmentId, 10);
    if (Number.isFinite(normalizedEnvironmentId)) {
        return `env:${normalizedEnvironmentId}:${normalizedCli}`;
    }

    return `base:${normalizedCli}`;
}

export function buildLlmSelectionOptions(environments = [], options = {}) {
    const {
        includeGroups = true,
        includeDefaultSuffix = true,
        includeShell = false,
        includeBase = true,
        includeHidden = false
    } = options;

    const items = [];

    (Array.isArray(environments) ? environments : []).forEach((environment) => {
        const cli = normalizeCliValue(environment?.cli);
        const environmentId = Number.parseInt(environment?.id, 10);
        if (!cli || !Number.isFinite(environmentId)) {
            return;
        }

        // Automation Workers never appear in LLM/environment selects — they belong to
        // the automation editor's Worker picker (pickers/worker-picker.js) only.
        if (environment?.automationWorker) {
            return;
        }

        // Hidden environments are kept out of the dropdowns to keep them tractable. A
        // currently-selected hidden env is preserved separately by populateLlmSelectionSelect
        // so an existing reference (e.g. an Automation) is never silently dropped.
        if (!includeHidden && environment?.hidden) {
            return;
        }

        const environmentName = (environment?.name || '').toString().trim();
        const displayName = environmentName || `Env ${environmentId}`;
        items.push({
            group: includeGroups ? 'Custom Environments' : null,
            value: buildLlmSelectionValue(cli, environmentId),
            label: `${displayName} (${cli})`,
            cli,
            environmentId,
            environmentName: environmentName || null,
            kind: 'environment'
        });
    });

    if (includeBase) {
        BASE_LLM_CHOICES.forEach((baseCli) => {
            items.push({
                group: includeGroups ? 'Base CLIs' : null,
                value: buildLlmSelectionValue(baseCli.cli),
                label: includeDefaultSuffix ? `${baseCli.label} (default)` : baseCli.label,
                cli: baseCli.cli,
                environmentId: null,
                environmentName: null,
                kind: 'base'
            });
        });
    }

    if (includeShell) {
        items.push({
            group: includeGroups ? 'Base CLIs' : null,
            value: buildLlmSelectionValue(SHELL_LLM_CHOICE.cli),
            label: SHELL_LLM_CHOICE.label,
            cli: SHELL_LLM_CHOICE.cli,
            environmentId: null,
            environmentName: null,
            kind: 'base'
        });
    }

    return items;
}

export function groupLlmSelectionOptions(optionItems = []) {
    const groups = [];
    const groupMap = new Map();

    optionItems.forEach((item) => {
        const groupName = item?.group || '';
        if (!groupMap.has(groupName)) {
            const group = {
                label: groupName,
                options: []
            };
            groupMap.set(groupName, group);
            groups.push(group);
        }

        groupMap.get(groupName).options.push(item);
    });

    return groups;
}

export function buildLlmSelectionOptionsMarkup(optionItems = [], options = {}) {
    const {
        placeholder = 'Select LLM...',
        selectedValue = ''
    } = options;

    let html = '';

    if (placeholder !== null) {
        const selectedAttribute = selectedValue ? '' : ' selected';
        html += `<option value="" disabled${selectedAttribute}>${escapeHtml(placeholder)}</option>`;
    }

    groupLlmSelectionOptions(optionItems).forEach((group) => {
        if (group.label) {
            html += `<optgroup label="${escapeHtml(group.label)}">`;
        }

        group.options.forEach((item) => {
            const selectedAttribute = item.value === selectedValue ? ' selected' : '';
            html += `<option value="${escapeHtml(item.value)}"${selectedAttribute}>${escapeHtml(item.label)}</option>`;
        });

        if (group.label) {
            html += '</optgroup>';
        }
    });

    return html;
}

export function populateLlmSelectionSelect(selectEl, environments = [], options = {}) {
    if (!selectEl) return [];

    const {
        placeholder = 'Select LLM...',
        selectedValue = '',
        includeGroups = true,
        includeDefaultSuffix = true,
        includeShell = false,
        includeBase = true,
        includeHidden = false
    } = options;

    const normalizedSelectedValue = String(selectedValue || '');
    const safeEnvironments = Array.isArray(environments) ? environments : [];
    const optionItems = buildLlmSelectionOptions(safeEnvironments, {
        includeGroups,
        includeDefaultSuffix,
        includeShell,
        includeBase,
        includeHidden
    });

    // If the current selection is a hidden environment, it was filtered out above.
    // Re-inject just that one option so an existing reference (e.g. an Automation using a
    // hidden env) survives an edit instead of being silently cleared.
    if (!includeHidden && normalizedSelectedValue.startsWith('env:')) {
        const selectedEnvId = Number.parseInt(normalizedSelectedValue.split(':')[1], 10);
        const alreadyIncluded = optionItems.some((item) => item.value === normalizedSelectedValue);
        if (!alreadyIncluded && Number.isFinite(selectedEnvId)) {
            const hiddenEnv = safeEnvironments.find((item) =>
                Number.parseInt(item?.id, 10) === selectedEnvId && item?.hidden
            );
            if (hiddenEnv) {
                const cli = normalizeCliValue(hiddenEnv.cli);
                const environmentName = (hiddenEnv.name || '').toString().trim();
                optionItems.push({
                    group: includeGroups ? 'Custom Environments' : null,
                    value: normalizedSelectedValue,
                    label: `${environmentName || `Env ${selectedEnvId}`} (${cli})`,
                    cli,
                    environmentId: selectedEnvId,
                    environmentName: environmentName || null,
                    kind: 'environment'
                });
            }
        }
    }

    return populateLlmSelectionItemsSelect(selectEl, optionItems, {
        ...options,
        selectedValue: normalizedSelectedValue
    });
}

/**
 * Low-level native-select + Tom Select renderer for already-resolved picker
 * items. Persistence/context orchestration belongs to llm-picker-controller;
 * this helper only renders the declared snapshot.
 */
export function populateLlmSelectionItemsSelect(selectEl, optionItems = [], options = {}) {
    if (!selectEl) return [];

    const {
        placeholder = 'Select LLM...',
        selectedValue = '',
        enhance = true,
        searchable = true,
        searchPlaceholder = 'Search LLMs...',
        onCustomize = null,
        emptyMessage = 'No matching LLMs.'
    } = options;
    const normalizedSelectedValue = String(selectedValue || '');
    const safeItems = Array.isArray(optionItems) ? optionItems : [];

    if (selectEl.tomselect) {
        selectEl.tomselect.destroy();
    }

    selectEl.innerHTML = '';

    if (placeholder !== null) {
        const placeholderOption = document.createElement('option');
        placeholderOption.value = '';
        placeholderOption.disabled = true;
        placeholderOption.selected = !normalizedSelectedValue;
        placeholderOption.textContent = placeholder;
        selectEl.appendChild(placeholderOption);
    }

    groupLlmSelectionOptions(safeItems).forEach((group) => {
        let parent = selectEl;
        if (group.label) {
            const optgroup = document.createElement('optgroup');
            optgroup.label = group.label;
            selectEl.appendChild(optgroup);
            parent = optgroup;
        }

        group.options.forEach((item) => {
            const option = document.createElement('option');
            option.value = item.value;
            option.textContent = item.label;
            option.dataset.cli = item.cli || '';
            if (item.environmentId != null) {
                option.dataset.envId = String(item.environmentId);
            }
            parent.appendChild(option);
        });
    });

    if (normalizedSelectedValue) {
        selectEl.value = normalizedSelectedValue;
    }

    if (enhance) {
        enhanceLlmSelectWithTomSelect(selectEl, {
            placeholder: placeholder || 'Select LLM...',
            searchable,
            searchPlaceholder,
            onCustomize,
            emptyMessage
        });
    }

    return safeItems;
}

function renderCliRowHtml(cliKey, data, escape) {
    const cli = (data[cliKey] || '').toString();
    const brand = getCliBrand(cli);
    const logoStyle = brand.logoFilter ? ` style="filter: ${escape(brand.logoFilter)};"` : '';
    const logo = brand.logo
        ? `<img class="ts-cli-logo" src="${escape(brand.logo)}" alt="" loading="lazy"${logoStyle}>`
        : `<span class="ts-cli-logo ts-cli-logo-fallback"><i class="fa-solid fa-terminal" aria-hidden="true"></i></span>`;
    const label = escape(data.text || '');
    return `<div class="ts-cli-row">${logo}<span class="ts-cli-label">${label}</span></div>`;
}

export function enhanceLlmSelectWithTomSelect(selectEl, options = {}) {
    if (!selectEl || typeof window.TomSelect !== 'function') return null;

    const {
        placeholder = 'Select LLM...',
        cliKey = 'cli',
        searchable = true,
        searchPlaceholder = 'Search LLMs...',
        onCustomize = null,
        emptyMessage = 'No matching LLMs.'
    } = options;

    if (selectEl.tomselect) {
        selectEl.tomselect.destroy();
    }

    const config = {
        placeholder,
        allowEmptyOption: true,
        maxOptions: null,
        plugins: searchable ? ['dropdown_input'] : [],
        // Render the dropdown in <body> so parent cards with overflow:hidden
        // (#vb-terminal-panel, sandbox cards) can't clip it.
        dropdownParent: 'body',
        render: {
            option: (data, escape) => renderCliRowHtml(cliKey, data, escape),
            item: (data, escape) => renderCliRowHtml(cliKey, data, escape),
            no_results: (_data, escape) =>
                `<div class="no-results llm-picker-empty-state">${escape(emptyMessage)}</div>`
        }
    };

    if (!searchable) {
        config.controlInput = null;
    }

    const ts = new window.TomSelect(selectEl, config);

    if (typeof onCustomize === 'function') {
        mountLlmPickerFooter(ts, selectEl, onCustomize);
    }

    if (searchable) {
        const configureSearchInput = () => {
            ts.control_input.placeholder = searchPlaceholder;
            ts.control_input.setAttribute('aria-label', searchPlaceholder);
        };
        configureSearchInput();
        ts.on('dropdown_open', configureSearchInput);
    }

    // Wrap TomSelect's body-parent positioning to (a) clear the inline width
    // it sets to match the control (our content needs more room — see CSS
    // .ts-dropdown min-width 320px / max-width 560px) and (b) flip above the
    // control when the dropdown would otherwise overflow the viewport bottom.
    const originalPosition = ts.positionDropdown.bind(ts);
    ts.positionDropdown = function () {
        originalPosition();
        positionTomSelectDropdown(ts);
    };
    ts.on('dropdown_close', () => {
        ts.dropdown?.classList.remove('ts-dropdown-flipped');
    });

    return ts;
}

// NOTE: the footer's focus hand-off below writes Tom Select PRIVATE state (ts.isFocused;
// llm-picker-controller's focus restore additionally uses ts.ignoreFocus). Verified against
// the vendored Tom Select v2.4.3 bundle (wwwroot/assets/tom-select/). When bumping that
// bundle, re-run UITests/tests/llm-picker-preferences.spec.js — the footer Tab hand-off and
// modal focus-restore tests fail fast if these internals moved.
function mountLlmPickerFooter(ts, selectEl, onCustomize) {
    if (!ts?.dropdown) return;

    const footer = document.createElement('div');
    footer.className = 'llm-picker-dropdown-footer';
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'llm-picker-customize-button';
    button.setAttribute('aria-label', 'Customize LLM list');
    button.innerHTML = '<i class="fa-solid fa-gear" aria-hidden="true"></i><span>Customize LLM list</span>';
    footer.appendChild(button);
    ts.dropdown.appendChild(footer);

    // Tom Select normally closes on the mousedown that precedes click. Keeping
    // focus here lets the real button receive the click/keyboard activation.
    footer.addEventListener('mousedown', (event) => {
        event.preventDefault();
        event.stopPropagation();
    });

    const moveFocusWithinDropdown = (target) => {
        // Moving focus out of Tom Select's search input normally triggers its
        // blur handler and hides the dropdown before the footer can receive Tab.
        // Its onBlur closes whenever the instance believes it owns focus, so
        // clear that flag for this synchronous hand-off and then restore the
        // logical focused state while the footer owns focus.
        ts.isFocused = false;
        target?.focus?.({ preventScroll: true });
        ts.isFocused = true;
        requestAnimationFrame(() => {
            if (document.activeElement === button && !ts.isOpen) ts.open();
            ts.refreshState?.();
        });
    };
    const handleForwardTab = (event) => {
        if (event.key !== 'Tab' || event.shiftKey || !ts.isOpen) return;
        event.preventDefault();
        event.stopImmediatePropagation();
        moveFocusWithinDropdown(button);
    };
    new Set([ts.focus_node, ts.control, ts.control_input])
        .forEach((focusNode) => focusNode?.addEventListener('keydown', handleForwardTab, true));
    button.addEventListener('keydown', (event) => {
        if (event.key !== 'Tab' || !event.shiftKey) return;
        event.preventDefault();
        event.stopPropagation();
        moveFocusWithinDropdown(ts.focus_node || ts.control_input);
    });
    button.addEventListener('blur', (event) => {
        const next = event.relatedTarget;
        if (next && (ts.wrapper?.contains(next) || ts.dropdown?.contains(next))) return;
        ts.isFocused = false;
        ts.close();
        ts.refreshState?.();
    });
    button.addEventListener('click', (event) => {
        event.preventDefault();
        event.stopPropagation();
        ts.close();
        onCustomize({ selectEl, tomSelect: ts, triggerElement: ts.control });
    });
}

function positionTomSelectDropdown(ts) {
    const wrapper = ts.wrapper;
    const dropdown = ts.dropdown;
    if (!wrapper || !dropdown) return;

    // Reset before measuring so we get the natural placement first.
    dropdown.classList.remove('ts-dropdown-flipped');
    dropdown.style.width = '';

    const margin = 8;
    const wrapperRect = wrapper.getBoundingClientRect();
    const dropdownHeight = dropdown.offsetHeight || dropdown.getBoundingClientRect().height || 200;
    const spaceBelow = window.innerHeight - wrapperRect.bottom;
    const spaceAbove = wrapperRect.top;

    if (spaceBelow < dropdownHeight + margin && spaceAbove > spaceBelow) {
        // Flip above. With dropdownParent='body' the dropdown is positioned in
        // document coordinates, so we set `top` directly rather than rely on
        // CSS bottom:100% (which is relative to body, not the wrapper).
        const flippedTop = wrapperRect.top + window.scrollY - dropdownHeight - 4;
        dropdown.style.top = `${flippedTop}px`;
        dropdown.classList.add('ts-dropdown-flipped');
    }
}

export function parseLlmSelection(selection, environments = []) {
    const value = (selection || '').toString().trim();
    if (!value) {
        return {
            kind: null,
            value: '',
            cli: null,
            envId: null,
            environmentName: null,
            displayName: null
        };
    }

    if (value.startsWith('base:')) {
        const cli = normalizeCliValue(value.slice(5));
        const baseCli = [...BASE_LLM_CHOICES, SHELL_LLM_CHOICE].find((item) => item.cli === cli);
        return {
            kind: 'base',
            value,
            cli,
            envId: null,
            environmentName: null,
            displayName: baseCli?.label || formatCliDisplayName(cli)
        };
    }

    if (value.startsWith('env:')) {
        const parts = value.split(':');
        const envId = Number.parseInt(parts[1], 10);
        const cli = normalizeCliValue(parts.slice(2).join(':'));
        const environment = (Array.isArray(environments) ? environments : []).find((item) =>
            Number.parseInt(item?.id, 10) === envId
        );
        const environmentName = environment?.name || null;
        const fallbackName = Number.isFinite(envId) ? `Env ${envId}` : 'Environment';

        return {
            kind: 'environment',
            value,
            cli,
            envId: Number.isFinite(envId) ? envId : null,
            environmentName,
            displayName: `${environmentName || fallbackName} (${cli})`
        };
    }

    const cli = normalizeCliValue(value);
    return {
        kind: 'raw',
        value,
        cli,
        envId: null,
        environmentName: null,
        displayName: formatCliDisplayName(cli)
    };
}

export function escapeHtml(text) {
    if (text == null) return '';
    return String(text)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

// Escape HTML, then turn newlines into <br>. The escape runs FIRST so any markup
// in the input is neutralized and only literal newlines become breaks. Shared by
// the global app toast (toast-service.js) and the terminal toast (terminal-toast.js).
export function escapeHtmlWithLineBreaks(text) {
    return escapeHtml(text).replace(/\n/g, '<br>');
}

// Open confirm overlays, newest last. Escape settles only the top-most dialog so
// stacked confirms dismiss in visual order. Capture-phase Escape handlers that
// were registered BEFORE a dialog opened (jobs inline editor, env modal) run
// first regardless of stopImmediatePropagation — they must check
// isConfirmDialogOpen() and stand down while any overlay is up.
const openConfirmOverlays = [];

export function isConfirmDialogOpen() {
    return openConfirmOverlays.length > 0;
}

// In-app replacement for window.confirm, which the sandboxed VS Code webview
// suppresses (it returns false without showing anything — see settings-controller).
// Renders its own fixed overlay above any open Bootstrap modal, so callers inside
// a modal (e.g. the run-history dialog) can confirm without their modal being torn
// down by app.showModal's single shared container. Resolves true only on an
// explicit confirm; Escape, the backdrop, and Cancel all resolve false.
export function confirmDialog({
    title = 'Are you sure?',
    message = '',
    confirmLabel = 'Confirm',
    cancelLabel = 'Cancel',
    danger = false
} = {}) {
    if (typeof document === 'undefined' || !document.body) return Promise.resolve(false);

    return new Promise(resolve => {
        const overlay = document.createElement('div');
        overlay.className = 'vb-confirm-overlay';

        const dialog = document.createElement('div');
        dialog.className = 'vb-confirm';
        dialog.setAttribute('role', 'alertdialog');
        dialog.setAttribute('aria-modal', 'true');

        const heading = document.createElement('h6');
        heading.className = 'vb-confirm-title';
        heading.textContent = title;

        const body = document.createElement('p');
        body.className = 'vb-confirm-message';
        body.textContent = message;

        const actions = document.createElement('div');
        actions.className = 'vb-confirm-actions';

        const cancelButton = document.createElement('button');
        cancelButton.type = 'button';
        cancelButton.className = 'btn btn-sm btn-outline-secondary';
        cancelButton.textContent = cancelLabel;

        const confirmButton = document.createElement('button');
        confirmButton.type = 'button';
        confirmButton.className = `btn btn-sm ${danger ? 'btn-danger' : 'btn-primary'}`;
        confirmButton.textContent = confirmLabel;

        actions.append(cancelButton, confirmButton);
        dialog.append(heading, ...(message ? [body] : []), actions);
        overlay.append(dialog);

        const previousFocus = document.activeElement;
        const openedAt = Date.now();
        openConfirmOverlays.push(overlay);
        let settled = false;
        const settle = result => {
            if (settled) return;
            settled = true;
            const at = openConfirmOverlays.indexOf(overlay);
            if (at !== -1) openConfirmOverlays.splice(at, 1);
            window.removeEventListener('keydown', handleKeydown, true);
            overlay.remove();
            if (previousFocus && typeof previousFocus.focus === 'function') {
                try { previousFocus.focus(); } catch { /* detached */ }
            }
            resolve(result);
        };
        const handleKeydown = event => {
            // With dialogs stacked, only the top-most one may react.
            if (openConfirmOverlays[openConfirmOverlays.length - 1] !== overlay) return;
            if (event.key === 'Escape') {
                event.preventDefault();
                event.stopImmediatePropagation();
                settle(false);
                return;
            }
            // Two focusable controls: Tab and Shift+Tab both toggle between them,
            // so keyboard focus can never wander into the page beneath the overlay.
            if (event.key === 'Tab') {
                event.preventDefault();
                event.stopImmediatePropagation();
                (document.activeElement === confirmButton ? cancelButton : confirmButton).focus();
            }
        };

        confirmButton.addEventListener('click', () => settle(true));
        cancelButton.addEventListener('click', () => settle(false));
        overlay.addEventListener('mousedown', event => {
            // The second press of a double-click on the triggering button lands on
            // the overlay milliseconds after open and would instantly self-cancel —
            // reproducing the very "delete does nothing" bug this dialog fixes.
            // Ignore the backdrop for a beat.
            if (event.target === overlay && Date.now() - openedAt > 300) settle(false);
        });
        window.addEventListener('keydown', handleKeydown, true);

        document.body.appendChild(overlay);
        // Destructive dialogs land focus on the safe action; Enter never deletes.
        cancelButton.focus();
    });
}

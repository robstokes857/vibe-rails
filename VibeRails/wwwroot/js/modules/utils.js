export function getLlmName(llmEnum) {
    const names = {
        0: 'Unknown',
        1: 'Codex',
        2: 'Claude',
        3: 'Antigravity',
        4: 'Copilot',
        6: 'OpenCode'
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

const BASE_LLM_CHOICES = Object.freeze([
    { cli: 'claude', label: 'Claude' },
    { cli: 'codex', label: 'Codex' },
    { cli: 'antigravity', label: 'Antigravity' },
    { cli: 'copilot', label: 'Copilot' },
    { cli: 'opencode', label: 'OpenCode' }
]);

// Plain shell — a no-agent terminal. Kept out of BASE_LLM_CHOICES so it only surfaces
// where explicitly opted in (the in-app terminal tab picker), not in multi-run /
// chat-history / sandbox pickers where a bare shell has no meaning.
const SHELL_LLM_CHOICE = Object.freeze({ cli: 'shell', label: 'Terminal' });

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
        includeShell = false
    } = options;

    const items = [];

    (Array.isArray(environments) ? environments : []).forEach((environment) => {
        const cli = normalizeCliValue(environment?.cli);
        const environmentId = Number.parseInt(environment?.id, 10);
        if (!cli || !Number.isFinite(environmentId)) {
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
        enhance = true
    } = options;

    const optionItems = buildLlmSelectionOptions(environments, {
        includeGroups,
        includeDefaultSuffix,
        includeShell
    });

    if (selectEl.tomselect) {
        selectEl.tomselect.destroy();
    }

    selectEl.innerHTML = '';

    if (placeholder !== null) {
        const placeholderOption = document.createElement('option');
        placeholderOption.value = '';
        placeholderOption.disabled = true;
        placeholderOption.selected = !selectedValue;
        placeholderOption.textContent = placeholder;
        selectEl.appendChild(placeholderOption);
    }

    groupLlmSelectionOptions(optionItems).forEach((group) => {
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

    if (selectedValue) {
        selectEl.value = selectedValue;
    }

    if (enhance) {
        enhanceLlmSelectWithTomSelect(selectEl, {
            placeholder: placeholder || 'Select LLM...'
        });
    }

    return optionItems;
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
        searchable = false
    } = options;

    if (selectEl.tomselect) {
        selectEl.tomselect.destroy();
    }

    const config = {
        placeholder,
        allowEmptyOption: true,
        maxOptions: null,
        plugins: [],
        // Render the dropdown in <body> so parent cards with overflow:hidden
        // (#vb-terminal-panel, sandbox cards) can't clip it.
        dropdownParent: 'body',
        render: {
            option: (data, escape) => renderCliRowHtml(cliKey, data, escape),
            item: (data, escape) => renderCliRowHtml(cliKey, data, escape)
        }
    };

    if (!searchable) {
        config.controlInput = null;
    }

    const ts = new window.TomSelect(selectEl, config);

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

export function getLlmName(llmEnum) {
    const names = {
        0: 'Unknown',
        1: 'Codex',
        2: 'Claude',
        3: 'Gemini',
        4: 'Copilot'
    };
    return names[llmEnum] || 'Unknown';
}

export function getProjectNameFromPath(path) {
    if (!path) return 'Unknown Project';
    // Handle both Windows and Unix paths
    const parts = path.replace(/\\/g, '/').split('/').filter(p => p);
    return parts[parts.length - 1] || 'Unknown Project';
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
            accentColor: '#c15f3c'
        },
        gemini: {
            label: 'Gemini',
            logo: getAssetPath('assets/img/gemini-color.svg'),
            className: 'badge-cli-gemini',
            accentColor: '#568be0'
        },
        copilot: {
            label: 'Copilot',
            logo: getAssetPath('assets/img/copilot.svg'),
            className: 'badge-cli-copilot',
            accentColor: '#199fd7'
        }
    };

    return brands[key] || { label: cli || 'Unknown', logo: '', className: '', accentColor: null };
}

const BASE_LLM_CHOICES = Object.freeze([
    { cli: 'claude', label: 'Claude' },
    { cli: 'codex', label: 'Codex' },
    { cli: 'gemini', label: 'Gemini' },
    { cli: 'copilot', label: 'Copilot' }
]);

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
        includeDefaultSuffix = true
    } = options;

    const items = BASE_LLM_CHOICES.map((baseCli) => ({
        group: includeGroups ? 'Base CLIs' : null,
        value: buildLlmSelectionValue(baseCli.cli),
        label: includeDefaultSuffix ? `${baseCli.label} (default)` : baseCli.label,
        cli: baseCli.cli,
        environmentId: null,
        environmentName: null,
        kind: 'base'
    }));

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
        includeDefaultSuffix = true
    } = options;

    const optionItems = buildLlmSelectionOptions(environments, {
        includeGroups,
        includeDefaultSuffix
    });

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
            parent.appendChild(option);
        });
    });

    if (selectedValue) {
        selectEl.value = selectedValue;
    }

    return optionItems;
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
        const baseCli = BASE_LLM_CHOICES.find((item) => item.cli === cli);
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
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}

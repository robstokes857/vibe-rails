export function getLlmName(llmEnum) {
    const names = {
        0: 'Unknown',
        1: 'Codex',
        2: 'Claude',
        3: 'Gemini'
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
            className: 'badge-cli-codex'
        },
        chatgpt: {
            label: 'ChatGPT',
            logo: getAssetPath('assets/img/openai.svg'),
            className: 'badge-cli-codex'
        },
        openai: {
            label: 'OpenAI',
            logo: getAssetPath('assets/img/openai.svg'),
            className: 'badge-cli-codex'
        },
        claude: {
            label: 'Claude',
            logo: getAssetPath('assets/img/claude-color.svg'),
            className: 'badge-cli-claude'
        },
        gemini: {
            label: 'Gemini',
            logo: getAssetPath('assets/img/gemini-color.svg'),
            className: 'badge-cli-gemini'
        }
    };

    return brands[key] || { label: cli || 'Unknown', logo: '', className: '' };
}

export function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
}
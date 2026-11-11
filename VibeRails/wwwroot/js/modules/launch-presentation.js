import { escapeHtml, getCliBrand } from './utils.js';

const kinds = {
    agent: { label: 'Agent', icon: 'robot' },
    'quick-launch': { label: 'Quick launch', icon: 'bolt' },
    script: { label: 'Script', icon: 'code' },
    vca: { label: 'VCA check', icon: 'list-check' },
    'code-quality': { label: 'Code quality check', icon: 'list-check' }
};

/** VIBE-309: hide the built-in quality Action offering without changing saved workflows. */
export const isLaunchVisible = item => item?.kind !== 'code-quality';

/** Shared identity for an AI Agents catalog row and its Quick actions entry. */
export function launchPresentation(item) {
    const brand = item.cli ? getCliBrand(item.cli) : null;
    const kind = Object.hasOwn(kinds, item.kind) ? kinds[item.kind] : { label: 'Action', icon: 'list-check' };
    return {
        logoHtml: brand?.logo
            ? `<img class="launch-provider-logo" src="${escapeHtml(brand.logo)}" alt=""${brand.logoFilter ? ` style="filter:${escapeHtml(brand.logoFilter)}"` : ''}>`
            : `<span class="launch-provider-logo" aria-hidden="true"><i class="fa-solid fa-${kind.icon}"></i></span>`,
        subtitle: item.description || (item.definitionType === 'action' ? kind.label : brand?.label || item.cli || kind.label)
    };
}

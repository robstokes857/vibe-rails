import { escapeHtml, getCliBrand } from './utils.js';
import { scriptRuntimeFor } from './script-runtimes.js';

const kinds = {
    agent: { label: 'Agent', icon: 'robot' },
    'quick-launch': { label: 'Quick launch', icon: 'bolt' },
    script: { label: 'Script', icon: 'code' },
    vca: { label: 'VCA check', icon: 'list-check' },
    'code-quality': { label: 'Code quality check', icon: 'list-check' }
};

const fallbackLogo = icon => `<span class="launch-provider-logo" aria-hidden="true"><i class="fa-solid fa-${icon}"></i></span>`;

/**
 * The signed script library's entries as the AI Agents catalog and Quick actions list them
 * (VIBE-311). `state` is PythonScriptsController.state, whose `name` is the registration ID.
 * Only a signed script is runnable; the others stay listed so they can be opened and signed.
 */
export function libraryScripts(state) {
    if (!Array.isArray(state?.scripts)) return [];
    return state.scripts.filter(script => script?.name).map((script) => {
        const fileName = script.fileName || script.name;
        const label = script.displayName || fileName;
        const runtime = scriptRuntimeFor(fileName);
        return {
            id: script.name,
            name: label,
            status: script.status,
            canRun: script.status === 'approved',
            logoHtml: fallbackLogo(kinds.script.icon),
            // The display name defaults to the file name; say the file only when it adds something.
            subtitle: label === fileName ? `${runtime.label} script` : `${runtime.label} script · ${fileName}`
        };
    });
}

/** Shared identity for an AI Agents catalog row and its Quick actions entry. */
export function launchPresentation(item) {
    const brand = item.cli ? getCliBrand(item.cli) : null;
    const kind = Object.hasOwn(kinds, item.kind) ? kinds[item.kind] : { label: 'Action', icon: 'list-check' };
    return {
        logoHtml: brand?.logo
            ? `<img class="launch-provider-logo" src="${escapeHtml(brand.logo)}" alt=""${brand.logoFilter ? ` style="filter:${escapeHtml(brand.logoFilter)}"` : ''}>`
            : fallbackLogo(kind.icon),
        subtitle: item.description || (item.definitionType === 'action' ? kind.label : brand?.label || item.cli || kind.label)
    };
}

const providers = ['codex', 'claude'];
const title = provider => provider[0].toUpperCase() + provider.slice(1);
const savedByRoot = new WeakMap();
// The URL each provider's credential entry filled in from the environment, until the user edits it.
const autoFilledByRoot = new WeakMap();

// One-step provider API keys. A preset is a client-side shorthand for the provider's own API URL
// plus its header format; the server still receives an explicit URL and bearer/api-key mode, so
// API_SEC's rule that a credential is bound to an explicitly saved URL is unchanged. A reload shows
// the preset again when the saved URL, mode and header match it.
const presets = {
    codex: { value: 'openai-api-key', label: 'OpenAI API key', url: 'https://api.openai.com/v1', mode: 'bearer' },
    claude: { value: 'anthropic-api-key', label: 'Anthropic API key', url: 'https://api.anthropic.com', mode: 'api-key', header: 'x-api-key' }
};

const control = (root, provider, suffix) => root?.querySelector(`#setting-${provider}-${suffix}`);
const sameUrl = (left, right) => (left || '').trim().replace(/\/+$/, '').toLowerCase()
    === (right || '').trim().replace(/\/+$/, '').toLowerCase();
// Mirrors AppSettingsRoutes.SameGatewayOrigin: a saved credential stays with its gateway's origin
// (scheme, host and port), so pointing the URL anywhere else removes it on save.
const gatewayOrigin = value => {
    try {
        const url = new URL((value || '').trim());
        return url.protocol === 'http:' || url.protocol === 'https:' ? url.origin.toLowerCase() : null;
    } catch {
        return null;
    }
};
const keepsSavedCredential = (entered, saved) => sameUrl(entered, saved)
    || (gatewayOrigin(entered) !== null && gatewayOrigin(entered) === gatewayOrigin(saved));
// Claude takes its base with or without /v1, so both spellings name the preset.
const isPresetUrl = (provider, url) => sameUrl(url, presets[provider].url)
    || (provider === 'claude' && sameUrl(url, `${presets.claude.url}/v1`));

/** The wire mode and header the selected option stands for. */
function selectedAuth(root, provider) {
    const selected = control(root, provider, 'gateway-auth-mode')?.value || 'bearer';
    const preset = presets[provider];
    const header = control(root, provider, 'gateway-api-key-header')?.value.trim() || 'api-key';
    if (selected === preset.value) return { mode: preset.mode, header: preset.header || header, preset };
    return { mode: selected, header, preset: null };
}

/** Read only edits: saved credential bytes never enter the browser. */
export function gatewayCredentialEdits(root) {
    return Object.fromEntries(providers.flatMap(provider => {
        const auth = selectedAuth(root, provider);
        return [
            [`${provider}GatewayBearerToken`, control(root, provider, 'gateway-token')?.value || ''],
            [`clear${title(provider)}GatewayBearerToken`, control(root, provider, 'gateway-token-clear')?.checked === true],
            [`${provider}GatewayAuthMode`, auth.mode],
            [`${provider}GatewayApiKeyHeader`, auth.header]
        ];
    }).concat([['claudeGatewaySupportsToolSearch', control(root, 'claude', 'gateway-tool-search')?.checked === true]]));
}

export function applyGatewaySettings(root, settings) {
    savedByRoot.set(root, settings);
    autoFilledByRoot.set(root, {});
    for (const provider of providers) {
        const input = control(root, provider, 'gateway-token');
        const clear = control(root, provider, 'gateway-token-clear');
        if (input) {
            input.value = '';
        }
        if (clear) clear.checked = false;
        const mode = control(root, provider, 'gateway-auth-mode');
        const header = control(root, provider, 'gateway-api-key-header');
        const savedMode = settings[`${provider}GatewayAuthMode`] || 'bearer';
        const savedHeader = settings[`${provider}GatewayApiKeyHeader`] || 'api-key';
        const preset = presets[provider];
        const isPreset = isPresetUrl(provider, settings[`${provider}EnterpriseNetworkProxy`])
            && savedMode === preset.mode && (!preset.header || savedHeader.toLowerCase() === preset.header);
        if (mode) mode.value = isPreset ? preset.value : savedMode;
        if (header) header.value = savedHeader;
        updateGatewayAuth(root, settings, provider);
        updateGatewayRoute(root, settings, provider);
    }
    const toolSearch = control(root, 'claude', 'gateway-tool-search');
    if (toolSearch) toolSearch.checked = settings.claudeGatewaySupportsToolSearch === true;
}

function updateGatewayAuth(root, settings, provider) {
    const { mode, header, preset } = selectedAuth(root, provider);
    const isApiKey = mode === 'api-key';
    const headerRow = control(root, provider, 'gateway-api-key-row');
    if (headerRow) headerRow.hidden = !isApiKey || !!preset;
    const label = control(root, provider, 'gateway-token-label');
    if (label) label.textContent = preset ? preset.label : isApiKey ? 'Gateway API key' : 'Gateway bearer token';
    const input = control(root, provider, 'gateway-token');
    const hasSaved = settings[`has${title(provider)}GatewayBearerToken`] === true;
    const entered = control(root, provider, 'enterprise-network-proxy')?.value;
    const moving = hasSaved && entered !== undefined
        && !keepsSavedCredential(entered, settings[`${provider}EnterpriseNetworkProxy`]);
    if (input) input.placeholder = moving
        ? 'Saving removes the credential saved for the previous gateway — enter one for this URL'
        : hasSaved ? 'Credential saved — enter a replacement' : isApiKey ? 'Optional API key' : 'Optional bearer token';
    const preview = control(root, provider, 'gateway-auth-preview');
    if (preview) preview.textContent = isApiKey ? `${header}: <API key>` : 'Authorization: Bearer <token>';
}

function updateGatewayRoute(root, settings, provider) {
    const route = control(root, provider, 'gateway-route');
    if (!route) return;
    const entered = control(root, provider, 'enterprise-network-proxy')?.value.trim();
    const inherited = settings[`${provider}InheritedApiBaseUrl`];
    const saver = root.querySelector(`#setting-token-saver-${provider}`)?.checked !== false;
    const destination = entered || inherited;
    const pending = (entered || '') !== (settings[`${provider}EnterpriseNetworkProxy`] || '');
    const filled = !!entered && autoFilledByRoot.get(root)?.[provider] === entered;
    const typedCredential = !!control(root, provider, 'gateway-token')?.value.trim();
    const direct = !entered && settings[`${provider}InheritedBaseBypassesRelay`] === true;
    route.textContent = `Default managed route: ${pending ? 'After saving: ' : ''}` + (direct
        ? 'CLI → inherited base from the environment (direct, Token Saver off)'
        : `CLI → ${saver ? 'Token Saver' : 'VibeRails relay (Token Saver off)'} → `
        + (destination ? `${destination}${!entered ? ' (from environment)' : filled ? ' (filled in from the environment)' : ''}` : 'provider default')
        )
        + (typedCredential && !destination ? `. A credential needs a gateway URL: enter one, or choose ${presets[provider].label}.` : '');
    const use = root.querySelector(`[data-use-inherited-gateway="${provider}"]`);
    if (use) use.hidden = !inherited || entered === inherited;
}

function setGatewayUrl(root, provider, value) {
    const url = control(root, provider, 'enterprise-network-proxy');
    if (!url) return;
    url.value = value;
    url.dispatchEvent(new Event('input', { bubbles: true }));
}

/**
 * A credential typed while the URL is blank needs a URL to be saved. When the backend inherited
 * one, fill it in visibly (the route line says so) instead of making the user adopt it first.
 * Removing the credential again undoes a fill the user has not edited since.
 */
function fillInheritedUrl(root, saved, provider) {
    const url = control(root, provider, 'enterprise-network-proxy');
    if (!url) return;
    const filled = autoFilledByRoot.get(root) || {};
    autoFilledByRoot.set(root, filled);
    const typed = !!control(root, provider, 'gateway-token')?.value.trim();
    const inherited = saved[`${provider}InheritedApiBaseUrl`];
    if (typed && !url.value.trim() && inherited) {
        filled[provider] = inherited;
        setGatewayUrl(root, provider, inherited);
    } else if (!typed && filled[provider] && url.value.trim() === filled[provider]) {
        filled[provider] = null;
        setGatewayUrl(root, provider, '');
    } else {
        updateGatewayRoute(root, saved, provider);
    }
}

export function initGatewaySettings(root, settings) {
    applyGatewaySettings(root, settings);
    for (const provider of providers) {
        const saved = () => savedByRoot.get(root) || settings;
        const preset = presets[provider];
        const url = control(root, provider, 'enterprise-network-proxy');
        const mode = control(root, provider, 'gateway-auth-mode');
        url?.addEventListener('input', () => {
            const filled = autoFilledByRoot.get(root);
            if (filled?.[provider] && filled[provider] !== url.value.trim()) filled[provider] = null;
            // Pointing a preset somewhere else turns it back into the plain format it stands for.
            if (mode?.value === preset.value && !isPresetUrl(provider, url.value)) {
                mode.value = preset.mode;
                const header = control(root, provider, 'gateway-api-key-header');
                if (header && preset.header) header.value = preset.header;
                mode.dispatchEvent(new Event('change', { bubbles: true }));
            }
            updateGatewayAuth(root, saved(), provider);
            updateGatewayRoute(root, saved(), provider);
        });
        root.querySelector(`#setting-token-saver-${provider}`)
            ?.addEventListener('input', () => updateGatewayRoute(root, saved(), provider));
        control(root, provider, 'gateway-token')?.addEventListener('input', () => fillInheritedUrl(root, saved(), provider));
        for (const suffix of ['gateway-auth-mode', 'gateway-api-key-header']) {
            const input = control(root, provider, suffix);
            for (const event of ['input', 'change'])
                input?.addEventListener(event, () => updateGatewayAuth(root, saved(), provider));
        }
        mode?.addEventListener('change', () => {
            if (mode.value !== preset.value || isPresetUrl(provider, url?.value)) return;
            autoFilledByRoot.get(root)[provider] = null;
            setGatewayUrl(root, provider, preset.url);
        });
        root.querySelector(`[data-use-inherited-gateway="${provider}"]`)?.addEventListener('click', () => {
            setGatewayUrl(root, provider, saved()[`${provider}InheritedApiBaseUrl`] || '');
        });
    }
}

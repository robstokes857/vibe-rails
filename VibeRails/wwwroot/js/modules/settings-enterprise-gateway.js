const providers = ['codex', 'claude'];
const title = provider => provider[0].toUpperCase() + provider.slice(1);
const savedByRoot = new WeakMap();

/** Read only edits: saved credential bytes never enter the browser. */
export function gatewayCredentialEdits(root) {
    return Object.fromEntries(providers.flatMap(provider => [
        [`${provider}GatewayBearerToken`, root?.querySelector(`#setting-${provider}-gateway-token`)?.value || ''],
        [`clear${title(provider)}GatewayBearerToken`, root?.querySelector(`#setting-${provider}-gateway-token-clear`)?.checked === true],
        [`${provider}GatewayAuthMode`, root?.querySelector(`#setting-${provider}-gateway-auth-mode`)?.value || 'bearer'],
        [`${provider}GatewayApiKeyHeader`, root?.querySelector(`#setting-${provider}-gateway-api-key-header`)?.value.trim() || 'api-key']
    ]));
}

export function applyGatewaySettings(root, settings) {
    savedByRoot.set(root, settings);
    for (const provider of providers) {
        const input = root.querySelector(`#setting-${provider}-gateway-token`);
        const clear = root.querySelector(`#setting-${provider}-gateway-token-clear`);
        if (input) {
            input.value = '';
        }
        if (clear) clear.checked = false;
        const mode = root.querySelector(`#setting-${provider}-gateway-auth-mode`);
        const header = root.querySelector(`#setting-${provider}-gateway-api-key-header`);
        if (mode) mode.value = settings[`${provider}GatewayAuthMode`] || 'bearer';
        if (header) header.value = settings[`${provider}GatewayApiKeyHeader`] || 'api-key';
        updateGatewayAuth(root, settings, provider);
        updateGatewayRoute(root, settings, provider);
    }
}

function updateGatewayAuth(root, settings, provider) {
    const isApiKey = root.querySelector(`#setting-${provider}-gateway-auth-mode`)?.value === 'api-key';
    const headerRow = root.querySelector(`#setting-${provider}-gateway-api-key-row`);
    if (headerRow) headerRow.hidden = !isApiKey;
    const label = root.querySelector(`#setting-${provider}-gateway-token-label`);
    if (label) label.textContent = isApiKey ? 'Gateway API key' : 'Gateway bearer token';
    const input = root.querySelector(`#setting-${provider}-gateway-token`);
    if (input) input.placeholder = settings[`has${title(provider)}GatewayBearerToken`]
        ? 'Credential saved — enter a replacement' : isApiKey ? 'Optional API key' : 'Optional bearer token';
    const preview = root.querySelector(`#setting-${provider}-gateway-auth-preview`);
    if (preview) preview.textContent = isApiKey
        ? `${root.querySelector(`#setting-${provider}-gateway-api-key-header`)?.value.trim() || 'api-key'}: <API key>`
        : 'Authorization: Bearer <token>';
}

function updateGatewayRoute(root, settings, provider) {
    const route = root.querySelector(`#setting-${provider}-gateway-route`);
    if (!route) return;
    const entered = root.querySelector(`#setting-${provider}-enterprise-network-proxy`)?.value.trim();
    const inherited = settings[`${provider}InheritedApiBaseUrl`];
    const saver = root.querySelector(`#setting-token-saver-${provider}`)?.checked !== false;
    const destination = entered || inherited;
    const pending = (entered || '') !== (settings[`${provider}EnterpriseNetworkProxy`] || '');
    route.textContent = `Default managed route: ${pending ? 'After saving: ' : ''}CLI → ${saver ? 'Token Saver' : 'VibeRails relay (Token Saver off)'} → `
        + (destination ? `${destination}${!entered ? ' (from environment)' : ''}` : 'provider default');
    const use = root.querySelector(`[data-use-inherited-gateway="${provider}"]`);
    if (use) use.hidden = !inherited || entered === inherited;
}

export function initGatewaySettings(root, settings) {
    applyGatewaySettings(root, settings);
    for (const provider of providers) {
        const saved = () => savedByRoot.get(root) || settings;
        for (const selector of [`#setting-${provider}-enterprise-network-proxy`, `#setting-token-saver-${provider}`]) {
            root.querySelector(selector)?.addEventListener('input', () => updateGatewayRoute(root, saved(), provider));
        }
        for (const suffix of ['gateway-auth-mode', 'gateway-api-key-header']) {
            const input = root.querySelector(`#setting-${provider}-${suffix}`);
            for (const event of ['input', 'change'])
                input?.addEventListener(event, () => updateGatewayAuth(root, saved(), provider));
        }
        root.querySelector(`[data-use-inherited-gateway="${provider}"]`)?.addEventListener('click', () => {
            const input = root.querySelector(`#setting-${provider}-enterprise-network-proxy`);
            input.value = saved()[`${provider}InheritedApiBaseUrl`] || '';
            input.dispatchEvent(new Event('input', { bubbles: true }));
        });
    }
}

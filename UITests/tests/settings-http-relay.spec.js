// @ts-check

const { test, expect } = require('./fixtures');

const MASKED_API_KEY = '••••••••relay';
const HTTP_RELAY_ROUTE = /\/api\/v1\/http-relay\/test\/posts(?:\/\d+)?(?:\?.*)?$/;

function buildSettings(routeThroughVibeRailsAi = false, apiKey = MASKED_API_KEY) {
    return {
        remoteAccess: false,
        apiKey,
        dataExportConfigured: false,
        routeThroughVibeRailsAi,
        useVsCodeTheme: false,
        mcpEnabled: true,
        computerName: '',
        codexLlmProxyEnabled: false,
        codexLlmProxyMode: 'subscription',
        claudeLlmProxyEnabled: false,
        openCodeLlmProxyEnabled: false,
        claudeTokenSaverEnabled: true,
        codexTokenSaverEnabled: true,
        openCodeTokenSaverEnabled: true,
        tokenSaverCaptureEnabled: false,
        removeCoAuthorTrailers: true,
        machineName: 'Playwright machine'
    };
}

async function installSettingsApi(page, initialSettings) {
    let settings = { ...initialSettings };
    const writes = [];

    await page.route('**/api/v1/settings/db-size', route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ bytes: 0 })
    }));

    await page.route('**/api/v1/settings/pin/status', route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSet: false })
    }));

    await page.route('**/api/v1/settings', async route => {
        const request = route.request();
        if (request.method() === 'POST') {
            const body = request.postDataJSON();
            writes.push(body);
            settings = {
                ...settings,
                ...body,
                apiKey: body.clearApiKey ? '' : (body.apiKey || settings.apiKey)
            };
        }

        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify(settings)
        });
    });

    return { writes };
}

async function openSettings(page) {
    await page.goto('/');
    await expect(page.locator('#terminal-settings-btn')).toBeVisible({ timeout: 15_000 });
    await page.locator('.app-subnav-link[data-action="navigate-settings"]:visible').click();

    const root = page.locator('[data-view="settings"]');
    await expect(root).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    return {
        root,
        toggle: root.locator('#setting-route-through-viberails-ai'),
        apiKey: root.locator('#setting-api-key'),
        save: root.locator('#settings-save-button'),
        testRelay: root.locator('#settings-http-relay-test-button'),
        hint: root.locator('#settings-http-relay-test-hint')
    };
}

test('relay test availability follows the saved toggle and saved API key', async ({ page }) => {
    const api = await installSettingsApi(page, buildSettings(false));
    const ui = await openSettings(page);

    await expect(ui.testRelay).toBeDisabled();
    await ui.toggle.check();
    await expect(ui.testRelay).toBeDisabled();
    await expect(ui.hint).toContainText('Save or discard');

    await ui.save.click();
    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].routeThroughVibeRailsAi).toBe(true);
    await expect(ui.testRelay).toBeEnabled();

    await ui.apiKey.fill('unsaved-key');
    await expect(ui.testRelay).toBeDisabled();
    await expect(ui.hint).toContainText('Save or discard');

    await ui.apiKey.fill(MASKED_API_KEY);
    await expect(ui.testRelay).toBeEnabled();
});

test('individual relay actions preserve methods, paths, bodies, auth, and raw responses', async ({ page }) => {
    await installSettingsApi(page, buildSettings(true));
    const requests = [];

    await page.route(HTTP_RELAY_ROUTE, async route => {
        const request = route.request();
        const url = new URL(request.url());
        requests.push({
            method: request.method(),
            path: url.pathname,
            headers: request.headers(),
            body: request.postDataJSON?.() ?? null
        });

        const method = request.method();
        const status = method === 'POST'
            ? 201
            : method === 'DELETE'
                ? 204
                : method === 'GET' && url.pathname.endsWith('/1')
                    ? 404
                    : 200;
        await route.fulfill({
            status,
            headers: {
                'content-type': 'application/json',
                'x-relay-method': method
            },
            body: status === 204 ? undefined : JSON.stringify({
                method,
                path: url.pathname,
                title: '<img src=x onerror="window.__relayXss=true">'
            })
        });
    });

    const ui = await openSettings(page);
    await ui.testRelay.click();

    const modal = page.locator('#http-relay-test-modal');
    const id = modal.locator('#http-relay-test-id');
    await expect(id).toHaveValue('1');

    await modal.locator('[data-http-relay-method="GET"]').click();
    await expect(modal.locator('[data-http-relay-result="GET"] [data-http-relay-status]'))
        .toContainText('404');
    await expect(modal.locator('[data-http-relay-result="GET"]'))
        .toHaveAttribute('data-state', 'error');
    await expect(modal.locator('[data-http-relay-result="GET"] [data-http-relay-body]'))
        .toContainText('<img src=x');
    await expect(modal.locator('[data-http-relay-result="GET"] img')).toHaveCount(0);
    expect(await page.evaluate(() => window.__relayXss)).toBeUndefined();

    await id.fill('');
    await modal.locator('[data-http-relay-method="GET"]').click();
    await expect.poll(() => requests.filter(request => request.method === 'GET').length).toBe(2);
    await modal.locator('[data-http-relay-method="POST"]').click();
    await expect(modal.locator('[data-http-relay-result="POST"] [data-http-relay-status]'))
        .toContainText('201');

    await modal.locator('[data-http-relay-method="PUT"]').click();
    await expect(modal.locator('[data-http-relay-result="PUT"] [data-http-relay-status]'))
        .toHaveText('Not sent');

    await id.fill('7');
    await modal.locator('[data-http-relay-method="PUT"]').click();
    await expect(modal.locator('[data-http-relay-result="PUT"] [data-http-relay-status]'))
        .toContainText('200');
    await modal.locator('[data-http-relay-method="DELETE"]').click();
    await expect(modal.locator('[data-http-relay-result="DELETE"] [data-http-relay-status]'))
        .toContainText('204');
    await expect(modal.locator('[data-http-relay-result="DELETE"] [data-http-relay-body]'))
        .toHaveText('(empty response body)');

    expect(requests.map(request => `${request.method} ${request.path}`)).toEqual([
        'GET /api/v1/http-relay/test/posts/1',
        'GET /api/v1/http-relay/test/posts',
        'POST /api/v1/http-relay/test/posts',
        'PUT /api/v1/http-relay/test/posts/7',
        'DELETE /api/v1/http-relay/test/posts/7'
    ]);
    expect(requests.every(request => Boolean(request.headers.viberails_tab))).toBe(true);
    expect(requests[2].body).toMatchObject({
        title: 'VibeRails WSS POST test',
        userId: 1
    });
    expect(requests[3].body).toMatchObject({
        id: 7,
        title: 'VibeRails WSS PUT test',
        userId: 1
    });
});

test('Run all dispatches all four methods concurrently for the selected ID', async ({ page }) => {
    await installSettingsApi(page, buildSettings(true));
    const requests = [];
    let active = 0;
    let maxActive = 0;

    await page.route(HTTP_RELAY_ROUTE, async route => {
        active++;
        maxActive = Math.max(maxActive, active);
        requests.push(route.request().method());
        await new Promise(resolve => setTimeout(resolve, 60));
        active--;
        await route.fulfill({
            status: route.request().method() === 'POST' ? 201 : 200,
            contentType: 'application/json',
            body: JSON.stringify({ ok: true })
        });
    });

    const ui = await openSettings(page);
    await ui.testRelay.click();
    const modal = page.locator('#http-relay-test-modal');
    await modal.locator('#http-relay-test-id').fill('2147483648');
    await modal.locator('[data-http-relay-run-all]').click();
    await expect(modal.locator('#http-relay-test-validation'))
        .toContainText('positive whole number');
    expect(requests).toHaveLength(0);

    await modal.locator('#http-relay-test-id').fill('9');
    await modal.locator('[data-http-relay-run-all]').click();

    await expect.poll(() => requests.length).toBe(4);
    for (const method of ['GET', 'POST', 'PUT', 'DELETE']) {
        await expect(modal.locator(`[data-http-relay-result="${method}"]`))
            .toHaveAttribute('data-state', 'success');
    }
    expect(new Set(requests)).toEqual(new Set(['GET', 'POST', 'PUT', 'DELETE']));
    expect(maxActive).toBeGreaterThan(1);
});

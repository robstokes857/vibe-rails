// Production frontend with intercepted APIs. No application database or GitHub calls.
const { test, expect } = require('@playwright/test');

function reviewFixture() {
    return {
        repository: 'octo/widgets', url: 'https://github.com/octo/widgets/pull/7',
        pullRequest: {
            number: 7, title: 'Widgets', author: 'octocat', summary: 'Saved overview', status: 'in_review',
            gitHubState: 'open', completedCount: 1, openedUtc: '2026-08-01T00:00:00Z'
        },
        reviews: [
            { id: 10, status: 'in_progress', feedback: 'Saved draft', verdict: 'comments', blocking: false, headSha: 'abc1234' },
            { id: 9, status: 'completed', feedback: 'Previous pass', verdict: 'comments', blocking: false }
        ]
    };
}

async function mount(page, detail, listExtras = {}) {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'reviews-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/hooks/status': { inGitRepo: true, isInstalled: true },
            '/api/v1/pr-reviews': {
                repository: 'octo/widgets', repositoryUrl: 'https://github.com/octo/widgets',
                problem: null, pullRequests: [detail.pullRequest], ...listExtras
            },
            '/api/v1/pr-reviews/queue': {
                repository: 'octo/widgets', next: null, queue: [], skipped: [],
                counts: { open: 0, eligible: 0, drafts: 0, upToDate: 0 }
            },
            '/api/v1/pr-reviews/7': detail
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=pr-reviews', { waitUntil: 'domcontentloaded' });
    await page.locator('[data-pr-open="7"]').first().click();
    await expect(page.getByRole('heading', { name: 'Widgets', exact: true })).toBeVisible();
}

for (const useLegacy of [false, true]) {
    test(`legacy history conflict can be inspected and resolved with ${useLegacy ? 'legacy' : 'current'} history`, async ({ page }) => {
        const detail = reviewFixture();
        const list = { legacyConflicts: [{ number: 7, sourceHash: 'a'.repeat(64), targetHash: 'b'.repeat(64),
            current: { pullRequest: null, reviews: [] }, legacy: { pullRequest: detail.pullRequest, reviews: detail.reviews } }] };
        await mount(page, detail, list);
        let submitted;
        await page.route('**/api/v1/pr-reviews/7/legacy-conflict/resolve', route => {
            submitted = route.request().postDataJSON();
            list.legacyConflicts = [];
            return route.fulfill({ json: { number: 7 } });
        });
        await page.locator('[data-pr-back]').click();
        const panel = page.locator('[data-pr-legacy-conflicts]');
        await panel.getByText('Current history', { exact: true }).click();
        await expect(panel.getByText('This copy of the PR was deleted.')).toBeVisible();
        await panel.getByText('Legacy history', { exact: true }).click();
        await expect(panel.getByText('Saved draft', { exact: true })).toBeVisible();
        await panel.getByRole('button', { name: useLegacy ? 'Use legacy history' : 'Keep current history', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: useLegacy ? 'Use legacy history' : 'Keep current history', exact: true }).click();
        await expect(panel).toBeHidden();
        expect(submitted).toEqual({ sourceHash: 'a'.repeat(64), targetHash: 'b'.repeat(64), useLegacy });
    });
}

test('dedicated PR Reviewer settings persist and disclose in-progress behavior', async ({ page }) => {
    await mount(page, reviewFixture());
    let enabled = false;
    await page.route('**/api/v1/pr-reviews/settings', route => {
        if (route.request().method() === 'PUT') enabled = route.request().postDataJSON().enabled;
        return route.fulfill({ json: { enabled } });
    });
    await page.locator('[data-action="navigate-settings"]').click();
    await page.getByRole('tab', { name: 'PR Reviewer', exact: true }).click();
    const toggle = page.getByRole('switch', { name: 'Enable PR Reviewer and GitHub publishing' });
    await expect(toggle).toBeEnabled();
    await expect(toggle).not.toBeChecked();
    await expect(page.getByText(/A run already in progress can finish/)).toBeVisible();
    await toggle.check();
    await expect(page.locator('[data-pr-reviewer-status]')).toHaveText(/Saved:.*on/);
    await page.reload();
    await page.locator('[data-action="navigate-settings"]').click();
    await page.getByRole('tab', { name: 'PR Reviewer', exact: true }).click();
    await expect(toggle).toBeChecked();
    await toggle.uncheck();
    await expect(page.locator('[data-pr-reviewer-status]')).toHaveText(/Saved:.*off/);
    expect(enabled).toBe(false);
});

test('delivery errors stay distinct from local completion and retry checks show confirmed receipts', async ({ page }) => {
    const detail = reviewFixture();
    detail.reviews = [detail.reviews[1]];
    detail.pullRequest.status = 'blocker';
    detail.deliveries = [{ reviewId: 9, repository: 'octo/widgets', prNumber: 7, headSha: 'abc1234', event: 'REQUEST_CHANGES',
        status: 'unknown', error: 'GitHub delivery is unconfirmed.' }];
    await mount(page, detail);
    await expect(page.getByText('GitHub delivery is unconfirmed.')).toBeVisible();
    await page.route('**/api/v1/pr-reviews/7/reviews/9/publish', route => {
        detail.deliveries[0] = { ...detail.deliveries[0], status: 'published', gitHubId: 42, login: 'reviewer', error: null };
        return route.fulfill({ json: detail.deliveries[0] });
    });
    await page.getByRole('button', { name: 'Check GitHub receipt' }).click();
    await expect(page.getByRole('link', { name: 'Published REQUEST_CHANGES' })).toHaveAttribute('href', 'https://github.com/octo/widgets/pull/7#pullrequestreview-42');
    await expect(page.getByRole('button', { name: 'Check GitHub receipt' })).toHaveCount(0);
});

test('pending feedback survives editing and cancelling other forms', async ({ page }) => {
    await mount(page, reviewFixture());
    await page.locator('#pr-feedback').fill('Unsaved finding');
    await page.locator('#pr-summary').fill('Unsaved overview');
    await page.locator('#pr-verdict').selectOption('request_changes');
    await page.locator('[data-pr-edit-review="9"]').click();
    await expect(page.locator('#pr-feedback')).toHaveValue('Unsaved finding');
    await expect(page.locator('#pr-summary')).toHaveValue('Unsaved overview');
    await expect(page.locator('#pr-blocking')).toBeChecked();

    await page.locator('#pr-edit-feedback-9').fill('Unsaved history edit');
    await page.getByRole('button', { name: 'Edit details', exact: true }).click();
    await expect(page.locator('#pr-feedback')).toHaveValue('Unsaved finding');
    await expect(page.locator('#pr-edit-feedback-9')).toHaveValue('Unsaved history edit');
    await page.locator('[data-pr-cancel-pr-edit]').click();
    await page.locator('[data-pr-cancel-edit]').click();
    await expect(page.locator('#pr-feedback')).toHaveValue('Unsaved finding');
    await expect(page.locator('#pr-blocking')).toBeChecked();
});

test('a draft save locks inputs and preserves an unrelated history edit', async ({ page }) => {
    const detail = reviewFixture();
    await mount(page, detail);
    let finishWrite;
    const writeGate = new Promise(resolve => { finishWrite = resolve; });
    await page.route('**/api/v1/pr-reviews/7/reviews/draft', async route => {
        const body = route.request().postDataJSON();
        await writeGate;
        Object.assign(detail.reviews[0], body);
        detail.pullRequest.summary = body.summary;
        await route.fulfill({ json: { pullRequest: detail.pullRequest, review: detail.reviews[0] } });
    });
    await page.locator('#pr-feedback').fill('New finding');
    await page.locator('[data-pr-edit-review="9"]').click();
    await page.locator('#pr-edit-feedback-9').fill('Keep this history edit');
    await page.getByRole('button', { name: 'Save draft', exact: true }).click();
    await expect(page.locator('#pr-feedback')).toBeDisabled();
    await expect(page.locator('#pr-edit-feedback-9')).toBeDisabled();
    finishWrite();
    await expect(page.locator('#pr-feedback')).toBeEnabled();
    await expect(page.locator('#pr-feedback')).toHaveValue('New finding');
    await expect(page.locator('#pr-edit-feedback-9')).toHaveValue('Keep this history edit');
});

test('starting from tracked history requests a current head SHA', async ({ page }) => {
    const detail = reviewFixture();
    detail.reviews.shift();
    detail.pullRequest.status = 'complete';
    await mount(page, detail);
    let submitted;
    await page.route('**/api/v1/pr-reviews/7/reviews/start', route => {
        submitted = route.request().postDataJSON();
        detail.reviews.unshift({
            id: 11, status: 'in_progress', headSha: 'current-head-123', feedback: '', verdict: null, blocking: false
        });
        detail.pullRequest.status = 'in_review';
        return route.fulfill({ status: 201, json: { pullRequest: detail.pullRequest, review: detail.reviews[0] } });
    });

    await page.getByRole('button', { name: 'Start review', exact: true }).click();

    await expect(page.locator('[data-pr-pending-form]')).toContainText('current-head-123');
    expect(submitted).toEqual({ useCurrentHead: true });
});

test('confirming an old delete after navigation cannot remove that review', async ({ page }) => {
    await mount(page, reviewFixture());
    const deleted = [];
    await page.route('**/api/v1/pr-reviews/reviews/9', route => {
        deleted.push(route.request().method());
        return route.fulfill({ json: { number: 7, reviewId: 9 } });
    });
    await page.locator('[data-pr-delete-review="9"]').click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toBeVisible();
    await page.evaluate(() => window.app.navigate('pr-reviews', {}));
    await expect(page.locator('[data-pr-list-page]')).toBeVisible();
    await dialog.getByRole('button', { name: 'Delete review', exact: true }).click();
    await expect(dialog).toHaveCount(0);
    expect(deleted).toEqual([]);
});

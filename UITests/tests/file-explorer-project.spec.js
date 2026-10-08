// The production picker with filesystem API fixtures; no backend or application database.
const { test, expect } = require('@playwright/test');
const DIALOG = '[role="dialog"][aria-labelledby="vb-file-explorer-title"]';

async function openPickerFixture(page) {
    await page.route('**/picker-fixture', route => route.fulfill({
        contentType: 'text/html',
        body: `<!doctype html><html><head>
            <link rel="stylesheet" href="/assets/bootstrap.min.css">
            <link rel="stylesheet" href="/style.css">
            </head><body><div id="modal-container"></div></body></html>`
    }));
    await page.goto('/picker-fixture');
    await page.evaluate(async () => {
        const { openFileExplorer } = await import('/js/modules/file-explorer.js');
        window.app = {
            data: { configs: {} },
            apiCall: async (url, method, body, options) => {
                const response = await fetch(url, { signal: options?.signal });
                if (!response.ok) throw new Error('Folder unavailable');
                return response.json();
            },
            pickFileSystemEntry(options) { return openFileExplorer(this, options); }
        };
    });
}

async function waitForFolder(page) {
    const dialog = page.locator(DIALOG);
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('[data-file-explorer-grid]')).toHaveAttribute('aria-busy', 'false');
    return dialog;
}

async function currentPath(dialog) {
    const crumb = dialog.locator('[data-file-explorer-breadcrumb] .vb-file-explorer-crumb[aria-current="location"]');
    await expect(crumb).toBeVisible();
    return crumb.getAttribute('title');
}

test('reopening the file picker uses the current project and retains only its own folder', async ({ page }) => {
    await openPickerFixture(page);
    await page.route('**/api/v1/filesystem/entries?*', async route => {
        const folder = new URL(route.request().url()).searchParams.get('path');
        await route.fulfill({ json: {
            currentPath: folder,
            defaultPath: '/projects/first',
            breadcrumbs: [{ label: folder.split('/').pop(), path: folder }],
            entries: [{ name: 'readme.md', path: `${folder}/readme.md`, kind: 'file' }]
        } });
    });
    await page.evaluate(() => {
        localStorage.setItem('viberails.fileExplorer.lastPath:file', '/projects/legacy');
    });

    const open = async (project, initialPath) => {
        await page.evaluate(({ project, initialPath }) => {
            window.app.data.configs.rootPath = project;
            window.__vbFileExplorerPick = window.app.pickFileSystemEntry({ mode: 'file', initialPath });
        }, { project, initialPath });
        return waitForFolder(page);
    };
    const cancel = async dialog => {
        await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
        await expect(page.locator(DIALOG)).toHaveCount(0);
    };

    // Ignore the pre-fix global preference, even if that directory is still readable.
    let dialog = await open('/projects/first');
    expect(await currentPath(dialog)).toBe('/projects/first');
    await cancel(dialog);

    // Accepting from a subfolder remembers that folder for this project.
    dialog = await open('/projects/first', '/projects/first/src');
    await dialog.locator('[data-file-explorer-entry][data-kind="file"]').dblclick();
    await expect(page.locator(DIALOG)).toHaveCount(0);
    expect((await page.evaluate(() => window.__vbFileExplorerPick)).path).toBe('/projects/first/src/readme.md');

    dialog = await open('/projects/second');
    expect(await currentPath(dialog)).toBe('/projects/second');
    await cancel(dialog);

    dialog = await open('/projects/first');
    expect(await currentPath(dialog)).toBe('/projects/first/src');
    await cancel(dialog);

    // An explicit caller path still wins, and canceling it does not replace the saved folder.
    dialog = await open('/projects/first', '/projects/first/docs');
    expect(await currentPath(dialog)).toBe('/projects/first/docs');
    await cancel(dialog);
    dialog = await open('/projects/first');
    expect(await currentPath(dialog)).toBe('/projects/first/src');
    await cancel(dialog);
});

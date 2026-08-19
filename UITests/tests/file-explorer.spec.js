// @ts-check
//
// Server-backed File Explorer dialog (js/modules/file-explorer.js): the desktop-style Open /
// Select Folder modal reached from the Python scripts "Add file" button and from
// window.app.pickFileSystemEntry(). READ-ONLY on purpose — the spec navigates, sorts, types
// paths, and dismisses; it never imports a script or touches signing state. Only the final
// directory-picker step accepts anything, and that only resolves a promise in the page.

const { test, expect } = require('./fixtures');

const DIALOG = '[role="dialog"][aria-labelledby="vb-file-explorer-title"]';
const IMPORT_BUTTON = '[data-python-scripts-action="import"]';

/** Strips trailing separators and case-folds so a Windows path compares like the server's. */
function pathKey(value) {
    const trimmed = String(value || '').replace(/[\\/]+$/, '');
    return /^[a-z]:/i.test(trimmed) ? trimmed.replace(/\//g, '\\').toLowerCase() : trimmed;
}

async function openJobsPage(page) {
    await page.goto('/?view=terminal-focus', { waitUntil: 'domcontentloaded' });
    await page.locator('.app-subnav-link[data-view="jobs"]:visible').click();
    await expect(page.locator('[data-python-scripts-root]')).toBeVisible({ timeout: 15_000 });
}

/** The dialog is loaded when its grid stops being busy and the status line has settled. */
async function waitForFolder(page) {
    const dialog = page.locator(DIALOG);
    await expect(dialog).toBeVisible({ timeout: 10_000 });
    await expect(dialog.locator('[data-file-explorer-grid]')).toHaveAttribute('aria-busy', 'false', { timeout: 15_000 });
    await expect(dialog.locator('[data-file-explorer-status]')).not.toHaveText('Loading…', { timeout: 15_000 });
    return dialog;
}

/** Path of the breadcrumb segment marked aria-current (its title is the full path). */
async function currentPath(dialog) {
    const crumb = dialog.locator('[data-file-explorer-breadcrumb] .vb-file-explorer-crumb[aria-current="location"]');
    await expect(crumb).toBeVisible();
    return (await crumb.getAttribute('title')) || '';
}

async function projectRoot(context) {
    const response = await context.request.get('/api/v1/context');
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    return String(body.rootPath || body.launchDirectory || '');
}

test('the Add-file dialog is a navigable, sortable, keyboard-friendly Open dialog', async ({ page, context }) => {
    await openJobsPage(page);
    const trigger = page.locator(`${IMPORT_BUTTON}:visible`).first();
    if (await trigger.count() === 0) {
        // Host-path import only exists on the active root backend; a child/relay backend has no button.
        test.skip(true, 'import button absent (not the active root backend)');
        return;
    }
    const root = await projectRoot(context);
    expect(root.length).toBeGreaterThan(0);

    await trigger.click();
    const dialog = await waitForFolder(page);
    await expect(dialog.locator('#vb-file-explorer-title')).toHaveText('Add a Python script');

    // Places sidebar: Project is always there; Home (when the host has one) moves the view.
    const places = dialog.locator('[data-file-explorer-places] .vb-file-explorer-place');
    await expect(places.filter({ hasText: 'Project' }).first()).toBeVisible();
    const startPath = await currentPath(dialog);
    expect(pathKey(startPath)).toBe(pathKey(root));
    const home = places.filter({ hasText: 'Home' }).first();
    if (await home.count() > 0) {
        const homePath = await home.getAttribute('title');
        await home.click();
        await waitForFolder(page);
        expect(pathKey(await currentPath(dialog))).toBe(pathKey(homePath));
        await expect(home).toHaveAttribute('aria-current', 'location');
    }

    // Column headers sort: the Name header flips between ascending and descending.
    const nameHeader = dialog.locator('[data-file-explorer-column="name"]');
    await expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');
    await dialog.locator('[data-file-explorer-sort="name"]').click();
    await expect(nameHeader).toHaveAttribute('aria-sort', 'descending');
    await dialog.locator('[data-file-explorer-sort="name"]').click();
    await expect(nameHeader).toHaveAttribute('aria-sort', 'ascending');

    // Typing an absolute folder path (trailing separator = "go to this folder") into the
    // File name box and pressing Enter navigates there.
    const separator = /^[a-z]:/i.test(root) ? '\\' : '/';
    const fileName = dialog.locator('[data-file-explorer-filename]');
    await fileName.fill(`${root.replace(/[\\/]+$/, '')}${separator}`);
    await fileName.press('Enter');
    await waitForFolder(page);
    expect(pathKey(await currentPath(dialog))).toBe(pathKey(root));

    // Double-clicking a folder row descends; Alt+Up climbs back out. (Linked rows are listed
    // for context only and refuse to open, so they are left out of the pick.)
    const folderRows = dialog.locator('[data-file-explorer-entry][data-kind="directory"]:not([aria-disabled="true"])');
    if (await folderRows.count() > 0) {
        const firstFolder = folderRows.first();
        const folderPath = await firstFolder.getAttribute('data-path');
        await firstFolder.dblclick();
        await waitForFolder(page);
        expect(pathKey(await currentPath(dialog))).toBe(pathKey(folderPath));
        await dialog.locator('[data-file-explorer-view]').focus();
        await page.keyboard.press('Alt+ArrowUp');
        await waitForFolder(page);
        expect(pathKey(await currentPath(dialog))).toBe(pathKey(root));
    }

    // "Files of type" exists because the caller supplied filters, with Python files selected.
    const filter = dialog.locator('[data-file-explorer-filter]');
    await expect(filter).toBeVisible();
    await expect(filter).toHaveValue('0');
    await expect(filter.locator('option:checked')).toHaveText(/^Python files/);
    // The primary button reads Open in a file picker; a highlighted muted row never appears
    // here (that is the folder picker's case, covered below).
    await expect(dialog.locator('[data-file-explorer-select-label]')).toHaveText('Open');

    // Escape closes the dialog and hands focus back to the button that opened it.
    await page.keyboard.press('Escape');
    await expect(page.locator(DIALOG)).toHaveCount(0);
    await expect.poll(() => page.evaluate(
        () => document.activeElement?.getAttribute('data-python-scripts-action') || ''
    )).toBe('import');
});

test('a directory picker resolves the folder being viewed from Select Folder', async ({ page }) => {
    await openJobsPage(page);
    await page.evaluate(() => {
        // Park the promise on window so a later evaluate can await its outcome.
        window.__vbFileExplorerPick = window.app.pickFileSystemEntry({ mode: 'directory', title: 'e2e folder' });
    });
    const dialog = await waitForFolder(page);
    await expect(dialog.locator('#vb-file-explorer-title')).toHaveText('e2e folder');
    await expect(dialog.locator('[data-file-explorer-filter]')).toBeHidden();
    await expect(dialog.locator('[data-file-explorer-select-label]')).toHaveText('Select Folder');
    const expectedPath = await currentPath(dialog);

    // Highlighting a muted file row (folder pickers list files greyed out) must not turn the
    // primary button into a dead click: Select Folder still returns the folder being viewed.
    const fileRow = dialog.locator('[data-file-explorer-entry][data-kind="file"]').first();
    if (await fileRow.count() > 0) {
        await fileRow.click();
        await expect(fileRow).toHaveClass(/is-muted/);
    }
    const select = dialog.locator('[data-file-explorer-action="select"]');
    await expect(select).toBeEnabled();
    await select.click();
    await expect(page.locator(DIALOG)).toHaveCount(0);

    const result = await page.evaluate(() => window.__vbFileExplorerPick);
    expect(result.canceled).toBe(false);
    expect(result.kind).toBe('directory');
    expect(typeof result.path).toBe('string');
    expect(pathKey(result.path)).toBe(pathKey(expectedPath));
});

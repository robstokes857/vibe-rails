// @ts-check
//
// The shared Monaco diff viewer (js/modules/diff-modal.js), a nested modal layer used by Board
// commits. These tests moved here from the Sandboxes card spec when that card left the UI.

const { test, expect } = require('./fixtures');

test('Escape disposes the shared diff editor before the layer is removed', async ({ page }) => {
    // Asserts the leak invariant through real Monaco rather than by stubbing a fake editor onto a
    // controller: after Escape there must be no diff editor and no leftover models. Monaco does
    // NOT dispose externally-set models with the editor, which is the actual bug this guards.
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    await page.evaluate(async () => {
        const { openDiffModal } = await import('/js/modules/diff-modal.js');
        window.__diffHandle = openDiffModal({
            title: 'Test diff',
            files: [{
                fileName: 'src/a.txt',
                language: 'plaintext',
                originalContent: 'one\ntwo\n',
                modifiedContent: 'one\ntwo changed\n'
            }]
        });
        window.__diffReady = await window.__diffHandle.ready;
    });

    expect(await page.evaluate(() => window.__diffReady)).toBe(true);
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(1);
    // Two models per open file: the "before" and "after" text.
    expect(await page.evaluate(() => window.monaco.editor.getModels().length)).toBe(2);

    // Escape raised from inside .monaco-editor is deliberately left to Monaco, so
    // press it from the close button instead.
    await page.locator('.vb-diff-modal [data-vb-diff-close]').focus();
    await page.keyboard.press('Escape');

    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(0);
    // getModels() is the leak signal that matters: a TextModel holds the whole file
    // text, and Monaco does not dispose externally-set models with the editor.
    // NOT getDiffEditors() — that registry accumulates and is never pruned on
    // dispose, so it grows by one per open/close even when nothing leaked.
    expect(await page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
});

test('the diff layer opens over an app.showModal dialog and gives it back on close', async ({ page }) => {
    // The reason the viewer is a nested layer: opened from a Board card editor it must
    // not destroy the dialog underneath, and closing it must return there rather than
    // dismissing both.
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    await page.evaluate(async () => {
        window.app.showModal('Underlying dialog', '<button id="under-btn">Under</button>');
        const { openDiffModal } = await import('/js/modules/diff-modal.js');
        window.__diffHandle = openDiffModal({
            title: 'Nested diff',
            files: [{ fileName: 'a.txt', language: 'plaintext', originalContent: 'a\n', modifiedContent: 'b\n' }]
        });
        await window.__diffHandle.ready;
    });

    // Both are present, and the dialog underneath is inert while the diff is up.
    await expect(page.locator('#under-btn')).toHaveCount(1);
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(1);
    expect(await page.evaluate(() =>
        document.getElementById('under-btn').closest('.modal').parentElement.inert)).toBe(true);

    await page.locator('.vb-diff-modal [data-vb-diff-close]').click();

    // The diff is gone; the dialog underneath survived and is interactive again.
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(0);
    await expect(page.locator('#under-btn')).toHaveCount(1);
    expect(await page.evaluate(() =>
        document.getElementById('under-btn').closest('.modal').parentElement.inert)).toBe(false);
});

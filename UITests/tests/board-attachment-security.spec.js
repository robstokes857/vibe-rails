const { test, expect } = require('@playwright/test');

async function isolatedViewerPage(page) {
    // Module tests against the same static assets as the app, with no production host.
    await page.route('**/attachment-fixture', route => route.fulfill({ contentType: 'text/html', body:
        '<!doctype html><html><head></head><body><div id="modal-container"><div id="parent-card"><textarea id="unsaved">unsaved description</textarea></div></div></body></html>' }));
    await page.goto('/attachment-fixture');
}

test('Markdown previews as inert source text, never as rendered HTML', async ({ page }) => {
    await isolatedViewerPage(page);
    const unexpected = [];
    page.on('request', request => { if (request.url().includes('attacker.example')) unexpected.push(request.url()); });
    const source = '# Scope\n\n**Strong** and *emphasis*\n\n'
        + '![tracker](https://attacker.example/pixel)\n\n'
        + '<svg><g/onload=alert(1)//<p>\n\n<img src=x onerror="window.__xss=1">\n\n'
        + '<iframe srcdoc="<script>alert(1)</script>"></iframe>\n\n'
        + '[bad](javascript:alert(1))';
    await page.evaluate(async markdown => {
        const { openBoardAttachment } = await import('/js/modules/board-attachments.js');
        await openBoardAttachment({ apiCall: async () => new Blob([markdown]) },
            'card-1', { id: 'att-md', name: 'notes.md', mimeType: 'text/markdown' });
    }, source);
    // The whole source survives verbatim, which is only possible if nothing parsed it.
    await expect(page.locator('.vb-board-attachment-text')).toHaveText(source);
    const result = await page.evaluate(() => ({
        live: document.querySelectorAll('.vb-board-attachment-layer img, .vb-board-attachment-layer svg,'
            + ' .vb-board-attachment-layer iframe, .vb-board-attachment-layer a, .vb-board-attachment-layer h1').length,
        executed: Boolean(window.__xss)
    }));
    expect(result.live).toBe(0);
    expect(result.executed).toBe(false);
    expect(unexpected).toEqual([]);
});

test('active binaries are download-only; nested close restores card and revokes URLs', async ({ page }) => {
    await isolatedViewerPage(page);
    await page.evaluate(async () => {
        window.__revoked = [];
        const revoke = URL.revokeObjectURL.bind(URL);
        URL.revokeObjectURL = url => { window.__revoked.push(url); revoke(url); };
        const { openBoardAttachment } = await import('/js/modules/board-attachments.js');
        document.querySelector('#unsaved').focus();
        await openBoardAttachment({ apiCall: async () => new Blob(['<svg onload="window.__xss=1"></svg>']) },
            'card-1', { id: 'att-1', name: '<img onerror=alert(1)>.svg', mimeType: 'application/octet-stream' });
    });
    await expect(page.locator('[data-attachment-body]')).toContainText('Download this attachment');
    await expect(page.locator('.modal-title')).toHaveText('<img onerror=alert(1)>.svg');
    expect(await page.locator('#parent-card').evaluate(element => element.inert)).toBe(true);
    expect(await page.locator('.vb-board-attachment-layer img, .vb-board-attachment-layer iframe, .vb-board-attachment-layer svg').count()).toBe(0);
    const download = page.waitForEvent('download');
    await page.locator('[data-attachment-download]').click();
    await download;
    await page.keyboard.press('Escape');
    expect(await page.locator('.vb-board-attachment-layer').count()).toBe(0);
    expect(await page.locator('#parent-card').evaluate(element => element.inert)).toBe(false);
    await expect(page.locator('#unsaved')).toHaveValue('unsaved description');
    await expect(page.locator('#unsaved')).toBeFocused();
    expect(await page.evaluate(() => window.__revoked.length)).toBe(1);
    expect(await page.evaluate(() => Boolean(window.__xss))).toBe(false);
});

test('closing a loading preview aborts its authenticated request and restores the parent', async ({ page }) => {
    await isolatedViewerPage(page);
    await page.evaluate(async () => {
        const { openBoardAttachment } = await import('/js/modules/board-attachments.js');
        window.__aborted = false;
        void openBoardAttachment({ apiCall: (_url, _method, _data, options) => new Promise((_resolve, reject) => {
            options.signal.addEventListener('abort', () => { window.__aborted = true; reject(new DOMException('Aborted', 'AbortError')); });
        }) }, 'card-1', { id: 'att-1', name: 'slow.pdf', mimeType: 'application/pdf' });
    });
    await page.locator('[data-attachment-close]').click();
    expect(await page.evaluate(() => window.__aborted)).toBe(true);
    expect(await page.locator('#parent-card').evaluate(element => element.inert)).toBe(false);
});

test('description image filenames stay literal alt text without generated attribute corruption', async ({ page }) => {
    await isolatedViewerPage(page);
    const result = await page.evaluate(async () => {
        const { renderCommentHtml } = await import('/js/modules/board-text.js');
        const caption = 'https://example.com `caption` " onerror="window.__imageXss=1';
        const image = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
        const host = document.createElement('div');
        host.innerHTML = renderCommentHtml(`![${caption}](attachment:img_1)`, { attachments: [{ id: 'img_1', url: image }] });
        document.body.append(host);
        const element = host.querySelector('img');
        element.dispatchEvent(new Event('error'));
        return { caption, alt: element.alt, imageId: element.dataset.boardImage, src: element.src,
            attributes: [...element.attributes].map(a => a.name).sort(), tags: [...host.querySelectorAll('*')].map(e => e.tagName),
            executed: Boolean(window.__imageXss) };
    });
    expect(result.alt).toBe(result.caption);
    expect(result.imageId).toBe('img_1');
    expect(result.src).toMatch(/^data:image\/png;base64,/);
    expect(result.attributes).toEqual(['alt', 'class', 'data-board-image', 'loading', 'src']);
    expect(result.tags).toEqual(['IMG']);
    expect(result.executed).toBe(false);
});

// A real two-page PDF, xref offsets computed so pdf.js takes its normal parse path
// rather than its damaged-file recovery path. Pure ASCII, so byte length == string length.
function twoPagePdf() {
    const objects = [
        '<</Type/Catalog/Pages 2 0 R>>',
        '<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>',
        '<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>',
        '<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 100]>>'
    ];
    let pdf = '%PDF-1.4\n';
    const offsets = [];
    objects.forEach((body, index) => {
        offsets.push(pdf.length);
        pdf += `${index + 1} 0 obj\n${body}\nendobj\n`;
    });
    const startxref = pdf.length;
    pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`
        + offsets.map(offset => `${String(offset).padStart(10, '0')} 00000 n \n`).join('')
        + `trailer\n<</Size ${objects.length + 1}/Root 1 0 R>>\nstartxref\n${startxref}\n%%EOF\n`;
    return Array.from(Buffer.from(pdf, 'latin1'));
}

// Guards the trimmed vendored build: only pdf.min.js and pdf.worker.min.js ship, with no
// CMaps, standard fonts or Wasm decoders. If a future upgrade reinstates a dependency on
// those directories, this paints nothing and fails here rather than in front of a user.
test('PDF previews paint and page through the trimmed pdf.js build', async ({ page }) => {
    await isolatedViewerPage(page);
    const missing = [];
    page.on('response', response => { if (response.status() === 404) missing.push(new URL(response.url()).pathname); });
    await page.evaluate(async bytes => {
        const { openBoardAttachment } = await import('/js/modules/board-attachments.js');
        await openBoardAttachment({ apiCall: async () => new Blob([new Uint8Array(bytes)], { type: 'application/pdf' }) },
            'card-1', { id: 'att-pdf', name: 'two-pages.pdf', mimeType: 'application/pdf' });
    }, twoPagePdf());

    await expect(page.locator('[data-pdf-position]')).toHaveText('Page 1 of 2');
    const canvas = await page.locator('.vb-board-pdf-canvas').evaluate(element => ({
        width: element.width, height: element.height,
        painted: element.getContext('2d').getImageData(0, 0, 1, 1).data.length === 4
    }));
    expect(canvas.width).toBeGreaterThan(0);
    expect(canvas.height).toBeGreaterThan(0);
    expect(canvas.painted).toBe(true);

    await expect(page.locator('[data-pdf-prev]')).toBeDisabled();
    await page.locator('[data-pdf-next]').click();
    await expect(page.locator('[data-pdf-position]')).toHaveText('Page 2 of 2');
    await expect(page.locator('[data-pdf-next]')).toBeDisabled();
    expect(missing).toEqual([]);
});

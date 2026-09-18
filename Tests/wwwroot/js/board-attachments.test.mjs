import test from 'node:test';
import assert from 'node:assert/strict';
import { getAttachmentPreviewKind, openBoardAttachment, disposeBoardAttachmentPreview } from '../../../VibeRails/wwwroot/js/modules/board-attachments.js';

test('preview choice only trusts server-normalized safe MIME categories', () => {
    for (const mimeType of ['image/svg+xml', 'text/html', 'application/javascript', 'image/unknown', '']) {
        assert.equal(getAttachmentPreviewKind({ mimeType, name: 'innocent.pdf' }), 'download');
    }
    assert.equal(getAttachmentPreviewKind({ mimeType: 'image/png' }), 'image');
    assert.equal(getAttachmentPreviewKind({ mimeType: 'application/pdf' }), 'pdf');
});

test('Markdown previews as its own source rather than as rendered HTML', () => {
    assert.equal(getAttachmentPreviewKind({ mimeType: 'text/markdown' }), 'text');
    assert.equal(getAttachmentPreviewKind({ mimeType: 'text/plain' }), 'text');
});

function previewDom(t) {
    const originals = ['document', 'MutationObserver', 'FileReader'].map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]);
    const created = [], revoked = [], nodes = [];
    function element(tag = 'div') {
        const selectors = new Map();
        const node = {
            tag, children: [], isConnected: true,
            append(child) { this.children.push(child); child.parent = this; },
            replaceChildren() { this.children = []; },
            querySelector(selector) {
                if (!selectors.has(selector)) selectors.set(selector, element());
                return selectors.get(selector);
            },
            querySelectorAll() { return []; },
            addEventListener() {}, focus() {}, setAttribute() {}, getAttribute() { return null; }, removeAttribute() {},
            remove() {
                this.isConnected = false;
                if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this);
            }
        };
        nodes.push(node);
        return node;
    }
    const host = element();
    globalThis.document = {
        getElementById(id) { return id === 'modal-container' ? host : {}; },
        createElement: element, activeElement: null,
        addEventListener() {}, removeEventListener() {}, body: element(), head: element()
    };
    globalThis.MutationObserver = class { observe() {} disconnect() {} };
    globalThis.FileReader = class { constructor() { throw new Error('Image preview must not copy bytes into a data URL'); } };
    t.mock.method(URL, 'createObjectURL', blob => { created.push(blob); return `blob:preview-${created.length}`; });
    t.mock.method(URL, 'revokeObjectURL', url => revoked.push(url));
    t.after(() => {
        disposeBoardAttachmentPreview();
        for (const [key, descriptor] of originals) {
            if (descriptor) Object.defineProperty(globalThis, key, descriptor);
            else delete globalThis[key];
        }
    });
    return { created, revoked, nodes };
}

test('image previews use a Blob URL and revoke it exactly once on close', async t => {
    const h = previewDom(t);
    const blob = new Blob(['raster bytes'], { type: 'application/octet-stream' });
    const close = await openBoardAttachment({ apiCall: async () => blob }, 'card',
        { id: 'image', name: 'image.png', mimeType: 'image/png' });
    assert.equal(h.created.length, 1);
    assert.equal(h.created[0].size, blob.size);
    assert.equal(h.created[0].type, 'image/png');
    assert.equal(h.nodes.find(node => node.tag === 'img').src, 'blob:preview-1');
    close();
    close();
    assert.deepEqual(h.revoked, ['blob:preview-1']);
});

test('closing during fetch prevents a late image response from allocating a Blob URL', async t => {
    const h = previewDom(t);
    let finish;
    const opening = openBoardAttachment({ apiCall: () => new Promise(resolve => { finish = resolve; }) },
        'card', { id: 'image', name: 'image.png', mimeType: 'image/png' });
    disposeBoardAttachmentPreview();
    finish(new Blob(['raster bytes']));
    await opening;
    assert.deepEqual(h.created, []);
    assert.deepEqual(h.revoked, []);
});

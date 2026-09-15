import test from 'node:test';
import assert from 'node:assert/strict';
import { getAttachmentPreviewKind } from '../../../VibeRails/wwwroot/js/modules/board-attachments.js';

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

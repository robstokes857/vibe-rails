import { escapeHtml } from './utils.js';
import {
    fileToAttachmentPayload,
    getAttachmentPreviewKind
} from './board-attachments.js';

const RASTER_DATA_URL_RE = /^data:image\/(?:png|jpeg|gif|webp);base64,/i;
const fileSize = (bytes) => {
    const size = Math.max(0, Number(bytes) || 0);
    return size >= 1024 * 1024
        ? `${(size / (1024 * 1024)).toFixed(1)} MB`
        : size >= 1024
          ? `${Math.ceil(size / 1024)} KB`
          : `${size} bytes`;
};

export function attachmentSection(count = 0) {
    return `<section class="board-block">
        <h3 class="board-block-label">Attachments <span class="board-count" data-board-count="attachments">${Number(count) || 0}</span></h3>
        <div class="board-attachment-list" data-board-attachments></div>
        <button type="button" class="btn btn-sm btn-outline-secondary" data-board-add-files>
            <i class="fa-solid fa-paperclip" aria-hidden="true"></i> Upload files
        </button>
        <input type="file" hidden multiple data-board-files>
        <p class="board-editor-muted mt-2">Up to 40 files, any size. Preview images, text and PDFs.</p>
    </section>`;
}

/** Owns presentation and pending files; only the supplied adapter can persist or read an attachment. */
export function createEditorAttachments({
    editor,
    getOwner,
    isBusy,
    setBusy,
    upload,
    remove,
    preview,
    confirmRemove,
    onChanged,
    showError
}) {
    let disposed = false;
    const abort = new AbortController();
    const host = editor.querySelector('[data-board-attachments]');
    const current = () => !disposed && editor.isConnected;

    function render() {
        if (!current() || !host) return;
        const owner = getOwner();
        const attachments = owner.attachments || [],
            pending = owner.pendingAttachments || [];
        const count = editor.querySelector('[data-board-count="attachments"]');
        if (count)
            count.textContent = String(attachments.length + pending.length);
        const lead = (attachment) =>
            RASTER_DATA_URL_RE.test(String(attachment.url || ''))
                ? `<img class="board-attachment-thumb" src="${escapeHtml(attachment.url)}" alt="" loading="lazy">`
                : '<i class="fa-solid fa-file" aria-hidden="true"></i>';
        host.innerHTML =
            attachments
                .map(
                    (attachment) => `
            <div class="board-attachment-row">
                <button type="button" class="board-side-main" data-board-view-attachment="${escapeHtml(attachment.id)}">
                    ${lead(attachment)}
                    <span class="board-side-copy"><span class="board-side-title">${escapeHtml(attachment.name)}</span>
                    <span class="board-side-sub">${fileSize(attachment.bytes)} · ${getAttachmentPreviewKind(attachment) === 'download' ? 'Download' : 'Preview'}</span></span>
                </button>
                <button type="button" class="btn btn-sm btn-outline-danger board-attachment-remove" data-board-remove-attachment="${escapeHtml(attachment.id)}" aria-label="Delete ${escapeHtml(attachment.name)}"><i class="fa-solid fa-trash-can" aria-hidden="true"></i> Delete</button>
            </div>`
                )
                .join('') +
                pending
                    .map(
                        (attachment, index) => `
            <div class="board-attachment-row"><span class="board-side-copy"><span class="board-side-title">${escapeHtml(attachment.name)}</span>
                <span class="board-side-sub">${fileSize(attachment.bytes)} · Uploads when you save</span></span>
                <button type="button" class="btn btn-sm btn-outline-secondary board-attachment-remove" data-board-remove-pending="${index}" aria-label="Remove ${escapeHtml(attachment.name)}"><i class="fa-solid fa-xmark" aria-hidden="true"></i> Remove</button>
            </div>`
                    )
                    .join('') ||
            '<p class="board-editor-muted">No files attached.</p>';
    }

    function changed() {
        if (!current()) return;
        render();
        editor
            .querySelectorAll('[data-board-composer]')
            .forEach((composer) => composer._refreshPreview?.());
        onChanged?.();
    }

    host?.addEventListener(
        'click',
        async (event) => {
            const button = event.target.closest('button');
            if (!button || !current()) return;
            const owner = getOwner();
            try {
                if (button.hasAttribute('data-board-view-attachment')) {
                    const attachment = owner.attachments?.find(
                        (item) => item.id === button.dataset.boardViewAttachment
                    );
                    if (attachment)
                        await preview(attachment, { signal: abort.signal });
                    return;
                }
                if (isBusy()) return;
                if (button.hasAttribute('data-board-remove-pending')) {
                    const removed = owner.pendingAttachments?.splice(
                        Number(button.dataset.boardRemovePending),
                        1
                    )[0];
                    if (removed) {
                        const escapedId = removed.id.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
                        const token = new RegExp(`!?\\[[^\\]\\r\\n]*\\]\\(attachment:${escapedId}\\)`, 'g');
                        editor.querySelectorAll('[data-board-composer-input]').forEach(input => {
                            const value = input.value.replace(token, '');
                            if (value !== input.value) {
                                input.value = value;
                                input.dispatchEvent(new Event('input', { bubbles: true }));
                            }
                        });
                    }
                    changed();
                    return;
                }
                const attachment = owner.attachments?.find(
                    (item) => item.id === button.dataset.boardRemoveAttachment
                );
                if (!attachment) return;
                setBusy(true);
                button.disabled = true;
                try {
                    if (!(await confirmRemove(attachment)) || !current())
                        return;
                    await remove(attachment, { signal: abort.signal });
                    if (!current()) return;
                    owner.attachments = owner.attachments.filter(
                        (item) => item.id !== attachment.id
                    );
                    changed();
                } finally {
                    setBusy(false);
                    button.disabled = false;
                }
            } catch (error) {
                if (current())
                    showError(
                        error?.message || 'Could not open or remove the file.'
                    );
            }
        },
        { signal: abort.signal }
    );

    async function addFiles(composer, input, files, { inline = true } = {}) {
        if (!files.length || !current() || isBusy()) return;
        setBusy(true);
        const busy = composer?.querySelector('[data-board-composer-busy]');
        if (busy) busy.hidden = false;
        // Keep the insertion at the original selection while uploads run. Track edits
        // before that selection so moving the caret or typing elsewhere cannot redirect it.
        let previous = input?.value || '',
            start = input?.selectionStart ?? previous.length,
            end = input?.selectionEnd ?? start;
        const track = () => {
            const next = input.value;
            let left = 0,
                right = 0;
            while (
                left < Math.min(previous.length, next.length) &&
                previous[left] === next[left]
            )
                left++;
            while (
                right < Math.min(previous.length, next.length) - left &&
                previous[previous.length - 1 - right] ===
                    next[next.length - 1 - right]
            )
                right++;
            const oldEnd = previous.length - right,
                delta = next.length - previous.length;
            const adjust = (point) =>
                point <= left
                    ? point
                    : point >= oldEnd
                      ? point + delta
                      : next.length - right;
            start = adjust(start);
            end = adjust(end);
            previous = next;
        };
        input?.addEventListener('input', track);
        try {
            const owner = getOwner();
            for (const file of files) {
                if (
                    (owner.attachments?.length || 0) +
                        (owner.pendingAttachments?.length || 0) >=
                    40
                )
                    throw new Error('At most 40 attachments are allowed.');
                const payload = await fileToAttachmentPayload(file);
                if (!current()) return;
                let attachment;
                if (!owner.id) {
                    attachment = {
                        ...payload,
                        id: `pending_${crypto.randomUUID().replaceAll('-', '')}`
                    };
                    (owner.pendingAttachments ||= []).push(attachment);
                } else {
                    attachment = await upload(payload, {
                        signal: abort.signal
                    });
                    if (!current()) return;
                    (owner.attachments ||= []).push(attachment);
                }
                if (
                    inline &&
                    input &&
                    getAttachmentPreviewKind(attachment) === 'image'
                ) {
                    const focused = document.activeElement === input;
                    const caret = insertAttachmentAtCursor(
                        input,
                        `![${attachment.name.replace(/[\[\]\r\n]/g, '')}](attachment:${attachment.id})`,
                        start,
                        end,
                        focused
                    );
                    start = end = caret;
                    previous = input.value;
                }
                changed();
            }
            input?.dispatchEvent(new Event('input', { bubbles: true }));
        } catch (error) {
            if (current())
                showError(error?.message || 'Failed to add the file.');
        } finally {
            input?.removeEventListener('input', track);
            if (busy) busy.hidden = true;
            setBusy(false);
        }
    }

    return {
        render,
        addFiles,
        dispose() {
            disposed = true;
            abort.abort();
        }
    };
}

/** Inserts a plain attachment token without replacing text on either side of the selection. */
function insertAttachmentAtCursor(input, text, start, end, focus) {
    const oldStart = input.selectionStart,
        oldEnd = input.selectionEnd;
    const before = input.value.slice(0, start),
        after = input.value.slice(end);
    const lead = before && !before.endsWith('\n') ? '\n' : '';
    input.value = `${before}${lead}${text}\n${after}`;
    const caret = before.length + lead.length + text.length + 1;
    const delta = caret - end;
    if (focus && oldStart === start && oldEnd === end)
        input.setSelectionRange(caret, caret);
    else
        input.setSelectionRange(
            oldStart >= end ? oldStart + delta : oldStart,
            oldEnd >= end ? oldEnd + delta : oldEnd
        );
    return caret;
}

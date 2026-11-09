import { createBoardImagePreviews } from './board-image-previews.js';
import { getAttachmentPreviewKind, openEditorAttachment } from './board-attachments.js';

const RASTER_DATA = /^data:image\/(?:png|jpeg|gif|webp);base64,[A-Za-z0-9+/=\s]+$/i;

/** Image-only attachment previews inside the prompt, including files not saved yet. */
export function bindPromptImagePreviews(app, composer, definition, loadContent) {
    const abort = new AbortController();
    const host = document.createElement('div');
    host.className = 'prompt-image-previews';
    host.setAttribute('role', 'group');
    host.setAttribute('aria-label', 'Attached image previews');
    composer.append(host);
    const images = createBoardImagePreviews(() => definition, () => {}, loadContent);
    const attachments = () => [...definition.attachments, ...definition.pendingAttachments]
        .filter(attachment => getAttachmentPreviewKind(attachment) === 'image');

    function update() {
        host.replaceChildren();
        const records = attachments();
        host.hidden = records.length === 0;
        for (const attachment of records) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'btn btn-secondary prompt-image-preview';
            button.dataset.promptImage = attachment.id;
            button.setAttribute('aria-label', `Preview ${attachment.name}`);
            const img = document.createElement('img');
            img.alt = attachment.name;
            img.dataset.boardImage = attachment.id;
            const dataUrl = attachment.dataUrl || attachment.url || '';
            if (RASTER_DATA.test(dataUrl)) img.src = dataUrl;
            const caption = document.createElement('span');
            caption.textContent = attachment.name;
            button.append(img, caption);
            host.append(button);
        }
        images.hydrate(host);
    }

    host.addEventListener('click', event => {
        const button = event.target.closest('[data-prompt-image]');
        const attachment = attachments().find(item => item.id === button?.dataset.promptImage);
        if (!attachment) return;
        event.stopPropagation();
        void openEditorAttachment(app, attachment, options => {
            const dataUrl = attachment.dataUrl || attachment.url || '';
            if (!RASTER_DATA.test(dataUrl)) return loadContent(attachment.id, options);
            return new Blob([Uint8Array.from(atob(dataUrl.split(',')[1]), char => char.charCodeAt(0))],
                { type: attachment.mimeType });
        });
    }, { signal: abort.signal });
    composer._refreshPreview = update;
    update();
    return () => {
        abort.abort();
        images.dispose();
        host.remove();
        delete composer._refreshPreview;
    };
}

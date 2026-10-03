import { BoardApi } from './board-api.js';

/** Owns authenticated raster loads for one editor surface, including retries and Blob cleanup. */
export function createBoardImagePreviews(getCard, onLoad = () => {}) {
    const abort = new AbortController();
    const blobs = new Map(), fetching = new Map();
    let disposed = false;
    const attachments = () => getCard()?.attachments || [];
    function hydrate(host) {
        if (disposed || !host) return;
        for (const [id, url] of blobs) {
            if (!attachments().some(a => a.id === id)) { URL.revokeObjectURL(url); blobs.delete(id); }
        }
        for (const img of host.querySelectorAll('img[data-board-image]')) {
            if (img.hasAttribute('src')) continue;
            const id = img.dataset.boardImage;
            const record = attachments().find(a => a.id === id);
            if (!record || !/^image\/(png|jpeg|gif|webp)$/.test(record.mimeType) || !getCard()?.id) continue;
            const show = url => {
                if (!url || disposed || !img.isConnected || !attachments().some(a => a.id === id)) return;
                img.addEventListener('load', onLoad, { once: true, signal: abort.signal });
                img.src = url;
            };
            if (blobs.has(id)) { show(blobs.get(id)); continue; }
            if (!fetching.has(id)) {
                const request = BoardApi.getCardAttachmentContentAsync(getCard().id, id, { signal: abort.signal })
                    .then(blob => {
                        if (disposed || !attachments().some(a => a.id === id)) return null;
                        const url = URL.createObjectURL(new Blob([blob], { type: record.mimeType }));
                        blobs.set(id, url);
                        return url;
                    })
                    .catch(() => null) // Leave alt text; a later render can retry a transient failure.
                    .finally(() => fetching.delete(id));
                fetching.set(id, request);
            }
            void fetching.get(id).then(show);
        }
    }
    return { hydrate, dispose() {
        disposed = true;
        abort.abort();
        blobs.forEach(url => URL.revokeObjectURL(url));
        blobs.clear();
    } };
}

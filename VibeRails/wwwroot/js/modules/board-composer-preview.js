import { renderCommentHtml } from './board-text.js';
import { createBoardImagePreviews } from './board-image-previews.js';

/** One option set for a card's rendered text, so posted comments match the composer preview. */
export function boardTextOptions(card) {
    return { attachments: [...(card?.attachments || []), ...(card?.pendingAttachments || []).map(a => ({ ...a, url: a.dataUrl }))],
        sessions: card?.sessions || [], commits: card?.commits || [], markdown: true };
}

/** Owns the comment composer's authenticated raster previews. */
export function bindComposerPreview(composer, input, card) {
    const live = composer.querySelector('[data-board-composer-live]');
    if (!live) return () => {};
    const abort = new AbortController();
    let disposed = false;
    const images = createBoardImagePreviews(() => card);
    function update() {
        if (disposed) return;
        const options = boardTextOptions(card);
        const html = renderCommentHtml(input.value, options);
        live.innerHTML = html;
        images.hydrate(live);
    }
    input.addEventListener('input', update, { signal: abort.signal });
    composer._refreshPreview = update;
    update();
    return () => { disposed = true; abort.abort(); images.dispose(); delete composer._refreshPreview; };
}

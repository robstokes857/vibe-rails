import { renderCommentHtml } from './board-text.js';
import { createBoardImagePreviews } from './board-image-previews.js';

const MARKDOWN_PREFERENCE = 'viberails.board.markdown';
let markdownFallback = true; // Without storage the toggle still holds for this page.

/** The per-browser Markdown display preference shared by every composer and posted comment. */
export function boardMarkdownEnabled() {
    try { const value = localStorage.getItem(MARKDOWN_PREFERENCE); return value === null ? markdownFallback : value !== 'off'; }
    catch { return markdownFallback; }
}

export function setBoardMarkdownEnabled(enabled) {
    markdownFallback = Boolean(enabled);
    try { localStorage.setItem(MARKDOWN_PREFERENCE, enabled ? 'on' : 'off'); } catch { /* unavailable storage */ }
}

/** One option set for a card's rendered text, so posted comments match the composer preview. */
export function boardTextOptions(card) {
    return { attachments: [...(card?.attachments || []), ...(card?.pendingAttachments || []).map(a => ({ ...a, url: a.dataUrl }))],
        sessions: card?.sessions || [], commits: card?.commits || [], markdown: boardMarkdownEnabled() };
}

/** Owns authenticated raster previews and the per-browser Markdown display preference. */
export function bindComposerPreview(composer, input, card, { onMarkdownChange } = {}) {
    const toggle = composer.querySelector('[data-board-markdown]');
    const live = composer.querySelector('[data-board-composer-live]');
    const preview = composer.querySelector('[data-board-composer-preview]');
    const abort = new AbortController();
    let disposed = false;
    const images = createBoardImagePreviews(() => card);
    function update() {
        if (disposed) return;
        const options = boardTextOptions(card);
        const html = renderCommentHtml(input.value, options);
        if (preview) { preview.innerHTML = html; images.hydrate(preview); }
        if (live) {
            live.hidden = input.hidden;
            if (options.markdown) live.innerHTML = html;
            else {
                const template = document.createElement('template'); template.innerHTML = html;
                live.replaceChildren(...template.content.querySelectorAll('img[data-board-image]'));
            }
            images.hydrate(live);
        }
        toggle?.setAttribute('aria-pressed', String(options.markdown));
    }
    toggle?.addEventListener('click', () => {
        setBoardMarkdownEnabled(!boardMarkdownEnabled());
        update();
        onMarkdownChange?.();
    }, { signal: abort.signal });
    input.addEventListener('input', update, { signal: abort.signal });
    composer._refreshPreview = update;
    update();
    return () => { disposed = true; abort.abort(); images.dispose(); delete composer._refreshPreview; };
}

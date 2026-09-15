// Card files never execute as documents in our origin. We fetch with the normal API
// credentials, show text as text, and paint PDFs onto a canvas only.
import { isConfirmDialogOpen } from './utils.js';

const MAX_TEXT_PREVIEW_CHARACTERS = 200_000;
const MAX_TEXT_PREVIEW_BYTES = 1024 * 1024;
// A PDF whose worker never answers must not strand the viewer on "Loading…".
const PDF_LOAD_TIMEOUT_MS = 20_000;
let activePreview = null;

export function getAttachmentPreviewKind(attachment) {
    const mime = String(attachment?.mimeType || '').toLowerCase();
    if (['image/png', 'image/jpeg', 'image/gif', 'image/webp'].includes(mime)) return 'image';
    // Markdown previews as its own source. Rendering it would cost a parser plus a
    // sanitizer to re-earn the safety textContent gives us for free, and anyone who
    // wants it formatted can open the file in an editor.
    if (mime === 'text/markdown' || mime === 'text/plain') return 'text';
    if (mime === 'application/pdf') return 'pdf';
    return 'download';
}

// No size limit: the only ceiling on a file is what the browser can read into memory and what
// the user's own disk will hold.
export async function fileToAttachmentPayload(file) {
    const dataUrl = await new Promise((resolve, reject) => {
        const reader = new FileReader();
        reader.onload = () => resolve(String(reader.result));
        reader.onerror = () => reject(new Error('The file could not be read.'));
        reader.onabort = () => reject(new Error('Reading the file was cancelled.'));
        reader.readAsDataURL(file);
    });
    return { name: file.name || 'attachment', dataUrl, bytes: file.size, mimeType: file.type || 'application/octet-stream' };
}

export function disposeBoardAttachmentPreview() {
    activePreview?.();
    activePreview = null;
}

/** Opens an independent nested layer, keeping the card editor and unsaved edits alive. */
export async function openBoardAttachment(app, cardId, attachment) {
    disposeBoardAttachmentPreview();
    const host = document.getElementById('modal-container');
    if (!host) return () => {};
    if (!document.getElementById('board-attachment-styles')) {
        const stylesheet = document.createElement('link');
        stylesheet.id = 'board-attachment-styles';
        stylesheet.rel = 'stylesheet';
        stylesheet.href = new URL('../../assets/board/board-attachments.css', import.meta.url).href;
        document.head.append(stylesheet);
    }
    const layer = document.createElement('div');
    // This existing nested-layer class tells app.js to suspend its parent focus trap.
    layer.className = 'vb-diff-modal-layer vb-board-attachment-layer';
    layer.innerHTML = `<div class="modal fade show d-block vb-diff-modal vb-board-attachment-modal" tabindex="-1"
          role="dialog" aria-modal="true" aria-label="Attachment preview">
        <div class="modal-dialog"><div class="modal-content">
          <div class="modal-header"><h5 class="modal-title"></h5>
            <button type="button" class="btn btn-sm btn-outline-secondary me-3" data-attachment-download disabled>Download</button>
            <button type="button" class="btn-close" data-attachment-close aria-label="Close attachment"></button></div>
          <div class="vb-board-attachment-toolbar" data-attachment-toolbar hidden></div>
          <div class="modal-body vb-board-attachment-body" data-attachment-body aria-live="polite">Loading attachment…</div>
        </div></div></div><div class="modal-backdrop fade show vb-diff-modal-backdrop"></div>`;
    layer.querySelector('.modal-title').textContent = attachment.name || 'Attachment';
    const previousFocus = document.activeElement;
    const underlying = Array.from(host.children).map(element => ({ element, inert: element.inert, hidden: element.getAttribute('aria-hidden') }));
    underlying.forEach(({ element }) => { element.inert = true; element.setAttribute('aria-hidden', 'true'); });
    host.append(layer);
    const body = layer.querySelector('[data-attachment-body]');
    const toolbar = layer.querySelector('[data-attachment-toolbar]');
    const download = layer.querySelector('[data-attachment-download]');
    const abort = new AbortController();
    const urls = new Set();
    let disposed = false;
    let pdf = null;
    let pdfLoading = null;
    let renderTask = null;
    let blob = null;

    const objectUrl = value => { const url = URL.createObjectURL(value); urls.add(url); return url; };
    const close = () => {
        if (disposed) return;
        disposed = true;
        abort.abort();
        renderTask?.cancel();
        pdfLoading?.destroy().catch(() => {});
        pdf = null;
        observer.disconnect();
        document.removeEventListener('keydown', onKey, true);
        urls.forEach(url => URL.revokeObjectURL(url));
        urls.clear();
        layer.remove();
        underlying.forEach(({ element, inert, hidden }) => {
            if (!element.isConnected) return;
            element.inert = inert;
            if (hidden === null) element.removeAttribute('aria-hidden'); else element.setAttribute('aria-hidden', hidden);
        });
        if (previousFocus?.isConnected) previousFocus.focus({ preventScroll: true });
        if (activePreview === close) activePreview = null;
    };
    activePreview = close;
    const onKey = event => {
        if (isConfirmDialogOpen()) return;
        if (event.key === 'Escape') { event.preventDefault(); event.stopImmediatePropagation(); close(); }
        if (event.key === 'Tab') {
            const focusable = [...layer.querySelectorAll('button:not([disabled]),a[href],input:not([disabled])')]
                .filter(element => element.offsetParent !== null);
            const first = focusable[0], last = focusable.at(-1);
            if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last?.focus(); }
            if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first?.focus(); }
        }
    };
    const observer = new MutationObserver(() => { if (!layer.isConnected) close(); });
    observer.observe(host, { childList: true });
    document.addEventListener('keydown', onKey, true);
    layer.querySelector('[data-attachment-close]').addEventListener('click', close);
    layer.querySelector('[data-attachment-close]').focus();
    download.addEventListener('click', () => {
        if (!blob) return;
        const anchor = document.createElement('a');
        // A download-only octet-stream Blob can never become an active HTML/SVG document.
        const url = objectUrl(new Blob([blob], { type: 'application/octet-stream' }));
        anchor.href = url;
        anchor.download = attachment.name || 'attachment';
        document.body.append(anchor);
        anchor.click();
        anchor.remove();
    });

    try {
        blob = await app.apiCall(`/api/v1/board/cards/${encodeURIComponent(cardId)}/attachments/${encodeURIComponent(attachment.id)}/content`,
            'GET', null, { responseType: 'blob', showLoading: false, signal: abort.signal });
        if (disposed) return close;
        download.disabled = false;
        const kind = getAttachmentPreviewKind(attachment);
        body.replaceChildren();
        if (kind === 'image') {
            const img = document.createElement('img');
            img.className = 'vb-board-attachment-image';
            img.alt = attachment.name || 'Attached image';
            img.src = objectUrl(new Blob([blob], { type: attachment.mimeType }));
            img.onerror = () => { body.textContent = 'This image cannot be previewed. You can still download the original file.'; };
            body.append(img);
        } else if (kind === 'text') {
            const bytes = await blob.slice(0, MAX_TEXT_PREVIEW_BYTES).arrayBuffer();
            if (disposed) return close;
            // The streaming decoder tolerates only a cut final UTF-8 sequence, not malformed bytes.
            const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes, { stream: blob.size > MAX_TEXT_PREVIEW_BYTES });
            const preview = document.createElement('pre');
            preview.className = 'vb-board-attachment-text';
            preview.textContent = text.slice(0, MAX_TEXT_PREVIEW_CHARACTERS);
            if (disposed) return close;
            if (blob.size > MAX_TEXT_PREVIEW_BYTES || text.length > MAX_TEXT_PREVIEW_CHARACTERS) {
                const notice = document.createElement('p');
                notice.className = 'alert alert-info';
                notice.textContent = 'Preview truncated. Download the file to read all of it.';
                body.append(notice);
            }
            body.append(preview);
        } else if (kind === 'pdf') {
            // Previewing a PDF is a convenience, never a requirement: the bytes are already
            // downloadable and a real PDF viewer renders them better than we do. So every
            // failure below — a blocked module, a worker that never starts, a corrupt file,
            // a render that throws — collapses to the same download-only state instead of
            // propagating. A broken preview must never take the surrounding page with it.
            let loadTimer = null;
            const downloadInstead = () => {
                clearTimeout(loadTimer);
                // Tear the parser down too, not just the render. A timed-out load leaves the
                // loading task — and its worker — still chewing on the file; waiting for the
                // viewer to close to destroy it kept a stuck parse running behind a preview
                // that had already given up on it.
                pdfLoading?.destroy().catch(() => {});
                pdfLoading = null;
                pdf = null;
                if (disposed) return;
                renderTask?.cancel();
                renderTask = null;
                toolbar.hidden = true;
                toolbar.replaceChildren();
                body.replaceChildren();
                body.textContent = 'This PDF cannot be previewed here. Use Download to open it in your PDF viewer.';
            };
            try {
                const pdfjs = await import('../../assets/board/pdf.min.js');
                if (disposed) return close;
                // Cross-origin in a VS Code webview, so PDF.js cannot start a real Worker there
                // and falls back to its main-thread path. Same origin under Kestrel, where the
                // Worker starts normally.
                pdfjs.GlobalWorkerOptions.workerSrc = new URL('../../assets/board/pdf.worker.min.js', import.meta.url).href;
                const data = new Uint8Array(await blob.arrayBuffer());
                if (disposed) return close;
                pdfLoading = pdfjs.getDocument({ data,
                    // Production CSP intentionally disallows eval/Wasm compilation. PDF.js's
                    // bundled JavaScript decoders preserve that boundary without loosening CSP.
                    // We ship neither CMaps nor standard fonts — 6.4 MB across 194 files to
                    // sharpen CJK text and non-embedded base-14 fonts, which useSystemFonts
                    // already approximates on the desktops this runs on.
                    isEvalSupported: false, useWasm: false, enableXfa: false, useSystemFonts: true,
                    maxImageSize: 16_000_000, canvasMaxAreaInBytes: 32_000_000 });
                // Racing bounds a worker that never answers. The loading promise keeps this
                // race's handler even when the timeout wins, so a late rejection still counts
                // as handled and never surfaces as an unhandled rejection.
                pdf = await Promise.race([pdfLoading.promise, new Promise((_resolve, reject) => {
                    loadTimer = setTimeout(() => reject(new Error('PDF load timed out')), PDF_LOAD_TIMEOUT_MS);
                })]);
                clearTimeout(loadTimer);
                if (disposed) return close;
                toolbar.hidden = false;
                toolbar.innerHTML = '<button type="button" class="btn btn-sm btn-outline-secondary" data-pdf-prev>Previous</button>'
                    + '<span data-pdf-position></span><button type="button" class="btn btn-sm btn-outline-secondary" data-pdf-next>Next</button>';
                const canvas = document.createElement('canvas');
                canvas.className = 'vb-board-pdf-canvas';
                canvas.setAttribute('aria-label', 'PDF page preview');
                body.append(canvas);
                let pageNumber = 1;
                const previous = toolbar.querySelector('[data-pdf-prev]');
                const next = toolbar.querySelector('[data-pdf-next]');
                const position = toolbar.querySelector('[data-pdf-position]');
                const paintPage = async () => {
                    if (disposed || !pdf) return;
                    previous.disabled = next.disabled = true;
                    const page = await pdf.getPage(pageNumber);
                    if (disposed) return;
                    const unscaled = page.getViewport({ scale: 1 });
                    const scale = Math.min(2, (Math.max(300, body.clientWidth - 40)) / unscaled.width,
                        Math.sqrt(8_000_000 / (unscaled.width * unscaled.height)));
                    const viewport = page.getViewport({ scale });
                    canvas.width = Math.ceil(viewport.width);
                    canvas.height = Math.ceil(viewport.height);
                    position.textContent = `Page ${pageNumber} of ${pdf.numPages}`;
                    renderTask = page.render({ canvasContext: canvas.getContext('2d'), viewport });
                    try { await renderTask.promise; }
                    catch (error) { if (!disposed) throw error; }
                    finally { page.cleanup(); }
                    if (!disposed) { previous.disabled = pageNumber <= 1; next.disabled = pageNumber >= pdf.numPages; }
                };
                // A page that will not paint drops the whole preview rather than leaving a
                // blank canvas and a pager pointing at nothing.
                const changePage = amount => {
                    pageNumber += amount;
                    paintPage().catch(downloadInstead);
                };
                previous.addEventListener('click', () => changePage(-1));
                next.addEventListener('click', () => changePage(1));
                await paintPage();
            } catch (error) {
                // Closing mid-load is a cancellation, not a failure: let the outer handler
                // keep its existing abort path.
                if (error?.name === 'AbortError') throw error;
                downloadInstead();
            }
        } else {
            body.textContent = 'Preview is available for images, text and PDF files. Download this attachment to open it.';
        }
    } catch (error) {
        if (!disposed && error.name !== 'AbortError') {
            toolbar.hidden = true;
            body.textContent = `Preview unavailable. ${error.message || 'Download the original file to open it.'}`;
        }
    }
    return close;
}

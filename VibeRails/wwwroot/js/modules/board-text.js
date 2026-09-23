// ============================================
// Board text rendering
// ============================================
//
// Turns a comment / description body into display HTML.
//
// THE RULE THIS FILE EXISTS TO ENFORCE: escape first, always. `escapeHtml` runs
// over the whole input before any transform, so raw HTML never enters the
// pipeline and cannot survive to the output. An `<img onerror=...>` typed or
// pasted into a comment renders as visible literal text, never as an element.
// That is why this needs no sanitizer — there is nothing to sanitize, because
// no transform below ever reintroduces markup from user input. Every tag in the
// output is one this file wrote itself.
//
// If you add a transform here, it MUST hold that property. In particular:
//   - never interpolate raw (unescaped) user text into a tag
//   - never let user text decide an attribute value except through an allowlist
//     (see how images resolve through the attachment record, not through the
//      URL in the text)
//
// The supported syntax is deliberately tiny — fenced code, inline code, images,
// `@path` file references and http(s) autolinks. This is not Markdown and is not
// trying to become it.

import { escapeHtml } from './utils.js';

// Sentinels park every generated fragment until parsing is complete, so no
// transform can rewrite generated attributes or code. U+0000 cannot collide:
// control characters are stripped from the input first and tokens cannot cross it.
const BLOCK_OPEN = '\u0000B';
const BLOCK_CLOSE = '\u0000';

const FENCE_RE = /```([a-zA-Z0-9_+#.-]*)[ \t]*\r?\n?([\s\S]*?)```/g;
const INLINE_CODE_RE = /`([^`\n]+)`/g;
const IMAGE_RE = /!\[([^\]\n]*)\]\(attachment:([A-Za-z0-9_-]+)\)/g;
// Parse inline constructs together: a URL/backtick inside an image caption is
// caption text, and image syntax/URLs inside inline code are code. Trailing URL
// punctuation is excluded so "see https://x.com/a." does not swallow the period.
//
// `@path` file references (VB-35) sit right after inline code so `@x` stays code.
// A reference starts the text or follows whitespace — an email or "@claude" in
// the middle of a sentence is prose — and is either `@"a quoted path"` (already
// escaped here, so the quotes read &quot;) or a bare run of non-space characters.
// `&` is excluded from the bare run, so no escaped entity (&lt; &quot; &amp;) can
// ever be part of a token: an `@<img onerror>` stays literal text. Like URLs, a
// trailing period/comma/bracket stays outside the reference. A bare reference
// must also contain a slash or a dot (see the callback), so it looks like a path.
const INLINE_TOKEN_RE = /`([^`\n\u0000]+)`|(?<![^\s])@(?:&quot;([^\n\u0000]+?)&quot;|([^\s&`\u0000]*[^\s&`\u0000.,;:!?)\]}]))|!\[([^\]\n\u0000]*)\]\(attachment:([A-Za-z0-9_-]+)\)|\bhttps?:\/\/[^\s<>"'`\u0000]+[^\s<>"'`.,;:!?)\]}\u0000]/g;
const BARE_FILE_REF_LOOKS_LIKE_A_PATH = /[./]/;
const RASTER_DATA_URL_RE = /^data:image\/(?:png|jpe?g|gif|webp);base64,[A-Za-z0-9+/=\s]+$/i;

/** Strips control characters that would corrupt the sentinels or the display. */
function stripControlChars(value) {
    // C0 controls except tab, newline and carriage return (real content in a
    // comment body), plus DEL.
    // eslint-disable-next-line no-control-regex
    return String(value ?? '').replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '');
}

function renderCodeBlock({ lang, body }) {
    // `lang` is matched by a character-class regex, so it is already restricted
    // to safe characters, and it is escaped again here for good measure.
    const label = lang ? `<span class="board-code-lang">${escapeHtml(lang)}</span>` : '';
    // Trailing newline before the closing fence is the author's delimiter, not content.
    const code = body.replace(/\r?\n$/, '');
    return `<div class="board-code-wrap">${label}<pre class="board-code"><code>${code}</code></pre></div>`;
}

/**
 * Renders a comment / description body as display HTML.
 *
 * @param {string} text raw author text (never trusted)
 * @param {{ attachments?: Array<{id: string, url: string, name?: string}> }} options
 *        Images resolve through this list: the id in the text is looked up here
 *        and the *record's* URL is used. A URL written into the text is never
 *        used as a src, so an image can only ever point at our own attachment.
 * @returns {string} HTML safe to assign to innerHTML
 */
export function renderCommentHtml(text, { attachments = [] } = {}) {
    const source = stripControlChars(text);
    if (!source.trim()) return '';

    // 1. Escape everything. Nothing below reintroduces markup from user input.
    let work = escapeHtml(source);

    // 2. Park generated markup. No returned fragment is parsed a second time.
    const fragments = [];
    const park = html => {
        const index = fragments.push(html) - 1;
        return `${BLOCK_OPEN}${index}${BLOCK_CLOSE}`;
    };
    work = work.replace(FENCE_RE, (_match, lang, body) => park(renderCodeBlock({ lang, body })));

    // 3. Only source text is tokenized; generated attributes never enter a regex.
    work = work.replace(INLINE_TOKEN_RE, (match, code, quotedPath, barePath, alt, id) => {
        if (code !== undefined) return park(`<code class="board-inline-code">${code}</code>`);
        if (quotedPath !== undefined || barePath !== undefined) {
            // "@claude" or "@rob" mid-prose: no slash, no extension, not a file.
            if (barePath !== undefined && !BARE_FILE_REF_LOOKS_LIKE_A_PATH.test(barePath)) return match;
            // The whole token is source text that was escaped in step 1; no attribute
            // is built from it (no href in v1), so nothing here can carry markup.
            return park(`<code class="board-file-ref">${match}</code>`);
        }
        if (id !== undefined) {
            const attachment = attachments.find(item => item.id === id);
            // The backend supplies raster data URLs only. Files with authenticated
            // content URLs (and large rasters) open through the separate file viewer.
            if (typeof attachment?.url !== 'string' || !RASTER_DATA_URL_RE.test(attachment.url)) return match;
            // alt was already escaped with the source; escaping again corrupts names.
            return park(`<img class="board-image" src="${escapeHtml(attachment.url)}" alt="${alt}"`
                + ` data-board-image="${escapeHtml(id)}" loading="lazy">`);
        }
        // Only a literal http(s) source token reaches this branch, already escaped.
        return park(`<a href="${match}" target="_blank" rel="noopener noreferrer">${match}</a>`);
    });

    // 4. Reinsert once, after all transforms. Fragments never contain sentinels.
    work = work.replace(/\u0000B(\d+)\u0000/g, (_match, index) => fragments[Number(index)]);

    return work;
}

/**
 * Plain-text preview for a card tile / list row: strips the little syntax we
 * support so a fenced block does not leak backticks into a one-line summary.
 * `@path` references are left exactly as typed; they read fine in an excerpt.
 */
export function toPlainPreview(text, maxLength = 140) {
    const flat = stripControlChars(text)
        .replace(FENCE_RE, ' [code] ')
        .replace(IMAGE_RE, ' [image] ')
        .replace(INLINE_CODE_RE, '$1')
        .replace(/\s+/g, ' ')
        .trim();
    return flat.length > maxLength ? `${flat.slice(0, maxLength - 1)}…` : flat;
}

/**
 * Wraps the selected range of a textarea in a fenced code block, or inserts an
 * empty fence at the caret. Returns the new value and where the caret should
 * land, leaving the DOM write to the caller.
 */
export function wrapSelectionAsCode(value, selectionStart, selectionEnd) {
    const before = value.slice(0, selectionStart);
    const selected = value.slice(selectionStart, selectionEnd);
    const after = value.slice(selectionEnd);

    const leadingBreak = before && !before.endsWith('\n') ? '\n' : '';
    const trailingBreak = after && !after.startsWith('\n') ? '\n' : '';
    const body = selected || '';
    const opening = `${leadingBreak}\`\`\`\n`;
    const closing = `\n\`\`\`${trailingBreak}`;

    return {
        value: `${before}${opening}${body}${closing}${after}`,
        // Empty fence: land the caret inside it. With a selection: land after the block.
        selectionStart: before.length + opening.length + (selected ? body.length : 0),
        selectionEnd: before.length + opening.length + (selected ? body.length : 0)
    };
}

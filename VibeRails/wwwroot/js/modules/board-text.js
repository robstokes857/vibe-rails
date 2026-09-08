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
// and http(s) autolinks. This is not Markdown and is not trying to become it.

import { escapeHtml } from './utils.js';

// Sentinels park extracted code blocks while inline transforms run over the
// rest, so nothing rewrites the inside of a code block. U+0000 cannot collide:
// control characters are stripped from the input first.
const BLOCK_OPEN = '\u0000B';
const BLOCK_CLOSE = '\u0000';

const FENCE_RE = /```([a-zA-Z0-9_+#.-]*)[ \t]*\r?\n?([\s\S]*?)```/g;
const INLINE_CODE_RE = /`([^`\n]+)`/g;
const IMAGE_RE = /!\[([^\]\n]*)\]\(attachment:([A-Za-z0-9_-]+)\)/g;
// Trailing punctuation is excluded so "see https://x.com/a." does not swallow the period.
const URL_RE = /\bhttps?:\/\/[^\s<>"']+[^\s<>"'.,;:!?)\]}]/g;

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

    // 2. Park fenced code blocks so inline transforms cannot reach inside them.
    const blocks = [];
    work = work.replace(FENCE_RE, (_match, lang, body) => {
        const index = blocks.push({ lang, body }) - 1;
        return `${BLOCK_OPEN}${index}${BLOCK_CLOSE}`;
    });

    // 3. Images, resolved against the attachment records (never against the text).
    work = work.replace(IMAGE_RE, (match, alt, id) => {
        const attachment = attachments.find(item => item.id === id);
        if (!attachment?.url) return match; // Unknown id stays literal text.
        return `<img class="board-image" src="${escapeHtml(attachment.url)}" alt="${escapeHtml(alt)}"`
            + ` data-board-image="${escapeHtml(id)}" loading="lazy">`;
    });

    // 4. Inline code.
    work = work.replace(INLINE_CODE_RE, (_match, code) => `<code class="board-inline-code">${code}</code>`);

    // 5. Autolink http(s) only. The URL is already escaped; the scheme is fixed
    //    by the pattern, so javascript: and data: cannot appear here.
    work = work.replace(URL_RE, url => `<a href="${url}" target="_blank" rel="noopener noreferrer">${url}</a>`);

    // 6. Put the code blocks back.
    work = work.replace(/\u0000B(\d+)\u0000/g, (_match, index) => renderCodeBlock(blocks[Number(index)]));

    return work;
}

/**
 * Plain-text preview for a card tile / list row: strips the little syntax we
 * support so a fenced block does not leak backticks into a one-line summary.
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

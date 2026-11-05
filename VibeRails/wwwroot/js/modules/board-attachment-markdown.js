// Dedicated read-only attachment renderer. Never enable HTML or add plugins here
// without reviewing this boundary: every attachment is untrusted.
import MarkdownIt from '../../assets/board/markdown-it.min.js';

const parser = new MarkdownIt({ html: false, linkify: false, typographer: false });
parser.validateLink = value => {
    // Relative URLs must not resolve against the application's authenticated origin.
    // No file:, command:, data:, protocol-relative or fragment navigation either.
    if (!/^https?:\/\//i.test(value)) return false;
    try {
        const url = new URL(value);
        return ['http:', 'https:'].includes(url.protocol) && !!url.hostname && !url.username && !url.password;
    } catch { return false; }
};
parser.renderer.rules.link_open = (tokens, index, options, env, renderer) => {
    tokens[index].attrSet('target', '_blank');
    tokens[index].attrSet('rel', 'noopener noreferrer');
    tokens[index].attrSet('referrerpolicy', 'no-referrer');
    return renderer.renderToken(tokens, index, options);
};
parser.renderer.rules.image = (tokens, index, options, env, renderer) => {
    const alt = renderer.renderInlineAsText(tokens[index].children || [], options, env);
    return `<span class="vb-board-markdown-image">[Image: ${parser.utils.escapeHtml(alt || 'image')} — not loaded]</span>`;
};

/** Render bounded Markdown source as inert markup with explicit HTTP(S) links only. */
export function renderAttachmentMarkdown(source) {
    return parser.render(String(source));
}

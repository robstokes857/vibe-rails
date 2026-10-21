import { escapeHtml } from './utils.js';

export function codeLinksSection(card) {
    return `<section class="board-side-section" data-board-code-links>
        <h3 class="board-side-label"><i class="fa-brands fa-github" aria-hidden="true"></i>
            GitHub links <span class="board-count" data-board-count="code-links">${card?.codeLinks?.length || 0}</span>
        </h3>
        <div class="board-side-list" data-board-code-link-list></div>
        ${card?.id ? `<form class="board-side-form" data-board-add-code-link>
            <input type="url" class="form-control form-control-sm" name="url" maxlength="500" required
                placeholder="Paste a PR or commit URL" aria-label="GitHub pull request or commit URL" autocomplete="off" spellcheck="false">
            <button type="submit" class="board-side-add" title="Link PR or commit" aria-label="Link PR or commit">
                <i class="fa-solid fa-plus" aria-hidden="true"></i>
            </button>
        </form><p class="board-editor-muted mt-2">Links save immediately and are included in shared card and session data.</p>`
        : '<p class="board-editor-muted">Save this card to link a GitHub PR or commit.</p>'}
    </section>`;
}

/** Defense in depth for stored or remote data before creating an external anchor. */
export function safeCodeLinkUrl(value) {
    if (typeof value !== 'string' || value.length > 500) return null;
    return /^https:\/\/github\.com\/[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\/(?!\.{1,2}\/)[a-z0-9_.-]+\/(?:pull\/[1-9][0-9]*|commit\/[a-f0-9]{7,40})$/i.test(value) ? value : null;
}

export function renderCodeLinks(editor, card) {
    const host = editor.querySelector('[data-board-code-link-list]');
    if (!host) return;
    const links = card?.codeLinks || [];
    editor.querySelector('[data-board-count="code-links"]').textContent = String(links.length);
    host.innerHTML = links.length ? links.map(link => {
        const url = safeCodeLinkUrl(link.url);
        const label = escapeHtml(link.label);
        return `<div class="board-side-row">
            ${url ? `<a class="board-side-main" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">${label}</a>` : `<span class="board-side-main">${label}</span>`}
            <button type="button" class="board-side-remove" data-board-remove-code-link="${escapeHtml(link.id)}"
                title="Unlink ${label}" aria-label="Unlink ${label}"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>
        </div>`;
    }).join('') : '<p class="board-side-empty">No GitHub links.</p>';
}

import { escapeHtml } from './utils.js';

export function previousWorkHtml(card) {
    const work = card?.previousWork;
    const candidates = card?.fileCandidates || [];
    if (!work && !candidates.length) return '';
    const fields = work ? [['Outcome', work.outcome], ['Decisions', work.decisions], ['Validation', work.validation], ['Outstanding', work.outstanding]] : [];
    const files = work?.files || candidates;
    return `<section class="board-side-section" aria-label="Previous work">
        <h3 class="board-side-label">Previous work</h3>
        ${work ? `<p class="board-editor-muted">${escapeHtml(work.author?.label || 'Agent')} · ${escapeHtml(work.createdUtc || '')}</p>` : '<p class="board-editor-muted">Changed-file candidates from linked commits. An agent can curate useful entry points.</p>'}
        ${fields.filter(([, text]) => text).map(([label, text]) => `<p style="white-space:pre-wrap;overflow-wrap:anywhere"><strong>${label}:</strong> ${escapeHtml(text)}</p>`).join('')}
        <ul>${files.map(file => `<li style="overflow-wrap:anywhere"><code>${escapeHtml(file.path)}</code>
            ${file.symbol ? ` · ${escapeHtml(file.symbol)}` : ''} [${escapeHtml(file.role)}]
            <div>${escapeHtml(file.reason)}</div>
            <small class="board-editor-muted">${escapeHtml(file.commit || 'Commit unspecified')} · ${escapeHtml(file.status || 'Historical reference; verify current code')}</small></li>`).join('')}</ul>
    </section>`;
}

/**
 * Shared card-editor layout. Slots contain trusted component markup; owners escape
 * their data and provide their own persistence, activity and settings adapters.
 * Keep activity below attachments and the footer outside the only scroll region.
 */
export function cardEditorLayout({
    main = '',
    attachments = '',
    activity = '',
    sidebar = '',
    footer = ''
}) {
    return `<div class="board-editor-scroll">
        <div class="board-editor-main">${main}${attachments}${activity}</div>
        <aside class="board-editor-side"><div class="board-side-scroll">${sidebar}</div></aside>
    </div>
    <div class="board-editor-actions">${footer}</div>`;
}

/** Applies the Board editor's full-height chrome without a second Bootstrap scroller. */
export function configureCardEditorDialog(container) {
    const dialog = container?.querySelector('.modal-dialog');
    dialog?.classList.remove('modal-lg', 'modal-dialog-scrollable');
    dialog?.classList.add('modal-xl', 'board-card-modal-dialog');
}

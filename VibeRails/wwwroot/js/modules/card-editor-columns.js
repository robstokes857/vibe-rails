// The card editor's two columns share one scrollbar, and the right rail is usually much longer
// than the card. Each column is `position: sticky` (see the board template's styles) with the
// offset set here: 0 for a column shorter than the dialog, so it stays put, and a negative offset
// for a taller one, so it scrolls to its own end first and then holds its last screenful. The
// taller column of the two never reaches its offset before the scroll ends, so it scrolls as before.

/** The sticky `top` for a column of `columnHeight` inside a scroll region of `viewportHeight`. */
export function columnStickOffset(viewportHeight, columnHeight) {
    const viewport = Number(viewportHeight) || 0, column = Number(columnHeight) || 0;
    return Math.min(0, Math.round(viewport - column));
}

/** Keeps both columns' offsets current while the dialog, the card or the rail changes size. Returns a disposer. */
export function keepShorterColumnInView(editor) {
    const scroller = editor?.querySelector?.('.board-editor-scroll');
    const columns = scroller ? [scroller.querySelector('.board-editor-main'), scroller.querySelector('.board-editor-side')].filter(Boolean) : [];
    if (columns.length !== 2 || typeof ResizeObserver !== 'function') return () => {};
    const update = () => {
        for (const column of columns)
            column.style.setProperty('--board-editor-stick', `${columnStickOffset(scroller.clientHeight, column.offsetHeight)}px`);
    };
    // An offset changes where a column sits, never its size, so this cannot feed itself.
    const observer = new ResizeObserver(update);
    [scroller, ...columns].forEach(node => observer.observe(node));
    update();
    return () => observer.disconnect();
}

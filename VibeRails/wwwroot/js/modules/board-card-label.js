// How a card is named to people: its display ID (the label Board settings number), else its
// immutable key, which older writers' cards show. The key stays the identity for links and lookups.
// Every surface that names a card goes through these, so the precedence is decided once.

/** The card's display ID, else its key; empty for no card. */
export function cardDisplayId(card) {
    return card?.displayId || card?.key || '';
}

/** "VIBE-12 · Title" for titles, toasts and lists. `title` replaces the card's own, e.g. an unsaved edit. */
export function cardLabel(card, title = card?.title) {
    const id = cardDisplayId(card);
    const text = title || '';
    return id && text ? `${id} · ${text}` : id || text;
}

/** The human shorthand of a stored key omits its random middle part (VB-ABC12-64 → VB-64). */
export function shortCardKey(key) {
    return (key || '').replace(/^([^-]+)-[A-Z0-9]{5}-(\d+)$/i, '$1-$2');
}

/** Every name a card answers to in a search: display ID, key, the key's short form and the title. */
export function cardSearchText(card) {
    return [card?.displayId, card?.key, shortCardKey(card?.key), card?.title].filter(Boolean).join('\n');
}

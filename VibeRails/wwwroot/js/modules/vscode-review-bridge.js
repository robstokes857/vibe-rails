/**
 * Dashboard → host relay for this project's review requests (VB-50 slice F). The poller and the
 * sender publish `review_request_updated` on the app event socket when a request is sent, cancelled,
 * deleted or receives a decision. The VS Code extension's "Review requests" view cannot hear that
 * socket, so this module forwards each event through `__viberails_reviewRequestChanged__`, which the
 * extension installs on `window` beside `__viberails_launcherChanged__`. Only the event's four fields
 * travel, each typed and bounded; the extension re-reads the list itself and nothing comes back here.
 * The other direction, host → dashboard (`openReviewRequest`), is handled by the launcher bridge.
 */
export function setupVSCodeReviewBridge(app, host = window) {
    if (!host.__viberails_VSCODE__) return null;
    const unsubscribe = app.appEventClient?.on?.('review_request_updated', payload => {
        const change = reviewRequestChange(payload);
        if (change) host.__viberails_reviewRequestChanged__?.(change);
    });
    return typeof unsubscribe === 'function' ? unsubscribe : null;
}

/** The forwarded shape `{ id, status, cardKey, sent }`, or null when the payload is not a review request change. */
export function reviewRequestChange(payload) {
    if (!payload || typeof payload !== 'object') return null;
    const id = typeof payload.id === 'string' ? payload.id.trim() : '';
    const status = typeof payload.status === 'string' ? payload.status.trim() : '';
    if (!id || id.length > 64 || !status || status.length > 32) return null;
    const cardKey = typeof payload.cardKey === 'string' && payload.cardKey.trim() ? payload.cardKey.trim().slice(0, 64) : null;
    return { id, status, cardKey, sent: payload.sent === true };
}

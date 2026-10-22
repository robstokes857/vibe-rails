/** Verify the immediate parent, including VS Code's replacement of window.parent with self. */
export function isParentMessage({ source }, host = window) {
    if (!source || source === host) return false;
    if (host.parent !== host) return source === host.parent;
    // VS Code hides parent/top before our scripts run. WindowProxy indexed access still
    // exposes the sender's actual child frames, even across origins. A child/sibling cannot
    // contain this window, so it cannot impersonate the host through this check.
    try {
        for (let index = 0; index < source.length; index++) {
            if (source[index] === host) return true;
        }
    } catch { /* A closed or inaccessible sender is untrusted. */ }
    return false;
}

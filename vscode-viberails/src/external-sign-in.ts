/** Only the fixed verification page and its optional public user-code fragment may open. */
export function isAllowedSignInUrl(value: unknown): value is string {
    return typeof value === 'string' && value.trim() === value
        && /^https:\/\/viberails\.ai\/link(?:#code=[A-Z0-9]{4}-[A-Z0-9]{4})?$/.test(value);
}

/**
 * The hosted reviewer page of one review request (VB-50 slice D): the fixed host and path, a
 * `rvw_` reference of 32 lowercase hex characters, and nothing else (no port, query or fragment).
 */
export function isAllowedReviewRequestUrl(value: unknown): value is string {
    return typeof value === 'string' && value.trim() === value
        && /^https:\/\/viberails\.ai\/Reviews\/requests\/rvw_[0-9a-f]{32}$/.test(value);
}

/**
 * The Billing page, where an account upgrades to Vibe Rails Pro: the fixed host and path, optionally
 * naming the feature that asked (`?feature=remote-boards`, lowercase words joined by hyphens) and
 * nothing else. A feature name is not an identifier: digits, other keys and fragments are refused.
 */
export function isAllowedBillingUrl(value: unknown): value is string {
    return typeof value === 'string' && value.trim() === value
        && /^https:\/\/viberails\.ai\/Billing(?:\?feature=[a-z]+(?:-[a-z]+){0,3})?$/.test(value);
}

/** Every page the dashboard may hand to the browser: sign-in, a review request's page and Billing. */
export function isAllowedExternalUrl(value: unknown): value is string {
    return isAllowedSignInUrl(value) || isAllowedReviewRequestUrl(value) || isAllowedBillingUrl(value);
}

/** Validate before crossing from the sandboxed webview to the user's browser. */
export async function openExternalSignIn(
    value: unknown,
    open: (url: string) => Thenable<boolean>
): Promise<boolean> {
    if (!isAllowedExternalUrl(value)) return false;
    return await open(value);
}

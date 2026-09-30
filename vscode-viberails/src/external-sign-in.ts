/** Only the fixed verification page and its optional public user-code fragment may open. */
export function isAllowedSignInUrl(value: unknown): value is string {
    return typeof value === 'string' && value.trim() === value
        && /^https:\/\/viberails\.ai\/link(?:#code=[A-Z0-9]{4}-[A-Z0-9]{4})?$/.test(value);
}

/** Validate before crossing from the sandboxed webview to the user's browser. */
export async function openExternalSignIn(
    value: unknown,
    open: (url: string) => Thenable<boolean>
): Promise<boolean> {
    if (!isAllowedSignInUrl(value)) return false;
    return await open(value);
}

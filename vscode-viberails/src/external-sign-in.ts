/** The webview may open only the code-entry page, with no URL-carried credentials. */
export function isAllowedSignInUrl(value: unknown): value is string {
    return value === 'https://viberails.ai/link';
}

/** Validate before crossing from the sandboxed webview to the user's browser. */
export async function openExternalSignIn(
    value: unknown,
    open: (url: string) => Thenable<boolean>
): Promise<boolean> {
    if (!isAllowedSignInUrl(value)) return false;
    return await open(value);
}

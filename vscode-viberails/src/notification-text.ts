/**
 * VS Code renders `[label](https://…)`, `[label](command:…)` and `[label](file:…)` inside a
 * notification as a link, and a command link runs that command with its arguments when clicked.
 * Notification text here can carry words written by someone else (a reviewer's comment, a
 * repository or definition name, a backend error), so every notification is shown as plain text:
 * the `](` pair that makes a link is split. Must not import `vscode`.
 */
export function plainNotificationText(value: unknown): string {
    return String(value ?? '').replace(/\]\(/g, '] (');
}

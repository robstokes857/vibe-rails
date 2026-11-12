import * as vscode from 'vscode';
import { plainNotificationText } from './notification-text';

/**
 * The only place the extension raises a VS Code notification. Each message goes through
 * `plainNotificationText`, so text from a reviewer, a repository or the backend can never become
 * a clickable link; `notification-text.test.ts` fails on a direct `vscode.window.show…Message` call
 * anywhere else in `src`.
 */
export function showInfo(message: string, ...actions: string[]): Thenable<string | undefined> {
    return vscode.window.showInformationMessage(plainNotificationText(message), ...actions);
}

export function showWarning(message: string, ...actions: string[]): Thenable<string | undefined> {
    return vscode.window.showWarningMessage(plainNotificationText(message), ...actions);
}

export function showError(message: string, ...actions: string[]): Thenable<string | undefined> {
    return vscode.window.showErrorMessage(plainNotificationText(message), ...actions);
}

/** A modal warning with one confirming action; resolves true only when that action was chosen. */
export async function confirmWarning(message: string, action: string): Promise<boolean> {
    return await vscode.window.showWarningMessage(plainNotificationText(message), { modal: true }, action) === action;
}

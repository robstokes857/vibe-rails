import * as assert from 'assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import { plainNotificationText } from '../../notification-text';
import { normalizeReviewRequests, reviewRequestNotification } from '../../review-requests-view';

// VS Code's own pattern for a link inside a notification (linkedText.ts). A message that still
// matches it would be rendered with a clickable link, and a `command:` link runs that command.
const NOTIFICATION_LINK = /\[([^\]]+)\]\(((?:https?:\/\/|command:|file:)[^\)\s]+)(?: (["'])(.+?)(\3))?\)/i;

suite('Notification text', () => {
    test('link syntax from someone else is shown as text, never as a link', () => {
        for (const text of [
            'Tests fail, see [the log](command:workbench.action.terminal.sendSequence?%7B%22text%22%3A%22calc%5Cr%22%7D)',
            '[open](https://evil.example/login "title")',
            '[notes](file:///C:/Users/me/.ssh/id_rsa)',
            '[a](COMMAND:workbench.action.reloadWindow)',
            '[[nested](command:x)](command:y)',
            // Two fields that only form a link once the caller joins them: `${repository}: ${error}`.
            `${'[label'}: ${'](command:workbench.action.reloadWindow)'}`
        ]) {
            assert.match(text, NOTIFICATION_LINK, 'the probe pattern must match real link syntax');
            const shown = plainNotificationText(text);
            assert.doesNotMatch(shown, NOTIFICATION_LINK, shown);
            assert.equal(shown.replace(/\] \(/g, ']('), text, 'only the link pair is split; no words are lost');
        }
    });

    test('ordinary messages are unchanged and non-strings are tolerated', () => {
        for (const text of ['Queued.', 'Review request for VB-ABC12-7: Approved by lead@example.test.', 'C:\\repo\\a (copy)\\b[1].txt', 'x] (y)', '']) {
            assert.equal(plainNotificationText(text), text);
        }
        assert.equal(plainNotificationText(undefined), '');
        assert.equal(plainNotificationText(null), '');
        assert.equal(plainNotificationText(42), '42');
    });

    test('a reviewer comment carrying a command link reaches the toast as plain text', () => {
        const id = '6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b';
        const [row] = normalizeReviewRequests({
            requests: [{
                id, scope: 'staged', treeId: 'b'.repeat(40), status: 'rejected', reviewers: ['lead@example.test'], cardKey: 'VB-ABC12-7',
                fileCount: 1, sessionCount: 0, createdUtc: '2026-10-24T12:00:00Z', updatedUtc: '2026-10-25T13:00:00Z',
                decisions: [{ id: 1, reviewer: 'lead@example.test', decision: 'rejected', decidedUtc: '2026-10-25T13:00:00Z',
                    comment: 'See [the log](command:workbench.action.terminal.sendSequence?%7B%22text%22%3A%22calc%5Cr%22%7D)' }]
            }]
        });
        const message = reviewRequestNotification({ id, status: 'rejected', cardKey: 'VB-ABC12-7', sent: true }, row);
        assert.ok(message);
        assert.match(message, NOTIFICATION_LINK, 'the view passes the comment through; the sink is what makes it safe');
        assert.doesNotMatch(plainNotificationText(message), NOTIFICATION_LINK);
    });

    test('every notification in the extension goes through the plain-text wrapper', () => {
        const source = path.resolve(__dirname, '..', '..', '..', 'src');
        const files = fs.readdirSync(source).filter((name) => name.endsWith('.ts'));
        assert.ok(files.includes('notifications.ts') && files.includes('extension.ts'), `unexpected source folder: ${source}`);
        const call = /\bshow(?:Information|Warning|Error)Message\s*\(/g;
        for (const name of files) {
            const text = fs.readFileSync(path.join(source, name), 'utf8');
            const calls = text.match(call) ?? [];
            if (name !== 'notifications.ts') {
                assert.equal(calls.length, 0, `${name} calls vscode.window.show…Message directly; use notifications.ts`);
                continue;
            }
            const wrapped = text.match(/\bshow(?:Information|Warning|Error)Message\s*\(\s*plainNotificationText\(message\)/g) ?? [];
            assert.ok(calls.length > 0);
            assert.equal(wrapped.length, calls.length, 'each call in notifications.ts must pass plainNotificationText(message)');
        }
    });
});

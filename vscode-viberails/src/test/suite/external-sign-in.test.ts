import * as assert from 'assert/strict';
import { isAllowedSignInUrl, openExternalSignIn } from '../../external-sign-in';

suite('External sign-in bridge', () => {
    test('opens only the fixed page and a strict public-code fragment', async () => {
        const opened: string[] = [];
        const open = async (url: string) => { opened.push(url); return true; };
        for (const url of [undefined, null, 3, {},
            'command:workbench.action.terminal.new', 'vscode://viberails/callback', 'file:///C:/secret',
            'javascript:alert(1)', 'https://viberails.ai.evil.example/link', 'https://evil.example/link',
            'https://user:secret@viberails.ai/link', 'https://viberails.ai:443/link',
            'https://viberails.ai/link?code=ABCD-1234', 'https://viberails.ai/link#secret',
            'https://viberails.ai/link#code=secret-device-capability',
            'https://viberails.ai/link#code=ABCD-1234&redirect=evil',
            'https://viberails.ai/link#code=ABCD-1234\n',
            'http://viberails.ai/link', 'http://localhost:5000/link', 'https://viberails.ai/link/',
            ' https://viberails.ai/link', 'https://viberails.ai/other/../link']) {
            assert.equal(isAllowedSignInUrl(url), false, String(url));
            assert.equal(await openExternalSignIn(url, open), false);
        }
        assert.deepEqual(opened, []);
        assert.equal(await openExternalSignIn('https://viberails.ai/link', open), true);
        assert.deepEqual(opened, ['https://viberails.ai/link']);
        assert.equal(await openExternalSignIn('https://viberails.ai/link#code=ABCD-2345', open), true);
        assert.equal(opened[1], 'https://viberails.ai/link#code=ABCD-2345');
    });

    test('reports browser refusal and propagates launch failure for the host to display', async () => {
        assert.equal(await openExternalSignIn('https://viberails.ai/link', async () => false), false);
        await assert.rejects(openExternalSignIn('https://viberails.ai/link', async () => {
            throw new Error('Browser unavailable');
        }), /Browser unavailable/);
    });
});

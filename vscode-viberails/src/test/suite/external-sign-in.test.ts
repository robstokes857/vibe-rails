import * as assert from 'assert/strict';
import { isAllowedBillingUrl, isAllowedReviewRequestUrl, isAllowedSignInUrl, openExternalSignIn } from '../../external-sign-in';

suite('External sign-in bridge', () => {
    test('opens the Billing page only for the fixed host and path, with at most a feature name', async () => {
        const opened: string[] = [];
        const open = async (url: string) => { opened.push(url); return true; };
        for (const url of [
            'https://evil.example/Billing', 'https://viberails.ai.evil.example/Billing?feature=remote-boards',
            'https://viberails.ai:443/Billing', 'http://viberails.ai/Billing', 'https://user:secret@viberails.ai/Billing',
            'https://viberails.ai/billing', 'https://viberails.ai/Billing/', 'https://viberails.ai/Billing/Checkout',
            'https://viberails.ai/Billing?', 'https://viberails.ai/Billing?feature=', 'https://viberails.ai/Billing#feature=remote-boards',
            'https://viberails.ai/Billing?feature=Remote-Boards', 'https://viberails.ai/Billing?feature=remote_boards',
            'https://viberails.ai/Billing?feature=-remote', 'https://viberails.ai/Billing?feature=remote-',
            'https://viberails.ai/Billing?feature=brd-7k2qz', 'https://viberails.ai/Billing?feature=0123456789abcdef',
            'https://viberails.ai/Billing?feature=a-b-c-d-e', 'https://viberails.ai/Billing?feature=remote-boards&board=one',
            'https://viberails.ai/Billing?board=one', 'https://viberails.ai/Billing?feature=remote-boards#paid',
            'https://viberails.ai/Billing?feature=remote-boards\n', ' https://viberails.ai/Billing']) {
            assert.equal(isAllowedBillingUrl(url), false, url);
            assert.equal(await openExternalSignIn(url, open), false, url);
        }
        assert.deepEqual(opened, []);
        const allowed = ['https://viberails.ai/Billing', 'https://viberails.ai/Billing?feature=remote-boards',
            'https://viberails.ai/Billing?feature=remote-terminals', 'https://viberails.ai/Billing?feature=session-sharing',
            'https://viberails.ai/Billing?feature=card-sharing'];
        for (const url of allowed) assert.equal(await openExternalSignIn(url, open), true, url);
        assert.deepEqual(opened, allowed);
        assert.equal(isAllowedSignInUrl(allowed[1]), false);
        assert.equal(isAllowedReviewRequestUrl(allowed[1]), false);
    });

    test('opens a review request page only for the fixed host, path and a strict reference', async () => {
        const reference = 'rvw_0123456789abcdef0123456789abcdef';
        const opened: string[] = [];
        const open = async (url: string) => { opened.push(url); return true; };
        for (const url of [
            `https://evil.example/Reviews/requests/${reference}`, `https://viberails.ai.evil.example/Reviews/requests/${reference}`,
            `https://viberails.ai:443/Reviews/requests/${reference}`, `http://viberails.ai/Reviews/requests/${reference}`,
            'https://viberails.ai/Reviews/requests/rvw_short', `https://viberails.ai/Reviews/requests/${reference.toUpperCase()}`,
            `https://viberails.ai/Reviews/requests/${reference}?x=1`, `https://viberails.ai/Reviews/requests/${reference}#x`,
            `https://viberails.ai/Reviews/requests/${reference}/`, ` https://viberails.ai/Reviews/requests/${reference}`,
            'https://viberails.ai/Reviews/requests', `https://user:secret@viberails.ai/Reviews/requests/${reference}`]) {
            assert.equal(isAllowedReviewRequestUrl(url), false, url);
            assert.equal(await openExternalSignIn(url, open), false, url);
        }
        assert.deepEqual(opened, []);
        assert.equal(await openExternalSignIn(`https://viberails.ai/Reviews/requests/${reference}`, open), true);
        assert.deepEqual(opened, [`https://viberails.ai/Reviews/requests/${reference}`]);
        assert.equal(isAllowedSignInUrl(`https://viberails.ai/Reviews/requests/${reference}`), false);
    });

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

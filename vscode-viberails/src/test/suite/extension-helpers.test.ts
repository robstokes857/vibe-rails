import * as assert from 'assert/strict';
import {
    clampStartupTimeoutMs,
    isRetryableBootstrapError,
    parseSessionTokenFromSetCookie
} from '../../extension';

suite('Extension helpers', () => {
    test('startup timeout is clamped to the manifest bounds', () => {
        assert.equal(clampStartupTimeoutMs(1), 5000);
        assert.equal(clampStartupTimeoutMs(45000), 45000);
        assert.equal(clampStartupTimeoutMs(900000), 600000);
        assert.equal(clampStartupTimeoutMs(Number.NaN), 30000);
        assert.equal(clampStartupTimeoutMs(undefined), 30000);
    });

    test('session cookie parsing requires the exact cookie name', () => {
        assert.equal(
            parseSessionTokenFromSetCookie([
                'x_viberails_session=wrong; Path=/',
                'viberails_session=right%2Ftoken%2Bvalue%3D; Path=/'
            ]),
            'right/token+value='
        );
        assert.equal(
            parseSessionTokenFromSetCookie(['x_viberails_session=wrong; Path=/']),
            null
        );
    });

    test('only definitely unaccepted bootstrap requests are retried', () => {
        const refused = Object.assign(new Error('refused'), { code: 'ECONNREFUSED' });
        const reset = Object.assign(new Error('reset'), { code: 'ECONNRESET' });
        const timedOut = Object.assign(new Error('timed out'), {
            name: 'BootstrapAmbiguousError'
        });

        assert.equal(isRetryableBootstrapError(refused), true);
        assert.equal(isRetryableBootstrapError(reset), false);
        assert.equal(isRetryableBootstrapError(timedOut), false);
        assert.equal(isRetryableBootstrapError(new Error('plain error')), false);
    });
});

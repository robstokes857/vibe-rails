import * as assert from 'assert/strict';
import { buildLaunchArgs, parseBootstrapEndpoint } from '../../backend-manager';

suite('BackendManager', () => {
    test('root launch args stay detached from the extension host PID', () => {
        const args = buildLaunchArgs();

        assert.deepEqual(args, ['--vs-code-v1']);
        assert.ok(!args.includes('--parent-pid'));
    });

    test('accepts only explicit loopback HTTP bootstrap endpoints', () => {
        assert.deepEqual(
            parseBootstrapEndpoint('http://localhost:43123/auth/bootstrap?code=secret'),
            {
                bootstrapUrl: 'http://localhost:43123/auth/bootstrap?code=secret',
                host: 'localhost',
                port: 43123
            }
        );
        assert.equal(
            parseBootstrapEndpoint('http://[::1]:43124/auth/bootstrap?code=secret').host,
            '::1'
        );

        assert.throws(
            () => parseBootstrapEndpoint('not a URL'),
            /Invalid URL/i
        );
        assert.throws(
            () => parseBootstrapEndpoint('http://localhost/auth/bootstrap?code=secret'),
            /valid port/i
        );
        assert.throws(
            () => parseBootstrapEndpoint('http://example.com:43123/auth/bootstrap?code=secret'),
            /loopback/i
        );
        assert.throws(
            () => parseBootstrapEndpoint('https://localhost:43123/auth/bootstrap?code=secret'),
            /must use HTTP/i
        );
    });
});

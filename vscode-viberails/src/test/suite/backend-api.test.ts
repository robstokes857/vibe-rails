import * as assert from 'assert/strict';
import * as http from 'http';
import { AddressInfo } from 'net';
import { BackendRequestError, errorMessageFromBody, requestJson } from '../../backend-api';

interface ReceivedRequest {
    method: string | undefined;
    url: string | undefined;
    headers: http.IncomingHttpHeaders;
    body: string;
}

suite('Backend API', () => {
    let server: http.Server;
    let port = 0;
    let received: ReceivedRequest[] = [];
    let respond: (request: http.IncomingMessage, response: http.ServerResponse) => void = () => undefined;
    const openResponses = new Set<http.ServerResponse>();

    suiteSetup(async () => {
        server = http.createServer((request, response) => {
            let body = '';
            request.setEncoding('utf8');
            request.on('data', (chunk: string) => { body += chunk; });
            request.on('end', () => {
                received.push({ method: request.method, url: request.url, headers: request.headers, body });
                openResponses.add(response);
                response.on('close', () => openResponses.delete(response));
                respond(request, response);
            });
        });
        await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
        port = (server.address() as AddressInfo).port;
    });

    suiteTeardown(async () => {
        for (const response of openResponses) { response.destroy(); }
        await new Promise<void>((resolve) => server.close(() => resolve()));
    });

    setup(() => { received = []; });

    const connection = () => ({ host: '127.0.0.1', port, sessionToken: 'session-token', tabToken: 'tab-token' });

    test('sends both auth headers and parses JSON replies', async () => {
        respond = (_request, response) => {
            response.setHeader('Content-Type', 'application/json');
            response.end(JSON.stringify({ items: [{ key: 'job:1' }] }));
        };

        const reply = await requestJson<{ items: unknown[] }>(connection(), 'GET', '/api/v1/automation-nav/preferences');

        assert.deepEqual(reply, { items: [{ key: 'job:1' }] });
        assert.equal(received[0].method, 'GET');
        assert.equal(received[0].url, '/api/v1/automation-nav/preferences');
        assert.equal(received[0].headers['viberails_session'], 'session-token');
        assert.equal(received[0].headers['viberails_tab'], 'tab-token');
        assert.equal(received[0].headers['accept'], 'application/json');
    });

    test('posts JSON bodies, and an empty 2xx body reads as an empty object', async () => {
        respond = (_request, response) => { response.statusCode = 204; response.end(); };
        assert.deepEqual(await requestJson(connection(), 'POST', '/api/v1/jobs/7/run'), {});
        assert.equal(received[0].method, 'POST');
        assert.equal(received[0].headers['content-length'], '0');
        assert.equal(received[0].headers['content-type'], undefined);

        respond = (_request, response) => { response.end('{"ok":true}'); };
        assert.deepEqual(await requestJson(connection(), 'PUT', '/api/v1/x', { items: [1] }), { ok: true });
        assert.equal(received[1].body, '{"items":[1]}');
        assert.equal(received[1].headers['content-type'], 'application/json');
    });

    test('non-2xx replies carry the backend error text, falling back to the status', async () => {
        respond = (_request, response) => {
            response.statusCode = 409;
            response.end(JSON.stringify({ error: 'A run is already queued.' }));
        };
        await assert.rejects(
            requestJson(connection(), 'POST', '/api/v1/jobs/7/run'),
            (error: unknown) => error instanceof BackendRequestError && error.status === 409 && error.message === 'A run is already queued.'
        );

        respond = (_request, response) => { response.statusCode = 500; response.end('<html>oops</html>'); };
        await assert.rejects(
            requestJson(connection(), 'GET', '/x'),
            (error: unknown) => error instanceof BackendRequestError && error.status === 500 && error.message === 'The backend answered with HTTP 500.'
        );

        respond = (_request, response) => { response.end('not json'); };
        await assert.rejects(requestJson(connection(), 'GET', '/x'), /unreadable reply/);
    });

    test('errorMessageFromBody prefers the ErrorResponse text', () => {
        assert.equal(errorMessageFromBody(400, '{"error":"  Bad  "}'), 'Bad');
        assert.equal(errorMessageFromBody(400, '{"error":""}'), 'The backend answered with HTTP 400.');
        assert.equal(errorMessageFromBody(400, '{"message":"x"}', 'fallback'), 'fallback');
        assert.equal(errorMessageFromBody(502, 'nope'), 'The backend answered with HTTP 502.');
        assert.equal(errorMessageFromBody(401, 'null'), 'The backend answered with HTTP 401.');
    });

    test('a silent backend times out instead of hanging', async () => {
        respond = () => undefined;
        await assert.rejects(requestJson(connection(), 'GET', '/slow', undefined, 50), /timed out/);
    });
});

const TEST_ROUTE = '/api/v1/http-relay/test/posts';
const UPSTREAM_ORIGIN = 'https://jsonplaceholder.typicode.com';
const MAX_POST_ID = 2_147_483_647;

const METHODS = ['GET', 'POST', 'PUT', 'DELETE'];

export function showHttpRelayTestModal(app) {
    app.showModal('Test HTTP relay', buildMarkup());

    const root = document.getElementById('http-relay-test-modal');
    if (!root) return;

    const idInput = root.querySelector('#http-relay-test-id');
    const runAllButton = root.querySelector('[data-http-relay-run-all]');
    const actionButtons = Array.from(root.querySelectorAll('[data-http-relay-method]'));

    const runMethod = async (method) => {
        const { id, raw, valid } = readPostId(idInput);
        if (method !== 'POST' && raw && !valid) {
            renderValidation(root, method, 'Post ID must be a positive whole number.');
            idInput?.focus();
            return;
        }
        if ((method === 'PUT' || method === 'DELETE') && !id) {
            renderValidation(root, method, 'Enter a positive Post ID for this request.');
            idInput?.focus();
            return;
        }

        await executeRequest(root, method, id);
    };

    actionButtons.forEach((button) => {
        button.addEventListener('click', () => runMethod(button.dataset.httpRelayMethod));
    });

    runAllButton?.addEventListener('click', async () => {
        const { id, raw, valid } = readPostId(idInput);
        if (raw && !valid) {
            root.querySelector('#http-relay-test-validation').textContent =
                'Post ID must be a positive whole number no greater than 2147483647.';
            idInput?.focus();
            return;
        }
        if (!id) {
            root.querySelector('#http-relay-test-validation').textContent =
                'Enter a positive Post ID to run all four requests.';
            idInput?.focus();
            return;
        }

        setActionsDisabled(root, true);
        root.querySelector('#http-relay-test-validation').textContent = '';
        try {
            await Promise.all(METHODS.map((method) => executeRequest(root, method, id)));
        } finally {
            setActionsDisabled(root, false);
        }
    });

    idInput?.addEventListener('input', () => {
        root.querySelector('#http-relay-test-validation').textContent = '';
    });

    requestAnimationFrame(() => idInput?.focus());
}

async function executeRequest(root, method, id) {
    const output = getOutput(root, method);
    if (!output) return;

    setOutputState(output, 'running');
    const startedAt = performance.now();
    const target = buildTarget(method, id);

    try {
        const response = await fetch(buildLocalUrl(method, id), buildFetchOptions(method, id));
        const bodyText = await response.text();
        const elapsedMs = Math.max(0, Math.round(performance.now() - startedAt));
        renderResponse(output, {
            method,
            target,
            status: response.status,
            statusText: response.statusText,
            headers: Array.from(response.headers.entries()),
            bodyText,
            elapsedMs
        });
    } catch (error) {
        const elapsedMs = Math.max(0, Math.round(performance.now() - startedAt));
        renderRequestError(output, method, target, error, elapsedMs);
    }
}

function buildLocalUrl(method, id) {
    const needsId = method !== 'POST' && Boolean(id);
    const path = needsId ? `${TEST_ROUTE}/${encodeURIComponent(id)}` : TEST_ROUTE;
    return `${window.__viberails_API_BASE__ || ''}${path}`;
}

function buildTarget(method, id) {
    const needsId = method !== 'POST' && Boolean(id);
    return `${UPSTREAM_ORIGIN}/posts${needsId ? `/${id}` : ''}`;
}

function buildFetchOptions(method, id) {
    const tabToken = sessionStorage.getItem('viberails_tab');
    const headers = {
        Accept: 'application/json',
        ...(tabToken ? { viberails_tab: tabToken } : {})
    };
    const options = {
        method,
        headers,
        credentials: 'include',
        cache: 'no-store'
    };

    if (method === 'POST' || method === 'PUT') {
        headers['Content-Type'] = 'application/json';
        options.body = JSON.stringify(method === 'POST'
            ? {
                title: 'VibeRails WSS POST test',
                body: 'Forwarded through viberails.ai over WebSocket.',
                userId: 1
            }
            : {
                id: Number(id),
                title: 'VibeRails WSS PUT test',
                body: 'Forwarded through viberails.ai over WebSocket.',
                userId: 1
            });
    }

    return options;
}

function readPostId(input) {
    const raw = input?.value.trim() || '';
    const numericId = Number(raw);
    const valid = /^[1-9]\d*$/.test(raw)
        && Number.isSafeInteger(numericId)
        && numericId <= MAX_POST_ID;
    return { raw, valid, id: valid ? raw : '' };
}

function getOutput(root, method) {
    return root.querySelector(`[data-http-relay-result="${method}"]`);
}

function setActionsDisabled(root, disabled) {
    root.querySelectorAll('[data-http-relay-method], [data-http-relay-run-all]')
        .forEach((button) => { button.disabled = disabled; });
}

function setOutputState(output, state) {
    output.dataset.state = state;
    output.querySelector('[data-http-relay-status]').textContent = 'Sending…';
    output.querySelector('[data-http-relay-target]').textContent = '';
    output.querySelector('[data-http-relay-headers]').textContent = '';
    output.querySelector('[data-http-relay-body]').textContent = '';
}

function renderValidation(root, method, message) {
    root.querySelector('#http-relay-test-validation').textContent = message;
    const output = getOutput(root, method);
    if (!output) return;

    output.dataset.state = 'error';
    output.querySelector('[data-http-relay-status]').textContent = 'Not sent';
    output.querySelector('[data-http-relay-target]').textContent = message;
    output.querySelector('[data-http-relay-headers]').textContent = '';
    output.querySelector('[data-http-relay-body]').textContent = '';
}

function renderResponse(output, result) {
    output.dataset.state = result.status >= 200 && result.status < 300 ? 'success' : 'error';
    output.querySelector('[data-http-relay-status]').textContent =
        `${result.status}${result.statusText ? ` ${result.statusText}` : ''} · ${result.elapsedMs} ms`;
    output.querySelector('[data-http-relay-target]').textContent = `${result.method} ${result.target}`;
    output.querySelector('[data-http-relay-headers]').textContent = formatHeaders(result.headers);
    output.querySelector('[data-http-relay-body]').textContent = formatBody(result.bodyText);
}

function renderRequestError(output, method, target, error, elapsedMs) {
    output.dataset.state = 'error';
    output.querySelector('[data-http-relay-status]').textContent = `Request failed · ${elapsedMs} ms`;
    output.querySelector('[data-http-relay-target]').textContent = `${method} ${target}`;
    output.querySelector('[data-http-relay-headers]').textContent = '';
    output.querySelector('[data-http-relay-body]').textContent =
        error?.message || 'The local relay request could not be completed.';
}

function formatHeaders(headers) {
    if (!headers.length) return '(no response headers)';
    return headers.map(([name, value]) => `${name}: ${value}`).join('\n');
}

function formatBody(bodyText) {
    if (!bodyText) return '(empty response body)';

    try {
        return JSON.stringify(JSON.parse(bodyText), null, 2);
    } catch {
        return bodyText;
    }
}

function buildMarkup() {
    const resultCards = METHODS.map((method) => `
        <section class="http-relay-result" data-http-relay-result="${method}" data-state="idle">
            <div class="http-relay-result-heading">
                <strong>${method}</strong>
                <span data-http-relay-status>Not run</span>
            </div>
            <div class="http-relay-result-target" data-http-relay-target></div>
            <details>
                <summary>Response headers</summary>
                <pre data-http-relay-headers></pre>
            </details>
            <pre class="http-relay-result-body" data-http-relay-body>Run this request to see its response.</pre>
        </section>
    `).join('');

    return `
        <div id="http-relay-test-modal" class="http-relay-test-modal">
            <p class="text-muted mb-3">
                These requests go to the local VibeRails API, travel to viberails.ai over WSS,
                and are forwarded to JSONPlaceholder.
            </p>
            <div class="http-relay-test-controls">
                <div>
                    <label class="form-label" for="http-relay-test-id">Post ID</label>
                    <input class="form-control" id="http-relay-test-id" type="number" min="1" step="1"
                        max="2147483647" inputmode="numeric" value="1"
                        aria-describedby="http-relay-test-id-help">
                    <small class="form-text text-muted" id="http-relay-test-id-help">
                        Leave blank to make GET return all posts. PUT, DELETE, and Run all require an ID.
                    </small>
                </div>
                <div class="http-relay-test-actions" aria-label="HTTP relay test actions">
                    ${METHODS.map((method) => `
                        <button type="button" class="btn btn-outline-primary btn-sm"
                            data-http-relay-method="${method}">${method}</button>
                    `).join('')}
                    <button type="button" class="btn btn-primary btn-sm" data-http-relay-run-all>Run all</button>
                </div>
            </div>
            <div class="text-danger small" id="http-relay-test-validation" role="alert"></div>
            <div class="http-relay-results" aria-live="polite">${resultCards}</div>
            <div class="d-flex justify-content-end">
                <button type="button" class="btn btn-secondary btn-sm" data-action="close-modal">Close</button>
            </div>
        </div>
    `;
}

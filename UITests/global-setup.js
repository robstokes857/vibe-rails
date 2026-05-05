// @ts-check
const { spawn } = require('node:child_process');
const path = require('node:path');
const fs = require('node:fs');
const { chromium } = require('@playwright/test');

const REPO_ROOT = path.resolve(__dirname, '..');
const RUNTIME_FILE = path.resolve(__dirname, '.playwright-runtime.json');
const STORAGE_FILE = path.resolve(__dirname, '.playwright-auth.json');
const BACKEND_LOG = path.resolve(__dirname, '.playwright-backend.log');

const BOOTSTRAP_TIMEOUT_MS = 90_000;

module.exports = async () => {
    // Stale runtime file from a crashed prior run would confuse fixtures.
    for (const f of [RUNTIME_FILE, STORAGE_FILE, BACKEND_LOG]) {
        if (fs.existsSync(f)) fs.unlinkSync(f);
    }

    // VIBERAILS_TEST_FAKE_CLI=1 makes CommandService.PrepareSession short-circuit to a
    // portable echo+sleep so we exercise PTY+WS+xterm without needing a real LLM CLI.
    const env = { ...process.env, VIBERAILS_TEST_FAKE_CLI: '1' };

    const child = spawn(
        'dotnet',
        ['run', '--project', path.join(REPO_ROOT, 'VibeRails'), '-c', 'Debug', '--', '--vs-code-v1'],
        { cwd: REPO_ROOT, env, stdio: ['ignore', 'pipe', 'pipe'], shell: false }
    );

    const logStream = fs.createWriteStream(BACKEND_LOG);
    child.stdout.pipe(logStream);
    child.stderr.pipe(logStream);

    const bootstrapUrl = await new Promise((resolve, reject) => {
        let buf = '';
        const onData = (data) => {
            buf += data.toString();
            const m = buf.match(/vs-code-v1=(\S+)/);
            if (m) {
                child.stdout.off('data', onData);
                resolve(m[1]);
            }
        };
        child.stdout.on('data', onData);
        child.once('exit', (code) => {
            reject(new Error(`Backend exited before printing bootstrap URL (code=${code}). See ${BACKEND_LOG}.`));
        });
        setTimeout(() => {
            child.stdout.off('data', onData);
            reject(new Error(`Backend did not print bootstrap URL within ${BOOTSTRAP_TIMEOUT_MS}ms. See ${BACKEND_LOG}.`));
        }, BOOTSTRAP_TIMEOUT_MS);
    });

    const url = new URL(bootstrapUrl);
    const baseURL = `${url.protocol}//${url.host}`;

    // Bootstrap code is single-use. Consume it once via a throwaway browser context
    // and persist the resulting auth cookie + sessionStorage so every test context
    // can re-use it.
    const browser = await chromium.launch();
    try {
        const ctx = await browser.newContext();
        const page = await ctx.newPage();
        await page.goto(bootstrapUrl);
        await page.waitForLoadState('networkidle');
        await ctx.storageState({ path: STORAGE_FILE });
        await ctx.close();
    } finally {
        await browser.close();
    }

    fs.writeFileSync(RUNTIME_FILE, JSON.stringify({
        baseURL,
        storageState: STORAGE_FILE,
        pid: child.pid,
        backendLog: BACKEND_LOG,
    }, null, 2));

    // Detach so child keeps running after globalSetup returns. Workers reach the backend
    // via baseURL; teardown kills it by PID.
    child.unref();
};

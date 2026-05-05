// @ts-check
const path = require('node:path');
const fs = require('node:fs');
const { execSync } = require('node:child_process');

const RUNTIME_FILE = path.resolve(__dirname, '.playwright-runtime.json');

module.exports = async () => {
    if (!fs.existsSync(RUNTIME_FILE)) return;

    let runtime;
    try {
        runtime = JSON.parse(fs.readFileSync(RUNTIME_FILE, 'utf8'));
    } catch {
        return;
    }

    if (runtime.pid) {
        try {
            if (process.platform === 'win32') {
                // /T kills child processes (dotnet run spawns vb.exe as a child); /F forces.
                execSync(`taskkill /T /F /PID ${runtime.pid}`, { stdio: 'ignore' });
            } else {
                process.kill(runtime.pid, 'SIGTERM');
            }
        } catch {
            // Best-effort: process may have already exited.
        }
    }

    for (const f of [RUNTIME_FILE, runtime.storageState]) {
        if (f && fs.existsSync(f)) {
            try { fs.unlinkSync(f); } catch { /* ignore */ }
        }
    }
};

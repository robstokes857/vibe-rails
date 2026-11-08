import { spawn } from 'node:child_process';
import * as fs from 'node:fs';
import * as net from 'node:net';
import * as path from 'node:path';

/** Runs trusted mouse input against an isolated, backend-free VS Code webview. */
async function main(): Promise<void> {
    const extension = path.resolve(__dirname, '../..');
    const executable = process.env.VIBERAILS_VSCODE_CLI?.trim()
        || path.join(process.env.LOCALAPPDATA || '', 'Programs/Microsoft VS Code/Code.exe');
    if (!fs.existsSync(executable)) throw new Error('Set VIBERAILS_VSCODE_CLI to a VS Code executable.');
    const artifacts = path.join(extension, '.vscode-test');
    fs.mkdirSync(artifacts, { recursive: true });
    const profile = fs.mkdtempSync(path.join(artifacts, 'board-drag-'));
    const extensions = path.join(profile, 'extensions');
    fs.mkdirSync(path.join(profile, 'User'));
    fs.mkdirSync(extensions);
    fs.writeFileSync(path.join(profile, 'User/settings.json'), JSON.stringify({
        'viberails.autoStart': false,
        'workbench.startupEditor': 'none',
        'window.restoreWindows': 'none'
    }));
    const listener = net.createServer();
    await new Promise<void>((resolve, reject) => {
        listener.once('error', reject);
        listener.listen(0, '127.0.0.1', resolve);
    });
    const port = (listener.address() as net.AddressInfo).port;
    await new Promise<void>(resolve => listener.close(() => resolve()));
    const env = {
        ...process.env,
        VIBERAILS_DRAG_CDP_PORT: String(port),
        VIBERAILS_TEST_GREP: 'Board dragging in a debugged VS Code webview'
    };
    for (const key of Object.keys(env)) {
        if (key.startsWith('VIBERAILS_TOOL_') || key === 'VIBERAILS_SMOKE_WORKSPACE'
            || key === 'ELECTRON_RUN_AS_NODE' || key.startsWith('VSCODE_')) delete env[key as keyof typeof env];
    }
    const args = [
        '--new-window', '--skip-welcome', '--skip-release-notes', '--disable-updates', '--disable-extensions',
        `--user-data-dir=${profile}`, `--extensions-dir=${extensions}`,
        `--remote-debugging-port=${port}`, '--remote-debugging-address=127.0.0.1',
        `--extensionDevelopmentPath=${extension}`,
        `--extensionTestsPath=${path.join(extension, 'out/test/suite/index.js')}`
    ];
    const child = spawn(executable, args, { env, windowsHide: true, stdio: 'inherit' });
    console.log(`Board drag debug test: PID ${child.pid}, loopback port ${port}. No backend is started.`);
    const code = await new Promise<number>((resolve, reject) => {
        child.once('error', reject);
        child.once('exit', (code, signal) => {
            if (signal) reject(new Error(`VS Code test stopped by ${signal}`));
            else resolve(code ?? 1);
        });
    });
    if (code !== 0) throw new Error(`VS Code Board drag test failed with exit code ${code}.`);
}

main().catch(error => { console.error(error); process.exitCode = 1; });

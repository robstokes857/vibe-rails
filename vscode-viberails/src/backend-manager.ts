import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as http from 'http';
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';
import {
    BOOTSTRAP_STDOUT_PREFIX,
    DEFAULT_STARTUP_TIMEOUT_MS,
    GRACEFUL_SHUTDOWN_EXIT_TIMEOUT_MS,
    GRACEFUL_SHUTDOWN_REQUEST_TIMEOUT_MS,
    LAUNCH_ARG_VS_CODE,
    OUTPUT_CHANNEL_NAME,
    SESSION_TOKEN_HEADER,
    SHUTDOWN_PATH,
    STDIN_CLOSE_EXIT_TIMEOUT_MS,
    TAB_TOKEN_HEADER,
} from './constants';

/**
 * Root backend launch arguments.
 *
 * Do not pass --parent-pid here. The root backend must not tie its lifetime
 * to the VS Code extension host, which VS Code can recycle independently of
 * the dashboard. Per-tab child backends get --parent-pid from the root via
 * TerminalTabHostService.SpawnChildAsync.
 *
 * Exported (rather than a private method) so tests can assert on it without
 * constructing a manager or casting through `any`.
 */
export function buildLaunchArgs(): string[] {
    return [LAUNCH_ARG_VS_CODE];
}

export interface BootstrapEndpoint {
    bootstrapUrl: string;
    host: string;
    port: number;
}

/**
 * Parse and validate the backend's stdout bootstrap contract before retaining it.
 * Auth tokens are later sent back to this host, so only the exact local hosts used
 * by VibeRails are accepted.
 */
export function parseBootstrapEndpoint(bootstrapUrl: string): BootstrapEndpoint {
    const parsed = new URL(bootstrapUrl);
    if (parsed.protocol !== 'http:') {
        throw new Error('bootstrap URL must use HTTP');
    }

    const host = parsed.hostname.toLowerCase().replace(/^\[|\]$/g, '');
    if (host !== 'localhost' && host !== '127.0.0.1' && host !== '::1') {
        throw new Error('bootstrap URL host must be loopback');
    }

    const port = Number(parsed.port);
    if (!Number.isInteger(port) || port <= 0 || port > 65535) {
        throw new Error('bootstrap URL must include a valid port');
    }

    return {
        bootstrapUrl,
        host,
        port
    };
}

export class BackendManager {
    private process: cp.ChildProcess | null = null;
    private port: number | null = null;
    private host: string | null = null;
    private bootstrapUrl: string | null = null;
    private sessionToken: string | null = null;
    private tabToken: string | null = null;
    private stopPromise: Promise<void> | null = null;
    private disposed = false;
    private outputChannel: vscode.OutputChannel;
    private _onPortDetected: vscode.EventEmitter<number> = new vscode.EventEmitter<number>();
    public readonly onPortDetected: vscode.Event<number> = this._onPortDetected.event;

    constructor(private readonly exePath: string) {
        this.outputChannel = vscode.window.createOutputChannel(OUTPUT_CHANNEL_NAME);
    }

    public getPort(): number | null {
        return this.port;
    }

    public getBootstrapUrl(): string | null {
        return this.bootstrapUrl;
    }

    public getHost(): string | null {
        return this.host;
    }

    public getExePath(): string {
        return this.exePath;
    }

    public isRunning(): boolean {
        return this.process !== null
            && Number.isInteger(this.port)
            && this.port! > 0;
    }

    /**
     * Session/tab tokens are instance-wide and valid for the whole backend process
     * lifetime. The manager holds them so a graceful shutdown can authenticate even
     * after the webview panel is gone.
     */
    public setAuthTokens(sessionToken: string | null, tabToken: string | null): void {
        this.sessionToken = sessionToken;
        this.tabToken = tabToken;
    }

    public getAuthTokens(): { sessionToken: string | null, tabToken: string | null } {
        return { sessionToken: this.sessionToken, tabToken: this.tabToken };
    }

    public async start(
        targetProjectFolder: string | null,
        startupTimeoutMs: number = DEFAULT_STARTUP_TIMEOUT_MS
    ): Promise<number> {
        if (this.stopPromise) {
            await this.stopPromise;
        }

        if (this.isRunning()) {
            return this.port!;
        }

        if (!targetProjectFolder) {
            throw new Error('No workspace folder is open. Open a project folder in VS Code before starting VibeRails.');
        }
        const cwd = targetProjectFolder;
        const launchArgs = buildLaunchArgs();

        this.logLine(`Starting VibeRails: ${this.exePath}`);
        this.logLine(`Working directory: ${cwd}`);
        this.logLine(`Extension host PID: ${process.pid}`);
        this.logLine(`Launch args: ${launchArgs.join(' ')}`);

        // Ensure a crash dump location exists so native crashes (0xC0000005 etc.)
        // produce a minidump we can actually debug. The .NET runtime writes
        // DOTNET_DbgMiniDumpName at the path below; %d expands to the PID.
        const crashDir = path.join(os.homedir(), '.vibe_rails', 'crashdumps');
        try { fs.mkdirSync(crashDir, { recursive: true }); } catch { /* best effort */ }
        const crashDumpPath = path.join(crashDir, 'vb-crash.%d.dmp');
        this.logLine(`Crash dump path: ${crashDumpPath}`);

        const childEnv: NodeJS.ProcessEnv = {
            ...process.env,
            DOTNET_DbgEnableMiniDump: '1',
            DOTNET_DbgMiniDumpType: '2',
            DOTNET_DbgMiniDumpName: crashDumpPath,
        };

        return new Promise((resolve, reject) => {
            this.process = cp.spawn(this.exePath, launchArgs, {
                cwd,
                stdio: ['pipe', 'pipe', 'pipe'],
                shell: false,
                windowsHide: true,
                env: childEnv
            });
            const child = this.process;
            const launchedAt = Date.now();
            this.logLine(`[Extension] Spawned backend PID ${child.pid ?? 'unknown'}`);

            let resolved = false;
            let stdoutBuffer = '';
            let lastBootstrapError: string | null = null;
            let startupTimer: NodeJS.Timeout | null = null;
            const clearStartupTimer = () => {
                if (startupTimer) {
                    clearTimeout(startupTimer);
                    startupTimer = null;
                }
            };

            child.stdout?.on('data', (data: Buffer) => {
                const text = data.toString();
                this.log(text);

                if (resolved) { return; }

                stdoutBuffer += text;
                const lines = stdoutBuffer.split(/\r?\n/);
                stdoutBuffer = lines.pop() ?? '';

                // Parse structured line: vs-code-v1=<bootstrapUrl>
                for (const rawLine of lines) {
                    const line = rawLine.trim();
                    if (!line.startsWith(BOOTSTRAP_STDOUT_PREFIX)) { continue; }
                    const bootstrapUrl = line.slice(BOOTSTRAP_STDOUT_PREFIX.length).trim();
                    let endpoint: BootstrapEndpoint;
                    try {
                        endpoint = parseBootstrapEndpoint(bootstrapUrl);
                    } catch (error) {
                        lastBootstrapError = error instanceof Error ? error.message : String(error);
                        this.logLine(`[Extension] Ignoring invalid bootstrap URL: ${lastBootstrapError}`);
                        continue;
                    }

                    this.bootstrapUrl = endpoint.bootstrapUrl;
                    this.host = endpoint.host;
                    this.port = endpoint.port;
                    this.logLine(
                        `[Extension] Backend ready on port ${this.port} (PID ${child.pid ?? 'unknown'})`
                    );
                    resolved = true;
                    clearStartupTimer();
                    this._onPortDetected.fire(this.port);
                    resolve(this.port!);
                    break;
                }
            });

            child.stderr?.on('data', (data: Buffer) => {
                this.log(`[stderr] ${data.toString()}`);
            });

            child.on('error', (err) => {
                this.logLine(`[Extension] Backend process error: ${err.message}`);
                this.cleanup(child);
                if (!resolved) {
                    clearStartupTimer();
                    reject(err);
                }
            });

            child.on('exit', (code, signal) => {
                const uptimeMs = Date.now() - launchedAt;
                this.logLine(
                    `[Extension] Process exited (pid: ${child.pid ?? 'unknown'}, code: ${code}, signal: ${signal}, uptimeMs: ${uptimeMs}, lastPort: ${this.port ?? 'n/a'})`
                );
                this.cleanup(child);
                if (!resolved) {
                    clearStartupTimer();
                    reject(new Error(`Backend exited before starting (code: ${code})`));
                }
            });

            startupTimer = setTimeout(() => {
                if (!resolved) {
                    void this.stop();
                    reject(new Error(lastBootstrapError
                        ? `Backend did not provide a valid bootstrap URL: ${lastBootstrapError}`
                        : 'Timeout waiting for backend to start'));
                }
            }, startupTimeoutMs);
        });
    }

    /**
     * Shutdown ladder, most graceful first:
     *   1. POST /api/v1/shutdown — the backend flushes state and stops its own host.
     *   2. Close stdin — the backend treats the closed pipe as a stop signal.
     *      (It never *reads* stdin, so writing to it would be a no-op.)
     *   3. taskkill /T /F on Windows, SIGTERM then SIGKILL elsewhere.
     */
    public async stop(): Promise<void> {
        if (this.stopPromise) {
            await this.stopPromise;
            return;
        }

        const child = this.process;
        if (!child) { return; }

        this.stopPromise = (async () => {
            try {
                let exited = await this.requestGracefulShutdown(child);

                if (!exited) {
                    try { child.stdin?.end(); } catch { /* stdin may already be closed */ }
                    exited = await this.waitForExit(child, STDIN_CLOSE_EXIT_TIMEOUT_MS);
                }

                if (!exited) {
                    exited = await this.forceTerminate(child);
                }

                if (!exited) {
                    this.logLine(
                        `[Extension] Backend PID ${child.pid ?? 'unknown'} did not exit after termination attempts.`
                    );
                }
            } finally {
                this.cleanup(child);
            }
        })();

        try {
            await this.stopPromise;
        } finally {
            this.stopPromise = null;
        }
    }

    private async requestGracefulShutdown(child: cp.ChildProcess): Promise<boolean> {
        if (child.exitCode !== null || child.signalCode !== null) { return true; }

        const port = this.port;
        const sessionToken = this.sessionToken;
        const tabToken = this.tabToken;

        // The endpoint is auth-gated and needs BOTH tokens; without them (backend
        // never finished bootstrapping) skip straight to the next rung.
        if (!port || !sessionToken || !tabToken) { return false; }

        this.logLine(`[Extension] Requesting graceful shutdown: POST ${SHUTDOWN_PATH}`);
        const accepted = await postShutdown(this.host ?? '127.0.0.1', port, sessionToken, tabToken);
        if (!accepted) {
            this.logLine('[Extension] Graceful shutdown request was not accepted; falling back.');
            return false;
        }

        const exited = await this.waitForExit(child, GRACEFUL_SHUTDOWN_EXIT_TIMEOUT_MS);
        this.logLine(`[Extension] Graceful shutdown ${exited ? 'succeeded' : 'timed out'}.`);
        return exited;
    }

    private waitForExit(child: cp.ChildProcess, timeoutMs: number): Promise<boolean> {
        if (child.exitCode !== null || child.signalCode !== null) {
            return Promise.resolve(true);
        }

        return new Promise((resolve) => {
            let settled = false;

            const onExit = () => {
                if (settled) { return; }
                settled = true;
                clearTimeout(timer);
                resolve(true);
            };

            const timer = setTimeout(() => {
                if (settled) { return; }
                settled = true;
                child.off('exit', onExit);
                resolve(false);
            }, timeoutMs);

            child.once('exit', onExit);
        });
    }

    private async forceTerminate(child: cp.ChildProcess): Promise<boolean> {
        if (process.platform === 'win32') {
            const pid = child.pid;
            if (pid) {
                await this.killProcessTreeWindows(pid);
            } else {
                try { child.kill(); } catch { /* process may already be gone */ }
            }
            return this.waitForExit(child, 3000);
        }

        try { child.kill('SIGTERM'); } catch { /* process may already be gone */ }
        let exited = await this.waitForExit(child, 2000);
        if (exited) { return true; }

        try { child.kill('SIGKILL'); } catch { /* process may already be gone */ }
        exited = await this.waitForExit(child, 2000);
        return exited;
    }

    private killProcessTreeWindows(pid: number): Promise<void> {
        return new Promise((resolve) => {
            const killer = cp.spawn('taskkill', ['/PID', String(pid), '/T', '/F'], {
                stdio: 'ignore',
                shell: false,
                windowsHide: true
            });

            killer.on('error', () => resolve());
            killer.on('exit', () => resolve());
        });
    }

    private cleanup(child?: cp.ChildProcess): void {
        if (child && this.process !== child) {
            return;
        }

        this.process = null;
        this.port = null;
        this.host = null;
        this.bootstrapUrl = null;
        this.sessionToken = null;
        this.tabToken = null;
    }

    /**
     * Log helpers. `shutdown()` disposes the output channel, and a late-arriving
     * stdout chunk or exit event would otherwise throw on a disposed channel.
     */
    private log(text: string): void {
        if (this.disposed) { return; }
        this.outputChannel.append(text);
    }

    private logLine(text: string): void {
        if (this.disposed) { return; }
        this.outputChannel.appendLine(text);
    }

    public async shutdown(): Promise<void> {
        await this.stop();
        if (this.disposed) { return; }
        this.disposed = true;
        this._onPortDetected.dispose();
        this.outputChannel.dispose();
    }

    public dispose(): void {
        void this.shutdown();
    }
}

/**
 * Fire-and-forget POST to the backend's shutdown endpoint. Resolves `true` only on a
 * 2xx; never throws, so the caller can always fall through to a harder kill.
 */
function postShutdown(host: string, port: number, sessionToken: string, tabToken: string): Promise<boolean> {
    return new Promise((resolve) => {
        let settled = false;
        const finish = (accepted: boolean) => {
            if (settled) { return; }
            settled = true;
            resolve(accepted);
        };

        const req = http.request(
            {
                host,
                port,
                path: SHUTDOWN_PATH,
                method: 'POST',
                headers: {
                    [SESSION_TOKEN_HEADER]: sessionToken,
                    [TAB_TOKEN_HEADER]: tabToken,
                    'content-length': '0'
                }
            },
            (res) => {
                const statusCode = res.statusCode ?? 0;
                res.resume();
                res.on('end', () => finish(statusCode >= 200 && statusCode < 300));
                res.on('error', () => finish(false));
            }
        );

        req.on('error', () => finish(false));
        req.setTimeout(GRACEFUL_SHUTDOWN_REQUEST_TIMEOUT_MS, () => {
            req.destroy();
            finish(false);
        });
        req.end();
    });
}

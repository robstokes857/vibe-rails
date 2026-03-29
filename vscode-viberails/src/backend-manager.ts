import * as vscode from 'vscode';
import * as cp from 'child_process';

export class BackendManager {
    private process: cp.ChildProcess | null = null;
    private port: number | null = null;
    private bootstrapUrl: string | null = null;
    private stopPromise: Promise<void> | null = null;
    private disposed = false;
    private outputChannel: vscode.OutputChannel;
    private _onPortDetected: vscode.EventEmitter<number> = new vscode.EventEmitter<number>();
    public readonly onPortDetected: vscode.Event<number> = this._onPortDetected.event;

    constructor(private readonly exePath: string) {
        this.outputChannel = vscode.window.createOutputChannel('VibeRails Backend');
    }

    public getPort(): number | null {
        return this.port;
    }

    public getBootstrapUrl(): string | null {
        return this.bootstrapUrl;
    }

    public isRunning(): boolean {
        return this.process !== null && this.port !== null;
    }

    public async start(targetProjectFolder: string | null): Promise<number> {
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

        this.outputChannel.appendLine(`Starting VibeRails: ${this.exePath}`);
        this.outputChannel.appendLine(`Working directory: ${cwd}`);
        this.outputChannel.show(true);

        return new Promise((resolve, reject) => {
            this.process = cp.spawn(this.exePath, this.buildLaunchArgs(), {
                cwd,
                stdio: ['pipe', 'pipe', 'pipe'],
                shell: false,
                windowsHide: true
            });
            const child = this.process;

            let resolved = false;
            let stdoutBuffer = '';

            child.stdout?.on('data', (data: Buffer) => {
                const text = data.toString();
                this.outputChannel.append(text);

                if (resolved) { return; }

                stdoutBuffer += text;
                const lines = stdoutBuffer.split(/\r?\n/);
                stdoutBuffer = lines.pop() ?? '';

                // Parse structured line: vs-code-v1=<bootstrapUrl>
                for (const rawLine of lines) {
                    const line = rawLine.trim();
                    if (!line.startsWith('vs-code-v1=')) { continue; }
                    const bootstrapUrl = line.slice('vs-code-v1='.length).trim();
                    this.bootstrapUrl = bootstrapUrl;
                    this.port = parseInt(new URL(bootstrapUrl).port, 10);
                    resolved = true;
                    this._onPortDetected.fire(this.port);
                    resolve(this.port!);
                    break;
                }
            });

            child.stderr?.on('data', (data: Buffer) => {
                this.outputChannel.append(`[stderr] ${data.toString()}`);
            });

            child.on('error', (err) => {
                this.cleanup(child);
                if (!resolved) { reject(err); }
            });

            child.on('exit', (code, signal) => {
                this.outputChannel.appendLine(`[Extension] Process exited (code: ${code}, signal: ${signal})`);
                this.cleanup(child);
                if (!resolved) {
                    reject(new Error(`Backend exited before starting (code: ${code})`));
                }
            });

            setTimeout(() => {
                if (!resolved) {
                    void this.stop();
                    reject(new Error('Timeout waiting for backend to start'));
                }
            }, 30000);
        });
    }

    private buildLaunchArgs(): string[] {
        const args = ['--vs-code-v1'];
        const parentPid = typeof process.pid === 'number' ? process.pid : 0;
        if (parentPid > 0) {
            args.push('--parent-pid', String(parentPid));

            const parentStartTicks = this.getCurrentProcessStartTimeTicks();
            if (parentStartTicks) {
                args.push('--parent-start-ticks', parentStartTicks);
            }
        }

        return args;
    }

    private getCurrentProcessStartTimeTicks(): string | null {
        try {
            const startMs = Date.now() - Math.floor(process.uptime() * 1000);
            if (!Number.isFinite(startMs) || startMs <= 0) {
                return null;
            }

            const unixEpochTicks = 621355968000000000n;
            return (BigInt(startMs) * 10000n + unixEpochTicks).toString();
        } catch {
            return null;
        }
    }

    public async stop(): Promise<void> {
        if (this.stopPromise) {
            await this.stopPromise;
            return;
        }

        const child = this.process;
        if (!child) { return; }

        this.stopPromise = (async () => {
            try {
                try {
                    child.stdin?.write('\n');
                    child.stdin?.end();
                } catch { /* stdin may already be closed */ }

                let exited = await this.waitForExit(child, 3000);
                if (!exited) {
                    exited = await this.forceTerminate(child);
                }

                if (!exited) {
                    this.outputChannel.appendLine(
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
        this.bootstrapUrl = null;
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

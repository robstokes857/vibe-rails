import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as path from 'path';
import * as os from 'os';
import * as fs from 'fs';

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
        const launchArgs = this.buildLaunchArgs();

        this.outputChannel.appendLine(`Starting VibeRails: ${this.exePath}`);
        this.outputChannel.appendLine(`Working directory: ${cwd}`);
        this.outputChannel.appendLine(`Extension host PID: ${process.pid}`);
        this.outputChannel.appendLine(`Launch args: ${launchArgs.join(' ')}`);
        this.outputChannel.show(true);

        // Ensure a crash dump location exists so native crashes (0xC0000005 etc.)
        // produce a minidump we can actually debug. The .NET runtime writes
        // DOTNET_DbgMiniDumpName at the path below; %d expands to the PID.
        const crashDir = path.join(os.homedir(), '.vibe_rails', 'crashdumps');
        try { fs.mkdirSync(crashDir, { recursive: true }); } catch { /* best effort */ }
        const crashDumpPath = path.join(crashDir, 'vb-crash.%d.dmp');
        this.outputChannel.appendLine(`Crash dump path: ${crashDumpPath}`);

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
            this.outputChannel.appendLine(`[Extension] Spawned backend PID ${child.pid ?? 'unknown'}`);

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
                    this.outputChannel.appendLine(
                        `[Extension] Backend ready on port ${this.port} (PID ${child.pid ?? 'unknown'})`
                    );
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
                this.outputChannel.appendLine(`[Extension] Backend process error: ${err.message}`);
                this.cleanup(child);
                if (!resolved) { reject(err); }
            });

            child.on('exit', (code, signal) => {
                const uptimeMs = Date.now() - launchedAt;
                this.outputChannel.appendLine(
                    `[Extension] Process exited (pid: ${child.pid ?? 'unknown'}, code: ${code}, signal: ${signal}, uptimeMs: ${uptimeMs}, lastPort: ${this.port ?? 'n/a'})`
                );
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
        // Do not attach the root VS Code backend to the extension-host PID.
        // VS Code can recycle that host process, and the parent watchdog will
        // then kill a healthy vb.exe instance even while the dashboard is open.
        // Child tab processes still opt into parent-linked cleanup separately.
        return ['--vs-code-v1'];
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

import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as path from 'path';
import * as http from 'http';

const INSTALL_DIR = path.join(process.env.USERPROFILE || process.env.HOME || '', '.vibe_rails');
const EXE_NAME = process.platform === 'win32' ? 'vb.exe' : 'vb';
const EXE_PATH = path.join(INSTALL_DIR, EXE_NAME);

export class BackendManager {
    private process: cp.ChildProcess | null = null;
    private port: number | null = null;
    private bootstrapUrl: string | null = null;
    private outputChannel: vscode.OutputChannel;
    private _onPortDetected: vscode.EventEmitter<number> = new vscode.EventEmitter<number>();
    public readonly onPortDetected: vscode.Event<number> = this._onPortDetected.event;

    constructor() {
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
        if (this.isRunning()) {
            return this.port!;
        }

        const cwd = targetProjectFolder || INSTALL_DIR;

        this.outputChannel.appendLine(`Starting VibeRails: ${EXE_PATH}`);
        this.outputChannel.appendLine(`Working directory: ${cwd}`);
        this.outputChannel.show(true);

        return new Promise((resolve, reject) => {
            this.process = cp.spawn(EXE_PATH, ['--vs-code-v1'], {
                cwd,
                stdio: ['pipe', 'pipe', 'pipe'],
                shell: process.platform === 'win32'
            });

            let resolved = false;

            this.process.stdout?.on('data', (data: Buffer) => {
                const text = data.toString();
                this.outputChannel.append(text);

                if (resolved) { return; }

                // Parse structured line: vs-code-v1=<bootstrapUrl>
                for (const line of text.split('\n')) {
                    if (!line.startsWith('vs-code-v1=')) { continue; }
                    const bootstrapUrl = line.trim().slice('vs-code-v1='.length).trim();
                    this.bootstrapUrl = bootstrapUrl;
                    this.port = parseInt(new URL(bootstrapUrl).port, 10);
                    resolved = true;
                    this._onPortDetected.fire(this.port);
                    this.waitForHealthy().then(() => resolve(this.port!)).catch(reject);
                    break;
                }
            });

            this.process.stderr?.on('data', (data: Buffer) => {
                this.outputChannel.append(`[stderr] ${data.toString()}`);
            });

            this.process.on('error', (err) => {
                this.cleanup();
                if (!resolved) { reject(err); }
            });

            this.process.on('exit', (code, signal) => {
                this.outputChannel.appendLine(`[Extension] Process exited (code: ${code}, signal: ${signal})`);
                this.cleanup();
                if (!resolved) {
                    reject(new Error(`Backend exited before starting (code: ${code})`));
                }
            });

            setTimeout(() => {
                if (!resolved) {
                    this.stop();
                    reject(new Error('Timeout waiting for backend to start'));
                }
            }, 30000);
        });
    }

    private async waitForHealthy(): Promise<void> {
        for (let i = 0; i < 30; i++) {
            if (await this.checkHealth()) {
                return;
            }
            await this.delay(500);
        }
        throw new Error('Backend health check failed');
    }

    private checkHealth(): Promise<boolean> {
        return new Promise((resolve) => {
            if (!this.port) { resolve(false); return; }
            const req = http.request({
                hostname: 'localhost',
                port: this.port,
                path: '/api/v1/IsLocal',
                method: 'GET',
                timeout: 2000
            }, (res) => {
                resolve(res.statusCode === 200);
            });
            req.on('error', () => resolve(false));
            req.on('timeout', () => { req.destroy(); resolve(false); });
            req.end();
        });
    }

    private delay(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    public async stop(): Promise<void> {
        if (!this.process) { return; }

        try {
            this.process.stdin?.write('\n');
            this.process.stdin?.end();
        } catch { /* stdin may already be closed */ }

        const graceful = await Promise.race([
            new Promise<boolean>(resolve => { this.process?.once('exit', () => resolve(true)); }),
            this.delay(3000).then(() => false)
        ]);

        if (!graceful && this.process) {
            this.process.kill('SIGTERM');
            await Promise.race([
                new Promise<void>(resolve => { this.process?.once('exit', () => resolve()); }),
                this.delay(2000)
            ]);
            if (this.process && !this.process.killed) {
                this.process.kill('SIGKILL');
            }
        }

        this.cleanup();
    }

    private cleanup(): void {
        this.process = null;
        this.port = null;
        this.bootstrapUrl = null;
    }

    public dispose(): void {
        this.stop();
        this._onPortDetected.dispose();
        this.outputChannel.dispose();
    }
}

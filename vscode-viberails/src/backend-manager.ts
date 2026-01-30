import * as vscode from 'vscode';
import * as cp from 'child_process';
import * as path from 'path';
import * as fs from 'fs';
import * as http from 'http';

export class BackendManager {
    private process: cp.ChildProcess | null = null;
    private port: number | null = null;
    private outputChannel: vscode.OutputChannel;
    private _onPortDetected: vscode.EventEmitter<number> = new vscode.EventEmitter<number>();
    public readonly onPortDetected: vscode.Event<number> = this._onPortDetected.event;

    constructor() {
        this.outputChannel = vscode.window.createOutputChannel('VibeRails Backend');
    }

    public getPort(): number | null {
        return this.port;
    }

    public isRunning(): boolean {
        return this.process !== null && this.port !== null;
    }

    public async start(installFolder: string, targetProjectFolder: string | null): Promise<number> {
        if (this.isRunning()) {
            return this.port!;
        }

        const execInfo = this.resolveExecutablePath(installFolder, targetProjectFolder);
        if (!execInfo) {
            throw new Error('Could not find VibeRails executable. Please set viberails.executablePath in settings or ensure VibeRails.csproj exists.');
        }

        this.outputChannel.appendLine(`Starting VibeRails backend: ${execInfo.command} ${execInfo.args.join(' ')}`);
        this.outputChannel.appendLine(`Working directory (local context): ${execInfo.cwd}`);
        this.outputChannel.show(true);

        return new Promise((resolve, reject) => {
            try {
                this.process = cp.spawn(execInfo.command, execInfo.args, {
                    cwd: execInfo.cwd,
                    stdio: ['pipe', 'pipe', 'pipe'],
                    shell: process.platform === 'win32'
                });

                let portDetected = false;

                this.process.stdout?.on('data', (data: Buffer) => {
                    const text = data.toString();
                    this.outputChannel.append(text);

                    // Parse stdout for port: "Vibe Rails server running on http://localhost:{port}"
                    const match = text.match(/running on http:\/\/localhost:(\d+)/i);
                    if (match && !portDetected) {
                        portDetected = true;
                        this.port = parseInt(match[1], 10);
                        this.outputChannel.appendLine(`\n[Extension] Detected port: ${this.port}`);
                        this._onPortDetected.fire(this.port);

                        // Start health check
                        this.waitForHealthy().then(() => {
                            resolve(this.port!);
                        }).catch(reject);
                    }
                });

                this.process.stderr?.on('data', (data: Buffer) => {
                    this.outputChannel.append(`[stderr] ${data.toString()}`);
                });

                this.process.on('error', (err) => {
                    this.outputChannel.appendLine(`[Extension] Process error: ${err.message}`);
                    this.cleanup();
                    if (!portDetected) {
                        reject(err);
                    }
                });

                this.process.on('exit', (code, signal) => {
                    this.outputChannel.appendLine(`[Extension] Process exited with code ${code}, signal ${signal}`);
                    this.cleanup();
                    if (!portDetected) {
                        reject(new Error(`Backend exited before port was detected (code: ${code})`));
                    }
                });

                // Timeout if port not detected within 30 seconds
                setTimeout(() => {
                    if (!portDetected) {
                        this.stop();
                        reject(new Error('Timeout waiting for backend to start'));
                    }
                }, 30000);

            } catch (err) {
                reject(err);
            }
        });
    }

    private async waitForHealthy(): Promise<void> {
        const maxAttempts = 30;
        const delayMs = 500;

        for (let i = 0; i < maxAttempts; i++) {
            try {
                const healthy = await this.checkHealth();
                if (healthy) {
                    this.outputChannel.appendLine('[Extension] Backend is healthy');
                    return;
                }
            } catch {
                // Ignore errors during health check
            }
            await this.delay(delayMs);
        }
        throw new Error('Backend health check failed after maximum attempts');
    }

    private checkHealth(): Promise<boolean> {
        return new Promise((resolve) => {
            if (!this.port) {
                resolve(false);
                return;
            }

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
            req.on('timeout', () => {
                req.destroy();
                resolve(false);
            });
            req.end();
        });
    }

    private delay(ms: number): Promise<void> {
        return new Promise(resolve => setTimeout(resolve, ms));
    }

    public async stop(): Promise<void> {
        if (!this.process) {
            return;
        }

        this.outputChannel.appendLine('[Extension] Stopping backend...');

        // Try graceful shutdown by writing newline to stdin (unblocks WaitForShutdownAsync)
        if (this.process.stdin) {
            try {
                this.process.stdin.write('\n');
                this.process.stdin.end();
            } catch {
                // Ignore errors if stdin already closed
            }
        }

        // Wait up to 3 seconds for graceful shutdown
        const gracefulShutdown = await Promise.race([
            new Promise<boolean>((resolve) => {
                this.process?.once('exit', () => resolve(true));
            }),
            this.delay(3000).then(() => false)
        ]);

        if (!gracefulShutdown && this.process) {
            this.outputChannel.appendLine('[Extension] Graceful shutdown timed out, killing process...');
            this.process.kill('SIGTERM');

            // Wait another 2 seconds, then force kill
            await Promise.race([
                new Promise<void>((resolve) => {
                    this.process?.once('exit', () => resolve());
                }),
                this.delay(2000)
            ]);

            if (this.process && !this.process.killed) {
                this.process.kill('SIGKILL');
            }
        }

        this.cleanup();
        this.outputChannel.appendLine('[Extension] Backend stopped');
    }

    private cleanup(): void {
        this.process = null;
        this.port = null;
    }

    private resolveExecutablePath(installFolder: string, targetProjectFolder: string | null): { command: string; args: string[]; cwd: string } | null {
        // The cwd is where we want to run (for local context) - use target project folder if available
        const cwd = targetProjectFolder || installFolder;

        // 1. Check user setting
        const config = vscode.workspace.getConfiguration('viberails');
        const configPath = config.get<string>('executablePath');
        if (configPath && fs.existsSync(configPath)) {
            return { command: configPath, args: [], cwd };
        }

        // 2. Check common development build paths relative to install folder
        const devPaths = [
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net10.0', 'VibeRails.exe'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net10.0', 'VibeRails.exe'),
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net9.0', 'VibeRails.exe'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net9.0', 'VibeRails.exe'),
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net8.0', 'VibeRails.exe'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net8.0', 'VibeRails.exe'),
            // Linux/macOS variants (no .exe)
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net10.0', 'VibeRails'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net10.0', 'VibeRails'),
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net9.0', 'VibeRails'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net9.0', 'VibeRails'),
            path.join(installFolder, 'VibeRails', 'bin', 'Debug', 'net8.0', 'VibeRails'),
            path.join(installFolder, 'VibeRails', 'bin', 'Release', 'net8.0', 'VibeRails'),
        ];

        for (const p of devPaths) {
            if (fs.existsSync(p)) {
                return { command: p, args: [], cwd };
            }
        }

        // 3. Check PATH environment variable for 'viberails' or 'VibeRails'
        const pathEnv = process.env.PATH || '';
        const pathSeparator = process.platform === 'win32' ? ';' : ':';
        const pathDirs = pathEnv.split(pathSeparator);

        for (const dir of pathDirs) {
            const candidates = process.platform === 'win32'
                ? ['VibeRails.exe', 'viberails.exe', 'vb.exe']
                : ['VibeRails', 'viberails', 'vb'];

            for (const candidate of candidates) {
                const fullPath = path.join(dir, candidate);
                if (fs.existsSync(fullPath)) {
                    return { command: fullPath, args: [], cwd };
                }
            }
        }

        // 4. Fallback: Use 'dotnet run' if csproj exists
        // For dotnet run, we need to run from the VibeRails project directory
        // but we pass the target folder as an argument or via environment
        const csprojPath = path.join(installFolder, 'VibeRails', 'VibeRails.csproj');
        if (fs.existsSync(csprojPath)) {
            return {
                command: 'dotnet',
                args: ['run', '--no-build', '--project', csprojPath],
                cwd  // Run from target project folder for local context
            };
        }

        return null;
    }

    public dispose(): void {
        this.stop();
        this._onPortDetected.dispose();
        this.outputChannel.dispose();
    }
}

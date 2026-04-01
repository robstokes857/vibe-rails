import * as path from 'path';
import { runTests } from '@vscode/test-electron';

async function main(): Promise<void> {
    const extensionDevelopmentPath = path.resolve(__dirname, '../..');
    const extensionTestsPath = path.resolve(__dirname, './suite/index');
    const workspacePath = path.resolve(extensionDevelopmentPath, '..');
    const vscodeExecutablePath = process.env.VIBERAILS_VSCODE_CLI?.trim()
        || (process.platform === 'win32' ? 'code.cmd' : undefined);

    await runTests({
        extensionDevelopmentPath,
        extensionTestsPath,
        vscodeExecutablePath,
        extensionTestsEnv: {
            VIBERAILS_SMOKE_WORKSPACE: workspacePath
        },
        launchArgs: [
            '--disable-workspace-trust',
            '--skip-welcome',
            '--skip-release-notes',
            '--disable-updates'
        ]
    });
}

main().catch((error) => {
    console.error('Failed to run VS Code smoke tests.');
    console.error(error);
    process.exit(1);
});

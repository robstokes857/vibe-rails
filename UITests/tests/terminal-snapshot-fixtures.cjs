const { execFileSync } = require('node:child_process');
const { mkdtempSync, readFileSync, rmSync } = require('node:fs');
const { tmpdir } = require('node:os');
const path = require('node:path');

// Shared by headless xterm and the real-browser tests. Always generate from current production code.
module.exports = function snapshotFixtures() {
    const directory = mkdtempSync(path.join(tmpdir(), 'vb-terminal-snapshots-'));
    try {
        const output = path.join(directory, 'snapshots.json');
        execFileSync('dotnet', ['run', '--project',
            path.resolve(__dirname, '../../Tests/headless/TerminalSnapshots/TerminalSnapshots.csproj'),
            '--no-launch-profile', '--verbosity', 'quiet', '--', output],
        { encoding: 'utf8', timeout: 120000, windowsHide: true });
        return JSON.parse(readFileSync(output, 'utf8'));
    } finally {
        rmSync(directory, { recursive: true, force: true });
    }
};

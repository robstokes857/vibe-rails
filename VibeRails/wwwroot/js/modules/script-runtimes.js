/**
 * The three runtimes a script in ~/.vibe_rails/scripts can use. The file extension picks
 * the interpreter, exactly as on the server (PythonScriptService.RuntimeFor), so a name
 * is all any view needs to label, highlight or describe a script.
 */
export const SCRIPT_RUNTIMES = Object.freeze([
    Object.freeze({
        id: 'python',
        extension: '.py',
        label: 'Python',
        command: 'python',
        icon: 'fa-brands fa-python',
        tabIcon: '🐍',
        monacoLanguage: 'python',
        argumentsHint: 'sys.argv'
    }),
    Object.freeze({
        id: 'pwsh',
        extension: '.ps1',
        label: 'PowerShell',
        command: 'pwsh',
        icon: 'fa-solid fa-terminal',
        tabIcon: '⚡',
        monacoLanguage: 'powershell',
        argumentsHint: '$args (or a param() block)'
    }),
    Object.freeze({
        id: 'bash',
        extension: '.sh',
        label: 'Bash',
        command: 'bash',
        icon: 'fa-solid fa-terminal',
        tabIcon: '🐚',
        monacoLanguage: 'shell',
        argumentsHint: '$1, $2, … ("$@")'
    })
]);

// Mirrors PythonScriptService.ScriptNamePattern. Client-side it only buys a better
// message before the round trip; the backend is still the authority.
export const SCRIPT_NAME_PATTERN = /^[A-Za-z0-9][A-Za-z0-9._ -]{0,120}\.(?:py|ps1|sh)$/;

export const SCRIPT_NAME_RULE =
    'Use a plain .py, .ps1 or .sh file name — letters, digits, dots, dashes and spaces only.';

/** Upload/drop filter: any of the three extensions, in any case. */
const SCRIPT_EXTENSION_PATTERN = /\.(py|ps1|sh)$/i;

export function isScriptFileName(name) {
    return SCRIPT_EXTENSION_PATTERN.test(String(name || ''));
}

export function scriptRuntimeById(id) {
    return SCRIPT_RUNTIMES.find((runtime) => runtime.id === id) || SCRIPT_RUNTIMES[0];
}

/** The runtime for a file name; anything without a known extension reads as Python. */
export function scriptRuntimeFor(name) {
    const lower = String(name || '').toLowerCase();
    return SCRIPT_RUNTIMES.find((runtime) => lower.endsWith(runtime.extension)) || SCRIPT_RUNTIMES[0];
}

/** The name without a known script extension ("deploy.ps1" → "deploy"). */
export function scriptStem(name) {
    return String(name || '').replace(SCRIPT_EXTENSION_PATTERN, '');
}

/** The same stem with `runtime`'s extension ("deploy.py" + bash → "deploy.sh"). */
export function withScriptExtension(name, runtime) {
    return `${scriptStem(name)}${runtime.extension}`;
}

/**
 * A new script that already demonstrates the two halves of the run window: arguments in,
 * and a return value out as JSON on the last line of stdout.
 */
export function newScriptTemplate(name) {
    const stem = scriptStem(name);
    const runtime = scriptRuntimeFor(name);
    if (runtime.id === 'pwsh') {
        return `<#\n${stem}\n\nRuns with pwsh from the VibeRails Automation page once you sign it with your PIN.\n\n`
            + `Arguments you pass in the run window arrive in $args; printing a JSON\nobject on the last line `
            + `makes it the return value the window shows.\n#>\n\n`
            + `Write-Output "${stem} ran with $($args.Count) argument(s)"\n`
            + `@{ ok = $true; arguments = @($args) } | ConvertTo-Json -Compress\n`;
    }
    if (runtime.id === 'bash') {
        return `#!/usr/bin/env bash\n# ${stem}\n#\n# Runs with bash from the VibeRails Automation page once you sign it with your PIN.\n#\n`
            + `# Arguments you pass in the run window arrive in "$@"; printing a JSON\n# object on the last line `
            + `makes it the return value the window shows.\n\nset -euo pipefail\n\n`
            + `echo "${stem} ran with $# argument(s)"\n`
            + `printf '{"ok": true, "argumentCount": %d}\\n' "$#"\n`;
    }
    return `"""${stem}\n\nRuns from the VibeRails Automation page once you sign it with your PIN.\n\nArguments you pass in the run window arrive in sys.argv; printing a JSON\nobject on the last line makes it the return value the window shows.\n"""\n\nimport json\nimport sys\n\n\ndef main(argv: list[str]) -> dict:\n    print(f"${stem} ran with {len(argv)} argument(s)")\n    return {"ok": True, "arguments": argv}\n\n\nif __name__ == "__main__":\n    print(json.dumps(main(sys.argv[1:])))\n`;
}

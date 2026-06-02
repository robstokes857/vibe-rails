// Shared Monaco editor loader.
//
// Monaco is bundled under wwwroot/assets/monaco/ and loaded lazily via its AMD
// loader (vs/loader.js). This module owns the load-once singletons so every
// consumer (sandbox diff viewer, terminal editor modal, ...) shares a single
// loader injection and a single `window.monaco`. Honors the CSP nonce
// (window.__viberails_NONCE__) and the asset base (window.__viberails_ASSETS_BASE__)
// used when the app is served under a sub-path (VS Code webview / remote frontend).
//
// On any failure the relevant singleton is reset to null so a later call retries.

let _monacoLoaderReady = null;
let _monacoReady = null;

function getAmdRequire() {
    if (typeof window.require === 'function' && window.require.config) {
        return window.require;
    }

    if (typeof require === 'function' && require.config) {
        return require;
    }

    return null;
}

function ensureMonacoLoader() {
    const existingRequire = getAmdRequire();
    if (existingRequire) {
        return Promise.resolve(existingRequire);
    }

    if (_monacoLoaderReady) {
        return _monacoLoaderReady;
    }

    _monacoLoaderReady = new Promise((resolve) => {
        const assetsBase = (window.__viberails_ASSETS_BASE__ || '').replace(/\/$/, '');
        const loaderSrc = assetsBase
            ? `${assetsBase}/assets/monaco/vs/loader.js`
            : 'assets/monaco/vs/loader.js';
        const script = document.createElement('script');
        script.src = loaderSrc;
        script.async = true;

        const cspNonce = window.__viberails_NONCE__ || '';
        if (cspNonce) {
            script.setAttribute('nonce', cspNonce);
        }

        script.addEventListener('load', () => {
            const loadedRequire = getAmdRequire();
            if (!loadedRequire) {
                console.error('Monaco loader loaded but AMD require was not available');
                _monacoLoaderReady = null;
                resolve(null);
                return;
            }

            resolve(loadedRequire);
        }, { once: true });

        script.addEventListener('error', () => {
            console.error('Monaco loader failed to load');
            _monacoLoaderReady = null;
            resolve(null);
        }, { once: true });

        document.head.appendChild(script);
    });

    return _monacoLoaderReady;
}

export function ensureMonaco() {
    if (_monacoReady) return _monacoReady;

    _monacoReady = (async () => {
        const amdRequire = await ensureMonacoLoader();
        if (!amdRequire) {
            console.error('Monaco loader not found');
            _monacoReady = null; // Allow retry
            return null;
        }

        const assetsBase = (window.__viberails_ASSETS_BASE__ || '').replace(/\/$/, '');
        const vsPath = assetsBase ? `${assetsBase}/assets/monaco/vs` : 'assets/monaco/vs';
        const cspNonce = window.__viberails_NONCE__ || undefined;
        amdRequire.config({ paths: { vs: vsPath }, cspNonce });

        return await new Promise((resolve) => {
            const timeout = setTimeout(() => {
                console.error('Monaco editor load timed out');
                _monacoReady = null; // Allow retry
                resolve(null);
            }, 15000);

            amdRequire(['vs/editor/editor.main'], () => {
                clearTimeout(timeout);
                const monacoInstance = window.monaco;
                if (!monacoInstance) {
                    console.error('Monaco editor loaded but global monaco was not available');
                    _monacoReady = null; // Allow retry
                    resolve(null);
                    return;
                }

                // Define custom theme once
                monacoInstance.editor.defineTheme('viberails-dark', {
                    base: 'vs-dark',
                    inherit: true,
                    rules: [
                        { token: 'comment', foreground: '6A6A7D', fontStyle: 'italic' },
                        { token: 'keyword', foreground: 'C586C0' },
                        { token: 'string', foreground: '9AC6C5' },
                        { token: 'number', foreground: 'B5CEA8' },
                        { token: 'type', foreground: '4EC9B0' },
                        { token: 'function', foreground: 'DCDCAA' },
                        { token: 'variable', foreground: '9CDCFE' },
                        { token: 'constant', foreground: '569CD6' },
                    ],
                    colors: {
                        'editor.background': '#1a1a22',
                        'editor.foreground': '#f0f0f5',
                        'editor.lineHighlightBackground': '#2b2b3640',
                        'editor.selectionBackground': '#5b2a8650',
                        'editorCursor.foreground': '#9ac6c5',
                        'editor.inactiveSelectionBackground': '#3e3e4a40',
                        'editorLineNumber.foreground': '#6A6A7D',
                        'editorLineNumber.activeForeground': '#9ac6c5',
                        'editorGutter.background': '#1a1a22',
                        'editorWidget.background': '#2b2b36',
                        'editorWidget.border': '#3e3e4a',
                        'input.background': '#1e1e24',
                        'input.border': '#3e3e4a',
                        'dropdown.background': '#2b2b36',
                        'dropdown.border': '#3e3e4a',
                        'list.hoverBackground': '#32323f',
                        'list.activeSelectionBackground': '#5b2a86',
                        'minimap.background': '#1a1a22',
                        'scrollbar.shadow': '#00000033',
                        'scrollbarSlider.background': '#3e3e4a80',
                        'scrollbarSlider.hoverBackground': '#7785ac80',
                        'scrollbarSlider.activeBackground': '#9ac6c580',
                        'diffEditor.insertedTextBackground': '#4caf5020',
                        'diffEditor.removedTextBackground': '#e5737320',
                        'diffEditor.insertedLineBackground': '#4caf5015',
                        'diffEditor.removedLineBackground': '#e5737315',
                    }
                });
                resolve(monacoInstance);
            }, (err) => {
                clearTimeout(timeout);
                console.error('Monaco editor failed to load:', err);
                _monacoReady = null; // Allow retry
                resolve(null);
            });
        });
    })();

    return _monacoReady;
}

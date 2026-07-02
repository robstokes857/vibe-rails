import { VibeTerminal } from './vibe-terminal.js';

const esc = value => String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');

function nextFrame() {
    return new Promise(resolve => requestAnimationFrame(() => resolve()));
}

// Only ever render a locally-produced image data URL. A tool result (which can come from
// an untrusted external MCP server) must not be able to steer the browser into fetching an
// arbitrary http(s) URL — that would be a tracking beacon / blind browser-side request.
function isSafeImageDataUrl(value) {
    return typeof value === 'string' && /^data:image\/(png|jpeg|webp|gif);/i.test(value.trim());
}

function decodeBase64Bytes(base64) {
    const normalized = String(base64 || '').trim();
    if (!normalized) return new Uint8Array();

    const binary = atob(normalized);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
    }
    return bytes;
}

export function getXtermPayload(value) {
    if (!value || typeof value !== 'object') return null;
    const bytes = value.xterm_ui_bytes;
    const png = value.xterm_png_string;
    if (bytes && typeof bytes === 'object') return value;
    if (typeof png === 'string' && png.trim()) return value;
    return null;
}

export async function renderXtermPayload(container, payload) {
    if (!container || !payload) return null;

    const xtermBytes = payload.xterm_ui_bytes;
    const cols = Math.max(20, Math.min(400, Number(xtermBytes?.cols || payload.cols || 120)));
    const rows = Math.max(5, Math.min(120, Number(xtermBytes?.rows || payload.rows || 30)));
    const byteLength = Number(xtermBytes?.byte_length || 0);

    container.innerHTML = `
        <div class="mcp-xterm-preview">
            <div class="mcp-xterm-toolbar">
                <span>xterm.js preview</span>
                <span>${cols}x${rows}${byteLength ? ` · ${byteLength.toLocaleString()} bytes` : ''}</span>
            </div>
            <div class="mcp-xterm-host vb-terminal-element" data-xterm-host></div>
            <div class="mcp-xterm-png" data-xterm-png hidden>
                <div class="mcp-xterm-toolbar">
                    <span>PNG render</span>
                    <span>data URL</span>
                </div>
                <img alt="Rendered terminal snapshot">
            </div>
        </div>
    `;

    const terminalHost = container.querySelector('[data-xterm-host]');
    const pngHost = container.querySelector('[data-xterm-png]');
    const pngImage = pngHost?.querySelector('img');

    let terminal = null;
    try {
        if (xtermBytes?.base64 && typeof window.Terminal === 'function') {
            terminal = new VibeTerminal({
                outputEl: terminalHost,
                cols,
                rows,
                disableStdin: true,
                scrollOnWrite: false,
                scrollback: xtermBytes.includes_scrollback === false ? rows : 20000,
                desktopFontSize: 13,
                mobileFontSize: 13,
                desktopLineHeight: 1.12,
                mobileLineHeight: 1.12,
                enableImageCapture: true
            });

            terminal.resetForSnapshotReplay();
            await terminal.writeAsync(decodeBase64Bytes(xtermBytes.base64));
            await nextFrame();
            await nextFrame();

            const png = terminal.captureImage();
            if (png) {
                payload.xterm_png_string = png;
            }
        } else if (terminalHost) {
            terminalHost.innerHTML = `<div class="mcp-xterm-fallback">xterm.js is not available in this browser context.</div>`;
        }

        if (isSafeImageDataUrl(payload.xterm_png_string) && pngImage && pngHost) {
            pngImage.src = payload.xterm_png_string;
            pngHost.hidden = false;
        }
    } catch (error) {
        if (terminalHost) {
            terminalHost.innerHTML = `<div class="mcp-xterm-fallback">Unable to render xterm payload: ${esc(error?.message || error)}</div>`;
        }
    }

    return {
        png: payload.xterm_png_string || null,
        dispose() {
            try { terminal?.dispose(); } catch { /* no-op */ }
        }
    };
}

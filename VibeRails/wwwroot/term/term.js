const tabToken = sessionStorage.getItem('viberails_tab');

const term = new Terminal({
  fontSize: 14,
  fontFamily: 'Consolas, "Courier New", monospace',
  scrollback: 5000,
  allowProposedApi: true,
  theme: {
    background: '#0d0d0d',
    foreground: '#d4d4d4',
    cursor: '#d4d4d4',
    cursorAccent: '#0d0d0d',
    selectionBackground: 'rgba(255,255,255,0.2)',
    black:         '#000000',
    red:           '#cd3131',
    green:         '#0dbc79',
    yellow:        '#e5e510',
    blue:          '#2472c8',
    magenta:       '#bc3fbc',
    cyan:          '#11a8cd',
    white:         '#e5e5e5',
    brightBlack:   '#666666',
    brightRed:     '#f14c4c',
    brightGreen:   '#23d18b',
    brightYellow:  '#f5f543',
    brightBlue:    '#3b8eea',
    brightMagenta: '#d670d6',
    brightCyan:    '#29b8db',
    brightWhite:   '#e5e5e5',
  },
});

const fitAddon = new FitAddon.FitAddon();
term.loadAddon(fitAddon);

if (window.WebglAddon) {
  try { term.loadAddon(new WebglAddon.WebglAddon()); } catch (_) {}
}
if (window.ImageAddon) {
  try { term.loadAddon(new ImageAddon.ImageAddon()); } catch (_) {}
}
if (window.WebLinksAddon) {
  try { term.loadAddon(new WebLinksAddon.WebLinksAddon()); } catch (_) {}
}

term.open(document.getElementById('terminal'));

// ── State ────────────────────────────────────────────────────────────────────

let ws = null;
let inputDisposable = null;
let reconnectTimer = null;
let reconnectDelay = 1000;
let kicked = false; // true when server told us to stop reconnecting

// ── Resize (debounced) ───────────────────────────────────────────────────────

let resizeTimer = null;

function sendResize() {
  if (ws && ws.readyState === WebSocket.OPEN) {
    ws.send(`__resize__:${term.cols},${term.rows}`);
  }
}

window.addEventListener('resize', () => {
  fitAddon.fit();
  clearTimeout(resizeTimer);
  resizeTimer = setTimeout(sendResize, 200);
});

// ── Connect ──────────────────────────────────────────────────────────────────

async function connect() {
  if (kicked) return;
  clearTimeout(reconnectTimer);

  try {
    const statusRes = await fetch('/api/v1/terminal/status', {
      headers: { 'viberails_tab': tabToken || '' },
    });
    if (!statusRes.ok) { scheduleReconnect(); return; }

    const { hasActiveSession } = await statusRes.json();
    if (!hasActiveSession) {
      const startRes = await fetch('/api/v1/terminal/start', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'viberails_tab': tabToken || '',
        },
        body: JSON.stringify({ cli: 'claude' }),
      });
      if (!startRes.ok) { scheduleReconnect(); return; }
    }
  } catch (_) {
    scheduleReconnect();
    return;
  }

  const protocol = location.protocol === 'https:' ? 'wss' : 'ws';
  ws = new WebSocket(
    `${protocol}://${location.host}/api/v1/terminal/ws?cols=${term.cols}&rows=${term.rows}`,
    [tabToken]
  );
  ws.binaryType = 'arraybuffer';

  ws.onopen = () => {
    reconnectDelay = 1000;
    sendResize();
    inputDisposable?.dispose();
    inputDisposable = term.onData(data => {
      if (ws.readyState === WebSocket.OPEN) ws.send(data);
    });
  };

  ws.onmessage = e => {
    if (e.data instanceof ArrayBuffer) {
      term.write(new Uint8Array(e.data));
      return;
    }
    // Check for kick command before writing to terminal
    if (typeof e.data === 'string' && e.data.startsWith('__disconnect_browser__')) {
      kicked = true;
      ws.close();
      return;
    }
    term.write(e.data);
  };

  ws.onerror = () => {};

  ws.onclose = () => {
    ws = null;
    if (!kicked) scheduleReconnect();
  };
}

function scheduleReconnect() {
  reconnectTimer = setTimeout(() => {
    reconnectDelay = Math.min(reconnectDelay * 2, 10000);
    connect();
  }, reconnectDelay);
}

// Fit first, then connect — so the PTY dimensions are correct when replay arrives
requestAnimationFrame(() => {
  fitAddon.fit();
  connect();
});

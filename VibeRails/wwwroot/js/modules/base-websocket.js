/**
 * Base WebSocket client with automatic reconnection, tab token auth, and URL construction.
 *
 * Usage:
 *   const ws = new BaseWebSocket('/api/v1/my-feature/ws', { app });
 *   ws.onMessage = (data) => console.log(JSON.parse(data));
 *   ws.start();
 *   // ws.send('hello');
 *   // ws.stop();
 */
export class BaseWebSocket {
    /**
     * @param {string} path - API path (e.g., '/api/v1/notifications/ws')
     * @param {object} [options]
     * @param {object} [options.app] - App instance with getApiBaseUrl(). Optional.
     * @param {number} [options.maxReconnectDelayMs=15000] - Max backoff delay.
     * @param {number} [options.maxReconnectAttempts=Infinity] - Max attempts before giving up.
     * @param {object} [options.queryParams] - Additional query parameters as key-value pairs.
     */
    constructor(path, options = {}) {
        this.path = path;
        this.app = options.app || null;
        this.maxReconnectDelayMs = options.maxReconnectDelayMs ?? 15000;
        this.maxReconnectAttempts = options.maxReconnectAttempts ?? Infinity;
        this.queryParams = options.queryParams || null;

        this.socket = null;
        this.reconnectTimer = null;
        this.reconnectAttempts = 0;
        this.stopped = true;

        /** @type {((data: string) => void) | null} */
        this.onMessage = null;

        /** @type {(() => void) | null} */
        this.onOpen = null;

        /** @type {((event: CloseEvent) => void) | null} */
        this.onClose = null;

        /** @type {((event: Event) => void) | null} */
        this.onError = null;

        /** @type {(() => void) | null} */
        this.onReconnecting = null;
    }

    /** Start the connection (idempotent). */
    start() {
        this.stopped = false;
        this._tryConnect();
    }

    /** Stop the connection and cancel reconnection. */
    stop() {
        this.stopped = true;

        if (this.reconnectTimer) {
            window.clearTimeout(this.reconnectTimer);
            this.reconnectTimer = null;
        }

        if (this.socket) {
            this.socket.close();
            this.socket = null;
        }
    }

    /** Send a string message. Returns false if not connected. */
    send(data) {
        if (!this.socket || this.socket.readyState !== WebSocket.OPEN) {
            return false;
        }
        this.socket.send(data);
        return true;
    }

    /** Send a JSON-serializable object. Returns false if not connected. */
    sendJson(obj) {
        return this.send(JSON.stringify(obj));
    }

    /** Whether the socket is currently open. */
    get isConnected() {
        return this.socket?.readyState === WebSocket.OPEN;
    }

    // --- Internal ---

    /** @private */
    _tryConnect() {
        void this._connect().catch((error) => {
            console.error(`[BaseWebSocket] Failed to connect to ${this.path}:`, error);
            this._scheduleReconnect();
        });
    }

    /** @private */
    async _connect() {
        if (this.stopped) return;

        if (this.socket
            && (this.socket.readyState === WebSocket.OPEN
                || this.socket.readyState === WebSocket.CONNECTING)) {
            return;
        }

        const tabToken = this._readTabToken();
        if (!tabToken) {
            this._scheduleReconnect();
            return;
        }

        const url = this._buildUrl();
        const socket = new WebSocket(url, [tabToken]);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.reconnectAttempts = 0;
            if (this.onOpen) this.onOpen();
        });

        socket.addEventListener('message', (event) => {
            if (this.onMessage) this.onMessage(event.data);
        });

        socket.addEventListener('close', (event) => {
            if (this.socket === socket) {
                this.socket = null;
            }

            if (this.onClose) this.onClose(event);

            if (!this.stopped) {
                this._scheduleReconnect();
            }
        });

        socket.addEventListener('error', (event) => {
            if (this.onError) this.onError(event);
        });
    }

    /** @private */
    _scheduleReconnect() {
        if (this.stopped || this.reconnectTimer) return;

        if (this.reconnectAttempts >= this.maxReconnectAttempts) {
            return;
        }

        if (this.onReconnecting) this.onReconnecting();

        const attempt = Math.min(this.reconnectAttempts, 5);
        const delayMs = Math.min(1000 * (2 ** attempt), this.maxReconnectDelayMs);
        this.reconnectAttempts += 1;

        this.reconnectTimer = window.setTimeout(() => {
            this.reconnectTimer = null;
            this._tryConnect();
        }, delayMs);
    }

    /** @private */
    _readTabToken() {
        return (window.sessionStorage.getItem('viberails_tab') || '').trim();
    }

    /** @private */
    _buildUrl() {
        let base;
        const apiBase = this.app?.getApiBaseUrl?.();
        if (apiBase) {
            base = apiBase.replace(/^http/, 'ws');
        } else {
            const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
            base = `${protocol}//${window.location.host}`;
        }

        let url = `${base}${this.path}`;

        if (this.queryParams) {
            const params = new URLSearchParams(this.queryParams);
            const qs = params.toString();
            if (qs) {
                url += `?${qs}`;
            }
        }

        return url;
    }
}

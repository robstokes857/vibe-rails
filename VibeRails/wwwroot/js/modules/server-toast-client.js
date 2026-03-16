export class ServerToastClient {
    constructor(app) {
        this.app = app;
        this.socket = null;
        this.reconnectTimer = null;
        this.reconnectAttempts = 0;
        this.stopped = false;
        this.hasShownConnectionErrorToast = false;
    }

    start() {
        this.stopped = false;
        this.tryConnect();
    }

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

    tryConnect() {
        void this.connect().catch((error) => {
            console.error('Failed to connect the app notification socket:', error);
            this.showConnectionErrorToast();
            this.scheduleReconnect();
        });
    }

    async connect() {
        if (this.stopped) {
            return;
        }

        if (this.socket
            && (this.socket.readyState === WebSocket.OPEN
                || this.socket.readyState === WebSocket.CONNECTING)) {
            return;
        }

        const tabToken = this.readStoredTabToken();
        if (!tabToken) {
            this.scheduleReconnect();
            return;
        }

        const socket = new WebSocket(this.getSocketUrl(), [tabToken]);
        this.socket = socket;

        socket.addEventListener('open', () => {
            this.reconnectAttempts = 0;
            this.hasShownConnectionErrorToast = false;
        });

        socket.addEventListener('message', (event) => {
            this.handleMessage(event.data);
        });

        socket.addEventListener('close', () => {
            if (this.socket === socket) {
                this.socket = null;
            }

            if (!this.stopped) {
                this.scheduleReconnect();
            }
        });

        socket.addEventListener('error', (error) => {
            console.error('App notification socket error:', error);
            this.showConnectionErrorToast();
        });
    }

    scheduleReconnect() {
        if (this.stopped || this.reconnectTimer) {
            return;
        }

        const attempt = Math.min(this.reconnectAttempts, 5);
        const delayMs = Math.min(1000 * (2 ** attempt), 15000);
        this.reconnectAttempts += 1;

        this.reconnectTimer = window.setTimeout(() => {
            this.reconnectTimer = null;
            this.tryConnect();
        }, delayMs);
    }

    readStoredTabToken() {
        return (window.sessionStorage.getItem('viberails_tab') || '').trim();
    }

    getSocketUrl() {
        const baseUrl = this.app.getApiBaseUrl();
        if (baseUrl) {
            return `${baseUrl.replace(/^http/, 'ws')}/api/v1/notifications/ws`;
        }

        const protocol = window.location.protocol === 'https:' ? 'wss:' : 'ws:';
        return `${protocol}//${window.location.host}/api/v1/notifications/ws`;
    }

    showConnectionErrorToast() {
        if (this.stopped || this.hasShownConnectionErrorToast) {
            return;
        }
        this.hasShownConnectionErrorToast = true;
        this.app.showToast(
            'Notifications Offline',
            'Live app notifications disconnected. Retrying in the background.',
            'error'
        );
    }

    handleMessage(rawMessage) {
        if (typeof rawMessage !== 'string' || rawMessage.length === 0) {
            return;
        }
        const message = JSON.parse(rawMessage);
        this.handleToastMessage(message);
    }

    handleToastMessage(payload) {
        if (!payload || typeof payload !== 'object') {
            return;
        }
        this.app.showToast(
            payload.title || 'Notification',
            payload.message || '',
            payload.type || 'info',
            {
                ...(payload.requireDismiss === true ? { requireDismiss: true } : {}),
                ...(payload.icon != null ? { icon: payload.icon } : {}),
                ...(payload.iconBackground != null ? { iconBackground: payload.iconBackground } : {}),
                ...(payload.iconColor != null ? { iconColor: payload.iconColor } : {})
            }
        );
    }
}

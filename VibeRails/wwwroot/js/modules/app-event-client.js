import { BaseWebSocket } from './base-websocket.js';

/**
 * Subscribes to /api/v1/events/ws and dispatches typed AppEvent messages
 * to registered handlers. Handles the built-in 'toast' type automatically.
 *
 * Usage:
 *   appEventClient.on('session_idle', payload => { ... });
 *   appEventClient.on('session_busy', payload => { ... });
 */
export class AppEventClient {
    constructor(app) {
        this.app = app;
        this._handlers = new Map(); // type -> Set<fn>
        this._hasShownConnectionErrorToast = false;

        this.ws = new BaseWebSocket('/api/v1/events/ws', { app });
        this.ws.onMessage = (data) => this._handleMessage(data);
        this.ws.onOpen = () => { this._hasShownConnectionErrorToast = false; };
        this.ws.onError = () => this._showConnectionErrorToast();
        this.ws.onReconnecting = () => this._showConnectionErrorToast();
    }

    start() {
        this.ws.start();
    }

    stop() {
        this.ws.stop();
    }

    /**
     * Register a handler for a specific event type.
     * @param {string} type
     * @param {function} fn  Called with the parsed payload object.
     * @returns {function} Unsubscribe function.
     */
    on(type, fn) {
        if (!this._handlers.has(type)) {
            this._handlers.set(type, new Set());
        }
        this._handlers.get(type).add(fn);
        return () => this.off(type, fn);
    }

    off(type, fn) {
        this._handlers.get(type)?.delete(fn);
    }

    _handleMessage(rawMessage) {
        if (typeof rawMessage !== 'string' || rawMessage.length === 0) return;

        let event;
        try {
            event = JSON.parse(rawMessage);
        } catch {
            return;
        }

        if (!event || typeof event.type !== 'string') return;

        const { type, payload } = event;

        // Built-in: handle toast events directly
        if (type === 'toast') {
            this._handleToast(payload);
            return;
        }

        const handlers = this._handlers.get(type);
        if (handlers) {
            for (const fn of handlers) {
                try { fn(payload); } catch (err) { console.error(`[AppEventClient] Handler error for "${type}":`, err); }
            }
        }
    }

    _handleToast(payload) {
        if (!payload || typeof payload !== 'object') return;
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

    _showConnectionErrorToast() {
        if (this.ws.stopped || this._hasShownConnectionErrorToast) return;
        this._hasShownConnectionErrorToast = true;
        this.app.showToast(
            'Notifications Offline',
            'Live app notifications disconnected. Retrying in the background.',
            'error'
        );
    }
}

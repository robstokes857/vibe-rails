import { BaseWebSocket } from './base-websocket.js';

export class ServerToastClient {
    constructor(app) {
        this.app = app;
        this.hasShownConnectionErrorToast = false;

        this.ws = new BaseWebSocket('/api/v1/notifications/ws', { app });
        this.ws.onMessage = (data) => this._handleMessage(data);
        this.ws.onOpen = () => { this.hasShownConnectionErrorToast = false; };
        this.ws.onError = () => this._showConnectionErrorToast();
        this.ws.onReconnecting = () => this._showConnectionErrorToast();
    }

    start() {
        this.ws.start();
    }

    stop() {
        this.ws.stop();
    }

    _handleMessage(rawMessage) {
        if (typeof rawMessage !== 'string' || rawMessage.length === 0) {
            return;
        }
        const message = JSON.parse(rawMessage);
        this._handleToastMessage(message);
    }

    _handleToastMessage(payload) {
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

    _showConnectionErrorToast() {
        if (this.ws.stopped || this.hasShownConnectionErrorToast) {
            return;
        }
        this.hasShownConnectionErrorToast = true;
        this.app.showToast(
            'Notifications Offline',
            'Live app notifications disconnected. Retrying in the background.',
            'error'
        );
    }
}

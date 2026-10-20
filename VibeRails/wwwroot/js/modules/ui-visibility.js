/** Browser visibility plus the VS Code panel state (retained webviews stay document-visible). */
export class UiVisibility {
    constructor(host = window, page = document) {
        this.host = host;
        this.page = page;
        this.hostHidden = false;
        this.listeners = new Set();
        this.hidden = this.isHidden();
        this.onChange = () => {
            const hidden = this.isHidden();
            const resumed = this.hidden && !hidden;
            this.hidden = hidden;
            if (resumed) this.notifyVisible();
        };
        this.onFocus = () => {
            if (!this.isHidden()) this.notifyVisible();
        };
        this.onMessage = ({ data }) => {
            if (!host.__viberails_VSCODE__ || data?.command !== 'hostVisibility'
                || typeof data.visible !== 'boolean') return;
            this.hostHidden = !data.visible;
            this.onChange();
        };
        host.addEventListener('message', this.onMessage);
        host.addEventListener('focus', this.onFocus);
        page.addEventListener('visibilitychange', this.onChange);
    }

    isHidden() {
        return Boolean(this.page.hidden || this.hostHidden);
    }

    onVisible(listener) {
        this.listeners.add(listener);
        return () => this.listeners.delete(listener);
    }

    notifyVisible() {
        for (const listener of this.listeners) listener();
    }

    dispose() {
        this.host.removeEventListener('message', this.onMessage);
        this.host.removeEventListener('focus', this.onFocus);
        this.page.removeEventListener('visibilitychange', this.onChange);
        this.listeners.clear();
    }
}

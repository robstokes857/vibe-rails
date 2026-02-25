(function (global) {
    class WebFontsAddon {
        constructor(options = {}) {
            this._options = options;
            this._terminal = null;
            this._onFontsChanged = null;
            this._refreshTimeoutId = null;
        }

        activate(terminal) {
            this._terminal = terminal;
            if (typeof document === 'undefined' || !document.fonts) {
                this._notifyLoaded();
                return;
            }

            const refreshTerminal = () => {
                if (!this._terminal) {
                    return;
                }

                try {
                    if (this._terminal.rows > 0) {
                        this._terminal.refresh(0, this._terminal.rows - 1);
                    } else {
                        this._terminal.refresh(0, 0);
                    }
                } catch {
                    // no-op
                }

                this._notifyLoaded();
            };

            this._onFontsChanged = () => {
                if (this._refreshTimeoutId) {
                    clearTimeout(this._refreshTimeoutId);
                }

                this._refreshTimeoutId = setTimeout(refreshTerminal, 0);
            };

            if (typeof document.fonts.addEventListener === 'function') {
                document.fonts.addEventListener('loadingdone', this._onFontsChanged);
                document.fonts.addEventListener('loadingerror', this._onFontsChanged);
            }

            this._warmFontCache(terminal.options.fontFamily);

            Promise.resolve(document.fonts.ready)
                .then(() => this._onFontsChanged && this._onFontsChanged())
                .catch(() => this._onFontsChanged && this._onFontsChanged());
        }

        _warmFontCache(fontFamily) {
            if (typeof document === 'undefined' || !document.fonts || typeof document.fonts.load !== 'function') {
                return;
            }

            const descriptors = this._buildFontDescriptors(fontFamily);
            if (descriptors.length === 0) {
                return;
            }

            const loads = descriptors.map((descriptor) =>
                document.fonts.load(descriptor).catch(() => false)
            );

            Promise.allSettled(loads)
                .then(() => this._onFontsChanged && this._onFontsChanged())
                .catch(() => this._onFontsChanged && this._onFontsChanged());
        }

        _buildFontDescriptors(fontFamily) {
            const families = String(fontFamily || '')
                .split(',')
                .map((part) => part.trim())
                .filter((part) => part.length > 0)
                .slice(0, 4);

            if (families.length === 0) {
                families.push('monospace');
            }

            const uniqueFamilies = [];
            for (const family of families) {
                if (!uniqueFamilies.includes(family)) {
                    uniqueFamilies.push(family);
                }
            }

            const descriptors = [];
            for (const family of uniqueFamilies) {
                descriptors.push(`14px ${family}`);
                descriptors.push(`700 14px ${family}`);
            }

            return descriptors;
        }

        _notifyLoaded() {
            if (typeof this._options.onLoaded !== 'function') {
                return;
            }

            try {
                this._options.onLoaded();
            } catch {
                // no-op
            }
        }

        dispose() {
            if (this._refreshTimeoutId) {
                clearTimeout(this._refreshTimeoutId);
                this._refreshTimeoutId = null;
            }

            if (typeof document !== 'undefined' && document.fonts && typeof document.fonts.removeEventListener === 'function' && this._onFontsChanged) {
                document.fonts.removeEventListener('loadingdone', this._onFontsChanged);
                document.fonts.removeEventListener('loadingerror', this._onFontsChanged);
            }

            this._onFontsChanged = null;
            this._terminal = null;
        }
    }

    global.WebFontsAddon = {
        WebFontsAddon
    };
})(globalThis);

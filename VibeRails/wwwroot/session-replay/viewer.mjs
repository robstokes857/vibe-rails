/** Mount an isolated replay document. See README.md for the data and lifecycle contract. */
export function mountSessionViewer(container, options = {}) {
    if (!container?.appendChild) throw new TypeError('A host element is required');
    const controller = new AbortController();
    const frame = document.createElement('iframe');
    frame.dataset.sessionReplay = 'true';
    frame.title = 'Session replay';
    frame.style.cssText = 'display:block;width:100%;height:100%;min-height:540px;border:0;color-scheme:dark';
    const loading = document.createElement('p');
    loading.setAttribute('role', 'status');
    loading.textContent = 'Loading session viewer…';
    container.append(loading);
    let api = null, disposed = false, shouldPlay = options.autoplay === true;
    let state = { ready: false, playing: false };
    let resolveInitialized;
    const initialized = new Promise(resolve => { resolveInitialized = resolve; });
    const listeners = new Set();
    if (options.onEvent) listeners.add(options.onEvent);
    const emit = event => {
        state = event.state;
        for (const listener of listeners) listener(event);
    };
    const ready = (async () => {
        try {
            const url = new URL('./index.html', import.meta.url);
            const response = await fetch(url, { signal: controller.signal, credentials: 'include' });
            if (!response.ok) throw new Error(`Viewer assets could not load (${response.status})`);
            const html = await response.text();
            if (disposed) return;
            const base = document.createElement('base'); base.href = url.href;
            const loaded = new Promise((resolve, reject) => {
                frame.addEventListener('load', resolve, { once: true });
                controller.signal.addEventListener('abort', () => reject(new DOMException('Viewer disposed', 'AbortError')), { once: true });
            });
            // Only the trusted static template enters srcdoc. Captured content is
            // supplied through the data source and rendered as text by the viewer.
            frame.srcdoc = html.replace('<head>', '<head>' + base.outerHTML);
            container.append(frame);
            await loaded;
            if (disposed) return;
            api = frame.contentWindow.sessionReplay;
            if (!api) throw new Error('The session viewer could not initialize');
            api.configure({ request: options.request, library: options.library, view: options.view, onEvent: emit });
            resolveInitialized();
            loading.remove();
            if (options.sessionId) {
                await api.load(options.sessionId);
                if (disposed || api.getState().sessionId !== options.sessionId) return;
                if (options.seekToUtc != null && api.getState().ready) {
                    const instant = typeof options.seekToUtc === 'number' ? options.seekToUtc : Date.parse(options.seekToUtc);
                    if (Number.isFinite(instant)) await api.seek(Math.max(api.getState().started, instant - 1500));
                }
                if (shouldPlay && api.getState().position < api.getState().end) await api.play();
            } else if (options.library !== false) api.browse();
        } catch (error) {
            resolveInitialized();
            if (disposed || error.name === 'AbortError') return;
            loading.textContent = error.message;
            frame.remove(); api?.dispose(); api = null;
            emit({ type: 'error', state, error: error.message });
            throw error;
        }
    })();
    // Hosts can await ready; an immediately closed or fire-and-forget mount never
    // produces an unhandled rejection before the host attaches its error handler.
    ready.catch(() => {});
    return {
        ready, element: frame,
        getState: () => api?.getState() ?? state,
        subscribe(listener) { listeners.add(listener); return () => listeners.delete(listener); },
        async load(id) { shouldPlay = false; await initialized; if (!disposed) return api?.load(id); },
        async play() { shouldPlay = true; await ready; if (!disposed && shouldPlay) await api?.play(); },
        pause() { shouldPlay = false; api?.pause(); },
        async seek(ms) { await ready; if (!disposed) await api?.seek(ms); },
        async setSpeed(speed) { await ready; if (!disposed) api?.setSpeed(speed); },
        async setSkipIdle(skip) { await ready; if (!disposed) api?.setSkipIdle(skip); },
        async setView(view) { await ready; if (!disposed) api?.setView(view); },
        async reload() { await ready; if (!disposed) await api?.reload(); },
        dispose() {
            if (disposed) return;
            disposed = true; resolveInitialized(); controller.abort(); api?.dispose(); api = null;
            frame.remove(); loading.remove(); listeners.clear(); state = { ready: false, playing: false };
        }
    };
}

import { PythonScriptsController } from '/VibeRails/wwwroot/js/modules/python-scripts-controller.js';

// Only this test fixture substitutes the socket. Backend tests run the same example in a real PTY.
const connections = [];
class FixtureSocket {
    static OPEN = 1;
    readyState = 0;
    constructor(url, protocols) {
        this.url = url; this.protocols = protocols; this.sent = []; this.input = '';
        connections.push(this);
        setTimeout(() => { this.readyState = 1; this.onopen?.(); }, 10);
    }
    send(data) {
        this.sent.push(data);
        if (typeof data === 'string' && data.startsWith('{')) {
            this.onmessage?.({ data: 'ready' });
            this.output('Hey!\r\nEnter something');
        } else if (data === 'stop') {
            this.onmessage?.({ data: 'stopped' });
        } else if (data instanceof Uint8Array) {
            const text = new TextDecoder().decode(data);
            this.input += text;
            this.output(text.replace(/\r/g, '\r\n'));
            if (this.input.endsWith('\r')) {
                this.output(`${this.input.trim()}\r\n`);
                this.onmessage?.({ data: 'exit:0' });
            }
        }
    }
    output(text) { this.onmessage?.({ data: new TextEncoder().encode(text).buffer }); }
    close() { this.readyState = 3; }
}
window.WebSocket = FixtureSocket;
sessionStorage.setItem('viberails_tab', 'fixture-tab');
const app = { showError: message => { throw Error(message); }, showToast() {}, apiCall: async () => scripts.state };
const scripts = new PythonScriptsController(app);
scripts.state = { pinConfigured: true, requirePinEachRun: false, scripts: [
    { name: 'test-script', fileName: 'test_script.py', displayName: 'test_script.py', status: 'approved' }
] };
document.getElementById('run-example').addEventListener('click', () => { void scripts.run('test-script'); });
window.scriptFixture = { scripts, connections };

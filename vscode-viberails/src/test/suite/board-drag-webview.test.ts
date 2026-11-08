import * as assert from 'assert/strict';
import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { WebviewPanelManager } from '../../webview-panel';

// Keep ordinary extension builds independent of UITests' optional installation.
// The debug-only runner loads Playwright from that harness after the skip guard.
interface CDPSession {
    send(method: string, params?: Record<string, unknown>): Promise<unknown>;
    detach(): Promise<void>;
}
interface Frame {
    url(): string;
    evaluate<T = unknown>(expression: string): Promise<T>;
    locator(selector: string): { boundingBox(): Promise<{x:number;y:number;width:number;height:number} | null> };
}
interface BrowserContext {
    pages(): Page[];
    on(event: 'page', listener: (page: Page) => void): void;
    newCDPSession(page: Page): Promise<CDPSession>;
}
interface Page {
    frames(): Frame[];
    bringToFront(): Promise<void>;
    context(): BrowserContext;
    screenshot(options: {path:string}): Promise<unknown>;
    on(event: 'pageerror', listener: (error: Error) => void): void;
}
interface Browser {
    contexts(): BrowserContext[];
    newBrowserCDPSession(): Promise<CDPSession>;
    close(): Promise<void>;
}

const pause = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

async function testFrame(browser: Browser): Promise<{ page: Page; frame: Frame }> {
    const deadline = Date.now() + 30000;
    while (Date.now() < deadline) {
        for (const context of browser.contexts()) for (const page of context.pages()) for (const frame of page.frames()) {
            if (await frame.evaluate('Boolean(window.__boardDragProbe?.ready)').catch(() => false)) return { page, frame };
        }
        await pause(100);
    }
    console.log('DEBUG TARGETS', JSON.stringify(await (await browser.newBrowserCDPSession()).send('Target.getTargets')));
    for (const context of browser.contexts()) for (const page of context.pages()) {
        for (const frame of page.frames()) console.log('DEBUG FRAME', frame.url(),
            await frame.evaluate('JSON.stringify({text:document.body?.innerText?.slice(-4000),probe:window.__boardDragProbe,frames:[...document.querySelectorAll("iframe")].map(x=>x.src)})').catch(String));
        await page.screenshot({path:path.resolve(__dirname,'../../../.vscode-test/board-drag-load-failure.png')});
    }
    throw new Error('The real VS Code Board webview did not become ready.');
}

async function mouse(cdp: CDPSession, type: 'mouseMoved' | 'mousePressed' | 'mouseReleased', x: number, y: number, held = false): Promise<void> {
    await cdp.send('Input.dispatchMouseEvent', {
        type, x, y, button: type === 'mouseMoved' && !held ? 'none' : 'left',
        buttons: held ? 1 : 0, clickCount: type === 'mouseMoved' ? 0 : 1
    });
}

async function drag(cdp: CDPSession, from: {x:number;y:number}, to: {x:number;y:number}, during?: () => Promise<void>): Promise<void> {
    await mouse(cdp,'mouseMoved',from.x,from.y);
    await mouse(cdp,'mousePressed',from.x,from.y,true);
    for (let step=1;step<=12;step++) {
        await mouse(cdp,'mouseMoved',from.x+(to.x-from.x)*step/12,from.y+(to.y-from.y)*step/12,true);
        await pause(25);
        if (during && step===3) await during();
    }
    await pause(150);
    await mouse(cdp,'mouseReleased',to.x,to.y);
    await pause(500);
}

suite('Board dragging in a debugged VS Code webview', () => {
    test('keeps native dragging intact during a lane repaint with real mouse input', async function () {
        const port = Number(process.env.VIBERAILS_DRAG_CDP_PORT);
        if (!port) { this.skip(); return; }
        this.timeout(180000);
        const { chromium } = require(path.resolve(__dirname, '../../../../UITests/node_modules/playwright-core')) as {
            chromium: { connectOverCDP(url:string): Promise<Browser> }
        };
        const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
        for (const context of browser.contexts()) {
            const watch = (page: Page) => { page.on('pageerror', error => console.log('DEBUG PAGE ERROR',error.message)); };
            context.pages().forEach(watch);
            context.on('page',watch);
        }
        const wwwroot = vscode.Uri.file(path.resolve(__dirname, '../../../../VibeRails/wwwroot'));
        const manager = new WebviewPanelManager(wwwroot.fsPath);
        const panels: vscode.WebviewPanel[] = [];
        const baselinePath = process.env.VIBERAILS_DRAG_BASELINE;
        const scenarios = [
            {mode:'current',repaint:false}, {mode:'current',repaint:true},
            ...(baselinePath ? [{mode:'baseline',repaint:false},{mode:'baseline',repaint:true}] : [])
        ];
        try {
            for (const {mode,repaint} of scenarios) {
                const label = `${mode}${repaint ? '-refresh' : ''}`;
                const roots = [wwwroot, ...(baselinePath ? [vscode.Uri.file(path.dirname(baselinePath))] : [])];
                const panel = vscode.window.createWebviewPanel('viberailsDragProbe', `Board drag probe: ${label}`,
                    vscode.ViewColumn.One, { enableScripts: true, localResourceRoots: roots });
                panels.push(panel);
                let baselineUrl = '';
                if (baselinePath) {
                    const baselineModule = path.join(path.dirname(baselinePath),'board-controller.mjs');
                    const moduleBase = panel.webview.asWebviewUri(vscode.Uri.joinPath(wwwroot,'js','modules')).toString();
                    fs.writeFileSync(baselineModule,fs.readFileSync(baselinePath,'utf8')
                        .replace(/(['"])\.\/([^'"]+)\1/g, (_,quote,relative) => `${quote}${moduleBase}/${relative}${quote}`));
                    baselineUrl = panel.webview.asWebviewUri(vscode.Uri.file(baselineModule)).toString();
                }
                const builder = manager as unknown as { buildHtml(webview: vscode.Webview, port: number, session: null, tab: null): string };
                let html = builder.buildHtml(panel.webview, 5000, null, null);
                const nonce = html.match(/<script nonce="([^"]+)"/)?.[1];
                assert.ok(nonce);
                const fixture = `<script nonce="${nonce}">
                (() => {
                    const columns = [{id:'review',name:'Review',position:0,color:'#a855f7'}, {id:'done',name:'Done',position:1,color:'#10b981'}];
                    const card = {id:'drag-probe',key:'VB-1',title:'Review fixes for a shared backend and frontend task editor',description:'Synthetic card for a VS Code drag regression.',columnId:'review',position:0,type:'bug',priority:'high',tags:[],assignee:'base:claude',commentCount:20};
                    const probe = window.__boardDragProbe = {ready:false,mode:${JSON.stringify(mode)},moves:[],events:[],errors:[]};
                    addEventListener('error', event => probe.errors.push(event.error?.stack || event.message));
                    for (const name of ['pointerdown','mousedown','dragstart','dragend','drop','mouseup'])
                        document.addEventListener(name, event => probe.events.push({name,target:event.target?.className}),true);
                    const originalFetch = window.fetch.bind(window);
                    window.fetch = async (input, init) => {
                        const url = new URL(typeof input === 'string' ? input : input.url, document.baseURI);
                        if (!url.pathname.startsWith('/api/v1/')) return originalFetch(input,init);
                        let result = {};
                        if (url.pathname === '/api/v1/context') result = {isInGit:true,rootPath:'C:/fixture'};
                        if (url.pathname === '/api/v1/board/boards') result = {boards:[{id:'main',name:'Drag probe',position:0,cardCount:1,columns}]};
                        if (url.pathname === '/api/v1/board/columns') result = {columns};
                        if (url.pathname === '/api/v1/board/cards') result = {cards:[card]};
                        if (url.pathname === '/api/v1/board/cards/activity') result = {cards:[card],activeAutomationColumnIds:[]};
                        if (url.pathname.endsWith('/drag-probe/move')) {
                            const move = JSON.parse(init.body); probe.moves.push(move); Object.assign(card,move); result=card;
                        }
                        return new Response(JSON.stringify(result),{status:200,headers:{'Content-Type':'application/json'}});
                    };
                    // The fixture has no backend and must never contact an existing root.
                    window.WebSocket = class extends EventTarget {
                        static CONNECTING=0; static OPEN=1; static CLOSING=2; static CLOSED=3;
                        readyState=3; close() {} send() {}
                    };
                    const ready = setInterval(async () => {
                        const controller = window.app?.boardController;
                        if (!controller?.root?.isConnected || !controller.state.cards.length) return;
                        clearInterval(ready);
                        if (probe.mode === 'baseline') {
                            const {BoardController} = await import(${JSON.stringify(baselineUrl)});
                            Object.setPrototypeOf(controller,BoardController.prototype);
                            controller.bindDragAndDrop();
                        }
                        probe.ready=true;
                    },50);
                    const locationUrl = new URL(location.href);
                    locationUrl.searchParams.set('view','board');
                    history.replaceState(null,'',locationUrl.href);
                })();
                </script>`;
                html = html.replace(/(<script nonce="[^"]+">\s*const loadVibeRailsScript)/, `${fixture}$1`);
                panel.webview.html = html;
                const { page, frame } = await testFrame(browser);
                await page.bringToFront();
                const cdp = await page.context().newCDPSession(page);
                const start = await frame.locator('.board-card-title').boundingBox();
                const target = await frame.locator('.board-lane-list[data-column-id="done"]').boundingBox();
                assert.ok(start && target);
                const x = start.x + 20, y = start.y + 10;
                const tx = target.x + target.width / 2, ty = target.y + 60;
                await drag(cdp,{x,y},{x:tx,y:ty},repaint ? async () => {
                        await frame.evaluate(`(() => {
                            const item=document.querySelector('[data-card-id="drag-probe"].is-chosen');
                            window.app.boardController.renderLanes();
                            window.__boardDragProbe.repaintPreserved=Boolean(item?.isConnected);
                        })()`);
                } : undefined);
                const result = await frame.evaluate<{ moves: unknown[]; lane: string; dragging: boolean; activeSortable: boolean; ghostCount: number; errors: string[]; repaintPreserved?: boolean }>(`JSON.parse(JSON.stringify({
                    ...window.__boardDragProbe,
                    dragging:Boolean(window.app.boardController._boardDragging),
                    activeSortable:Boolean(window.Sortable.active),
                    ghostCount:document.querySelectorAll('.is-ghost,.is-chosen,.sortable-fallback').length,
                    lane:document.querySelector('[data-card-id="drag-probe"]')?.closest('.board-lane-list')?.dataset.columnId
                }))`);
                console.log(`VS Code raw mouse ${label}: ${JSON.stringify(result)}`);
                const artifactRoot = path.resolve(__dirname, '../../../.vscode-test');
                fs.writeFileSync(path.join(artifactRoot, `board-drag-${label}.json`), JSON.stringify(result,null,2));
                await page.screenshot({path:path.join(artifactRoot, `board-drag-${label}.png`)});
                if (mode !== 'baseline') {
                    assert.deepEqual(result.moves, [{columnId:'done',position:0}]);
                    assert.equal(result.lane,'done');
                    assert.equal(result.dragging,false);
                    assert.equal(result.activeSortable,false);
                    assert.equal(result.ghostCount,0);
                    assert.deepEqual(result.errors,[]);
                    if (repaint) assert.equal(result.repaintPreserved,true);
                } else if (repaint) {
                    assert.deepEqual(result.moves,[]);
                    assert.equal(result.dragging,true);
                    assert.equal(result.repaintPreserved,false);
                    assert.match(result.errors.join('\n'),/removeEventListener/);
                }
                // The fix must preserve subsequent drags too. Record whether the
                // baseline recovers; do not assume every failure stays permanent.
                const retryStart = await frame.locator('.board-card-title').boundingBox();
                const retryColumn = result.lane === 'done' ? 'review' : 'done';
                const retryTarget = await frame.locator(`.board-lane-list[data-column-id="${retryColumn}"]`).boundingBox();
                assert.ok(retryStart && retryTarget);
                await drag(cdp,{x:retryStart.x+20,y:retryStart.y+10},
                    {x:retryTarget.x+retryTarget.width/2,y:retryTarget.y+60});
                const retryMoves = await frame.evaluate('window.__boardDragProbe.moves.length');
                console.log(`VS Code raw mouse ${label} second drag: ${retryMoves} total moves`);
                fs.writeFileSync(path.join(artifactRoot, `board-drag-${label}.json`),JSON.stringify({...result,retryMoves},null,2));
                if (mode !== 'baseline' || !repaint) assert.equal(retryMoves,2);
                await cdp.detach();
                panel.dispose();
                await pause(100);
            }
        } finally {
            panels.forEach(panel => panel.dispose());
            manager.dispose();
            await browser.close();
        }
    });
});

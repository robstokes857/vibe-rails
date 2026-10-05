const { test, expect } = require('@playwright/test');
const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const securityHeadersSource = readFileSync(join(__dirname, '../../VibeRails/Middleware/SecurityHeadersMiddleware.cs'), 'utf8');
const desktopCsp = [...securityHeadersSource.split('internal const string ContentSecurityPolicy =')[1].split('private readonly')[0].matchAll(/"([^"]*)"/g)].map(match => match[1]).join('');
const started = Date.UTC(2026,8,30);
const envelope = {
    session: { id:'fixture', cli:'codex', startedUtc:new Date(started).toISOString(), endedUtc:new Date(started+60000).toISOString(), sessionDisplayName:'Replay fixture' },
    sessionLogs: [
        { id:1,timestampUtc:new Date(started).toISOString(),rawBytes:Buffer.from('First screen\r\n').toString('base64') },
        { id:2,timestampUtc:new Date(started+10000).toISOString(),rawBytes:Buffer.from('\x1b[2J\x1b[HNext screen <img src=x onerror=alert(1)>').toString('base64') }
    ],
    terminalSessionLogs:[],
    userInputs:[{id:1,sequence:1,timestampUtc:new Date(started+2000).toISOString(),inputText:'Make it readable',fileChanges:[{id:1,filePath:'example.js',diffContent:'--- a/example.js\n+++ b/example.js\n+const works = true;',linesAdded:1,linesDeleted:0}]}]
};
async function shell(page, csp = '', headers = {}) {
    await page.route('**/fixture', route => route.fulfill({ headers, contentType:'text/html',body:`<!doctype html><html><head>${csp}</head><body style="margin:0"><div id="host" style="height:950px"></div><div id="second" style="height:850px"></div></body></html>` }));
    await page.goto('/fixture');
}
async function mount(page, options = {}) {
    await page.evaluate(async ({envelope,options}) => {
        const {mountSessionViewer} = await import('/session-replay/viewer.mjs');
        const {createEnvelopeSource} = await import('/session-replay/envelope.mjs');
        window.viewer = mountSessionViewer(document.getElementById('host'), {request:createEnvelopeSource(envelope),sessionId:'fixture',...options});
        await viewer.ready;
    }, {envelope,options});
    return page.frameLocator('#host iframe');
}
test('independent instances, patch/events, playback, rewind and complete disposal', async ({page}) => {
    const errors=[]; page.on('pageerror',error=>errors.push(error.message));
    await shell(page);
    const frame = await mount(page);
    await expect(frame.locator('#session-title')).toHaveText('Replay fixture');
    await expect(frame.locator('.html-preview')).toHaveCount(0);
    await page.evaluate(async envelope => {
        const {mountSessionViewer}=await import('/session-replay/viewer.mjs');
        const {createEnvelopeSource}=await import('/session-replay/envelope.mjs');
        window.other=mountSessionViewer(document.getElementById('second'),{request:createEnvelopeSource(envelope),sessionId:'fixture'});
        await other.ready;
        await viewer.seek(viewer.getState().started+10000);
    }, envelope);
    await expect(frame.locator('#position')).toHaveText('00:10');
    await expect(page.frameLocator('#second iframe').locator('#position')).toHaveText('00:00');
    await frame.locator('.file-row').click();
    await expect(frame.locator('#editor')).toBeVisible();
    await frame.locator('#events-tab').click();
    await frame.locator('.activity-row').first().click();
    await expect(frame.locator('#event-body')).toContainText('Make it readable');
    await page.evaluate(async()=>{await viewer.seek(viewer.getState().started);await viewer.setSpeed('max');await viewer.play();});
    await expect(frame.locator('#playback-status')).toHaveText('End of recording');
    expect(await page.evaluate(()=>{const state=viewer.getState();return [state.position===state.end,state.playing,state.frameIndex===state.frameCount];})).toEqual([true,false,true]);
    await page.evaluate(async()=>{await viewer.seek(viewer.getState().started);});
    await expect(frame.locator('#position')).toHaveText('00:00');
    await page.evaluate(()=>{viewer.dispose();viewer.dispose();other.dispose();});
    await expect(page.locator('iframe')).toHaveCount(0);
    await mount(page);
    await expect(page.frameLocator('#host iframe').locator('#play')).toBeEnabled();
    expect(errors).toEqual([]);
});
test('mobile readable screen, safe text, narrow layout and 3-second sampling', async ({page})=>{
    await page.setViewportSize({width:390,height:844});
    await shell(page); const frame=await mount(page);
    await expect(frame.locator('.html-replay')).toBeVisible();
    await expect(frame.locator('#terminal-viewport')).not.toBeVisible();
    await expect(frame.locator('#html-screen')).toContainText('First screen');
    await page.evaluate(async()=>{await viewer.seek(viewer.getState().started+10000);});
    await expect(frame.locator('#html-screen')).toContainText('<img src=x onerror=alert(1)>');
    await expect(frame.locator('#html-screen img')).toHaveCount(0);
    await page.evaluate(()=>viewer.setSkipIdle(false));
    await page.evaluate(async()=>{await viewer.seek(viewer.getState().started);await viewer.setSpeed(10);await viewer.play();});
    await expect.poll(()=>page.evaluate(()=>viewer.getState().position)).toBeGreaterThan(started+10000);
    await expect(frame.locator('#html-screen')).toContainText('First screen');
    await expect(frame.locator('#html-screen')).toContainText('Next screen',{timeout:5000});
    await page.evaluate(()=>viewer.pause());
    const inner=page.frames().find(f=>f.url()==='about:srcdoc');
    expect(await inner.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
    await page.setViewportSize({width:320,height:700});
    expect(await inner.evaluate(()=>document.documentElement.scrollWidth<=innerWidth)).toBe(true);
    await page.screenshot({path:'test-results/session-replay-mobile.png',fullPage:false});
});
test('stale load and teardown cancel pending data without resurfacing content', async({page})=>{
    await shell(page); await mount(page);
    await page.evaluate(async()=>{
        const {mountSessionViewer}=await import('/session-replay/viewer.mjs');
        window.aborted=false;
        viewer.dispose();
        window.viewer=mountSessionViewer(document.getElementById('host'),{sessionId:'pending',request:(_path,{signal})=>new Promise((resolve,reject)=>{
            window.requestStarted=true;signal.addEventListener('abort',()=>{window.aborted=true;reject(new DOMException('Aborted','AbortError'));});
        })});
    });
    await expect.poll(()=>page.evaluate(()=>window.requestStarted)).toBe(true);
    await page.evaluate(()=>viewer.dispose());
    await expect.poll(()=>page.evaluate(()=>window.aborted)).toBe(true);
    await expect(page.locator('iframe')).toHaveCount(0);
});
test('srcdoc works under the webview content policy',async({page})=>{
    await shell(page,`<meta http-equiv="Content-Security-Policy" content="default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; base-uri 'self'">`);
    const frame=await mount(page);await expect(frame.locator('#play')).toBeEnabled();
});

test('replay initializes under the desktop response security headers', async ({page}) => {
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/*', async route => {
        const response = await route.fetch();
        await route.fulfill({ response, headers: { ...response.headers(),
            'content-security-policy': desktopCsp,
            'x-content-type-options': 'nosniff',
            'x-frame-options': 'SAMEORIGIN' } });
    });
    await shell(page, '', { 'content-security-policy': desktopCsp,
        'x-content-type-options': 'nosniff', 'x-frame-options': 'SAMEORIGIN' });
    const frame = await mount(page);
    await expect(frame.locator('#play')).toBeEnabled();
    expect(errors).toEqual([]);
});
test('a new load supersedes an initial request and stale failure cannot replace it',async({page})=>{
    await shell(page);
    await page.evaluate(async envelope=>{
        const {mountSessionViewer}=await import('/session-replay/viewer.mjs');
        const {createEnvelopeSource}=await import('/session-replay/envelope.mjs');
        const source=createEnvelopeSource(envelope);
        window.viewer=mountSessionViewer(document.getElementById('host'),{sessionId:'old',request:(path,options)=>{
            if(path==='/api/sessions/old') return new Promise((resolve,reject)=>{
                window.initialPending=true;
                options.signal.addEventListener('abort',()=>{window.initialAborted=true;setTimeout(()=>reject(new Error('Late old failure')),30);});
            });
            return source(path,options);
        }});
    },envelope);
    await expect.poll(()=>page.evaluate(()=>window.initialPending)).toBe(true);
    await page.evaluate(()=>viewer.load('fixture'));
    await page.evaluate(()=>viewer.ready);
    await expect.poll(()=>page.evaluate(()=>window.initialAborted)).toBe(true);
    await expect(page.frameLocator('#host iframe').locator('#session-title')).toHaveText('Replay fixture');
    await expect(page.frameLocator('#host iframe').locator('#notice')).not.toHaveClass(/error/);
    await expect(page.frameLocator('#host iframe').locator('#notice')).not.toContainText('Late old failure');
});
test('desktop entry point carries tab auth, honors comment seek past end and closes on Escape',async({page})=>{
    await shell(page);
    const requests=[];
    await page.route('**/api/v1/session-replay/**',async route=>{
        requests.push(route.request().headers()['viberails_tab']);
        const path=new URL(route.request().url()).pathname;
        if(path.endsWith('/frames')) return route.fulfill({json:{items:[{id:1,at:started,data:'RG9uZQ==',cols:80,rows:24}],next:1,done:true}});
        return route.fulfill({json:{session:{id:'fixture',cli:'codex',title:'Fixture',started,ended:started+60000,directory:''},cards:[],prompts:[],changes:[],geometry:[],frameSource:'enriched',frameCount:1,frameBytes:4,frameMaxId:1,proxyMaxId:0,end:started+60000,notes:[]}});
    });
    await page.evaluate(async started=>{
        sessionStorage.setItem('viberails_tab','test-tab');
        const {showReplayModal}=await import('/js/modules/session-viewer.js');
        window.modal=await showReplayModal('fixture',{seekToUtc:started+100000});
    },started);
    expect(requests.every(token=>token==='test-tab')).toBe(true);
    const frame=page.frameLocator('iframe[data-session-replay]');
    await expect(frame.locator('#playback-status')).toHaveText('End of recording');
    expect(await page.evaluate(()=>modal.viewer.getState().playing)).toBe(false);
    await frame.locator('#expand-panel').click();
    await page.keyboard.press('Escape');
    await expect(page.locator('iframe')).toHaveCount(1);
    await expect(frame.locator('#expand-panel')).toHaveAttribute('aria-expanded','false');
    await page.keyboard.press('Escape');
    await expect(page.locator('iframe')).toHaveCount(0);
});
test('newer uploads expose their captured model, tool arguments and request/response bodies',async({page})=>{
    await shell(page);
    await page.evaluate(async envelope=>{
        const {mountSessionViewer}=await import('/session-replay/viewer.mjs');
        const {createEnvelopeSource}=await import('/session-replay/envelope.mjs');
        envelope.proxyExchanges=[{id:'capture',sessionId:'fixture',createdUtc:new Date(envelope.session.startedUtc).toISOString(),
            provider:'openai',method:'POST',path:'/responses',statusCode:200,elapsedMs:123,
            requestBefore:'{"model":"uploaded-model","reasoning":{"effort":"high"}}',requestAfter:'{"model":"uploaded-model"}',
            responseBody:JSON.stringify({output:[{type:'function_call',call_id:'call',name:'read_file',arguments:'{"path":"example.js"}'}]})}];
        window.viewer=mountSessionViewer(document.getElementById('host'),{request:createEnvelopeSource(envelope),sessionId:'fixture'});
        await viewer.ready;
    },envelope);
    const frame=page.frameLocator('#host iframe');
    await expect(frame.locator('#model')).toHaveText('uploaded-model');
    await expect(frame.locator('#effort')).toHaveText('high');
    await frame.locator('#events-tab').click();
    await frame.locator('.activity-row').filter({hasText:'read_file'}).click();
    await expect(frame.locator('#event-body')).toContainText('example.js');
    await frame.getByRole('button',{name:'Response',exact:true}).click();
    await expect(frame.locator('#event-body')).toContainText('function_call');
});

test('one full view: no view switch, the inspector always shows, and Max replaces 100x',async({page})=>{
    await shell(page);
    const frame=await mount(page);
    await expect(frame.locator('#view-simple, #view-advanced, .view-switch')).toHaveCount(0);
    for(const id of ['#inspector-panel','.metadata','.tool-strip','#timeline-markers','#skip-idle']) await expect(frame.locator(id)).toBeVisible();
    // Tom Select moves the selected option within the native select, so compare the set.
    expect(Object.fromEntries(await frame.locator('#speed option').evaluateAll(options=>options.map(option=>[option.value,option.textContent]))))
        .toEqual({1:'1× speed',2:'2× speed',5:'5× speed',10:'10× speed',25:'25× speed',max:'Max speed'});
    expect(await page.evaluate(async()=>{try{await viewer.setSpeed(100);return 'accepted';}catch(error){return error.name;}})).toBe('RangeError');
    expect(await page.evaluate(()=>'setView' in viewer)).toBe(false);
    // Choosing Max in the picker plays from the playhead straight to the end.
    await frame.locator('.play-controls .ts-control').click();
    await frame.locator('#speed-ts-dropdown [data-value="max"]').click();
    await expect(frame.locator('#speed')).toHaveValue('max');
    await frame.locator('#play').click();
    await expect(frame.locator('#playback-status')).toHaveText('End of recording');
    await expect(frame.locator('#position')).toHaveText(await frame.locator('#total').textContent());
    await expect(frame.locator('#play')).toContainText('Play');
});

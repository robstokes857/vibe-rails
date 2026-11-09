const { test, expect } = require('@playwright/test');

async function setup(page) {
    const state={presets:[],actions:[],bindings:[],runs:[],files:[],requests:[],conflict:false};
    const base=[['codex','Codex'],['claude','Claude'],['copilot','Copilot'],['opencode','OpenCode']];
    const items=()=>[...state.presets.map(p=>({id:p.id,name:p.name,description:p.description,definitionType:'preset',cli:p.cli,kind:p.initialPrompt?.trim()?'agent':'quick-launch',revision:p.revision,enabled:p.enabled,retired:false,canLaunch:p.enabled,canRunAutomatically:p.enabled && !!p.initialPrompt?.trim(),supportsAttachments:true})),...state.actions.map(p=>({...p,definitionType:'action',canLaunch:true,canRunAutomatically:true}))];
    await page.addInitScript(()=>sessionStorage.setItem('viberails_tab','fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*',()=>{});
    await page.route('**/api/v1/**',async route=>{
        const req=route.request(), path=new URL(req.url()).pathname, method=req.method(), body=req.postDataJSON();
        state.requests.push({path,method,body});
        const ok=json=>route.fulfill({json});
        if(path==='/api/v1/context')return ok({isInGit:true,rootPath:'C:\\fixtures\\project',launchDirectory:'C:\\fixtures\\project',isActiveRootBackend:true});
        if(path==='/api/v1/launch-catalog')return ok({items:items()});
        if(path==='/api/v1/llm-picker/preferences')return ok({items:[...base.map(([cli,label],order)=>({key:`base:${cli}`,kind:'base',group:'Base CLIs',label,cli,enabled:true,order})),...items().filter(p=>p.definitionType==='preset').map((p,order)=>({key:`preset:${p.id}:${p.cli}`,kind:p.kind,group:'AI Agents',label:p.name,cli:p.cli,enabled:p.enabled,order}))]});
        if(path.includes('/launch-defaults'))return ok({model:'',effort:'',speed:'',mode:'',yolo:false});
        if(path==='/api/v1/trigger-bindings'){if(method==='GET')return ok(state.bindings);const result={...body,id:'binding_'+state.bindings.length,revision:1};state.bindings.push(result);return ok(result);}
        if(path==='/api/v1/runs'){if(method==='GET')return ok({runs:state.runs,nextCursor:null});const p=state.presets.find(p=>p.id===body.definitionId);const run={id:'run_'+state.runs.length,sequence:state.runs.length+1,definitionId:body.definitionId,status:'queued',queuedUtc:new Date().toISOString(),snapshot:{preset:p},verdict:null};state.runs.push(run);return ok(run);}
        if(path.endsWith('/attachments')) {
            if(method==='GET')return ok(state.files);
            if(state.holdUpload)await state.holdUpload;
            if(state.failUploadName===body.name){state.failUploadName=null;return route.fulfill({status:500,json:{error:'Upload interrupted'}});}
            const file={id:'att_'+state.files.length,name:body.name,mimeType:'image/png',size:10};state.files.push(file);return ok(file);
        }
        const match=path.match(/^\/api\/v1\/(presets|actions)(?:\/([^/]+))?$/);
        if(match){const list=match[1]==='presets'?state.presets:state.actions;if(method==='GET')return ok(list.find(p=>p.id===match[2]));if(state.conflict)return route.fulfill({status:409,json:{error:'Changed elsewhere. Reopen the latest version before saving.',code:'revision_conflict',currentRevision:9}});const index=list.findIndex(p=>p.id===match[2]);const value={...body,id:match[2]||'preset_'+list.length,revision:(list[index]?.revision||0)+1};if(index<0)list.push(value);else list[index]=value;return ok(match[1]==='presets'?{preset:value,affectedBindings:[]}:value);}
        if(path==='/api/v1/board/files')return ok({files:['src/example.cs']});
        if(path==='/api/v1/agents')return ok({agents:[]});
        if(path==='/api/v1/rules/details')return ok({rules:[]});
        if(path==='/api/v1/board/boards')return ok([]);
        if(path.includes('terminal-tabs'))return ok({tabs:[]});
        if(path.includes('settings'))return ok({});
        return ok({});
    });
    await page.goto('/?view=ai-agents');
    await expect(page.getByRole('heading',{name:'AI Agents',exact:true})).toBeVisible();
    return state;
}

test('quick actions use explicit Run buttons and preserve launch and keyboard behavior', async ({ page }, testInfo) => {
    const state = await setup(page);
    state.presets.push(
        { id: 'release', name: 'Release Vibe Rails Client App', cli: 'codex', initialPrompt: 'Release', enabled: true },
        { id: 'hidden', name: 'Disabled agent', cli: 'codex', initialPrompt: 'Hidden', enabled: false }
    );
    state.actions.push({ id: 'script', name: 'Build <client> & test', kind: 'script' });
    await page.locator('[data-agent-refresh]').click();
    await expect(page.locator('[data-agent-edit="release"]')).toBeVisible();
    await page.evaluate(() => {
        window.quickActionLaunches = [];
        window.app.aiAgentsController.launch = async (...args) => window.quickActionLaunches.push(args);
    });
    const trigger = page.getByRole('button', { name: 'Launch an agent or automation', exact: true });
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await trigger.click();
    await expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
    await expect(panel.locator('.launch-menu-subtitle')).toHaveText(['Codex', 'Script']);
    for (const id of ['release', 'script']) {
        const catalogRow = page.locator('.launch-catalog-row').filter({ has: page.locator(`[data-agent-edit="${id}"]`) });
        const menuRow = panel.locator('.launch-menu-row').filter({ has: page.locator(`[data-launch-id="${id}"]`) });
        await expect(menuRow.locator('.launch-menu-name')).toHaveText(await catalogRow.locator('[data-agent-edit]').innerText());
        await expect(menuRow.locator('.launch-menu-subtitle')).toHaveText(await catalogRow.locator('.launch-catalog-copy p').innerText());
        expect(await menuRow.locator('.launch-provider-logo').evaluate(el => el.outerHTML))
            .toBe(await catalogRow.locator('.launch-provider-logo').evaluate(el => el.outerHTML));
    }
    await expect(panel.locator('img.launch-provider-logo')).toBeVisible();
    await expect.poll(() => panel.locator('img.launch-provider-logo').evaluate(el => el.complete && el.naturalWidth > 0)).toBe(true);
    await expect(panel.locator('.fa-code')).toBeVisible();
    await expect(panel.getByText('Disabled agent')).toHaveCount(0);
    await expect(panel.getByText('Build <client> & test', { exact: true })).toBeVisible();
    await expect(panel.locator('client')).toHaveCount(0);
    const runAgent = panel.getByRole('button', { name: 'Run Release Vibe Rails Client App', exact: true });
    await expect(runAgent).toBeFocused();
    await page.screenshot({ path: testInfo.outputPath('quick-actions-desktop.png') });
    await panel.getByText('Release Vibe Rails Client App', { exact: true }).click();
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([]);
    await runAgent.click();
    await expect(panel).toHaveCount(0);
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([['release', 'preset']]);
    await trigger.click();
    await expect(panel.getByRole('button', { name: 'Run Release Vibe Rails Client App' })).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(panel.getByRole('button', { name: 'Run Build <client> & test', exact: true })).toBeFocused();
    await page.keyboard.press('Enter');
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([['release', 'preset'], ['script', 'action']]);
    await trigger.click();
    await expect(panel).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(trigger).toBeFocused();
    state.presets = [];
    state.actions = [];
    await trigger.click();
    await expect(panel.getByRole('status')).toContainText('No quick actions available');
    await expect(panel.getByRole('button', { name: 'Manage AI Agents', exact: true })).toBeFocused();
    await panel.getByRole('button', { name: 'Manage AI Agents', exact: true }).click();
    await expect(panel).toHaveCount(0);
    await expect(page.getByRole('heading', { name: 'AI Agents', exact: true })).toBeVisible();
});

test('quick actions match catalog descriptions and provider branding for other launch types', async ({ page }) => {
    const state = await setup(page);
    state.presets.push(
        { id: 'claude', name: 'Review', description: 'Review <changes> & report', cli: 'claude', initialPrompt: 'Review', enabled: true },
        { id: 'quick', name: 'Quick terminal', cli: 'opencode', initialPrompt: '', enabled: true },
        { id: 'unknown', name: 'Custom provider', cli: 'custom', initialPrompt: 'Work', enabled: true }
    );
    state.actions.push({ id: 'script', name: 'Build', description: 'Compile <client>', kind: 'script' });
    await page.locator('[data-agent-refresh]').click();
    await expect(page.locator('[data-agent-edit="claude"]')).toBeVisible();
    await page.getByRole('button', { name: 'Launch an agent or automation', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await expect(panel.locator('.launch-menu-subtitle')).toHaveText(['Review <changes> & report', 'OpenCode', 'custom', 'Compile <client>']);
    await expect(panel.locator('changes, client')).toHaveCount(0);
    for (const id of ['claude', 'quick', 'unknown', 'script']) {
        const catalogRow = page.locator('.launch-catalog-row').filter({ has: page.locator(`[data-agent-edit="${id}"]`) });
        const menuRow = panel.locator('.launch-menu-row').filter({ has: page.locator(`[data-launch-id="${id}"]`) });
        await expect(menuRow.locator('.launch-menu-subtitle')).toHaveText(await catalogRow.locator('.launch-catalog-copy p').innerText());
        expect(await menuRow.locator('.launch-provider-logo').evaluate(el => el.outerHTML))
            .toBe(await catalogRow.locator('.launch-provider-logo').evaluate(el => el.outerHTML));
    }
});

for (const width of [320, 375, 768, 1024]) {
    test(`quick actions keep long labels and Run buttons inside the ${width}px viewport`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 700 });
        const state = await setup(page);
        state.presets.push({ id: 'release', name: 'Release Vibe Rails Client App with a very long descriptive agent name', cli: 'codex', initialPrompt: 'Release', enabled: true });
        state.actions.push({ id: 'script', name: 'BuildAndValidateAllClientPackagesWithoutSpaces', kind: 'script' });
        await page.getByRole('button', { name: 'Launch an agent or automation', exact: true }).click();
        const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
        await expect(panel.locator('.launch-menu-row')).toHaveCount(2);
        for (const element of [panel, ...await panel.locator('.launch-menu-name, .launch-menu-run').all()]) {
            const box = await element.boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width).toBeLessThanOrEqual(width);
        }
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        if (width <= 375) {
            const button = await panel.locator('.launch-menu-run').first().boundingBox();
            expect(button.height).toBeGreaterThanOrEqual(44);
        }
        await page.screenshot({ path: testInfo.outputPath(`quick-actions-${width}.png`) });
    });
}

test('shared editor creates an Agent, keeps provider drafts and uses native typed variables',async({page},testInfo)=>{
    const state=await setup(page);
    await page.locator('.vb-page-actions').getByRole('button',{name:'Create agent',exact:true}).click();
    const editor=page.locator('[data-launch-editor]');
    await editor.getByLabel('Name',{exact:true}).fill('Reviewer');
    const prompt=editor.getByLabel('Initial prompt (optional)',{exact:true});
    await prompt.fill('Review {{git_');
    await expect(page.getByRole('option',{name:/git_branch/})).toBeVisible();
    await prompt.press('Enter');
    await expect(prompt).toHaveValue('Review {{git_branch}} ');
    await expect(editor.locator('[data-board-composer-live]')).toHaveCount(0);
    await page.screenshot({path:testInfo.outputPath('agent-prompt-without-preview.png')});
    await expect(editor.locator('[data-board-launch-model]')).toBeVisible();
    await editor.locator('[data-board-launch-effort]').selectOption('high');
    await editor.locator('[data-launch-provider]').selectOption('base:claude');
    await editor.locator('[data-launch-provider]').selectOption('base:codex');
    await expect(editor.locator('[data-board-launch-effort]')).toHaveValue('high');
    await expect(prompt).toHaveValue('Review {{git_branch}} ');
    await editor.getByRole('button',{name:'Create agent',exact:true}).click();
    await expect.poll(()=>state.presets.length).toBe(1);
    expect(state.presets[0]).toMatchObject({name:'Reviewer',initialPrompt:'Review {{git_branch}} ',options:{base:{effort:'high'}},expectedRevision:0});
    expect(state.requests.filter(r=>/environments|\/jobs|\/automations/.test(r.path))).toEqual([]);
});

test('prompt help inserts every supported variable and saves literal placeholders', async ({ page }) => {
    const state = await setup(page);
    await page.locator('.vb-page-actions').getByRole('button', { name: 'Create agent', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    const prompt = editor.getByLabel('Initial prompt (optional)', { exact: true });
    const variables = editor.getByRole('group', { name: 'Insert a prompt variable' });
    const tokens = ['{{name}}', '{{name default="…"}}', '{{datetime}}', '{{date}}', '{{time}}',
        '{{git_branch}}', '{{env_name}}', '{{worktree}}', '{{board_card}}'];
    await expect(variables.getByRole('button')).toHaveText(tokens);
    await expect(prompt).toHaveAttribute('aria-describedby', 'launch-prompt-variable-help');
    await editor.getByLabel('Name', { exact: true }).fill('Variable examples');
    for (const token of tokens) {
        await variables.getByRole('button', { name: `Insert ${token}:`, exact: false }).click();
        await expect(prompt).toBeFocused();
        await expect(prompt).toHaveValue(tokens.slice(0, tokens.indexOf(token) + 1).join(''));
    }
    await expect(editor.locator('[data-launch-kind]')).toHaveText('Agent · starts with this prompt.');
    await expect(editor.locator('[data-board-composer-live]')).toHaveCount(0);
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets.length).toBe(1);
    expect(state.presets[0].initialPrompt).toBe(tokens.join(''));
    await page.getByRole('button', { name: 'Close dialog', exact: true }).click();
    await page.getByRole('button', { name: 'Variable examples', exact: true }).click();
    await expect(prompt).toHaveValue(tokens.join(''));
    await expect(editor.locator('[data-board-composer-live]')).toHaveCount(0);
});

test('variable shortcuts preserve the caret, replace selections, support undo and mark drafts dirty', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'quick', name: 'Quick', cli: 'codex', initialPrompt: '', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Quick', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    const prompt = editor.getByLabel('Initial prompt (optional)', { exact: true });
    const time = editor.getByRole('button', { name: 'Insert {{time}}: Current time', exact: true });
    await time.focus();
    await time.press('Enter');
    await expect(prompt).toHaveValue('{{time}}');
    await expect(prompt).toBeFocused();
    await expect(editor.locator('[data-launch-kind]')).toHaveText('Agent · starts with this prompt.');
    await page.getByRole('button', { name: 'Close dialog', exact: true }).click();
    const confirm = page.getByRole('alertdialog');
    await expect(confirm).toBeVisible();
    await confirm.getByRole('button', { name: 'Cancel', exact: true }).click();
    await prompt.fill('Before selected after');
    await prompt.evaluate(input => input.setSelectionRange(7, 7));
    await time.click();
    await expect(prompt).toHaveValue('Before {{time}}selected after');
    expect(await prompt.evaluate(input => input.selectionStart)).toBe(15);
    await prompt.press('ControlOrMeta+z');
    await expect(prompt).toHaveValue('Before selected after');
    await prompt.evaluate(input => input.setSelectionRange(7, 15));
    const branch = editor.getByRole('button', { name: 'Insert {{git_branch}}: Current Git branch', exact: true });
    await branch.focus();
    await branch.press('Space');
    await expect(prompt).toHaveValue('Before {{git_branch}} after');
    await expect(prompt).toBeFocused();
    expect(await prompt.evaluate(input => input.selectionStart)).toBe(21);
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0].initialPrompt).toBe('Before {{git_branch}} after');
});

for (const width of [375, 1280]) test(`prompt references highlight inside the native input at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    const state = await setup(page);
    await page.locator('.vb-page-actions').getByRole('button', { name: 'Create agent', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    const prompt = editor.getByLabel('Initial prompt (optional)', { exact: true });
    await editor.getByLabel('Name', { exact: true }).fill('Highlighted prompt');
    const source = 'Please release using @deploy/deploy.ps1\nAt {{time}}, review !VIBE-123 and #abcdef012345.\n<img src=x onerror="window.bad=1">';
    await prompt.fill(source);
    const mirror = editor.locator('.prompt-reference-mirror');
    await expect(mirror.locator('[data-prompt-reference]')).toHaveText(['@deploy/deploy.ps1', '{{time}}', '!VIBE-123', '#abcdef012345']);
    expect(await mirror.textContent()).toBe(source + '\u200b');
    await expect(mirror.locator('img,script')).toHaveCount(0);
    await expect(editor.locator('[data-board-composer-live]')).toHaveCount(0);
    await expect(editor.locator('.prompt-reference-overlay')).toHaveAttribute('aria-hidden', 'true');
    expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`inline-references-${width}.png`) });
    // The painted text and native caret must agree even when the file wraps.
    const point = await mirror.locator('[data-prompt-reference="variable"]').evaluate(el => {
        const rect = el.getBoundingClientRect(); return { x: rect.left + 3, y: rect.top + rect.height / 2 };
    });
    await page.mouse.click(point.x, point.y);
    const caret = await prompt.evaluate(input => input.selectionStart);
    expect(caret).toBeGreaterThanOrEqual(source.indexOf('{{time}}'));
    expect(caret).toBeLessThanOrEqual(source.indexOf('{{time}}') + 2);
    await prompt.evaluate(input => input.setSelectionRange(input.value.indexOf('{{time}}'), input.value.indexOf('{{time}}') + 8));
    await page.keyboard.insertText('{{date}}');
    await expect(mirror.locator('[data-prompt-reference="variable"]')).toHaveText('{{date}}');
    await prompt.press('ControlOrMeta+z');
    await expect(prompt).toHaveValue(source);
    await expect(mirror.locator('[data-prompt-reference="variable"]')).toHaveText('{{time}}');
    // The optional spelling wrapper can be added and removed without losing highlighting.
    await page.evaluate(async () => (await import('/js/modules/board-spelling.js')).setBoardSpellingEnabled(true));
    await expect(editor.locator('.board-spelling')).toHaveCount(1);
    await page.evaluate(async () => (await import('/js/modules/board-spelling.js')).setBoardSpellingEnabled(false));
    await expect(mirror.locator('[data-prompt-reference]')).toHaveCount(4);
    await prompt.dispatchEvent('compositionstart');
    await expect(editor.locator('.prompt-reference-overlay')).toHaveCSS('visibility', 'hidden');
    await prompt.dispatchEvent('compositionend');
    await page.emulateMedia({ forcedColors: 'active' });
    await expect(editor.locator('.prompt-reference-overlay')).toHaveCSS('display', 'none');
    await page.emulateMedia({ forcedColors: 'none' });
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.initialPrompt).toBe(source);
});

test('reference decorations follow scrolling, wrapping and font changes', async ({ page }) => {
    await setup(page);
    await page.locator('.vb-page-actions').getByRole('button', { name: 'Create agent', exact: true }).click();
    const prompt = page.getByLabel('Initial prompt (optional)', { exact: true });
    await prompt.fill(Array.from({ length: 30 }, (_, i) => `Line ${i}: @src/long-folder-name/example.cs and {{time}}`).join('\n'));
    await prompt.evaluate(input => {
        Object.assign(input.style, { height: '160px', minHeight: '160px', maxHeight: '160px', overflowY: 'auto', fontSize: '20px' });
        input.scrollTop = input.scrollHeight;
        input.dispatchEvent(new Event('scroll'));
    });
    const mirror = page.locator('.prompt-reference-mirror');
    await expect(mirror).toHaveCSS('font-size', '20px');
    const scroll = await prompt.evaluate(input => input.scrollTop);
    expect(scroll).toBeGreaterThan(0);
    await expect(mirror).toHaveCSS('transform', `matrix(1, 0, 0, 1, 0, -${scroll})`);
    await page.setViewportSize({ width: 375, height: 1000 });
    await expect.poll(async () => mirror.evaluate(el => el.getBoundingClientRect().width)).toBe(await prompt.evaluate(input => input.clientWidth));
    expect(await mirror.textContent()).toBe(await prompt.inputValue() + '\u200b');
    await expect(page.locator('[data-launch-editor]')).toBeVisible();
});

test('conflict retains source and history refresh never changes the draft',async({page})=>{
    const state=await setup(page);
    state.presets.push({id:'preset_saved',name:'Saved',cli:'codex',initialPrompt:'Original',enabled:true,revision:2,options:{base:{effort:'high'}}});
    await page.getByRole('button',{name:'Refresh',exact:true}).click();
    await page.getByRole('button',{name:'Saved',exact:true}).click();
    const editor=page.locator('[data-launch-editor]'),prompt=editor.getByLabel('Initial prompt (optional)',{exact:true});
    await prompt.fill('Draft remains');
    await editor.getByRole('button',{name:'Refresh',exact:true}).click();
    await expect(prompt).toHaveValue('Draft remains');
    state.conflict=true;
    await editor.getByRole('button',{name:'Save',exact:true}).click();
    await expect(editor.locator('[data-launch-error]')).toContainText('Your draft is kept');
    await expect(prompt).toHaveValue('Draft remains');
    await prompt.fill('{{step:old}}');
    await editor.getByRole('button',{name:'Save',exact:true}).click();
    await expect(editor.locator('[data-launch-error]')).toContainText('Step placeholders are retired');
});

for(const width of [320,375,768,1024])test(`editor footer and single scroller at ${width}px`,async({page},testInfo)=>{
    await page.setViewportSize({width,height:800});await setup(page);
    await page.locator('.vb-page-actions').getByRole('button',{name:'Create agent',exact:true}).click();
    const editor=page.locator('[data-launch-editor]');
    const bounds=await editor.getByRole('button',{name:'Create agent',exact:true}).boundingBox();
    expect(bounds.y+bounds.height).toBeLessThanOrEqual(800);
    const close=await page.getByRole('button',{name:'Close dialog',exact:true}).boundingBox();
    expect(close.width).toBeGreaterThanOrEqual(44);expect(close.height).toBeGreaterThanOrEqual(44);
    expect(await editor.evaluate(el=>el.scrollWidth<=el.clientWidth)).toBeTruthy();
    const variables = editor.getByRole('group', { name: 'Insert a prompt variable' });
    await expect(variables).toBeVisible();
    expect(await variables.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    if (width <= 375) {
        const time = variables.getByRole('button', { name: 'Insert {{time}}: Current time', exact: true });
        const target = await time.boundingBox();
        expect(target.width).toBeGreaterThanOrEqual(44);
        expect(target.height).toBeGreaterThanOrEqual(44);
    }
    await page.screenshot({path:testInfo.outputPath(`editor-${width}.png`)});
    await variables.scrollIntoViewIfNeeded();
    await page.screenshot({path:testInfo.outputPath(`variables-${width}.png`)});
    await variables.getByRole('button', { name: 'Insert {{time}}: Current time', exact: true }).click();
    await expect(editor.getByLabel('Initial prompt (optional)', { exact: true })).toHaveValue('{{time}}');
});
const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=','base64');
const file=name=>({name,mimeType:'image/png',buffer:png});

for (const width of [375, 1280]) test(`attached images preview inside the prompt before and after saving at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    const state = await setup(page);
    const dataUrl = await page.evaluate(() => {
        const canvas = document.createElement('canvas'); canvas.width = 360; canvas.height = 200;
        const context = canvas.getContext('2d');
        context.fillStyle = '#14141d'; context.fillRect(0, 0, 360, 200);
        context.fillStyle = '#8b5cf6'; context.fillRect(30, 35, 125, 90);
        context.fillStyle = '#14b8a6'; context.fillRect(205, 75, 125, 90);
        context.fillStyle = '#f8fafc'; context.font = '20px sans-serif'; context.fillText('Layout reference', 30, 180);
        return canvas.toDataURL('image/png');
    });
    const buffer = Buffer.from(dataUrl.split(',')[1], 'base64');
    const reads = [];
    await page.route('**/api/v1/presets/*/attachments/*/content', route => {
        reads.push(new URL(route.request().url()).pathname);
        return route.fulfill({ contentType: 'image/png', body: buffer });
    });
    await page.locator('.vb-page-actions').getByRole('button', { name: 'Create agent', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    const prompt = editor.getByLabel('Initial prompt (optional)', { exact: true });
    await editor.getByLabel('Name', { exact: true }).fill('Image references');
    await prompt.fill('Please use @src/example.cs and this layout at {{time}}.');
    await editor.locator('[data-board-composer-file]').setInputFiles({ name: 'layout.png', mimeType: 'image/png', buffer });
    const previews = editor.getByRole('group', { name: 'Attached image previews' });
    const thumbnail = previews.getByRole('button', { name: 'Preview layout.png', exact: true });
    await expect(thumbnail).toBeVisible();
    await expect.poll(() => thumbnail.locator('img').evaluate(img => img.naturalWidth)).toBe(360);
    expect(reads).toEqual([]);
    const draft = await prompt.inputValue();
    await thumbnail.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`prompt-image-${width}.png`) });
    await thumbnail.click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview', exact: true });
    await expect.poll(() => viewer.locator('img').evaluate(img => img.naturalWidth)).toBe(360);
    await viewer.getByRole('button', { name: 'Close attachment', exact: true }).click();
    await expect(prompt).toHaveValue(draft);
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.initialPrompt).toContain('attachment:att_0');
    await expect.poll(() => thumbnail.locator('img').evaluate(img => img.naturalWidth)).toBe(360);
    expect(reads.every(path => path.includes('/presets/preset_0/attachments/att_0/content'))).toBe(true);
    expect(await editor.locator('.prompt-reference-mirror').textContent()).toBe(await prompt.inputValue() + '\u200b');
    await page.getByRole('button', { name: 'Close dialog', exact: true }).click();
    await page.getByRole('button', { name: 'Image references', exact: true }).click();
    await expect.poll(() => thumbnail.locator('img').evaluate(img => img.naturalWidth)).toBe(360);
    await expect(editor.locator('[data-board-composer-live]')).toHaveCount(0);
    expect(await previews.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
});

for (const width of [320, 375, 768, 1024, 1440]) test(`catalog follows the shared design at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 1000 });
    const state = await setup(page);
    const catalog = page.locator('[data-agent-catalog]');
    await expect(page.locator('.app-subnav')).toHaveCSS('height', '44px');
    await expect(page.locator('.brand-logo-sm')).toHaveCSS('height', '36px');
    await expect(page.locator('[data-agent-refresh]')).toHaveCSS('border-top-width', '1px');
    await expect(page.locator('[data-agent-refresh]')).toHaveCSS('border-top-color', 'rgb(100, 116, 139)');
    expect(await page.locator('.app-subnav').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await expect(catalog.locator('.launch-catalog-empty')).toHaveCount(3);
    await expect(page.locator('.vb-page-description')).toHaveText('Save AI setups and scripts to run on demand or from Board lanes.');
    await expect(catalog.getByRole('button', { name: 'Create quick launch', exact: true })).toBeVisible();
    expect(await page.locator('.ai-agents-view').evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await expect(catalog.locator('.launch-catalog-section').first()).toHaveCSS('border-radius', '12px');
    await expect(catalog.locator('.launch-catalog-empty').first()).toHaveCSS('border-top-style', 'dashed');
    await page.screenshot({ path: testInfo.outputPath(`catalog-empty-${width}.png`), fullPage: true });
    state.presets.push({ id: 'reviewer', name: 'Review changes', cli: 'codex', initialPrompt: 'Review', enabled: true, revision: 1 },
        { id: 'quick', name: 'Everyday Claude', cli: 'claude', initialPrompt: '', enabled: true, revision: 1 });
    state.actions.push({ id: 'test', name: 'Run project tests', kind: 'script', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await expect(catalog.locator('.launch-catalog-row')).toHaveCount(3);
    expect(await catalog.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`catalog-populated-${width}.png`), fullPage: true });
});

test('saving an agent hides Purpose and preserves its existing purpose', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'reviewer', name: 'Reviewer', cli: 'codex', initialPrompt: 'Review', purpose: 'code_review', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Reviewer', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    await editor.getByText('Additional settings', { exact: true }).click();
    await expect(editor.getByLabel('Purpose', { exact: true })).toHaveCount(0);
    await editor.getByLabel('Name', { exact: true }).fill('Reviewer updated');
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0].name).toBe('Reviewer updated');
    expect(state.presets[0].purpose).toBe('code_review');
});

test('staged attachments retry without recreating the preset or reuploading successful files',async({page})=>{
    const state=await setup(page);
    await page.locator('.vb-page-actions').getByRole('button',{name:'Create agent',exact:true}).click();
    const editor=page.locator('[data-launch-editor]');
    await editor.getByLabel('Name',{exact:true}).fill('Files');
    await editor.locator('[data-board-composer-file]').setInputFiles([file('first.png'),file('second.png')]);
    await expect(editor.locator('[data-board-remove-pending]')).toHaveCount(2);
    state.failUploadName='second.png';
    await editor.locator('[data-launch-save]').click();
    await expect(editor.locator('[data-launch-error]')).toContainText('Your draft is kept');
    expect(state.presets).toHaveLength(1);expect(state.files).toHaveLength(1);
    expect(state.presets[0].enabled).toBe(false);
    expect(state.presets[0].initialPrompt).toBeNull();
    await editor.locator('[data-launch-save]').click();
    await expect(editor.locator('[data-launch-save]')).toHaveText('Save');
    await expect.poll(()=>state.files.length).toBe(2);
    expect(state.presets).toHaveLength(1);
    expect(state.presets[0].initialPrompt).toContain('attachment:att_0');
    expect(state.presets[0].initialPrompt).toContain('attachment:att_1');
    expect(state.presets[0].initialPrompt).not.toContain('pending_');
    expect(state.presets[0].enabled).toBe(true);
    expect(state.requests.filter(r=>r.path.endsWith('/attachments')&&r.method==='POST'&&r.body.name==='first.png')).toHaveLength(1);
});

test('upload keeps its original insertion while the user types and moves the caret',async({page})=>{
    const state=await setup(page);
    state.presets.push({id:'preset_saved',name:'Saved',cli:'codex',initialPrompt:'Start END',enabled:true,revision:1});
    await page.locator('[data-agent-refresh]').click();await page.getByRole('button',{name:'Saved',exact:true}).click();
    const editor=page.locator('[data-launch-editor]'),prompt=editor.locator('[data-board-composer-input]');
    await prompt.focus();await prompt.evaluate(el=>el.setSelectionRange(6,6));
    let release;state.holdUpload=new Promise(resolve=>{release=resolve;});
    await editor.locator('[data-board-composer-file]').setInputFiles(file('image.png'));
    await expect(editor.locator('[data-board-composer-busy]')).toBeVisible();
    await prompt.press('End');await prompt.pressSequentially(' remains');release();
    await expect(prompt).toHaveValue('Start \n![image.png](attachment:att_0)\nEND remains');
    expect(state.requests.some(r=>r.path.includes('/board/')&&r.method!=='GET')).toBeFalsy();
});

test('Escape dismisses suggestions first and navigation retains unsaved drafts',async({page})=>{
    await setup(page);await page.locator('.vb-page-actions').getByRole('button',{name:'Create agent',exact:true}).click();
    const editor=page.locator('[data-launch-editor]'),prompt=editor.locator('[data-board-composer-input]');
    await prompt.fill('Keep {{git_');await expect(page.getByRole('option',{name:/git_branch/})).toBeVisible();
    await prompt.press('Escape');await expect(page.getByRole('option',{name:/git_branch/})).toHaveCount(0);await expect(editor).toBeVisible();
    await page.evaluate(()=>window.app.navigate('board'));
    const confirm=page.getByRole('alertdialog');
    await expect(confirm).toBeVisible();await confirm.getByRole('button',{name:'Cancel',exact:true}).click();
    await expect(prompt).toHaveValue('Keep {{git_');
    expect(await page.evaluate(()=>window.app.currentView)).toBe('ai-agents');
});

test('Create script saves a script and schedule without an action type or purpose field',async({page})=>{
    const state=await setup(page);await page.locator('.vb-page-actions').getByRole('button',{name:'Create script',exact:true}).click();
    const editor=page.locator('[data-launch-editor]');
    await editor.getByLabel('Name',{exact:true}).fill('Run tests');
    await editor.getByLabel('Script path (relative to execution project)',{exact:true}).fill('scripts/test.py');
    await expect(editor.getByLabel('Action type',{exact:true})).toHaveCount(0);
    await expect(editor.getByLabel('Purpose',{exact:true})).toHaveCount(0);
    await editor.getByRole('button',{name:'Create schedule or event',exact:true}).click();
    await editor.getByLabel('Frequency',{exact:true}).selectOption('2');
    await editor.getByLabel('Local time',{exact:true}).fill('08:30');
    await editor.locator('[data-launch-save]').click();
    await expect.poll(()=>state.bindings.length).toBe(1);
    expect(state.actions[0]).toMatchObject({kind:'script',check:null,script:{path:'scripts/test.py',runtime:0}});
    expect(state.bindings[0]).toMatchObject({definitionType:'action',trigger:{kind:0,scheduleKind:2,localTime:'08:30'}});
    expect(await editor.locator('[data-definition-launch-options]').count()).toBe(0);
    await expect(editor.getByRole('group', { name: 'Insert a prompt variable' })).toHaveCount(0);
});

test('history polling preserves expanded configuration, evidence and focused controls',async({page},testInfo)=>{
    const state=await setup(page);
    const preset={id:'preset_saved',name:'Reviewer',cli:'codex',initialPrompt:'Original',enabled:true,revision:2};state.presets.push(preset);
    state.runs.push({id:'run_saved',sequence:1,status:'succeeded',queuedUtc:'2026-11-07T10:00:00Z',verdict:'failed',evidenceId:'review_saved',snapshot:{context:{cardId:'card_saved'},preset:{...preset,revision:1,initialPrompt:'Earlier prompt'}}});
    await page.route('**/api/v1/board/cards/card_saved/reviews/review_saved',route=>route.fulfill({json:{result:'Needs changes',findings:'Review requires changes',scope:'working-tree',scopeDescription:'Uncommitted changes'}}));
    await page.locator('[data-agent-refresh]').click();await page.getByRole('button',{name:'Reviewer',exact:true}).click();
    const editor=page.locator('[data-launch-editor]');
    await editor.getByText('Run configuration · revision 1').click();await editor.getByRole('button',{name:'Review result'}).click();
    await expect(editor.locator('[data-run-result]')).toContainText('Review requires changes');
    await editor.locator('[data-run-evidence]').focus();
    await page.clock.install();await page.clock.fastForward(10000);
    await expect(editor.locator('details[data-run-id]')).toHaveAttribute('open','');
    await expect(editor.locator('[data-run-evidence]')).toBeFocused();await expect(editor.locator('[data-run-result]')).toContainText('Review requires changes');
    await expect(editor.locator('[data-run-list]')).toContainText('Process: succeeded · Result: failed');
    await expect(editor.locator('[data-board-composer-input]')).toHaveValue('Original');
    const summary=editor.locator('details[data-run-id] > summary');
    await summary.focus();state.runs[0].error='Report did not pass';
    await page.clock.fastForward(10000);await expect(summary).toBeFocused();
    await expect(editor.locator('[data-run-result]')).toContainText('Review requires changes');
    await page.screenshot({path:testInfo.outputPath('saved-agent-history.png')});
});

test('ordered lane selections preserve gates and lock editing during writes',async({page})=>{
    const state=await setup(page);
    state.presets.push({id:'p1',name:'Review',cli:'codex',initialPrompt:'Review',enabled:true,revision:1},{id:'p2',name:'Test',cli:'codex',initialPrompt:'Test',enabled:true,revision:1});
    let saved,release;const pending=new Promise(resolve=>{release=resolve;});
    await page.route('**/api/v1/board/columns/lane/launches',async route=>{
        if(route.request().method()==='GET')return route.fulfill({json:{revision:4,selections:[{definitionId:'p1',definitionType:'preset',gate:'review'},{definitionId:'p2',definitionType:'preset',gate:'result'}]}});
        saved=route.request().postDataJSON();await pending;await route.fulfill({json:{...saved,revision:5}});
    });
    await page.evaluate(async()=>{const {mountLaunchLane}=await import('/js/modules/launch-lane-editor.js');const host=document.createElement('div');document.querySelector('#app-content').replaceChildren(host);mountLaunchLane(window.app,host,'lane');});
    await page.getByRole('button',{name:'Move Test up',exact:true}).click();
    await page.getByRole('button',{name:'Save lane settings',exact:true}).click();
    await expect(page.locator('[data-lane-gate]').first()).toBeDisabled();
    expect(saved).toMatchObject({revision:4,selections:[{definitionId:'p2',gate:'result'},{definitionId:'p1',gate:'review'}]});release();
    await expect(page.locator('[data-lane-gate]').first()).toBeEnabled();
});

for (const [kind, name, scope] of [['vca', 'VCA check', 'working-tree'], ['code-quality', 'Code quality check', 'card']])
test(`lane picker adds ${kind} without running or saving the lane early`, async ({ page }) => {
    const state = await setup(page);
    let savedLane, release;
    await page.route('**/api/v1/board/columns/lane/launches', route => {
        if (route.request().method() === 'GET') return route.fulfill({ json: { revision: 2, selections: [] } });
        savedLane = route.request().postDataJSON();
        return route.fulfill({ json: { ...savedLane, revision: 3 } });
    });
    await page.route('**/api/v1/actions', async route => {
        await new Promise(resolve => { release = resolve; });
        const action = { ...route.request().postDataJSON(), id: 'vca', revision: 1 };
        state.actions.push(action);
        return route.fulfill({ json: action });
    });
    await page.evaluate(async () => {
        const { mountLaunchLane } = await import('/js/modules/launch-lane-editor.js');
        const host = document.createElement('div');
        document.querySelector('#app-content').replaceChildren(host);
        window.disposeLane = mountLaunchLane(window.app, host, 'lane');
    });
    const picker = page.locator('[data-lane-add-choice]');
    await expect(page.locator('[data-lane-add]')).toBeDisabled();
    await expect(page.locator('.ts-control')).toBeVisible();
    await picker.evaluate((node, kind) => node.tomselect.setValue(`new:${kind}`), kind);
    await page.locator('[data-lane-add]').click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await expect(page.locator('[data-lane-save]')).toBeDisabled();
    expect(await picker.evaluate(node => node.tomselect.isDisabled)).toBe(true);
    release();
    await expect(page.locator('[data-lane-selection-list] strong')).toHaveText(name);
    expect(state.actions[0]).toMatchObject({ kind, check: { scope }, expectedRevision: 0 });
    expect(savedLane).toBeUndefined();
    expect(state.runs).toHaveLength(0);
    await page.locator('[data-lane-save]').click();
    await expect.poll(() => savedLane).toMatchObject({ revision: 2, selections: [{ definitionId: 'vca', definitionType: 'action', gate: 'result' }] });
    await page.evaluate(() => window.disposeLane());
    await expect(page.locator('.launch-lane-dropdown')).toHaveCount(0);
});

test('terminal picker manages Agents and Quick Launches in the shared editor', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'quick', name: 'Fast terminal', cli: 'codex', initialPrompt: '', enabled: true, revision: 1 },
        { id: 'agent', name: 'Review changes', cli: 'codex', initialPrompt: 'Review', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await expect(page.getByRole('button', { name: 'Review changes', exact: true })).toBeVisible();
    await page.evaluate(() => window.app.llmPickerController.openCustomizationModal());
    await expect(page.locator('[data-llm-picker-key="preset:agent:codex"]')).toBeVisible();
    const quick = page.locator('[data-llm-picker-key="preset:quick:codex"]');
    await expect(quick).toBeVisible();
    await expect(quick.locator('[data-llm-picker-delete]')).toHaveCount(0);
    await quick.locator('[data-llm-picker-edit]').click();
    await expect(page.locator('[data-launch-editor]').getByLabel('Name', { exact: true })).toHaveValue('Fast terminal');
    await expect(page.locator('[data-launch-kind]')).toContainText('Quick Launch');
});

test('a refused replacement keeps the open editor references and polling alive', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'original', name: 'Original', cli: 'codex', initialPrompt: 'Review', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Original', exact: true }).click();
    const prompt = page.locator('[data-board-composer-input]');
    await prompt.fill('Draft');
    await page.evaluate(() => window.app.aiAgentsController.openEditor());
    await page.getByRole('button', { name: 'Cancel', exact: true }).last().click();
    await prompt.fill('Draft {{git_');
    await expect(page.getByRole('option', { name: /git_branch/ })).toBeVisible();
    await prompt.press('Escape');
    await page.locator('[data-run-refresh]').click();
    await expect(prompt).toHaveValue('Draft {{git_');
});

test('Action runs show saved check evidence without using a Board review route', async ({ page }) => {
    const state = await setup(page);
    state.actions.push({ id: 'check', name: 'Quality gate', kind: 'code-quality', enabled: true, revision: 1, check: { scope: 'working-tree' } });
    state.runs.push({ id: 'check_run', sequence: 1, status: 'failed', verdict: 'failed', evidenceId: 'check_result', queuedUtc: '2026-11-07T10:00:00Z', snapshot: { action: state.actions[0] } });
    await page.route('**/api/v1/runs/check_run/evidence', route => route.fulfill({ json: {
        tool: 'Code quality', status: 'Failed', scope: 'working-tree', summary: 'Two findings require attention', analyzedCount: 4, findingCount: 2,
        resultJson: JSON.stringify({ details: { findings: ['Cyclomatic complexity'] } })
    } }));
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Quality gate', exact: true }).click();
    await page.getByRole('button', { name: 'Check result' }).click();
    await expect(page.locator('[data-run-result]')).toContainText('Two findings require attention');
    await page.getByText('Full recorded output', { exact: true }).click();
    await expect(page.locator('[data-run-result]')).toContainText('Cyclomatic complexity');
});

test('preset attachment readers use their owner and restore the editable draft', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'files_owner', name: 'Attached', cli: 'codex', initialPrompt: 'Original', enabled: true, revision: 1 });
    state.files = [{ id: 'note', name: 'note.md', mimeType: 'text/markdown', size: 18 }];
    const reads = [];
    await page.route('**/api/v1/presets/files_owner/attachments/note/content', route => {
        reads.push(route.request().url());
        return route.fulfill({ contentType: 'text/markdown', body: '# Agent attachment\nSafe **content**.' });
    });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Attached', exact: true }).click();
    const prompt = page.locator('[data-board-composer-input]');
    await prompt.fill('Keep this draft');
    await page.locator('[data-board-view-attachment="note"]').click();
    await expect(page.getByRole('heading', { name: 'Agent attachment', exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Close attachment', exact: true }).click();
    await expect(prompt).toHaveValue('Keep this draft');
    expect(reads).toHaveLength(1);
    expect(state.requests.some(request => request.path.includes('/board/cards/') && request.path.includes('/attachments'))).toBe(false);
});

test('removing a staged file also removes its unsaved inline reference', async ({ page }) => {
    await setup(page);
    await page.locator('.vb-page-actions').getByRole('button', { name: 'Create agent', exact: true }).click();
    await page.locator('[data-board-composer-file]').setInputFiles(file('remove.png'));
    await expect(page.locator('[data-board-composer-input]')).toHaveValue(/attachment:pending_/);
    await expect(page.getByRole('button', { name: 'Preview remove.png', exact: true })).toBeVisible();
    await page.locator('[data-board-remove-pending]').click();
    await expect(page.locator('[data-board-composer-input]')).not.toHaveValue(/pending_/);
    await expect(page.locator('[data-board-remove-pending]')).toHaveCount(0);
    await expect(page.locator('.prompt-image-previews')).toBeHidden();
});

test('save locks the enhanced provider and a failed run preserves disabled controls', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'quick_lock', name: 'Quick lock', cli: 'codex', initialPrompt: '', enabled: true, revision: 1 });
    let release;
    await page.route('**/api/v1/presets/quick_lock', async route => {
        if (route.request().method() !== 'PUT') return route.fallback();
        await new Promise(resolve => { release = resolve; });
        Object.assign(state.presets[0], route.request().postDataJSON(), { revision: 2 });
        return route.fulfill({ json: { preset: state.presets[0], affectedBindings: [] } });
    });
    await page.route('**/api/v1/runs', route => route.request().method() === 'POST'
        ? route.fulfill({ status: 503, json: { error: 'Runner unavailable' } }) : route.fallback());
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Quick lock', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    const provider = editor.locator('[data-launch-provider]');
    await editor.getByRole('button', { name: 'Save and run', exact: true }).click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await expect(provider).toBeDisabled();
    expect(await provider.evaluate(node => node.tomselect.isDisabled)).toBe(true);
    release();
    await expect(page.getByText('Runner unavailable', { exact: true })).toBeVisible();
    await expect(editor.locator('[data-launch-save]')).toBeEnabled();
    await expect(provider).toBeEnabled();
    expect(await provider.evaluate(node => node.tomselect.isDisabled)).toBe(false);
    await expect(editor.locator('[data-binding-add]')).toBeDisabled();
});

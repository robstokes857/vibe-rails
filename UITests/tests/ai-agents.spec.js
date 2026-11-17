const { test, expect } = require('@playwright/test');

async function setup(page) {
    const state={presets:[],actions:[],bindings:[],runs:[],files:[],requests:[],hiddenKeys:[],laneHidden:[],conflict:false, projects:[
        {id:'C:\\FIXTURES\\PROJECT',name:'Same name',path:'C:\\fixtures\\project',isCurrent:true},
        {id:'C:\\FIXTURES\\OTHER',name:'Same name',path:'C:\\fixtures\\other',isCurrent:false}
    ]};
    const base=[['codex','Codex'],['claude','Claude'],['copilot','Copilot'],['opencode','OpenCode']];
    const scope = p => !p.projectPaths?.length || p.projectPaths.includes(state.projects[0].id);
    const items=()=>[...state.presets.map(p=>({id:p.id,name:p.name,description:p.description,definitionType:'preset',cli:p.cli,kind:p.initialPrompt?.trim()?'agent':'quick-launch',revision:p.revision,enabled:p.enabled,retired:!!p.retired,canLaunch:p.enabled && !p.retired && scope(p),canRunAutomatically:p.enabled && !p.retired && !!p.initialPrompt?.trim() && scope(p),inProject:scope(p),ineligibleReason:scope(p)?null:'This definition is not available in this project.',supportsAttachments:true})),...state.actions.map(p=>({...p,definitionType:'action',canLaunch:!p.retired && scope(p),canRunAutomatically:!p.retired && scope(p),inProject:scope(p)}))];
    await page.addInitScript(()=>sessionStorage.setItem('viberails_tab','fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*',()=>{});
    await page.route('**/api/v1/**',async route=>{
        const req=route.request(), path=new URL(req.url()).pathname, method=req.method(), body=req.postDataJSON();
        state.requests.push({path,method,body});
        const ok=json=>route.fulfill({json});
        if(path==='/api/v1/context')return ok({isInGit:true,rootPath:'C:\\fixtures\\project',launchDirectory:'C:\\fixtures\\project',isActiveRootBackend:true});
        if(path==='/api/v1/launch-projects')return ok(state.projects);
        if(path==='/api/v1/quick-actions/preferences') {
            if(state.failQuickActions)return route.fulfill({status:503,json:{error:'Could not save quick actions. Try again.'}});
            if(method==='PUT') {
                state.hiddenKeys=state.hiddenKeys.filter(key=>key!==body.key);
                if(body.hidden)state.hiddenKeys.push(body.key);
                return route.fulfill({status:204});
            }
            return ok({hiddenKeys:state.hiddenKeys});
        }
        // VIBE-337: the Board lane picker's Manage list, separate from Quick actions.
        if(path==='/api/v1/lane-picker/preferences') {
            if(method==='GET'&&state.failLanePickerLoad)return route.fulfill({status:503,json:{error:'Preferences unavailable.'}});
            if(method==='PUT'&&state.failLanePicker)return route.fulfill({status:503,json:{error:'Could not save this choice. Try again.'}});
            if(method==='PUT') {
                state.laneHidden=state.laneHidden.filter(key=>key!==body.key);
                if(body.hidden)state.laneHidden.push(body.key);
                return route.fulfill({status:204});
            }
            return ok({hiddenKeys:state.laneHidden});
        }
        if(path==='/api/v1/launch-catalog')return ok({items:items().filter(p=>new URL(req.url()).searchParams.get('includeAll')==='true'||p.inProject)});
        if(path==='/api/v1/llm-picker/preferences')return ok({items:[...base.map(([cli,label],order)=>({key:`base:${cli}`,kind:'base',group:'Base CLIs',label,cli,enabled:true,order})),...items().filter(p=>p.definitionType==='preset').map((p,order)=>({key:`preset:${p.id}:${p.cli}`,kind:p.kind,group:'AI Agents',label:p.name,cli:p.cli,enabled:p.enabled,inProject:p.inProject,order}))]});
        if(path.includes('/launch-defaults'))return ok({model:'',effort:'',speed:'',mode:'',yolo:false});
        if(path==='/api/v1/trigger-bindings'){if(method==='GET')return ok(state.bindings);const result={...body,id:'binding_'+state.bindings.length,revision:1};state.bindings.push(result);return ok(result);}
        if(path==='/api/v1/runs'){if(method==='GET')return ok({runs:state.runs,nextCursor:null});const p=state.presets.find(p=>p.id===body.definitionId);const run={id:'run_'+state.runs.length,sequence:state.runs.length+1,definitionId:body.definitionId,status:'queued',queuedUtc:new Date().toISOString(),snapshot:{preset:p},verdict:null};state.runs.push(run);return ok(run);}
        if(/^\/api\/v1\/runs\/[^/]+$/.test(path) && method==='GET') {
            const run = state.runs.find(run => run.id === path.split('/').pop());
            return ok({ ...run, ...(state.runUpdate || {}) });
        }
        const retirement = path.match(/^\/api\/v1\/(presets|actions)\/([^/]+)\/retire$/);
        if (retirement && method === 'POST') {
            const list = retirement[1] === 'presets' ? state.presets : state.actions;
            const item = list.find(item => item.id === retirement[2]);
            if (state.conflict || body.expectedRevision !== item.revision)
                return route.fulfill({status:409,json:{error:'Changed elsewhere. Refresh before deleting.'}});
            item.retired = true;
            item.enabled = false;
            item.revision++;
            return route.fulfill({status:204});
        }
        if(path.endsWith('/attachments')) {
            if(method==='GET')return ok(state.files);
            if(state.holdUpload)await state.holdUpload;
            if(state.failUploadName===body.name){state.failUploadName=null;return route.fulfill({status:500,json:{error:'Upload interrupted'}});}
            const file={id:'att_'+state.files.length,name:body.name,mimeType:'image/png',size:10};state.files.push(file);return ok(file);
        }
        const match=path.match(/^\/api\/v1\/(presets|actions)(?:\/([^/]+))?$/);
        if(match){const list=match[1]==='presets'?state.presets:state.actions;if(method==='GET')return ok(list.find(p=>p.id===match[2]));if(state.conflict)return route.fulfill({status:409,json:{error:'Changed elsewhere. Reopen the latest version before saving.',code:'revision_conflict',currentRevision:9}});const index=list.findIndex(p=>p.id===match[2]);const value={...body,id:match[2]||'preset_'+list.length,revision:(list[index]?.revision||0)+1};if(index<0)list.push(value);else list[index]=value;return ok(match[1]==='presets'?{preset:value,affectedBindings:[]}:value);}
        // The signed script library (VIBE-311): its own store, listed beside the launch catalog.
        if(path==='/api/v1/python-scripts'||path.startsWith('/api/v1/python-scripts/')){
            const library=()=>ok({pinConfigured:true,scriptsDirectory:'C:\\fixtures\\scripts',scripts:state.scripts||[]});
            if(state.failScripts)return route.fulfill({status:400,json:{error:'The script library could not be read.'}});
            if(path.endsWith('/approve')){
                if(body.pin!=='1234')return route.fulfill({status:400,json:{error:'That PIN is not correct.'}});
                state.scripts.find(script=>script.id===body.name).status='approved';
                return library();
            }
            if(method==='DELETE'){const id=new URL(req.url()).searchParams.get('name');state.scripts=state.scripts.filter(script=>script.id!==id);return library();}
            return library();
        }
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

for (const width of [320, 375, 768, 1024]) test(`optional launch message reaches the immutable run at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 850 });
    const state = await setup(page);
    state.runUpdate = { status: 'running' };
    state.presets.push({ id:'review', name:'Review changes', cli:'codex', initialPrompt:'Review {{user_message}}', enabled:true, revision:1 });
    state.presets.push({ id:'quick', name:'Quick terminal', cli:'codex', initialPrompt:'', enabled:true, revision:1 });
    const trigger = page.locator('[data-action="automation-launcher"]');
    const menu = page.getByRole('dialog', { name:'Quick actions', exact:true });
    await trigger.click();
    // VIBE-334: rows open compact; the message box sits behind an "Add message" link.
    const run = menu.getByRole('button', { name:'Run Review changes', exact:true });
    await expect(run).toBeFocused();
    const messages = menu.locator('[data-launch-message]');
    await expect(messages).toHaveCount(2);
    await expect(messages.first()).toBeHidden();
    await expect(messages.last()).toBeHidden();
    const toggle = menu.locator('[aria-controls="launch-message-0-panel"]');
    await expect(toggle).toHaveAccessibleName('Add message for Review changes');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    expect(await menu.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`user-message-collapsed-${width}.png`) });
    await toggle.click();
    await expect(menu).toBeVisible();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const input = menu.getByRole('textbox', { name:'User message for Review changes (optional)', exact:true });
    await expect(input).toBeFocused();
    await expect(input).not.toHaveAttribute('required');
    await expect(menu.locator('.launch-menu-message-help')).toHaveCount(0);
    const inputBox = await input.boundingBox();
    const runBox = await run.boundingBox();
    expect(runBox.y).toBeGreaterThanOrEqual(inputBox.y + inputBox.height);
    // The visible form and the keyboard agree: the next action after typing is Run.
    await input.press('Tab');
    await expect(run).toBeFocused();
    await run.press('Shift+Tab');
    await expect(input).toBeFocused();
    const message = 'Focus on <input> and {{worktree}} literally';
    await input.fill(message);
    await input.press('Shift+Enter');
    await input.pressSequentially('second line');
    await expect(input).toHaveValue(`${message}\nsecond line`);
    await expect(messages.last()).toBeHidden();
    await expect(messages.last()).toHaveValue('');
    expect(await menu.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath(`user-message-${width}.png`) });
    // Collapsing keeps the draft and says so; reopening returns to it.
    await toggle.click();
    await expect(input).toBeHidden();
    await expect(run).toBeVisible();
    await expect(toggle).toHaveAccessibleName('Edit message for Review changes');
    await toggle.press('Shift+Tab');
    await expect(run).toBeFocused();
    await toggle.click();
    await expect(input).toBeFocused();
    await input.press('Enter');
    await expect.poll(() => state.requests.filter(r => r.path === '/api/v1/runs' && r.method === 'POST').length).toBe(1);
    const first = state.requests.find(r => r.path === '/api/v1/runs' && r.method === 'POST').body;
    expect(first.userMessage).toBe(`${message}\nsecond line`);
    expect(first.initialPrompt).toBe('Review {{user_message}}');
    expect(first.expectedRevision).toBe(1);
    await expect(page.locator('[data-prompt-template-modal]')).toHaveCount(0);
    await trigger.click();
    await expect(toggle).toHaveAccessibleName('Add message for Review changes');
    await expect(messages.first()).toBeHidden();
    await expect(messages.first()).toHaveValue('');
    await menu.getByRole('button', { name:'Run Quick terminal', exact:true }).click();
    await expect.poll(() => state.requests.filter(r => r.path === '/api/v1/runs' && r.method === 'POST').length).toBe(2);
    expect(state.requests.filter(r => r.path === '/api/v1/runs' && r.method === 'POST')[1].body.userMessage).toBe('');
    expect(state.presets[0].initialPrompt).toBe('Review {{user_message}}');
});

for (const suffix of ['', ' {{user_message}}']) test(`optional message survives the required variable dialog (${suffix || 'appended'}) and cancellation queues nothing`, async ({ page }) => {
    const state = await setup(page);
    state.runUpdate = { status: 'running' };
    state.presets.push({ id:'review', name:'Review', cli:'codex', initialPrompt:'Review {{ticket}}' + suffix, enabled:true, revision:3 });
    const trigger = page.locator('[data-action="automation-launcher"]');
    await trigger.click();
    await page.getByRole('button', { name:'Add message for Review', exact:true }).click();
    await page.locator('[data-launch-message]').fill('Focus on <safety>');
    await page.getByRole('button', { name:'Run Review', exact:true }).click();
    const form = page.locator('[data-prompt-template-modal]');
    await expect(form.locator('textarea')).toHaveCount(1);
    await expect(form.locator('[data-prompt-template-preview]')).toContainText('Focus on <safety>');
    await form.getByRole('button', { name:'Cancel', exact:true }).click();
    expect(state.runs).toHaveLength(0);
    await trigger.click();
    await page.getByRole('button', { name:'Add message for Review', exact:true }).click();
    await page.locator('[data-launch-message]').fill('Focus on {{worktree}}');
    await page.getByRole('button', { name:'Run Review', exact:true }).click();
    await form.getByLabel('Ticket', { exact:true }).fill('VB-123');
    await form.getByRole('button', { name:'Start Terminal', exact:true }).click();
    await expect.poll(() => state.runs.length).toBe(1);
    const body = state.requests.find(r => r.path === '/api/v1/runs' && r.method === 'POST').body;
    expect(body.initialPrompt).toBe('Review VB-123' + suffix);
    expect(body.userMessage).toBe('Focus on {{worktree}}');
});

test('a user message can supply the entire prompt when optional template values are blank', async ({ page }) => {
    const state = await setup(page);
    state.runUpdate = { status: 'running' };
    state.presets.push({ id:'optional', name:'Optional values', cli:'codex', initialPrompt:'{{context default=""}}', enabled:true, revision:1 });
    await page.locator('[data-action="automation-launcher"]').click();
    await page.getByRole('button', { name:'Add message for Optional values', exact:true }).click();
    await page.locator('[data-launch-message]').fill('Review this change');
    await page.getByRole('button', { name:'Run Optional values', exact:true }).click();
    const form = page.locator('[data-prompt-template-modal]');
    await expect(form.locator('[data-prompt-template-preview]')).toContainText('Review this change');
    await form.getByRole('button', { name:'Start Terminal', exact:true }).click();
    await expect.poll(() => state.runs.length).toBe(1);
    const body = state.requests.find(r => r.path === '/api/v1/runs' && r.method === 'POST').body;
    expect(body.initialPrompt).toBe('');
    expect(body.userMessage).toBe('Review this change');
});

for (const width of [375, 1280]) test(`Manage quick actions persists shared visibility at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 850 });
    const state = await setup(page);
    state.presets.push({ id:'reviewer', name:'Review <changes>', cli:'codex', initialPrompt:'Review', enabled:true, revision:1 });
    state.presets.push({ id:'quick', name:'Fast terminal', cli:'claude', enabled:true, revision:1 });
    state.actions.push({ id:'check', name:'Check', kind:'vca', enabled:true, revision:1 });
    state.scripts = [{ id:'reg_run', name:'run.ps1', displayName:'Start app', status:'approved' }];
    await page.evaluate(() => { window.quickActionRefreshes = 0; window.__viberails_launcherChanged__ = () => window.quickActionRefreshes++; });
    const trigger = page.locator('[data-action="automation-launcher"]');
    await trigger.click();
    const menu = page.getByRole('dialog', { name:'Quick actions', exact:true });
    await menu.getByRole('button', { name:'Manage quick actions…', exact:true }).click();
    const dialog = page.getByRole('dialog', { name:'Manage quick actions', exact:true });
    await expect(dialog.getByRole('switch')).toHaveCount(4);
    await dialog.getByRole('switch', { name:'Show Review <changes>', exact:true }).uncheck();
    await expect(dialog.getByRole('status')).toContainText('Saved.');
    expect(state.hiddenKeys).toEqual(['preset:reviewer']);
    await page.screenshot({ path:testInfo.outputPath(`quick-actions-${width}.png`) });
    expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
    await dialog.getByRole('button', { name:'Done', exact:true }).click();
    await expect(trigger).toBeFocused();
    await trigger.click();
    await expect(menu.locator('.launch-menu-name')).toHaveText(['Fast terminal', 'Check', 'Start app']);
    // The same definition still exists and remains enabled; its changed label keeps its preference.
    expect(state.presets[0].enabled).toBe(true);
    state.presets[0].name = 'Renamed reviewer';
    await page.reload();
    await trigger.click();
    await expect(menu.locator('.launch-menu-name')).toHaveText(['Fast terminal', 'Check', 'Start app']);
    await menu.getByRole('button', { name:'Manage quick actions…' }).click();
    await expect(dialog.getByRole('switch', { name:'Show Renamed reviewer' })).not.toBeChecked();
    for (const toggle of await dialog.getByRole('switch').all()) {
        if (await toggle.isChecked()) { await toggle.uncheck(); await expect(toggle).toBeEnabled(); }
    }
    await dialog.press('Escape');
    await expect(trigger).toBeFocused();
    await trigger.click();
    await expect(menu).toContainText('No quick actions shown.');
    await menu.getByRole('button', { name:'Manage quick actions…' }).click();
    await dialog.getByRole('switch', { name:'Show Renamed reviewer' }).check();
    await expect(dialog.getByRole('status')).toContainText('Saved.');
    await dialog.getByRole('button', { name:'Done' }).click();
    await trigger.click();
    await expect(menu.locator('.launch-menu-name')).toHaveText(['Renamed reviewer']);
});

test('quick action settings restore failed saves and open from the VS Code command', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id:'reviewer', name:'Reviewer', cli:'codex', initialPrompt:'Review', enabled:true, revision:1 });
    await page.evaluate(async () => {
        window.__viberails_VSCODE__ = true;
        window.quickActionRefreshes = 0;
        window.__viberails_launcherChanged__ = () => window.quickActionRefreshes++;
        const { setupVSCodeLauncherBridge } = await import('/js/modules/vscode-launcher-bridge.js');
        let receive;
        const host = { __viberails_VSCODE__:true, parent:{}, addEventListener: (type, handler) => { receive = handler; } };
        setupVSCodeLauncherBridge(window.app, host);
        receive({ source:host.parent, data:{ command:'openLauncherCustomize' } });
    });
    const dialog = page.getByRole('dialog', { name:'Manage quick actions', exact:true });
    const toggle = dialog.getByRole('switch', { name:'Show Reviewer' });
    await expect(toggle).toBeChecked();
    state.failQuickActions = true;
    await toggle.uncheck();
    await expect(dialog.getByRole('alert')).toContainText('Try again');
    await expect(toggle).toBeChecked();
    expect(state.hiddenKeys).toEqual([]);
    state.failQuickActions = false;
    await toggle.uncheck();
    await expect(dialog.getByRole('status')).toContainText('Saved.');
    await expect.poll(() => page.evaluate(() => window.quickActionRefreshes)).toBe(1);
    expect(state.requests.filter(r => r.method === 'PUT').map(r => r.path)).toEqual([
        '/api/v1/quick-actions/preferences', '/api/v1/quick-actions/preferences'
    ]);
});

for (const width of [375, 1024]) test(`project scope is searchable, retained and manageable at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const state = await setup(page);
    await page.getByRole('button', { name: 'Create agent', exact: true }).first().click();
    const editor = page.locator('[data-launch-editor]');
    await editor.locator('[data-launch-field="name"]').fill('Scoped agent');
    await editor.locator('[data-board-composer-input]').fill('Do work');
    const control = editor.locator('#launch-projects + .ts-wrapper .ts-control');
    await control.click();
    const search = page.getByRole('textbox', { name: 'Search projects or paths…' });
    await search.fill('other');
    const option = page.locator('.ts-dropdown:visible [data-selectable]').filter({ hasText: 'C:\\fixtures\\other' });
    await expect(option).toContainText('Same name');
    await option.click();
    await search.press('Escape');
    await page.screenshot({ path: testInfo.outputPath(`projects-${width}.png`) });
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.projectPaths).toEqual(['C:\\FIXTURES\\OTHER']);
    await editor.locator('[data-launch-cancel]').click();
    await expect(page.locator('[data-agent-edit]')).toHaveCount(0);
    await page.getByRole('button', { name: 'Manage all projects', exact: true }).click();
    await expect(page.locator('[data-agent-run]')).toBeDisabled();
    await page.getByRole('button', { name: 'Scoped agent', exact: true }).click();
    await expect(editor.locator('#launch-projects + .ts-wrapper')).toContainText('C:\\fixtures\\other');
    await control.click();
    await control.press('ArrowDown');
    await search.fill('project');
    await page.locator('.ts-dropdown:visible [data-selectable]').filter({ hasText: 'C:\\fixtures\\project' }).click();
    await search.press('Escape');
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.projectPaths?.length).toBe(2);
    await editor.locator('[data-launch-cancel]').click();
    await page.getByRole('button', { name: 'Scoped agent', exact: true }).click();
    await expect(editor.locator('#launch-projects option:checked')).toHaveCount(2);
    // Remove each selected project using the shared Tom Select multi-select control.
    const remove = editor.locator('#launch-projects + .ts-wrapper .remove');
    await remove.first().click();
    await remove.first().click();
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.projectPaths).toEqual([]);
});

test('missing projects and stale saves keep the selected scope', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id:'missing', name:'Missing project', cli:'codex', initialPrompt:'Work', enabled:true, revision:1, projectPaths:['C:\\UNAVAILABLE'] });
    await page.getByRole('button', { name:'Manage all projects', exact:true }).click();
    await page.getByRole('button', { name:'Missing project', exact:true }).click();
    const editor = page.locator('[data-launch-editor]');
    await expect(editor).toContainText('Previously selected project');
    state.conflict = true;
    await editor.locator('[data-launch-field="name"]').fill('Still scoped');
    await editor.locator('[data-launch-save]').click();
    await expect(editor.locator('[data-launch-error]')).toContainText('Your draft is kept');
    await expect(editor.locator('#launch-projects')).toHaveValues(['C:\\UNAVAILABLE']);
    state.conflict = false;
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.name).toBe('Still scoped');
    expect(state.presets[0].projectPaths).toEqual(['C:\\UNAVAILABLE']);
});

test('scope refresh excludes launch choices while preserving saved Board selections and management', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({id:'local',name:'Local agent',cli:'codex',initialPrompt:'Work',enabled:true,revision:1},
        {id:'elsewhere',name:'Elsewhere agent',cli:'codex',initialPrompt:'Work',enabled:true,revision:1,projectPaths:['C:\\FIXTURES\\OTHER']});
    await page.locator('[data-agent-refresh]').click();
    await expect(page.getByRole('button', { name: 'Local agent', exact: true })).toBeVisible();
    const visibleKeys = await page.evaluate(() => ({
        terminal: window.app.llmPickerController.getItems('terminal').map(x => x.key),
        board: window.app.llmPickerController.getItems('assignee', {includeHidden:true}).map(x => x.key)
    }));
    expect(visibleKeys.terminal).toContain('preset:local:codex');
    expect(visibleKeys.terminal).not.toContain('preset:elsewhere:codex');
    expect(visibleKeys.board).not.toContain('preset:elsewhere:codex');
    await page.getByRole('button', {name:'Launch an agent or automation',exact:true}).click();
    await expect(page.locator('[data-launch-id="local"]')).toBeVisible();
    await expect(page.locator('[data-launch-id="elsewhere"]')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await page.getByRole('button', {name:'Manage all projects',exact:true}).click();
    await expect(page.getByRole('button', {name:'Elsewhere agent',exact:true})).toBeVisible();
    await page.evaluate(async () => {
        const select = document.createElement('select');
        select.id = 'scope-board-fixture';
        document.querySelector('#app-content').append(select);
        window.app.llmPickerController.mount(select, {context:'assignee',includeHidden:true,selectedValue:'preset:elsewhere:codex'});
    });
    await expect(page.locator('#scope-board-fixture option:checked')).toContainText('unavailable in this project');
    // Simulate a saved scope change, then exercise the same refresh as the editor.
    state.presets[1].projectPaths = [];
    await page.locator('[data-agent-refresh]').click();
    await expect.poll(() => page.evaluate(() => window.app.llmPickerController.getItems('assignee',{includeHidden:true}).map(x=>x.key)))
        .toContain('preset:elsewhere:codex');
});

for (const surface of ['catalog', 'dropdown']) {
    for (const type of ['preset', 'action']) {
        test(`${surface} Run starts a native ${type} without opening the editor`, async ({ page }) => {
            const state = await setup(page);
            state.presets.push({ id: 'agent', name: 'Test agent', cli: 'codex', initialPrompt: 'Work', enabled: true, revision: 1 });
            state.actions.push({ id: 'script', name: 'Test script', kind: 'script' });
            state.runUpdate = { status: 'running', sessionId: 'native-session', tabId: null };
            await page.locator('[data-agent-refresh]').click();
            const id = type === 'preset' ? 'agent' : 'script';
            if (surface === 'dropdown') {
                await page.getByRole('button', { name: 'Launch an agent or automation', exact: true }).click();
                await page.locator(`[data-launch-id="${id}"]`).click();
            } else {
                await page.locator(`[data-agent-run="${id}"]`).click();
            }
            await expect(page.getByText('Started in a native terminal.', { exact: true })).toBeVisible();
            expect(state.requests.filter(r => r.path === '/api/v1/runs' && r.method === 'POST').map(r => r.body))
                .toEqual([expect.objectContaining({ definitionId: id, definitionType: type, nativeTerminal: true })]);
            await expect(page.locator('[data-launch-editor]')).toHaveCount(0);
            await expect(page.getByRole('heading', { name: 'AI Agents', exact: true })).toBeVisible();
            expect(state.requests.filter(r => r.method === 'POST' && /terminal-tabs/.test(r.path))).toEqual([]);
        });
    }
}

test('native startup errors are shown and repeated clicks do not enqueue duplicates', async ({ page }) => {
    const state = await setup(page);
    state.actions.push({ id: 'script', name: 'Test script', kind: 'script' });
    await page.locator('[data-agent-refresh]').click();
    await page.locator('[data-agent-run="script"]').click();
    await expect(page.getByText('Starting in a native terminal…', { exact: true })).toBeVisible();
    await page.locator('[data-agent-run="script"]').click();
    state.runUpdate = { status: 'failed', error: 'Native terminal executable unavailable' };
    await expect(page.getByText('Native terminal executable unavailable', { exact: true })).toBeVisible();
    expect(state.runs).toHaveLength(1);
    await expect(page.getByText('Started in a native terminal.', { exact: true })).toHaveCount(0);
    await expect(page.locator('[data-launch-editor]')).toHaveCount(0);
});

test('legacy terminal focus requests still launch saved agents natively', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'agent', name: 'Test agent', cli: 'codex', initialPrompt: 'Work', enabled: true, revision: 1 });
    state.runUpdate = { status: 'running', sessionId: 'session', tabId: null };
    await page.evaluate(async () => {
        window.adoptedTabs = [];
        window.app.terminalController.adoptLaunchedTab = async id => { window.adoptedTabs.push(id); return true; };
        await window.app.aiAgentsController.launch('agent', 'preset', { focusTerminal: true, nativeTerminal: false });
    });
    expect(state.requests.find(r => r.path === '/api/v1/runs' && r.method === 'POST').body.nativeTerminal).toBe(true);
    expect(await page.evaluate(() => window.adoptedTabs)).toEqual([]);
    await expect(page.getByText('Started in a native terminal.', { exact: true })).toBeVisible();
    await expect(page.locator('[data-launch-editor]')).toHaveCount(0);
});

test('quick actions use explicit Run buttons and preserve launch and keyboard behavior', async ({ page }, testInfo) => {
    const state = await setup(page);
    state.presets.push(
        { id: 'release', name: 'Release Vibe Rails Client App', cli: 'codex', initialPrompt: 'Release', enabled: true },
        { id: 'hidden', name: 'Disabled agent', cli: 'codex', initialPrompt: 'Hidden', enabled: false }
    );
    state.actions.push({ id: 'script', name: 'Build <client> & test', kind: 'script' });
    await page.locator('[data-agent-refresh]').click();
    await expect(page.getByRole('button', { name: 'Release Vibe Rails Client App', exact: true })).toBeVisible();
    await page.evaluate(() => {
        window.quickActionLaunches = [];
        window.app.aiAgentsController.launch = async (...args) => window.quickActionLaunches.push(args);
    });
    const trigger = page.getByRole('button', { name: 'Launch an agent or automation', exact: true });
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await trigger.click();
    await expect(trigger).toHaveAttribute('aria-haspopup', 'dialog');
    await expect(panel.locator('.launch-menu-subtitle')).toHaveText(['Script']);
    for (const id of ['release', 'script']) {
        const catalogRow = page.locator('.launch-catalog-row').filter({ has: page.locator(`[data-agent-edit="${id}"]`) });
        const menuRow = panel.locator('.launch-menu-row').filter({ has: page.locator(`[data-launch-id="${id}"]`) });
        await expect(menuRow.locator('.launch-menu-name')).toHaveText(await catalogRow.locator('.launch-catalog-copy [data-agent-edit]').innerText());
        if (id === 'release') await expect(menuRow.locator('.launch-menu-subtitle')).toHaveCount(0);
        else await expect(menuRow.locator('.launch-menu-subtitle')).toHaveText(await catalogRow.locator('.launch-catalog-copy p').innerText());
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
    const addMessage = panel.getByRole('button', { name: 'Add message for Release Vibe Rails Client App', exact: true });
    await expect(runAgent).toBeFocused();
    await expect(panel.locator('[data-launch-message]')).toBeHidden();
    await expect(addMessage).toHaveAttribute('aria-expanded', 'false');
    // Actions and library scripts keep their existing controls: no message link.
    await expect(panel.locator('[data-launch-message-toggle]')).toHaveCount(1);
    await page.screenshot({ path: testInfo.outputPath('quick-actions-desktop.png') });
    await panel.getByText('Release Vibe Rails Client App', { exact: true }).click();
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([]);
    await runAgent.click();
    await expect(panel).toHaveCount(0);
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([['release', 'preset', { userMessage: '' }]]);
    await trigger.click();
    await expect(runAgent).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(addMessage).toBeFocused();
    await page.keyboard.press('Tab');
    await expect(panel.getByRole('button', { name: 'Run Build <client> & test', exact: true })).toBeFocused();
    await page.keyboard.press('Enter');
    expect(await page.evaluate(() => window.quickActionLaunches)).toEqual([['release', 'preset', { userMessage: '' }], ['script', 'action', { userMessage: null }]]);
    await trigger.click();
    await expect(panel).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(trigger).toBeFocused();
    state.presets = [];
    state.actions = [];
    await trigger.click();
    await expect(panel.getByRole('status')).toContainText('No quick actions shown');
    await expect(panel.getByRole('button', { name: 'Manage quick actions…', exact: true })).toBeFocused();
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
    await expect(page.locator('.launch-catalog-copy [data-agent-edit="claude"]')).toBeVisible();
    await page.getByRole('button', { name: 'Launch an agent or automation', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await expect(panel.locator('.launch-menu-subtitle')).toHaveText(['Review <changes> & report', 'custom', 'Compile <client>']);
    await expect(panel.locator('changes, client')).toHaveCount(0);
    for (const id of ['claude', 'quick', 'unknown', 'script']) {
        const catalogRow = page.locator('.launch-catalog-row').filter({ has: page.locator(`[data-agent-edit="${id}"]`) });
        const menuRow = panel.locator('.launch-menu-row').filter({ has: page.locator(`[data-launch-id="${id}"]`) });
        if (id === 'quick') await expect(menuRow.locator('.launch-menu-subtitle')).toHaveCount(0);
        else await expect(menuRow.locator('.launch-menu-subtitle')).toHaveText(await catalogRow.locator('.launch-catalog-copy p').innerText());
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
        await panel.locator('[data-launch-message-toggle]').click();
        await expect(panel.locator('[data-launch-message]')).toBeFocused();
        const inputBox = await panel.locator('[data-launch-message]').boundingBox();
        const runBox = await panel.locator('.launch-menu-run').first().boundingBox();
        const iconBox = await panel.locator('.launch-menu-icon').first().boundingBox();
        expect(runBox.y).toBeGreaterThanOrEqual(inputBox.y + inputBox.height);
        expect(Math.abs(inputBox.x - iconBox.x)).toBeLessThanOrEqual(1);
        expect(Math.abs(inputBox.x + inputBox.width - runBox.x - runBox.width)).toBeLessThanOrEqual(1);
        for (const element of [panel, ...await panel.locator('.launch-menu-name, .launch-menu-run, .launch-menu-message-toggle, [data-launch-message]').all()]) {
            const box = await element.boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width).toBeLessThanOrEqual(width);
        }
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        if (width <= 375) {
            const button = await panel.locator('.launch-menu-run').first().boundingBox();
            expect(button.height).toBeGreaterThanOrEqual(44);
            const toggle = await panel.locator('.launch-menu-message-toggle').boundingBox();
            expect(toggle.height).toBeGreaterThanOrEqual(44);
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
    const tokens = ['{{user_message}}', '{{name}}', '{{name default="…"}}', '{{datetime}}', '{{date}}', '{{time}}',
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

for (const width of [375, 1280]) test(`attached images are listed in Attachments, not under the prompt, at ${width}px`, async ({ page }, testInfo) => {
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
    const pending = editor.locator('[data-board-attachments] .board-attachment-row', { hasText: 'layout.png' });
    await pending.scrollIntoViewIfNeeded();
    await expect.poll(() => pending.locator('img.board-attachment-thumb').evaluate(img => img.naturalWidth)).toBe(360);
    // The prompt stays one native field; the image is listed only under Attachments.
    const beneath = editor.locator('[data-board-composer] :is(img, .prompt-image-previews, [data-board-composer-live])');
    await expect(beneath).toHaveCount(0);
    expect(reads).toEqual([]);
    await page.screenshot({ path: testInfo.outputPath(`prompt-image-${width}.png`) });
    await editor.locator('[data-launch-save]').click();
    await expect.poll(() => state.presets[0]?.initialPrompt).toContain('attachment:att_0');
    const saved = await prompt.inputValue();
    await editor.locator('[data-board-view-attachment="att_0"]').click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview', exact: true });
    await expect.poll(() => viewer.locator('img').evaluate(img => img.naturalWidth)).toBe(360);
    await viewer.getByRole('button', { name: 'Close attachment', exact: true }).click();
    await expect(prompt).toHaveValue(saved);
    expect(reads.length).toBeGreaterThan(0);
    expect(reads.every(path => path.includes('/presets/preset_0/attachments/att_0/content'))).toBe(true);
    expect(await editor.locator('.prompt-reference-mirror').textContent()).toBe(await prompt.inputValue() + '\u200b');
    await page.getByRole('button', { name: 'Close dialog', exact: true }).click();
    await page.getByRole('button', { name: 'Image references', exact: true }).click();
    await expect(prompt).toHaveValue(saved);
    await expect(beneath).toHaveCount(0);
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
    await expect(catalog.getByRole('button', { name: 'Edit Review changes', exact: true })).toBeVisible();
    await expect(catalog.getByRole('button', { name: 'Delete Review changes', exact: true })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath(`catalog-populated-${width}.png`), fullPage: true });
});

for (const type of ['presets', 'actions']) test(`catalog Delete confirms and retires ${type} with the displayed revision`, async ({ page }) => {
    const state = await setup(page);
    state[type].push({ id: 'delete-me', name: 'Delete candidate', cli: 'codex', initialPrompt: 'Review', kind: 'script', enabled: true, revision: 3 });
    state.presets.push({ id: 'keep-me', name: 'Keep this agent', cli: 'codex', initialPrompt: 'Work', enabled: true, revision: 1 });
    state.files.push({ id: 'attachment', name: 'retained.png', mimeType: 'image/png', size: 10 });
    await page.locator('[data-agent-refresh]').click();
    const remove = page.getByRole('button', { name: 'Delete Delete candidate', exact: true });
    await expect(remove).toHaveClass(/btn-danger/);
    await expect(remove.locator('.fa-trash-can')).toBeVisible();
    await remove.click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('Delete "Delete candidate"?');
    await expect(confirmation).toContainText('history and attachments are retained');
    await expect(confirmation.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(remove).toBeFocused();
    expect(state.requests.filter(r => r.path.endsWith('/retire'))).toHaveLength(0);
    state.conflict = true;
    await remove.click();
    await confirmation.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(page.getByText('Changed elsewhere. Refresh before deleting.', { exact: true })).toBeVisible();
    await expect(remove).toBeEnabled();
    expect(state[type][0].retired).toBeUndefined();
    state.conflict = false;
    await remove.click();
    await confirmation.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(remove).toHaveCount(0);
    expect(state.requests.filter(r => r.path.endsWith('/retire')).map(r => ({ path:r.path, method:r.method, body:r.body })))
        .toEqual(Array(2).fill({path:`/api/v1/${type}/delete-me/retire`,method:'POST',body:{expectedRevision:3}}));
    expect(state[type][0]).toMatchObject({ id:'delete-me', retired:true, revision:4 });
    expect(state.files).toHaveLength(1);
    await expect(page.getByRole('button', { name: 'Keep this agent', exact: true })).toBeVisible();
    await page.locator('[data-agent-refresh]').click();
    await expect(page.getByRole('button', { name: 'Delete candidate', exact: true })).toHaveCount(0);
});

test('saving an agent hides Purpose and preserves its existing purpose', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'reviewer', name: 'Reviewer', cli: 'codex', initialPrompt: 'Review', purpose: 'code_review', enabled: true, revision: 1 });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Edit Reviewer', exact: true }).click();
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

test('ordered lane selections retain compatibility data and lock editing during writes',async({page})=>{
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
    await expect(page.locator('[data-lane-gate]')).toHaveCount(0);
    await expect(page.getByRole('button',{name:'Move Review up',exact:true})).toBeDisabled();
    expect(saved).toMatchObject({revision:4,selections:[{definitionId:'p2',gate:'result'},{definitionId:'p1',gate:'review'}]});release();
    await expect(page.getByRole('button',{name:'Move Review up',exact:true})).toBeEnabled();
});

// VIBE-271: the scope is chosen in the lane, defaulting to the card's linked commits for both kinds.
for (const [kind, name, scope, choose] of [['vca', 'Rules check · Working tree', 'working-tree', true], ['code-quality', 'Code quality scan · Linked commits', 'card', false]])
test(`lane picker adds ${kind} in ${scope} scope without running or saving the lane early`, async ({ page }) => {
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
    const scopeChoice = page.getByRole('combobox', { name: 'Check scope', exact: true });
    await expect(scopeChoice).toBeHidden();
    await picker.evaluate((node, kind) => node.tomselect.setValue(`new:${kind}`), kind);
    await expect(page.locator('.ts-control img')).toHaveAttribute('src', 'assets/img/logo.png');
    await expect(scopeChoice).toHaveValue('card');
    await expect(scopeChoice).toHaveAccessibleDescription(/linked commits/);
    expect(await scopeChoice.locator('option').evaluateAll(options => options.map(option => option.value)))
        .toEqual(['card', 'unpushed', 'working-tree', 'repository']);
    if (choose) {
        await scopeChoice.selectOption(scope);
        await expect(scopeChoice).toHaveAccessibleDescription(/uncommitted and untracked files/);
    }
    await page.locator('[data-lane-add]').click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await expect(page.locator('[data-lane-save]')).toBeDisabled();
    expect(await picker.evaluate(node => node.tomselect.isDisabled)).toBe(true);
    release();
    await expect(page.locator('[data-lane-selection-list] strong')).toHaveText(name);
    expect(state.actions[0]).toMatchObject({ name, kind, check: { scope }, expectedRevision: 0 });
    await expect(scopeChoice).toBeHidden();
    expect(savedLane).toBeUndefined();
    expect(state.runs).toHaveLength(0);
    await page.locator('[data-lane-save]').click();
    await expect.poll(() => savedLane).toMatchObject({ revision: 2, selections: [{ definitionId: 'vca', definitionType: 'action' }] });
    expect(savedLane.selections[0]).not.toHaveProperty('gate');
    await page.evaluate(() => window.disposeLane());
    await expect(page.locator('.launch-lane-dropdown')).toHaveCount(0);
});

test('lane picker labels existing checks by scope and reuses an identical one instead of creating a copy', async ({ page }) => {
    const state = await setup(page);
    // What an earlier quick-add saved: the bare name, working-tree scope.
    state.actions.push({ id: 'old_vca', name: 'VCA check', kind: 'vca', enabled: true, revision: 1, check: { scope: 'working-tree' } },
        { id: 'card_vca', name: 'Card rules', kind: 'vca', enabled: true, revision: 1, check: { scope: 'card' } });
    let posts = 0;
    await page.route('**/api/v1/board/columns/lane/launches', route => route.fulfill({ json: { revision: 1, selections: [] } }));
    await page.route('**/api/v1/actions', route => { posts++; return route.fulfill({ status: 500, json: { error: 'unexpected' } }); });
    await page.evaluate(async () => {
        const { mountLaunchLane } = await import('/js/modules/launch-lane-editor.js');
        const host = document.createElement('div');
        document.querySelector('#app-content').replaceChildren(host);
        mountLaunchLane(window.app, host, 'lane');
    });
    const picker = page.locator('[data-lane-add-choice]');
    await page.locator('.ts-control').click();
    // Saved names are kept; the kind reads as a Rules check (VIBE-337).
    await expect(page.locator('.launch-lane-dropdown .option:not([data-value=""]) .launch-lane-choice-label')).toHaveText(['VCA check · Working tree', 'Card rules · Rules check · Linked commits', 'Rules check', 'Code quality scan']);
    await page.keyboard.press('Escape');
    for (let attempt = 0; attempt < 2; attempt++) {
        await picker.evaluate(node => node.tomselect.setValue('new:vca'));
        await expect(page.getByRole('combobox', { name: 'Check scope', exact: true })).toHaveValue('card');
        await page.locator('[data-lane-add]').click();
    }
    await expect(page.locator('[data-lane-selection-list] strong')).toHaveText(['Card rules']);
    await expect(page.locator('[data-lane-error]')).toHaveText('This lane already runs “Card rules”.');
    expect(posts).toBe(0);
});

// VIBE-335: a new Code quality scan chooses here whether findings fail the step below a grade.
test('lane picker sets a new Code quality scan’s minimum grade and reuses a scan only at the same grade', async ({ page }, testInfo) => {
    const state = await setup(page);
    state.actions.push({ id: 'gate_c', name: 'Gate C', kind: 'code-quality', enabled: true, revision: 1, check: { scope: 'card', failGrade: 'C' } });
    const lane = await routeLane(page);
    await mountLaneEditor(page);
    const picker = page.locator('[data-lane-add-choice]');
    const findings = page.getByRole('combobox', { name: 'Findings', exact: true });
    const rows = page.locator('[data-lane-selection-list] strong');
    const posts = () => state.requests.filter(request => request.path === '/api/v1/actions' && request.method === 'POST').length;
    await picker.evaluate(node => node.tomselect.setValue('new:vca'));
    await expect(page.getByRole('combobox', { name: 'Check scope', exact: true })).toBeVisible();
    await expect(findings).toBeHidden();
    await picker.evaluate(node => node.tomselect.setValue('new:code-quality'));
    await expect(findings).toHaveValue('');
    await expect(findings).toHaveAccessibleDescription(/Code quality minimum/);
    expect(await findings.locator('option').evaluateAll(options => options.map(option => [option.value, option.textContent]))).toEqual([
        ['', 'Post as feedback only; never fail the step'], ['A', 'Fail the step below grade A'],
        ['B', 'Fail the step below grade B'], ['C', 'Fail the step below grade C']]);
    await findings.selectOption('B');
    await page.screenshot({ path: testInfo.outputPath('lane-picker-minimum-grade.png') });
    await page.locator('[data-lane-add]').click();
    await expect(rows).toHaveText(['Code quality scan · Linked commits, minimum B']);
    expect(state.actions.at(-1)).toMatchObject({ name: 'Code quality scan · Linked commits, minimum B', kind: 'code-quality', check: { scope: 'card', failGrade: 'B' } });
    expect(posts()).toBe(1);
    // The saved minimum C scan is reused at C rather than copied.
    await picker.evaluate(node => node.tomselect.setValue('new:code-quality'));
    await findings.selectOption('C');
    await page.locator('[data-lane-add]').click();
    await expect(rows).toHaveText(['Code quality scan · Linked commits, minimum B', 'Gate C']);
    expect(posts()).toBe(1);
    // Feedback only is its own scan, never one of the graded ones; the form starts at feedback only again.
    await picker.evaluate(node => node.tomselect.setValue('new:code-quality'));
    await expect(findings).toHaveValue('');
    await page.locator('[data-lane-add]').click();
    await expect(rows).toHaveText(['Code quality scan · Linked commits, minimum B', 'Gate C', 'Code quality scan · Linked commits']);
    expect(posts()).toBe(2);
    expect(state.actions.at(-1).check).toEqual({ scope: 'card' });
    expect(lane.writes).toHaveLength(0);
    await page.locator('[data-lane-save]').click();
    await expect.poll(() => lane.writes.length).toBe(1);
    expect(lane.writes[0].selections.map(selection => selection.definitionId)).toEqual(['preset_1', 'gate_c', 'preset_2']);
});

// VIBE-337: say what each choice does, stage with Add, and let the user hide entries from this picker.
async function routeLane(page, selections = []) {
    const writes = [];
    await page.route('**/api/v1/board/columns/lane/launches', route => {
        if (route.request().method() === 'GET') return route.fulfill({ json: { revision: 1, selections } });
        writes.push(route.request().postDataJSON());
        return route.fulfill({ json: { ...writes.at(-1), revision: 1 + writes.length } });
    });
    return { writes };
}
async function mountLaneEditor(page) {
    await page.evaluate(async () => {
        const { mountLaunchLane } = await import('/js/modules/launch-lane-editor.js');
        const host = document.createElement('div');
        document.querySelector('#app-content').replaceChildren(host);
        window.disposeLane = mountLaunchLane(window.app, host, 'lane');
    });
    await expect(page.locator('.launch-lane-add .ts-control, [data-lane-picker-empty]')).toBeVisible();
}
const laneLabels = page => page.locator('.launch-lane-dropdown .option:not([data-value=""]) .launch-lane-choice-label');

test('lane picker says what each choice does, escaped, and Add only stages the entry (VIBE-337)', async ({ page }, testInfo) => {
    const state = await setup(page);
    state.presets.push(
        { id: 'p1', name: 'Review', description: '<b>Reviews</b> the diff & reports', cli: 'claude', initialPrompt: 'Review', enabled: true, revision: 1 },
        { id: 'p2', name: 'Ship', description: '', cli: 'codex', initialPrompt: 'Ship', enabled: true, revision: 1 });
    state.actions.push({ id: 's1', name: 'Release', kind: 'script', enabled: true, revision: 1, script: { path: 'scripts/release.ps1', runtime: 1 } });
    const lane = await routeLane(page);
    await mountLaneEditor(page);
    await page.locator('.launch-lane-add .ts-control').click();
    const menu = page.locator('.launch-lane-dropdown');
    const detail = value => menu.locator(`.option[data-value="${value}"] .launch-lane-choice-detail`);
    await expect(detail('p1')).toHaveText('<b>Reviews</b> the diff & reports');
    await expect(menu.locator('.option b')).toHaveCount(0);
    await expect(detail('p2')).toHaveText('Starts Codex with its saved prompt.');
    await expect(detail('s1')).toHaveText('Runs scripts/release.ps1 with PowerShell.');
    await expect(detail('new:vca')).toContainText('rules in vc.rules.md');
    await expect(detail('new:code-quality')).toContainText('Grades the added code A–F');
    await expect(menu.locator('.option[data-value=""] .launch-lane-choice-detail')).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath('lane-picker-descriptions.png') });
    await menu.locator('.option[data-value="new:vca"]').click();
    // The selected value stays one line; the full text, its page and the scope sit below the picker.
    await expect(page.locator('.launch-lane-add .ts-control .launch-lane-choice-detail')).toHaveCount(0);
    const about = page.locator('[data-lane-about]');
    await expect(about).toContainText('What this does: Checks the card’s code against this repository’s rules in vc.rules.md');
    await expect(about.getByRole('link', { name: 'Open Rules', exact: true })).toHaveAttribute('href', '?view=dashboard');
    await expect(about.getByRole('combobox', { name: 'Check scope', exact: true })).toBeVisible();
    await page.locator('[data-lane-add-choice]').evaluate(node => node.tomselect.setValue('new:code-quality'));
    await expect(about.getByRole('link', { name: 'Open Quality', exact: true })).toHaveAttribute('href', '?view=code-quality');
    await page.locator('[data-lane-add-choice]').evaluate(node => node.tomselect.setValue('p1'));
    await expect(about).toContainText('What this does: <b>Reviews</b> the diff & reports');
    await expect(about.getByRole('link')).toHaveCount(0);
    await expect(page.getByRole('combobox', { name: 'Check scope', exact: true })).toBeHidden();
    const add = page.getByRole('button', { name: 'Add to run order', exact: true });
    await expect(add).toHaveAccessibleDescription(/Nothing runs until you save lane settings, and then only when a card enters this lane/);
    const unsaved = page.locator('[data-lane-unsaved]');
    await expect(unsaved).toBeHidden();
    await add.click();
    await expect(page.locator('.launch-lane-row .launch-lane-row-detail')).toHaveText(['Agent · <b>Reviews</b> the diff & reports']);
    await expect(unsaved).toHaveText('Unsaved changes. Save lane settings to apply them.');
    await expect(about).toBeHidden();
    expect(lane.writes).toHaveLength(0);
    expect(state.runs).toHaveLength(0);
    // The page links use guarded navigation: unsaved lane changes still ask first.
    await page.locator('[data-lane-add-choice]').evaluate(node => node.tomselect.setValue('new:vca'));
    await about.getByRole('link', { name: 'Open Rules', exact: true }).click();
    await expect(page.getByRole('alertdialog')).toContainText('Discard lane changes?');
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.locator('.launch-lane-editor')).toBeVisible();
    await page.getByRole('button', { name: 'Save lane settings', exact: true }).click();
    await expect.poll(() => lane.writes.length).toBe(1);
    expect(lane.writes[0].selections).toEqual([{ definitionId: 'p1', definitionType: 'preset' }]);
    await expect(unsaved).toBeHidden();
});

test('Manage list hides picker entries for this user only, survives remount and restores them (VIBE-337)', async ({ page }, testInfo) => {
    const state = await setup(page);
    state.presets.push(
        { id: 'p1', name: 'Review', cli: 'codex', initialPrompt: 'Review', enabled: true, revision: 1 },
        { id: 'p2', name: 'Ship', cli: 'codex', initialPrompt: 'Ship', enabled: true, revision: 1 });
    const lane = await routeLane(page, [{ definitionId: 'p2', definitionType: 'preset' }]);
    await mountLaneEditor(page);
    const toggle = page.getByRole('button', { name: 'Manage list', exact: true });
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.click();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const manage = page.locator('[data-lane-manage-list]');
    await expect(manage).toBeVisible();
    await expect(manage).toContainText('Hiding an entry does not disable it, remove it from lanes that already run it, or change Quick actions and AI Agents.');
    await expect(manage.locator('legend')).toHaveText(['Agents', 'Checks']);
    // Ship already runs in this lane: Manage list still offers it; the picker does not.
    await expect(manage.locator('.launch-lane-choice-label')).toHaveText(['Review · Agent', 'Ship · Agent', 'Rules check', 'Code quality scan']);
    await expect(manage.getByRole('switch')).toHaveCount(4);
    for (const name of ['Review · Agent', 'Ship · Agent', 'Rules check']) {
        const entry = manage.getByRole('switch', { name: `Show ${name} in this list`, exact: true });
        await entry.focus();
        await entry.press('Space');
        await expect(entry).not.toBeChecked();
        await expect(entry).toBeEnabled();
        await expect(entry).toBeFocused();
    }
    await expect.poll(() => [...state.laneHidden].sort()).toEqual(['check:vca', 'preset:p1', 'preset:p2']);
    await expect(manage.getByRole('status')).toHaveText('Saved. Rules check is hidden from this list.');
    await page.screenshot({ path: testInfo.outputPath('lane-manage-list.png') });
    // Only the preference was written: not the lane, a definition, or Quick actions.
    await expect(page.locator('[data-lane-selection-list] strong')).toHaveText(['Ship']);
    await expect(page.locator('[data-lane-unsaved]')).toBeHidden();
    expect(lane.writes).toHaveLength(0);
    expect(state.requests.filter(request => request.method !== 'GET' && !request.path.startsWith('/api/v1/lifecycle/')).map(request => request.path))
        .toEqual(Array(3).fill('/api/v1/lane-picker/preferences'));
    // Hidden entries are not searchable.
    await page.locator('.launch-lane-add .ts-control').click();
    await expect(laneLabels(page)).toHaveText(['Code quality scan']);
    const search = page.getByRole('textbox', { name: 'Search agents, scripts and checks…', exact: true });
    for (const query of ['Review', 'Rules']) {
        await search.fill(query);
        await expect(page.locator('.launch-lane-dropdown .option')).toHaveCount(0);
        await expect(page.locator('.launch-lane-dropdown')).toContainText('No matching agents, scripts or checks.');
    }
    await search.press('Escape');
    await manage.getByRole('button', { name: 'Done', exact: true }).click();
    await expect(manage).toBeHidden();
    await expect(toggle).toBeFocused();
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    // A saved preference, not panel state: it survives remounting.
    await page.evaluate(() => window.disposeLane());
    await mountLaneEditor(page);
    await page.locator('.launch-lane-add .ts-control').click();
    await expect(laneLabels(page)).toHaveText(['Code quality scan']);
    await page.keyboard.press('Escape');
    await toggle.click();
    await manage.getByRole('switch', { name: 'Show Review · Agent in this list', exact: true }).check();
    await expect.poll(() => [...state.laneHidden].sort()).toEqual(['check:vca', 'preset:p2']);
    await expect(manage.getByRole('status')).toHaveText('Saved. Review · Agent is shown in this list.');
    await page.locator('.launch-lane-add .ts-control').click();
    await expect(laneLabels(page)).toHaveText(['Review · Agent', 'Code quality scan']);
});

test('a hidden choice is cleared from the picker, and hiding everything points to Manage list (VIBE-337)', async ({ page }) => {
    const state = await setup(page);
    state.laneHidden = ['check:vca'];
    await routeLane(page);
    await mountLaneEditor(page);
    await page.locator('[data-lane-add-choice]').evaluate(node => node.tomselect.setValue('new:code-quality'));
    await expect(page.locator('[data-lane-about]')).toBeVisible();
    await page.getByRole('button', { name: 'Manage list', exact: true }).click();
    const manage = page.locator('[data-lane-manage-list]');
    await manage.getByRole('switch', { name: 'Show Code quality scan in this list', exact: true }).uncheck();
    const empty = page.locator('[data-lane-picker-empty]');
    await expect(empty).toContainText('Nothing to add: every remaining entry is hidden from this list.');
    await expect(page.locator('[data-lane-add-choice]')).toHaveCount(0);
    await expect(page.locator('[data-lane-about]')).toBeHidden();
    await expect(page.getByRole('combobox', { name: 'Check scope', exact: true })).toBeHidden();
    await expect(page.getByRole('button', { name: 'Add to run order', exact: true })).toBeDisabled();
    await manage.getByRole('button', { name: 'Done', exact: true }).click();
    await empty.getByRole('button', { name: 'Show hidden entries', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Manage list', exact: true })).toHaveAttribute('aria-expanded', 'true');
    const rules = manage.getByRole('switch', { name: 'Show Rules check in this list', exact: true });
    await expect(rules).toBeFocused();
    await rules.press('Space');
    await expect(page.locator('.launch-lane-add .ts-control')).toBeVisible();
    await expect(empty).toHaveCount(0);
    expect(state.laneHidden).toEqual(['check:code-quality']);
});

test('Manage list failures keep the previous choice, and a load failure shows every entry (VIBE-337)', async ({ page }) => {
    const state = await setup(page);
    state.laneHidden = ['check:vca'];
    state.failLanePickerLoad = true;
    await routeLane(page);
    await mountLaneEditor(page);
    await page.locator('.launch-lane-add .ts-control').click();
    await expect(laneLabels(page)).toHaveText(['Rules check', 'Code quality scan']);
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Manage list', exact: true }).click();
    const manage = page.locator('[data-lane-manage-list]');
    await expect(manage).toContainText('Your saved choices could not be loaded, so every entry is shown.');
    state.failLanePicker = true;
    const quality = manage.getByRole('switch', { name: 'Show Code quality scan in this list', exact: true });
    await quality.click();
    await expect(manage.getByRole('alert')).toHaveText('Could not save this choice. Try again.');
    await expect(quality).toBeChecked();
    await expect(quality).toBeEnabled();
    await expect(manage.locator('[data-lane-manage-status]')).toHaveText('');
    await page.locator('.launch-lane-add .ts-control').click();
    await expect(laneLabels(page)).toHaveText(['Rules check', 'Code quality scan']);
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
    await expect(page.locator('[data-board-attachments]')).toContainText('remove.png');
    await page.locator('[data-board-remove-pending]').click();
    await expect(page.locator('[data-board-composer-input]')).not.toHaveValue(/pending_/);
    await expect(page.locator('[data-board-remove-pending]')).toHaveCount(0);
    await expect(page.locator('[data-board-attachments]')).not.toContainText('remove.png');
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

// VIBE-311: scripts added in VS Code live in the signed script library, not the launch catalog.
const libraryFixture = () => [
    { id: 'reg_run', name: 'run.ps1', displayName: 'Start <CP210>', status: 'approved', path: 'C:\\fixtures\\project\\run.ps1', scope: 'repo', sizeBytes: 10 },
    { id: 'reg_draft', name: 'draft.py', status: 'unapproved', path: 'C:\\fixtures\\project\\draft.py', scope: 'repo', sizeBytes: 10 },
    { id: 'reg_edit', name: 'edited.sh', status: 'modified', path: 'C:\\fixtures\\project\\edited.sh', scope: 'global', sizeBytes: 10 }
];

for (const width of [375, 1280]) test(`library scripts list with script Actions and run from Quick actions under the agents at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const state = await setup(page);
    state.presets.push({ id: 'release', name: 'Release', cli: 'codex', initialPrompt: 'Release', enabled: true, revision: 1 });
    state.actions.push({ id: 'build', name: 'Build', kind: 'script', enabled: true, revision: 1 });
    state.scripts = libraryFixture();
    await page.locator('[data-agent-refresh]').click();
    const section = page.locator('.launch-catalog-section').filter({ has: page.getByRole('heading', { name: /^Scripts/ }) });
    await expect(section.locator('.launch-section-count')).toHaveText('4');
    await expect(section.locator('.launch-catalog-row')).toHaveCount(4);
    await expect(section.locator('.launch-catalog-empty')).toHaveCount(0);
    const signed = section.locator('[data-library-script="reg_run"]');
    await expect(signed.locator('.launch-catalog-copy p')).toHaveText(['PowerShell script · run.ps1']);
    await expect(section.locator('cp210')).toHaveCount(0);
    const draft = section.locator('[data-library-script="reg_draft"]');
    await expect(draft.locator('.launch-catalog-copy p')).toHaveText(['Python script', 'Not signed. Sign it before it can run.']);
    await expect(draft.getByRole('button', { name: 'Sign draft.py', exact: true })).toBeVisible();
    await expect(draft.getByRole('button', { name: /^Run / })).toHaveCount(0);
    await expect(section.locator('[data-library-script="reg_edit"]')).toContainText('Changed since signing. Sign it again before it can run.');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true);
    await section.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`library-scripts-${width}.png`) });

    await page.evaluate(() => {
        window.scriptCalls = [];
        const library = window.app.aiAgentsController.pythonScripts;
        library.run = async id => { window.scriptCalls.push(['run', id]); };
        library.openScript = id => { window.scriptCalls.push(['open', id]); };
    });
    await signed.getByRole('button', { name: 'Run Start <CP210>', exact: true }).click();
    await signed.getByRole('button', { name: 'Edit Start <CP210>', exact: true }).click();
    await draft.getByRole('button', { name: 'draft.py', exact: true }).click();
    expect(await page.evaluate(() => window.scriptCalls)).toEqual([['run', 'reg_run'], ['open', 'reg_run'], ['open', 'reg_draft']]);

    // Quick actions: the agents and Actions first, then only the signed scripts.
    const trigger = page.getByRole('button', { name: 'Launch an agent or automation', exact: true });
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await trigger.click();
    await expect(panel.locator('.launch-menu-name')).toHaveText(['Release', 'Build', 'Start <CP210>']);
    await expect(panel.locator('.launch-menu-subtitle')).toHaveText(['Script', 'PowerShell script · run.ps1']);
    const menuRow = panel.locator('.launch-menu-row').filter({ has: page.locator('[data-script-id="reg_run"]') });
    expect(await menuRow.locator('.launch-provider-logo').evaluate(el => el.outerHTML))
        .toBe(await signed.locator('.launch-provider-logo').evaluate(el => el.outerHTML));
    await page.screenshot({ path: testInfo.outputPath(`library-quick-actions-${width}.png`) });
    await panel.getByRole('button', { name: 'Run Start <CP210>', exact: true }).click();
    await expect(panel).toHaveCount(0);
    await expect(trigger).toHaveAttribute('aria-expanded', 'false');
    expect(await page.evaluate(() => window.scriptCalls.at(-1))).toEqual(['run', 'reg_run']);
    expect(await page.evaluate(() => window.scriptCalls.length)).toBe(4);
    expect(state.requests.filter(request => request.path === '/api/v1/runs' && request.method === 'POST')).toHaveLength(0);

    // Signed scripts alone are enough for the menu; an unreadable library never hides the agents.
    state.presets = []; state.actions = [];
    await trigger.click();
    await expect(panel.locator('.launch-menu-name')).toHaveText(['Start <CP210>']);
    await page.keyboard.press('Escape');
    state.presets.push({ id: 'release', name: 'Release', cli: 'codex', initialPrompt: 'Release', enabled: true, revision: 1 });
    state.failScripts = true;
    await trigger.click();
    await expect(panel.locator('.launch-menu-name')).toHaveText(['Release']);
    await page.keyboard.press('Escape');
    await page.locator('[data-agent-refresh]').click();
    await expect(page.getByRole('button', { name: 'Release', exact: true })).toBeVisible();
    await expect(page.locator('[data-library-script]')).toHaveCount(0);
    await expect(page.locator('[data-agent-page-error]')).toBeEmpty();
});

test('a library script is signed and removed from its catalog row without reloading the catalog', async ({ page }) => {
    const state = await setup(page);
    state.scripts = libraryFixture();
    await page.locator('[data-agent-refresh]').click();
    const draft = page.locator('[data-library-script="reg_draft"]');
    await expect(draft).toBeVisible();
    const catalogReads = () => state.requests.filter(request => request.path === '/api/v1/launch-catalog').length;
    const before = catalogReads();

    await draft.getByRole('button', { name: 'Sign draft.py', exact: true }).click();
    const pin = page.locator('.python-scripts-pin-modal');
    await expect(pin).toContainText('Sign draft.py');
    await pin.locator('[data-pin-field="pin"]').fill('1234');
    await pin.getByRole('button', { name: 'Sign script', exact: true }).click();
    await expect(draft.getByRole('button', { name: 'Run draft.py', exact: true })).toBeVisible();
    await expect(draft).not.toContainText('Not signed');
    expect(state.requests.find(request => request.path.endsWith('/approve')).body).toEqual({ name: 'reg_draft', pin: '1234' });

    const remove = page.locator('[data-library-script="reg_edit"]').getByRole('button', { name: 'Remove edited.sh from the script library', exact: true });
    await expect(remove).toHaveClass(/btn-danger/);
    await remove.click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('Remove edited.sh?');
    await expect(confirmation).toContainText('The script file stays on disk.');
    await page.keyboard.press('Escape');
    await expect(page.locator('[data-library-script]')).toHaveCount(3);
    await page.locator('[data-library-script="reg_edit"]').getByRole('button', { name: 'Remove edited.sh from the script library', exact: true }).click();
    await confirmation.getByRole('button', { name: 'Remove script', exact: true }).click();
    await expect(page.locator('[data-library-script="reg_edit"]')).toHaveCount(0);
    await expect(page.locator('[data-library-script]')).toHaveCount(2);
    const section = page.locator('.launch-catalog-section').filter({ has: page.getByRole('heading', { name: /^Scripts/ }) });
    await expect(section.locator('.launch-section-count')).toHaveText('2');
    expect(catalogReads()).toBe(before);
});

test('the editor deletes a definition with the catalog confirmation instead of Retire', async ({ page }) => {
    const state = await setup(page);
    state.presets.push({ id: 'reviewer', name: 'Review changes', cli: 'codex', initialPrompt: 'Review', enabled: true, revision: 2 });
    await page.locator('[data-agent-refresh]').click();
    await page.getByRole('button', { name: 'Review changes', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    await expect(editor.getByRole('button', { name: 'Retire', exact: true })).toHaveCount(0);
    const remove = editor.getByRole('button', { name: 'Delete', exact: true });
    await expect(remove).toBeVisible();
    await remove.click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('Delete "Review changes"?');
    await expect(confirmation).toContainText('history and attachments are retained');
    await expect(confirmation).not.toContainText('Retire');
    await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(editor).toBeVisible();
    expect(state.requests.filter(request => request.path.endsWith('/retire'))).toHaveLength(0);
    await remove.click();
    await confirmation.getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(editor).toHaveCount(0);
    expect(state.requests.filter(request => request.path.endsWith('/retire')).map(request => [request.path, request.body]))
        .toEqual([['/api/v1/presets/reviewer/retire', { expectedRevision: 2 }]]);
    await expect(page.getByRole('button', { name: 'Review changes', exact: true })).toHaveCount(0);
});

test('code quality checks are offered again in the catalog, Quick actions and the editor', async ({ page }) => {
    const state = await setup(page);
    state.actions.push({ id: 'quality', name: 'Quality gate', kind: 'code-quality', enabled: true, revision: 1, check: { scope: 'card', failGrade: 'B' } });
    await page.locator('[data-agent-refresh]').click();
    const checks = page.locator('.launch-catalog-section').filter({ has: page.getByRole('heading', { name: /^Board checks/ }) });
    await expect(checks.getByRole('button', { name: 'Quality gate', exact: true })).toBeVisible();
    await expect(checks.locator('.launch-catalog-copy p')).toHaveText(['Code quality scan']);
    await page.getByRole('button', { name: 'Launch an agent or automation', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Quick actions', exact: true });
    await expect(panel.getByRole('button', { name: 'Run Quality gate', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    await checks.getByRole('button', { name: 'Edit Quality gate', exact: true }).click();
    const editor = page.locator('[data-launch-editor]');
    await expect(editor.locator('[data-launch-field="grade"]')).toHaveValue('B');
    await expect(editor.locator('[data-launch-field="grade"]')).toBeVisible();
});

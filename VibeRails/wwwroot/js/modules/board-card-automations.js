import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { isLaunchVisible } from './launch-presentation.js';
import { showReplayModal } from './session-viewer.js';

export function cardAutomationControls(saved) {
    if (!saved) return '<p class="board-side-empty">Save the card to run an Agent or Action on it.</p>';
    return `<div data-board-automation-controls><label>Agent or Action<select class="form-select" data-board-automation-choice><option value="">Loading…</option></select></label><button class="btn btn-sm btn-outline-primary" data-board-run-automation>Run</button><p class="board-side-empty">Uses the saved card. The run stays linked here.</p><div data-board-automation-runs></div><p data-board-automation-message role="status"></p></div>`;
}

export function bindCardAutomations(editor, card, { app, onQueued }) {
    const host=editor.querySelector('[data-board-automation-controls]');
    if(!host || !card?.id)return null;
    const api=new LaunchApi(app), abort=new AbortController();
    const select=host.querySelector('[data-board-automation-choice]'), button=host.querySelector('[data-board-run-automation]'), message=host.querySelector('[data-board-automation-message]'), list=host.querySelector('[data-board-automation-runs]');
    const previews = new Set();
    let items=[], loading=false, busy=false, rendered='';
    const current=()=>!abort.signal.aborted && host.isConnected;
    async function refresh() {
        if(!current() || loading || busy)return;
        loading=true;
        try {
            // The card's own runs (VIBE-296): never page every project run and filter here.
            const [catalog,entries,page]=await Promise.all([api.catalog({signal:abort.signal}),api.call(`board/cards/${encodeURIComponent(card.id)}/launches`,'GET',null,{signal:abort.signal}),api.call(`board/cards/${encodeURIComponent(card.id)}/launches/runs`,'GET',null,{signal:abort.signal})]);
            if(!current())return;
            items=catalog.items.filter(item=>item.canLaunch && isLaunchVisible(item));
            const markup='<option value="">Choose…</option>'+items.map(item=>`<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} (${escapeHtml(item.kind)})</option>`).join('');
            if(markup!==rendered){const selected=select.value;select.innerHTML=rendered=markup;if(items.some(item=>item.id===selected))select.value=selected;}
            const runs=page?.runs||[];
            list.innerHTML=(entries||[]).filter(entry=>entry.current).map(entry=>`<div class="board-automation-run"><strong>${escapeHtml(catalog.items.find(item=>item.id===entry.definitionId)?.name || 'Unavailable definition')}</strong><span>Process: ${escapeHtml(entry.processStatus)} · Result: ${escapeHtml(entry.verdict||'Not reported')}</span><span>${escapeHtml(entry.summary||'')}</span>${entry.skipRequested?'Skip requested':`<button class="btn btn-sm btn-outline-secondary" data-launch-skip="${escapeHtml(entry.id)}">Stop and skip</button>`}</div>`).join('')+runs.map(run=>`<div class="board-automation-run"><span>${escapeHtml(run.snapshot.preset?.name||run.snapshot.action?.name)} · ${escapeHtml(run.status)}</span>${run.sessionId?`<button class="btn btn-sm btn-link" data-launch-replay="${escapeHtml(run.sessionId)}">Recorded terminal</button>`:''}</div>`).join('');
            button.disabled=!select.value;
        }catch(error){if(current())message.textContent=error.message;}finally{loading=false;}
    }
    select.addEventListener('change',()=>{button.disabled=!select.value;},{signal:abort.signal});
    button.addEventListener('click',async()=>{
        if(busy || !select.value || editor._boardSaving || editor._boardUploading || editor._boardStarting)return;
        const item=items.find(item=>item.id===select.value);if(!item)return;
        busy=true;button.disabled=true;
        try{await api.call(`board/cards/${encodeURIComponent(card.id)}/launches`,'POST',{definitionId:item.id,definitionType:item.definitionType,occurrenceId:crypto.randomUUID()});if(current()){message.textContent='Run queued.';onQueued?.();}}catch(error){if(current())message.textContent=error.message;}finally{busy=false;if(current()){button.disabled=!select.value;void refresh();}}
    },{signal:abort.signal});
    host.addEventListener('click',async event=>{
        const target=event.target.closest('[data-launch-skip], [data-launch-replay]');if(!target || busy)return;
        if(target.dataset.launchReplay){const viewer = await showReplayModal(target.dataset.launchReplay);if(current())previews.add(viewer.close);else viewer.close();return;}
        busy=true;target.disabled=true;
        try{await api.call(`board/cards/${encodeURIComponent(card.id)}/launches/${encodeURIComponent(target.dataset.launchSkip)}/skip`,'POST');if(current())onQueued?.();}catch(error){if(current())message.textContent=error.message;}finally{busy=false;void refresh();}
    },{signal:abort.signal});
    void refresh();
    return {refresh,dispose(){abort.abort();previews.forEach(close=>close());}};
}

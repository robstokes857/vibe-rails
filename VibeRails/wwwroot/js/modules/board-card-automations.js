import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { showReplayModal } from './session-viewer.js';
import { agentSkipNoticeMarkup, cardRunRowMarkup, laneEntryRowMarkup, toggleStepSummary } from './board-step-runs.js';

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
    let items=[], loading=false, busy=false, rendered='', listMarkup='';
    const current=()=>!abort.signal.aborted && host.isConnected;
    async function refresh() {
        if(!current() || loading || busy)return;
        loading=true;
        try {
            // The card's own runs (VIBE-296): never page every project run and filter here.
            const [catalog,entries,page]=await Promise.all([api.catalog({signal:abort.signal}),api.call(`board/cards/${encodeURIComponent(card.id)}/launches`,'GET',null,{signal:abort.signal}),api.call(`board/cards/${encodeURIComponent(card.id)}/launches/runs`,'GET',null,{signal:abort.signal})]);
            if(!current())return;
            items=catalog.items.filter(item=>item.canLaunch);
            const markup='<option value="">Choose…</option>'+items.map(item=>`<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} (${escapeHtml(item.kind)})</option>`).join('');
            if(markup!==rendered){const selected=select.value;select.innerHTML=rendered=markup;if(items.some(item=>item.id===selected))select.value=selected;}
            const runs=page?.runs||[];
            // Readable rows (VIBE-338): a status pill per step, the report only when it says more
            // than the pill, and an agent's open skip with its reason until a person dismisses it.
            const laneEntries=(entries||[]).filter(entry=>entry.current);
            const steps=laneEntries.map(entry=>laneEntryRowMarkup(entry, catalog.items.find(item=>item.id===entry.definitionId)?.name || 'Unavailable definition')).join('');
            const history=runs.map(cardRunRowMarkup).join('');
            const next=agentSkipNoticeMarkup(editor._boardCard?.agentSkip ?? card.agentSkip)
                +(steps?`<h4 class="board-step-runs-heading">This lane</h4>${steps}`:'')
                +(history?`<h4 class="board-step-runs-heading">Runs</h4>${history}`:'');
            if(next!==listMarkup){
                const expanded=new Set([...list.querySelectorAll('[data-step-summary].is-expanded')].map(item=>item.dataset.stepSummary));
                list.innerHTML=listMarkup=next;
                for(const button of list.querySelectorAll('[data-step-more]'))if(expanded.has(button.dataset.stepMore))toggleStepSummary(list,button);
            }
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
        const more=event.target.closest('[data-step-more]');
        if(more){toggleStepSummary(list,more);return;}
        const dismiss=event.target.closest('[data-board-dismiss-agent-skip]');
        if(dismiss && !busy){
            busy=true;dismiss.disabled=true;
            try{
                await api.call(`board/cards/${encodeURIComponent(card.id)}/agent-skips/dismiss`,'POST');
                if(editor._boardCard)editor._boardCard.agentSkip=null;
                card.agentSkip=null;
                if(current())onQueued?.();
            }catch(error){if(current())message.textContent=error.message;dismiss.disabled=false;}
            finally{busy=false;void refresh();}
            return;
        }
        const target=event.target.closest('[data-launch-skip], [data-launch-replay]');if(!target || busy)return;
        if(target.dataset.launchReplay){const viewer = await showReplayModal(target.dataset.launchReplay);if(current())previews.add(viewer.close);else viewer.close();return;}
        busy=true;target.disabled=true;
        try{await api.call(`board/cards/${encodeURIComponent(card.id)}/launches/${encodeURIComponent(target.dataset.launchSkip)}/skip`,'POST');if(current())onQueued?.();}catch(error){if(current())message.textContent=error.message;}finally{busy=false;void refresh();}
    },{signal:abort.signal});
    void refresh();
    return {refresh,dispose(){abort.abort();previews.forEach(close=>close());}};
}

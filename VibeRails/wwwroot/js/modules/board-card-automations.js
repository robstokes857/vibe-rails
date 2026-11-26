import { escapeHtml } from './utils.js';
import { LaunchApi } from './launch-api.js';
import { showReplayModal } from './session-viewer.js';
import { agentSkipNoticeMarkup, cardRunRowMarkup, laneEntryRowMarkup, toggleStepSummary } from './board-step-runs.js';

export function cardAutomationControls(saved) {
    if (!saved) return '<p class="board-side-empty">Save the card to run an Agent or Action on it.</p>';
    return `<div data-board-automation-controls><label class="board-editor-label">Agent or Action<select class="form-select form-select-sm mt-1" data-board-automation-choice><option value="">Loading…</option></select></label><button class="btn btn-sm btn-outline-primary" data-board-run-automation>Run</button><p class="board-side-empty">Uses the saved card. The run stays linked here.</p><div data-board-automation-runs></div><p data-board-automation-message role="status"></p></div>`;
}

/**
 * The run of each current lane entry, by run id. The card's run list holds only its newest 50
 * runs, so an entry whose run is older than that is read by id: a step's state, its error and
 * its Retry never depend on how many manual runs and retries came after it. A run that cannot
 * be read is left out, and its step shows the state the entry recorded.
 */
export async function currentEntryRuns(api, entries, runs, signal) {
    const byId=new Map(runs.map(run=>[run.id,run]));
    const missing=[...new Set(entries.map(entry=>entry.runId).filter(id=>id && !byId.has(id)))];
    await Promise.all(missing.map(async id=>{
        try{
            const run=await api.call(`runs/${encodeURIComponent(id)}`,'GET',null,{signal});
            if(run?.id)byId.set(run.id,run);
        }catch{ /* The step keeps its recorded state. */ }
    }));
    return byId;
}

/**
 * The rail's list. Readable rows (VIBE-338): a status pill per step, the report only when it says
 * more than the pill, and an agent's open skip with its reason until a person dismisses it. Each
 * step carries its run, so it shows the run's own state and error and offers Retry. This rail is
 * the one place a card's lane runs are managed; the lane panel used to list them for every card in
 * the lane (VIBE-372).
 */
function railMarkup(skip, laneEntries, entryRuns, runs, definitions) {
    const nameOf=entry=>definitions.find(item=>item.id===entry.definitionId)?.name || 'Unavailable definition';
    const steps=laneEntries.map(entry=>laneEntryRowMarkup(entry,nameOf(entry),entryRuns.get(entry.runId) || null)).join('');
    const history=runs.map(cardRunRowMarkup).join('');
    return agentSkipNoticeMarkup(skip)
        +(steps?`<h4 class="board-step-runs-heading">This lane</h4>${steps}`:'')
        +(history?`<h4 class="board-step-runs-heading">Runs</h4>${history}`:'');
}

export function bindCardAutomations(editor, card, { app, onQueued, onRuns }) {
    const host=editor.querySelector('[data-board-automation-controls]');
    if(!host || !card?.id)return null;
    const api=new LaunchApi(app), abort=new AbortController();
    const select=host.querySelector('[data-board-automation-choice]'), button=host.querySelector('[data-board-run-automation]'), message=host.querySelector('[data-board-automation-message]'), list=host.querySelector('[data-board-automation-runs]');
    const previews = new Set();
    const cardPath=`board/cards/${encodeURIComponent(card.id)}`;
    // No runs is what the editor already shows, so the first empty answer changes nothing.
    let items=[], loading=false, busy=false, rendered='', listMarkup='', recordedKey='0|';
    const current=()=>!abort.signal.aborted && host.isConnected;
    const showChoices=catalog=>{
        items=catalog.items.filter(item=>item.canLaunch);
        const markup='<option value="">Choose…</option>'+items.map(item=>`<option value="${escapeHtml(item.id)}">${escapeHtml(item.name)} (${escapeHtml(item.kind)})</option>`).join('');
        if(markup===rendered)return;
        const selected=select.value;
        select.innerHTML=rendered=markup;
        if(items.some(item=>item.id===selected))select.value=selected;
    };
    // A run row offers its own recording, so the recordings list below it leaves that session out.
    const reportRecordings=runs=>{
        const recorded=[...new Set(runs.map(run=>run.sessionId).filter(Boolean))].sort();
        const nextKey=`${runs.length}|${recorded.join('|')}`;
        if(nextKey===recordedKey)return;
        recordedKey=nextKey;
        onRuns?.({sessionIds:new Set(recorded),count:runs.length});
    };
    const showList=next=>{
        if(next===listMarkup)return;
        const expanded=new Set([...list.querySelectorAll('[data-step-summary].is-expanded')].map(item=>item.dataset.stepSummary));
        list.innerHTML=listMarkup=next;
        for(const more of list.querySelectorAll('[data-step-more]'))if(expanded.has(more.dataset.stepMore))toggleStepSummary(list,more);
    };
    async function refresh() {
        if(!current() || loading || busy)return;
        loading=true;
        try {
            // The card's own runs (VIBE-296): never page every project run and filter here.
            const [catalog,entries,page]=await Promise.all([api.catalog({signal:abort.signal}),api.call(`${cardPath}/launches`,'GET',null,{signal:abort.signal}),api.call(`${cardPath}/launches/runs`,'GET',null,{signal:abort.signal})]);
            if(!current())return;
            const runs=page?.runs||[];
            const laneEntries=(entries||[]).filter(entry=>entry.current);
            const entryRuns=await currentEntryRuns(api,laneEntries,runs,abort.signal);
            if(!current())return;
            showChoices(catalog);
            reportRecordings(runs);
            showList(railMarkup(editor._boardCard?.agentSkip ?? card.agentSkip,laneEntries,entryRuns,runs,catalog.items));
            button.disabled=!select.value;
        }catch(error){if(current())message.textContent=error.message;}finally{loading=false;}
    }
    // One request at a time. A request that fails leaves the list as it was, and an unchanged list
    // is not redrawn, so the pressed control is enabled again here: it used to stay disabled until
    // the dialog was reopened. After a request that worked, the redrawn row replaces it.
    async function act(control, request) {
        if(busy)return;
        busy=true;control.disabled=true;
        let failed=false;
        try{
            await request();
            if(current()){message.textContent='';onQueued?.();}
        }catch(error){
            failed=true;
            if(current())message.textContent=error.message;
        }finally{
            busy=false;
            if(failed && control.isConnected)control.disabled=false;
            void refresh();
        }
    }
    const dismissAgentSkip=async()=>{
        await api.call(`${cardPath}/agent-skips/dismiss`,'POST');
        if(editor._boardCard)editor._boardCard.agentSkip=null;
        card.agentSkip=null;
    };
    const replay=async sessionId=>{
        const viewer=await showReplayModal(sessionId);
        if(current())previews.add(viewer.close);else viewer.close();
    };
    select.addEventListener('change',()=>{button.disabled=!select.value;},{signal:abort.signal});
    button.addEventListener('click',async()=>{
        if(busy || !select.value || editor._boardSaving || editor._boardUploading || editor._boardStarting)return;
        const item=items.find(item=>item.id===select.value);if(!item)return;
        busy=true;button.disabled=true;
        try{await api.call(`${cardPath}/launches`,'POST',{definitionId:item.id,definitionType:item.definitionType,occurrenceId:crypto.randomUUID()});if(current()){message.textContent='Run queued.';onQueued?.();}}catch(error){if(current())message.textContent=error.message;}finally{busy=false;if(current()){button.disabled=!select.value;void refresh();}}
    },{signal:abort.signal});
    host.addEventListener('click',event=>{
        const control=event.target.closest('[data-step-more], [data-board-dismiss-agent-skip], [data-launch-skip], [data-launch-retry], [data-launch-replay]');
        if(!control)return;
        const data=control.dataset;
        if('stepMore' in data)return void toggleStepSummary(list,control);
        if(busy)return;
        if(data.launchReplay)return void replay(data.launchReplay);
        if('boardDismissAgentSkip' in data)return void act(control,dismissAgentSkip);
        if(data.launchRetry)return void act(control,()=>api.runAction(data.launchRetry,'retry'));
        void act(control,()=>api.call(`${cardPath}/launches/${encodeURIComponent(data.launchSkip)}/skip`,'POST'));
    },{signal:abort.signal});
    void refresh();
    return {refresh,dispose(){abort.abort();previews.forEach(close=>close());}};
}

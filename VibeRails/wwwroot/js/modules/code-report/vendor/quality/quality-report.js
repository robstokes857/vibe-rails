import {adaptApiResponse,gradeFromHealth,healthFromConcern,formatNumber,isNumber,clamp,escapeHtml as esc} from './report-model.js';
import {createRadar,skeletonMarkup} from './radar.js';

let instanceId=0;
const mounts=new WeakMap();
/** No networking, timers for analysis, global styles, dialogs, or API changes. */
export function mountQualityReport(host,options={}) {
  if (!host?.ownerDocument) throw new TypeError('A host element is required.');
  mounts.get(host)?.destroy();
  const doc=host.ownerDocument,win=doc.defaultView,id=`qr-${++instanceId}`;
  const root=doc.createElement('section');
  root.className='qr';root.dataset.state='loading';root.setAttribute('aria-label','Code quality');
  root.innerHTML=`
    <div class="qr-panel">
      <div class="qr-top"><h2 class="qr-title">Code quality</h2><span class="qr-status" role="status" aria-live="polite">Loading…</span></div>
      <div class="qr-error" role="alert" hidden></div>
      <div class="qr-hero" aria-busy="true">
        <div class="qr-health">
          <p class="qr-kicker">Overall health</p>
          <div class="qr-grade" aria-hidden="true">—</div>
          <div class="qr-score-line" aria-hidden="true"><span class="qr-score">—</span><small>/100</small></div>
          <div class="qr-health-track" aria-hidden="true"><i></i></div>
          <p class="qr-rating"></p>
          <span class="qr-sr qr-health-accessible"></span>
        </div>
        <div class="qr-chart">
          ${skeletonMarkup(id)}
          <svg class="qr-radar" viewBox="0 0 520 440" role="img" aria-labelledby="${id}-title ${id}-description">
            <title id="${id}-title">Category health</title>
            <desc id="${id}-description">Health is 100 minus API category concern. Larger is healthier. Missing measurements are not plotted.</desc>
          </svg>
          <p class="qr-legend">Health = 100 − concern</p>
        </div>
        <div class="qr-signals"><div class="qr-signal-heading"><span>Categories</span><span>Health /100</span></div><div class="qr-signal-list"></div></div>
      </div>
      <div class="qr-meta" hidden><time></time><span class="qr-exit"></span></div>
    </div>
    <div class="qr-results">
      <div class="qr-stats"></div>
      <section class="qr-metrics-section"><h3>Metrics</h3><div class="qr-metrics"></div></section>
      <section class="qr-files-section"><h3>Files</h3><div class="qr-files"></div></section>
    </div>`;
  host.append(root);
  const $=selector=>root.querySelector(selector);
  let model=null,raf=0,started=null,destroyed=false;
  const motion=win.matchMedia('(prefers-reduced-motion: reduce)');
  const radar=createRadar($('.qr-radar'),index=>activate('category',index));
  const callbacks={category:'onCategoryClick',metric:'onMetricClick',file:'onFileClick'};
  function activate(kind,index){
    if(root.dataset.state!=='complete'||!model)return;
    const collection={category:model.categories,metric:model.metrics,file:model.files}[kind];
    if(!collection?.[index]?.source)return;
    // A consumer gets the original wire-shaped item, not a fabricated view model.
    const item=structuredClone(collection[index].source);
    options[callbacks[kind]]?.(item);
    root.dispatchEvent(new win.CustomEvent(`quality:${kind}-click`,{detail:item,bubbles:true}));
  }
  function onClick(event){
    const target=event.target.closest('[data-qr-action]');
    if(target&&root.contains(target))activate(target.dataset.qrAction,Number(target.dataset.index));
  }
  root.addEventListener('click',onClick);
  function cancel(){win.cancelAnimationFrame(raf);raf=0;started=null;}
  function state(value){
    root.dataset.state=value;
    const pending=value==='loading'||value==='revealing';
    $('.qr-hero').setAttribute('aria-busy',String(pending));
    $('.qr-hero').hidden=value==='error';
    $('.qr-radar').setAttribute('aria-hidden',String(value!=='complete'&&value!=='revealing'));
    $('.qr-results').hidden=value==='loading'||value==='error';
    root.querySelectorAll('[data-qr-action]').forEach(node=>node.disabled=value!=='complete');
  }
  function setLoading(){
    if(destroyed)return;
    cancel();state('loading');
    $('.qr-error').hidden=true;$('.qr-meta').hidden=true;
    $('.qr-status').textContent='Loading…';
    $('.qr-health-accessible').textContent='';
  }
  function setError(message='Code analysis failed.'){
    if(destroyed)return;
    cancel();state('error');$('.qr-meta').hidden=true;
    $('.qr-status').textContent='';
    $('.qr-error').textContent=String(message);$('.qr-error').hidden=false;
  }
  function draw(progress){
    $('.qr-score').textContent=isNumber(model.score)?formatNumber(model.score*progress,1):'—';
    $('.qr-health-track i').style.width=`${clamp(model.score??0)*progress}%`;
    root.querySelectorAll('[data-qr-count]').forEach(node=>{
      const value=model.counts[node.dataset.qrCount];node.textContent=isNumber(value)?formatNumber(Math.round(value*progress)):'—';
    });
    root.querySelectorAll('[data-qr-health]').forEach(node=>{
      const health=healthFromConcern(model.categories[Number(node.dataset.qrHealth)].concern);
      node.textContent=health===null?'—':formatNumber(health*progress,1);
    });
    root.querySelectorAll('[data-qr-bar]').forEach(node=>{
      node.style.width=`${(healthFromConcern(model.categories[Number(node.dataset.qrBar)].concern)??0)*progress}%`;
    });
    root.querySelectorAll('[data-qr-metric]').forEach(node=>{
      const value=model.metrics[Number(node.dataset.qrMetric)].value;node.textContent=isNumber(value)?formatNumber(value*progress):'—';
    });
    radar.draw(model.categories,progress);
  }
  function complete(){
    cancel();if(!model||destroyed)return;
    draw(1);state('complete');
    $('.qr-status').textContent=model.status;
    $('.qr-health-accessible').textContent=`Overall health ${formatNumber(model.score,1)} out of 100. Grade ${gradeFromHealth(model.score)}. ${model.rating}`;
  }
  function tick(now){
    raf=0;
    if(destroyed)return;
    if(started===null)started=now;
    const progress=Math.min(1,(now-started)/850);
    draw(1-(1-progress)**3);
    if(progress===1)complete();else raf=win.requestAnimationFrame(tick);
  }
  function render(){
    $('.qr-title').textContent=model.title||'Code quality';
    $('.qr-grade').textContent=gradeFromHealth(model.score);
    $('.qr-grade').title='Derived from overall health: A ≥90, B ≥80, C ≥70, D ≥55, F <55.';
    root.dataset.tone=!isNumber(model.score)?'unknown':model.score>=80?'good':model.score>=55?'warning':'danger';
    $('.qr-rating').textContent=model.rating;
    $('.qr-signal-list').innerHTML=model.categories.map((category,i)=>`<button type="button" class="qr-signal" data-qr-action="category" data-index="${i}"><span><span>${esc(category.name)}</span><b data-qr-health="${i}">—</b></span><span class="qr-bar"><i data-qr-bar="${i}"></i></span></button>`).join('');
    $('.qr-stats').innerHTML=Object.entries({analyzed:'Files analyzed',skipped:'Skipped',ignored:'Ignored'}).map(([key,label])=>`<div class="qr-stat"><span>${label}</span><strong data-qr-count="${key}">—</strong></div>`).join('')+`<div class="qr-stat"><span>Duration</span><strong>${isNumber(model.durationMs)?esc(formatNumber(model.durationMs/1000))+'<small>s</small>':'—'}</strong></div>`;
    $('.qr-metrics-section').hidden=model.metrics.length===0;
    $('.qr-metrics').innerHTML=model.metrics.map((metric,i)=>`<button type="button" class="qr-metric" data-qr-action="metric" data-index="${i}"><span class="qr-metric-title">${esc(metric.name)}</span><strong data-qr-metric="${i}">—</strong><span class="qr-metric-meta"><span>Average</span><span>${esc(formatNumber(metric.concern,1))} concern</span></span></button>`).join('');
    $('.qr-files').innerHTML=model.files.length?`<div class="qr-file-head"><span>File</span><span>Concern</span><span>Priority</span></div>${model.files.map((file,i)=>`<button type="button" class="qr-file" data-qr-action="file" data-index="${i}"><span><b>${esc(file.path)}</b><small>${esc(file.rating)}</small></span><span>${esc(formatNumber(file.concern,1))}</span><span>${esc(formatNumber(file.priority,1))}</span></button>`).join('')}`:'<p class="qr-empty">No files.</p>';
    const date=new Date(model.startedUtc),valid=model.startedUtc&&!Number.isNaN(date.getTime());
    $('time').hidden=!valid;
    if(valid){$('time').dateTime=model.startedUtc;$('time').textContent=date.toLocaleString();}
    $('.qr-exit').textContent=isNumber(model.response.exitCode)?`Exit code ${model.response.exitCode}`:'';
    $('.qr-meta').hidden=!valid&&!isNumber(model.response.exitCode);
  }
  function setResponse(response,{animate=true}={}){
    if(destroyed)return false;
    cancel();
    try{model=adaptApiResponse(response);}catch(error){setError(error.message);return false;}
    $('.qr-error').hidden=true;render();
    if(!animate||motion.matches||doc.hidden){complete();return true;}
    state('revealing');$('.qr-status').textContent='';draw(0);
    raf=win.requestAnimationFrame(tick);return true;
  }
  function onMotion(){if(motion.matches&&root.dataset.state==='revealing')complete();}
  function onVisibility(){
    root.classList.toggle('qr-hidden-document',doc.hidden);
    if(doc.hidden&&root.dataset.state==='revealing')complete();
  }
  motion.addEventListener('change',onMotion);
  doc.addEventListener('visibilitychange',onVisibility);
  function destroy(){
    if(destroyed)return;
    cancel();destroyed=true;
    motion.removeEventListener('change',onMotion);doc.removeEventListener('visibilitychange',onVisibility);
    root.removeEventListener('click',onClick);root.remove();model=null;
    if(mounts.get(host)===api)mounts.delete(host);
  }
  const api={setLoading,setResponse,setError,destroy};
  mounts.set(host,api);
  if(options.response)setResponse(options.response,{animate:options.animate!==false});else setLoading();
  return api;
}


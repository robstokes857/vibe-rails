import {adaptApiResponse,healthFromConcern,formatNumber,isNumber} from './vendor/quality/report-model.js';
import {point} from './vendor/quality/radar.js';
import {isConfirmDialogOpen} from '../utils.js';

const descriptions={
  Complexity:'Branching, execution paths, and nesting in the analyzed code.',
  Size:'File size and the number of methods, fields, and parameters.',
  Cohesion:'How closely related the responsibilities within a class are.',
  Coupling:'Connections to other code and hard-coded dependencies.',
  Testability:'Dependency signals that affect how easily code can be isolated for tests.',
  Duplication:'Repeated code detected in the analyzed changes.',
  Maintainability:'Maintainability index and Halstead difficulty signals.',
};
let instanceId=0;

/** Host enhancement: preserves the portable radar renderer and its reveal animation. */
export function enhanceRadar(root,response,{onSelect}={}){
  const {categories}=adaptApiResponse(response),doc=root.ownerDocument,win=doc.defaultView;
  const svg=root.querySelector('.qr-radar'),chart=root.querySelector('.qr-chart');
  const labels=[...svg.querySelectorAll('.qr-label')],dots=[...svg.querySelectorAll('.qr-point')];
  const listeners=[],id=`radar-detail-${++instanceId}`;
  const listen=(node,event,handler,options)=>{node.addEventListener(event,handler,options);listeners.push(()=>node.removeEventListener(event,handler,options));};
  const node=(tag,attrs={})=>{const el=doc.createElementNS('http://www.w3.org/2000/svg',tag);for(const [name,value] of Object.entries(attrs))el.setAttribute(name,value);return el;};
  const interaction=node('g',{class:'radar-interaction-layer','aria-hidden':'true'});
  const spoke=node('line',{class:'radar-active-spoke',x1:260,y1:223});
  const halo=node('circle',{class:'radar-active-halo',r:11});
  const sectors=categories.map((category,index)=>{
    const angle=-Math.PI/2+index*Math.PI*2/7,half=Math.PI/7,radius=207;
    const xy=a=>[260+Math.cos(a)*radius,223+Math.sin(a)*radius];
    const start=xy(angle-half),end=xy(angle+half);
    const sector=node('path',{class:'radar-sector',d:`M260 223 L${start.join(' ')} A${radius} ${radius} 0 0 1 ${end.join(' ')} Z`,'data-radar-category':index});
    interaction.append(sector);return sector;
  });
  svg.insertBefore(interaction,svg.querySelector('.qr-shape'));
  svg.append(spoke,halo);
  svg.setAttribute('role','group');
  categories.forEach((category,index)=>{
    const health=healthFromConcern(category.concern),label=labels[index];
    label.dataset.radarCategory=String(index);
    label.setAttribute('role','button');label.setAttribute('tabindex','-1');
    label.setAttribute('aria-label',`${category.name}: ${health===null?'not measured':`${formatNumber(health,1)} out of 100 health`}. ${category.source?'Open category details.':'No saved details available.'}`);
    label.setAttribute('aria-describedby',`${id}-help`);
    dots[index].dataset.radarCategory=String(index);
  });
  const detail=doc.createElement('div');
  detail.className='radar-detail';detail.id=id;detail.hidden=true;
  detail.setAttribute('role','region');detail.setAttribute('aria-labelledby',`${id}-title`);
  detail.innerHTML=`<div class="radar-detail-heading"><strong id="${id}-title"></strong><span><b class="radar-detail-score"></b><small> /100</small></span></div><p class="radar-detail-scale">Category health · higher is healthier</p><div class="radar-detail-meter" aria-hidden="true"><i></i></div><p class="radar-detail-description"></p><div class="radar-detail-signal"><span></span><b></b><small></small></div><button type="button" class="radar-detail-action">View category details <span aria-hidden="true">↗</span></button>`;
  root.querySelector('.qr-panel').after(detail);
  const help=doc.createElement('span');help.id=`${id}-help`;help.className='qr-sr';
  help.textContent='Use arrow keys to explore categories. Enter opens details. Escape dismisses the preview.';chart.append(help);
  const title=detail.querySelector('strong'),score=detail.querySelector('.radar-detail-score');
  const meter=detail.querySelector('.radar-detail-meter i'),description=detail.querySelector('.radar-detail-description');
  const signal=detail.querySelector('.radar-detail-signal'),action=detail.querySelector('button');
  let active=-1,focused=-1,pointerInside=false,detailInside=false,closeTimer=0,dismissed=false;
  const ready=()=>root.dataset.state==='complete';
  function place(){
    const panel=root.querySelector('.qr-panel').getBoundingClientRect(),bounds=root.getBoundingClientRect();
    detail.style.width=`${panel.width}px`;
    detail.style.left=`${panel.left-bounds.left}px`;
    detail.style.top=`${panel.bottom-bounds.top+6}px`;
  }
  function clearTimer(){win.clearTimeout(closeTimer);closeTimer=0;}
  function hide(){
    clearTimer();active=-1;detail.hidden=true;svg.classList.remove('radar-exploring');
    labels.forEach(label=>label.classList.remove('radar-active'));
    sectors.forEach(sector=>sector.classList.remove('radar-active'));
    dots.forEach(dot=>dot.classList.remove('radar-active'));
  }
  function show(index){
    if(!ready()||dismissed||!categories[index])return;
    clearTimer();if(active===index&&!detail.hidden)return;
    active=index;const category=categories[index],source=category.source,health=healthFromConcern(category.concern);
    svg.classList.add('radar-exploring');
    labels.forEach((label,i)=>label.classList.toggle('radar-active',i===index));
    sectors.forEach((sector,i)=>sector.classList.toggle('radar-active',i===index));
    dots.forEach((dot,i)=>dot.classList.toggle('radar-active',i===index));
    const end=point(index,100),current=point(index,health??0);
    spoke.setAttribute('x2',end[0]);spoke.setAttribute('y2',end[1]);
    halo.setAttribute('cx',current[0]);halo.setAttribute('cy',current[1]);halo.style.display=health===null?'none':'';
    title.textContent=category.name;score.textContent=health===null?'—':formatNumber(health,1);
    meter.style.width=`${health??0}%`;
    description.textContent=health===null?'This category has no measurement in the saved report.':descriptions[category.name];
    signal.hidden=!source?.worstMetricName||health===null;
    signal.querySelector('span').textContent=source?.worstConcern===0?'No recorded concern · sample signal':'Highest-concern signal';
    signal.querySelector('b').textContent=source?.worstMetricName?`${source.worstMetricName.replaceAll('_',' ')}${isNumber(source.worstMetricValue)?` · ${formatNumber(source.worstMetricValue,2)}`:''}`:'';
    signal.querySelector('small').textContent=source?.worstMetricFile?.split(/[\\/]/).pop()||'No file supplied';
    signal.querySelector('small').title=source?.worstMetricFile||'';
    action.disabled=!source;detail.hidden=false;place();
  }
  function scheduleHide(){
    clearTimer();closeTimer=win.setTimeout(()=>{
      if(pointerInside||detailInside||detail.contains(doc.activeElement))return;
      if(focused>=0)show(focused);else hide();
    },280);
  }
  function categoryFor(event){
    const target=event.target.closest?.('[data-radar-category]');
    if(target&&svg.contains(target))return Number(target.dataset.radarCategory);
    return -1;
  }
  function select(index){
    if(!ready()||!categories[index]?.source)return;
    // Focus the source control so the inline details panel can return here.
    labels[index].focus({preventScroll:true});hide();dismissed=true;
    onSelect?.(structuredClone(categories[index].source));
  }
  listen(svg,'pointerenter',()=>{pointerInside=true;dismissed=false;});
  listen(svg,'pointermove',event=>{
    if(event.pointerType==='touch')return;
    pointerInside=true;const index=categoryFor(event);
    if(index>=0)show(index);
  });
  listen(svg,'pointerleave',()=>{pointerInside=false;dismissed=false;scheduleHide();});
  listen(svg,'click',event=>{const index=categoryFor(event);if(index<0)return;event.preventDefault();event.stopPropagation();select(index);},true);
  listen(svg,'focusin',event=>{const index=categoryFor(event);if(index>=0){focused=index;dismissed=false;show(index);}});
  listen(svg,'focusout',event=>{if(!svg.contains(event.relatedTarget)){focused=-1;scheduleHide();}});
  listen(svg,'keydown',event=>{
    const index=categoryFor(event);if(index<0||!ready())return;
    if(event.key==='Enter'||event.key===' '){event.preventDefault();select(index);return;}
    const delta={ArrowRight:1,ArrowDown:1,ArrowLeft:-1,ArrowUp:-1}[event.key];
    if(delta!==undefined||event.key==='Home'||event.key==='End'){
      event.preventDefault();const next=event.key==='Home'?0:event.key==='End'?categories.length-1:(index+delta+categories.length)%categories.length;
      labels[next].focus({preventScroll:true});
    }
  });
  listen(detail,'pointerenter',()=>{detailInside=true;clearTimer();});
  listen(detail,'pointerleave',()=>{detailInside=false;scheduleHide();});
  listen(detail,'focusin',clearTimer);listen(detail,'focusout',scheduleHide);
  listen(action,'click',()=>select(active));
  listen(doc,'keydown',event=>{if(isConfirmDialogOpen())return;if(event.key==='Escape'&&!detail.hidden){
    if(detail.contains(doc.activeElement)&&active>=0)labels[active].focus({preventScroll:true});
    dismissed=true;hide();event.preventDefault();
  }},true);
  listen(doc,'pointerdown',event=>{if(!detail.hidden&&!svg.contains(event.target)&&!detail.contains(event.target)){dismissed=true;hide();}},true);
  function sync(){
    labels.forEach((label,index)=>{label.setAttribute('tabindex',ready()?'0':'-1');label.setAttribute('aria-disabled',String(!ready()||!categories[index].source));});
    if(!ready())hide();
  }
  const observer=new win.MutationObserver(sync);observer.observe(root,{attributes:true,attributeFilter:['data-state']});sync();
  const resize=new win.ResizeObserver(()=>{if(!detail.hidden)place();});resize.observe(root);resize.observe(root.querySelector('.qr-panel'));
  return {destroy(){
    hide();listeners.forEach(remove=>remove());observer.disconnect();resize.disconnect();
    interaction.remove();spoke.remove();halo.remove();detail.remove();help.remove();svg.setAttribute('role','img');
    for(const label of labels)for(const name of ['data-radar-category','role','tabindex','aria-label','aria-describedby','aria-disabled'])label.removeAttribute(name);
    dots.forEach(dot=>dot.removeAttribute('data-radar-category'));
  }};
}

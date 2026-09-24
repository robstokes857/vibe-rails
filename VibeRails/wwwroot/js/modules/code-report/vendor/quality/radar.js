import {healthFromConcern,formatNumber} from './report-model.js';
const NS = 'http://www.w3.org/2000/svg';
export function point(index, score, radius=147) {
  const angle=-Math.PI/2+index*Math.PI*2/7;
  return [260+Math.cos(angle)*radius*score/100,223+Math.sin(angle)*radius*score/100];
}
export const polygonPoints = score => Array.from({length:7},(_,i)=>point(i,score).join(',')).join(' ');
export function skeletonMarkup(id) {
  const rings=[25,50,75,100].map(score=>`<polygon points="${polygonPoints(score)}"/>`).join('');
  const axes=Array.from({length:7},(_,i)=>{const [x,y]=point(i,100);return `<line x1="260" y1="223" x2="${x}" y2="${y}"/>`;}).join('');
  const labels=Array.from({length:7},(_,i)=>{const [x,y]=point(i,100,188),left=i===0?x-31:i<=3?x:x-62;return `<rect x="${left}" y="${y-12}" width="62" height="9" rx="4"/><rect x="${left}" y="${y+6}" width="27" height="7" rx="3"/>`;}).join('');
  return `<svg class="qr-skeleton" viewBox="0 0 520 440" aria-hidden="true"><defs><linearGradient id="${id}-light"><stop class="qr-stop-base"/><stop offset=".48" class="qr-stop-base"/><stop offset=".64" class="qr-stop-light"/><stop offset=".8" class="qr-stop-base"/><stop offset="1" class="qr-stop-base"/></linearGradient><mask id="${id}-mask"><g fill="none" stroke="white" stroke-width="2">${rings}${axes}</g><g fill="white">${labels}</g></mask></defs><g mask="url(#${id}-mask)"><rect class="qr-skeleton-base" width="520" height="440"/><rect class="qr-sweep" x="-1040" width="1560" height="440" fill="url(#${id}-light)"/></g></svg>`;
}
export function createRadar(svg, onSelect) {
  const doc=svg.ownerDocument;
  const el=(tag,attrs={})=>{const node=doc.createElementNS(NS,tag);for(const [key,value] of Object.entries(attrs))node.setAttribute(key,value);return node;};
  const grid=el('g',{class:'qr-grid'});
  for(const score of [25,50,75,100]) {
    grid.append(el('polygon',{points:polygonPoints(score)}));
    const label=el('text',{x:267,y:223-147*score/100+4,class:'qr-grid-value'});
    label.textContent=score;grid.append(label);
  }
  for(let i=0;i<7;i++){const [x,y]=point(i,100);grid.append(el('line',{x1:260,y1:223,x2:x,y2:y}));}
  const shape=el('polygon',{class:'qr-shape'});
  svg.append(grid,shape);
  const dots=[],values=[],labels=[];
  for(let i=0;i<7;i++){
    const dot=el('circle',{r:4,class:'qr-point'});
    const [x,y]=point(i,100,188),anchor=i===0?'middle':i<=3?'start':'end';
    const group=el('g',{class:'qr-label'});
    const name=el('text',{x,y:y-5,'text-anchor':anchor});
    const value=el('text',{x,y:y+13,'text-anchor':anchor,class:'qr-axis-value'});
    group.append(name,value);group.addEventListener('click',()=>onSelect(i));
    svg.append(dot,group);dots.push(dot);values.push(value);labels.push(name);
  }
  return {draw(categories,progress=1) {
    const health=categories.map(c=>healthFromConcern(c.concern));
    shape.style.display=health.some(value=>value===null)?'none':'';
    const points=health.map((value,i)=>point(i,(value??0)*progress));
    shape.setAttribute('points',points.map(p=>p.join(',')).join(' '));
    categories.forEach((category,i)=>{
      labels[i].textContent=category.name;
      values[i].textContent=health[i]===null?'—':formatNumber(health[i]*progress,1);
      dots[i].style.display=health[i]===null?'none':'';
      dots[i].setAttribute('cx',points[i][0]);dots[i].setAttribute('cy',points[i][1]);
    });
  }};
}


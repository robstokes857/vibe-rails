import {themeFromCss} from './vendor/atlas/code-atlas.mjs';

/** Read inherited VibeRails tokens after its own theme/VS Code bridge has run. */
export function readReportTheme(element){
  const theme=themeFromCss(element);
  // Keep domain connections legible in any host palette. Explicit graph tokens win.
  if(!element.ownerDocument.defaultView.getComputedStyle(element).getPropertyValue('--graph-cross-edge').trim()){
    theme.graph={...theme.graph,crossEdge:theme.colors.muted};
  }
  return theme;
}

/** Sync the isolated graph without remounting or changing camera/selection. */
export function observeReportTheme(element,onTheme,onError=()=>{}){
  const doc=element.ownerDocument,win=doc.defaultView;
  let frame=0,disposed=false,last='';
  function refresh(){
    frame=0;if(disposed||!element.isConnected)return;
    try{
      const theme=readReportTheme(element),key=JSON.stringify(theme);
      if(key===last)return;
      last=key;
      Promise.resolve(onTheme(theme)).catch(error=>{last='';if(!disposed)onError(error);});
    }catch(error){last='';onError(error);}
  }
  function schedule(){if(!disposed&&!frame)frame=win.requestAnimationFrame(refresh);}
  const ancestors=new win.MutationObserver(schedule);
  for(let current=element;current;current=current.parentElement)ancestors.observe(current,{attributes:true});
  // Also handle CSS custom-theme files/styles replaced by the host.
  const styles=new win.MutationObserver(schedule);
  styles.observe(doc.head,{childList:true,subtree:true,characterData:true,attributes:true,attributeFilter:['href','media','disabled']});
  doc.head.addEventListener('load',schedule,true);
  const preference=win.matchMedia('(prefers-color-scheme: dark)');
  preference.addEventListener('change',schedule);
  schedule();
  return {refresh:schedule,destroy(){disposed=true;win.cancelAnimationFrame(frame);ancestors.disconnect();styles.disconnect();doc.head.removeEventListener('load',schedule,true);preference.removeEventListener('change',schedule);}};
}

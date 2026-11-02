'use strict';

// The reading viewport keeps the activation point fixed while the pointer
// determines scrolling direction and speed. Nothing runs while it is idle.
function installReaderAutoScroll(reader) {
 let anchor=null, indicator=null, frame=0, previousTime=0, pointerY=0, dragged=false;
 const deadZone=12, maxSpeed=1800;
 const tick=time=>{
  frame=0;
  if(!anchor)return;
  const distance=pointerY-anchor.y;
  if(Math.abs(distance)<=deadZone){previousTime=0;return;}
  const maximum=Math.max(0,reader.scrollHeight-reader.clientHeight);
  if(distance<0&&reader.scrollTop<=0||distance>0&&reader.scrollTop>=maximum){previousTime=0;return;}
  const seconds=previousTime?Math.min((time-previousTime)/1000,.05):0;
  previousTime=time;
  const speed=Math.sign(distance)*Math.min(maxSpeed,(Math.abs(distance)-deadZone)*4);
  reader.scrollTop=Math.max(0,Math.min(maximum,reader.scrollTop+speed*seconds));
  if(distance<0&&reader.scrollTop>0||distance>0&&reader.scrollTop<maximum)frame=requestAnimationFrame(tick);
  else previousTime=0;
 };
 function move(event){
  pointerY=event.clientY;
  if(Math.hypot(event.clientX-anchor.x,event.clientY-anchor.y)>deadZone)dragged=true;
  if(!frame&&Math.abs(pointerY-anchor.y)>deadZone)frame=requestAnimationFrame(tick);
 }
 function up(event){if(event.button===1&&dragged)stop();}
 function down(event){
  event.preventDefault();event.stopPropagation();
  // Dismissing the anchor must not also activate the item beneath the click.
  const type=event.button===0?'click':event.button===1?'auxclick':'contextmenu';
  const swallow=click=>{click.preventDefault();click.stopImmediatePropagation();window.removeEventListener(type,swallow,true);};
  window.addEventListener(type,swallow,true);
  window.addEventListener('mouseup',()=>setTimeout(()=>window.removeEventListener(type,swallow,true),0),{capture:true,once:true});
  stop();
 }
 function key(event){if(event.key==='Escape'){event.preventDefault();event.stopPropagation();}stop();}
 function hidden(){if(document.hidden)stop();}
 function stop(){
  if(!anchor)return;
  anchor=null;previousTime=0;dragged=false;
  if(frame)cancelAnimationFrame(frame);frame=0;
  indicator.remove();indicator=null;
  window.removeEventListener('mousemove',move,true);window.removeEventListener('mouseup',up,true);
  window.removeEventListener('mousedown',down,true);window.removeEventListener('keydown',key,true);
  window.removeEventListener('wheel',stop,true);window.removeEventListener('blur',stop);
  document.removeEventListener('visibilitychange',hidden);
 }
 reader.addEventListener('mousedown',event=>{
  if(event.button!==1||event.defaultPrevented||reader.scrollHeight<=reader.clientHeight+1||
     event.target.closest('input,textarea,select,button,a,[contenteditable="true"]'))return;
  for(let node=event.target;node&&node!==reader;node=node.parentElement){
   const style=getComputedStyle(node);
   if((/(auto|scroll)/.test(style.overflowX)&&node.scrollWidth>node.clientWidth+1)||
      (/(auto|scroll)/.test(style.overflowY)&&node.scrollHeight>node.clientHeight+1))return;
  }
  event.preventDefault();
  anchor={x:event.clientX,y:event.clientY};pointerY=event.clientY;dragged=false;
  indicator=document.createElement('div');indicator.className='autoscroll-indicator';indicator.setAttribute('aria-hidden','true');
  indicator.style.left=Math.max(24,Math.min(innerWidth-24,anchor.x))+'px';
  indicator.style.top=Math.max(24,Math.min(innerHeight-24,anchor.y))+'px';
  indicator.innerHTML='<svg viewBox="0 0 48 48" aria-hidden="true"><path d="M18 17l6-6 6 6M18 31l6 6 6-6"/><circle cx="24" cy="24" r="2.6"/></svg>';
  document.body.append(indicator);
  window.addEventListener('mousemove',move,true);window.addEventListener('mouseup',up,true);
  window.addEventListener('mousedown',down,true);window.addEventListener('keydown',key,true);
  window.addEventListener('wheel',stop,{capture:true,passive:true});window.addEventListener('blur',stop);
  document.addEventListener('visibilitychange',hidden);
 });
 return stop;
}

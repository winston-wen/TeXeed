'use strict';
function lastSend(c){return Math.max(0,...[c.lastSentAt,...c.turns.map(t=>t.sentAt||t.createdAt)].filter(Boolean).map(x=>Date.parse(x)||0));}
function recentConversations(all){return all.filter(c=>lastSend(c)>0).sort((a,b)=>lastSend(b)-lastSend(a)).slice(0,5);}

class HistoryPage {
 constructor(getConversations){this.getConversations=getConversations;this.selected=new Set();this.matches=[];this.first=0;this.capacity=1;}
 refresh(query='',reset=false){const anchor=reset?null:this.matches[this.first]?.id,q=query.trim().toLowerCase(),all=this.getConversations();this.matches=all.filter(c=>[c.title,...c.turns.map(t=>t.question+' '+t.answer)].join(' ').toLowerCase().includes(q)).sort((a,b)=>lastSend(b)-lastSend(a)||new Date(b.updatedAt||0)-new Date(a.updatedAt||0));this.selected=new Set([...this.selected].filter(id=>all.some(c=>c.id===id)));const index=this.matches.findIndex(c=>c.id===anchor);this.first=reset?0:Math.max(0,Math.min(index>=0?index:this.first,this.matches.length-1));}
 resize(capacity){this.capacity=Math.max(1,capacity);}
 scroll(first){this.first=Math.max(0,Math.min(first,this.matches.length-1));}
 get pages(){return Math.max(1,Math.ceil(this.matches.length/this.capacity));}
 get maximum(){return Math.max(this.first,(this.pages-1)*this.capacity);}
 get page(){return Math.floor(this.first/this.capacity)+1;}
 goto(page){this.scroll((Math.max(1,Math.min(page,this.pages))-1)*this.capacity);}
 get visible(){return this.matches.slice(this.first,this.first+this.capacity);}
}
if(typeof module!=='undefined')module.exports={HistoryPage,lastSend,recentConversations};
function createHistoryManager({getConversations,getActive,isOpen,rename,remove,open}){
 const d=document.getElementById('history'),el=id=>document.getElementById(id),model=new HistoryPage(getConversations);
 let editing=null,timer=null;
 function selection(){el('history-selected').textContent='已选中 '+model.selected.size+' 条';el('history-delete').disabled=!model.selected.size;}
 function render(){
  if(editing)return;
  for(const id of model.selected)if(isOpen(id))model.selected.delete(id);
  el('history-list').replaceChildren();
  for(const c of model.visible){
   const row=document.createElement('div');row.className='history-row'+(c.id===getActive()?' active':'');row.dataset.id=c.id;
   const check=document.createElement('input');check.type='checkbox';check.disabled=isOpen(c.id);check.title=check.disabled?'会话窗口已打开，关闭后才能删除':'选择此会话';check.checked=model.selected.has(c.id);check.setAttribute('aria-label','选择对话：'+c.title);
   check.onchange=()=>{clearTimeout(timer);if(check.checked)model.selected.add(c.id);else model.selected.delete(c.id);selection();};
   const title=document.createElement('span');title.className='history-title';title.textContent=c.title;title.title=c.title;title.tabIndex=0;title.setAttribute('role','button');title.setAttribute('aria-label','打开对话：'+c.title);
   title.onclick=()=>{clearTimeout(timer);timer=setTimeout(()=>{if(!editing){open(c.id);}},500);};
   title.ondblclick=()=>{clearTimeout(timer);beginRename(c,row,title);};
   title.onkeydown=e=>{if(e.key==='F2'){e.preventDefault();beginRename(c,row,title);}else if(e.key==='Enter'){e.preventDefault();open(c.id);}};
   row.append(check,title);if(isOpen(c.id)){const badge=document.createElement('span');badge.className='history-open-status';badge.textContent='已打开';badge.title='会话窗口已打开，关闭后才能删除';row.append(badge);}el('history-list').append(row);
  }
  if(!model.matches.length){const empty=document.createElement('div');empty.className='muted';empty.textContent='没有匹配的对话';el('history-list').append(empty);}
  el('history-page').value=model.page;el('history-count').textContent='页，共 '+model.pages+' 页、'+model.matches.length+' 条';selection();
 }
 function refresh(reset=false){model.refresh(el('history-search').value,reset);render();}
 function beginRename(c,row,title){
  if(editing)return;clearTimeout(timer);
  const field=document.createElement('input');field.value=c.title;field.maxLength=200;field.className='history-title-editor';field.setAttribute('aria-label','修改历史标题');
  const accept=document.createElement('button');accept.textContent='✓';accept.setAttribute('aria-label','提交标题');
  title.hidden=true;row.append(field,accept);editing={c,field};
  const cancel=()=>{editing=null;render();};
  field.onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();commit();}else if(e.key==='Escape'){e.preventDefault();e.stopPropagation();cancel();}};
  accept.onclick=commit;field.focus();field.select();
 }
 function commit(){if(!editing)return true;const title=editing.field.value.trim();if(!title){editing.field.focus();return false;}rename(editing.c.id,title);editing=null;refresh();return true;}
 d.addEventListener('pointerdown',e=>{clearTimeout(timer);if(editing&&!editing.field.contains(e.target)){e.preventDefault();e.stopImmediatePropagation();commit();}},true);
 el('history-search').oninput=()=>{clearTimeout(timer);if(commit())refresh(true);};
 el('history-search-button').onclick=()=>{if(commit())refresh(true);};
 el('history-refresh').onclick=()=>{if(commit())refresh();};
 el('history-all').onclick=()=>{clearTimeout(timer);model.matches.filter(c=>!isOpen(c.id)).forEach(c=>model.selected.add(c.id));render();};
 el('history-clear').onclick=()=>{clearTimeout(timer);model.selected.clear();render();};
 el('history-delete').onclick=()=>{clearTimeout(timer);try{remove([...model.selected]);refresh();}catch(error){el('history-selected').textContent=error.message;}};
 el('history-go').onclick=()=>{clearTimeout(timer);const n=Number(el('history-page').value);if(Number.isInteger(n))model.goto(n);render();};
 el('history-page').onkeydown=e=>{if(e.key==='Enter'){e.preventDefault();el('history-go').click();}};
 el('history-viewport').addEventListener('wheel',e=>{if(editing)return;e.preventDefault();clearTimeout(timer);model.goto(model.page+Math.sign(e.deltaY));render();},{passive:false});
 el('history-prev').onclick=()=>{if(commit()){model.goto(model.page-1);render();}};
 el('history-next').onclick=()=>{if(commit()){model.goto(model.page+1);render();}};
 const observer=new ResizeObserver(()=>{if(d.hidden)return;model.resize(Math.floor(el('history-viewport').clientHeight/36));render();});
 observer.observe(el('history-viewport'));
 return {open(){refresh();model.resize(Math.floor(el('history-viewport').clientHeight/36));render();el('history-search').focus();},refresh,commit,model};
}
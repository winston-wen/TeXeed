
'use strict';
const DATA=/*__TRANSCRIPTS__*/;
const $=id=>document.getElementById(id);
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const KEY='texeed-ai-baseline-v02'+(location.search.includes('test=1')?'-test':'');
const clone=x=>JSON.parse(JSON.stringify(x));
const BUNDLED_TURNS=[...DATA.turns,...DATA.sampleConversations.flatMap(c=>c.turns)];
function fresh(){return {sampleHistoryVersion:1,active:'collected',conversations:[{id:'collected',title:DATA.title,updatedAt:DATA.updatedAt,turns:clone(DATA.turns),entries:clone(DATA.entries),next:DATA.next,draft:DATA.draft,permission:'完全访问'},...clone(DATA.sampleConversations)],events:[]};}
let state;try{state=JSON.parse(localStorage.getItem(KEY)||'null');}catch{}if(!state?.conversations?.length)state=fresh();
// Seed once: preserve the active conversation and drafts, and respect later deletions.
if(!state.sampleHistoryVersion){for(const c of DATA.sampleConversations)if(!state.conversations.some(existing=>existing.id===c.id))state.conversations.push(clone(c));state.sampleHistoryVersion=1;}
let running=null,editing=null,context=null,marking=false,timers=[],matchIndex=0,matches=[];
function current(){return state.conversations.find(c=>c.id===state.active)||state.conversations[0];}
function persist(){try{localStorage.setItem(KEY,JSON.stringify(state,function(key,value){return key==='answerHtml'&&[...BUNDLED_TURNS,DATA.playback].some(t=>t.answer===this.answer)?undefined:value;}));}catch{status('浏览器不允许持久保存；本次页面内仍可试用。');}}
function record(action,detail={}){state.events.push({at:new Date().toISOString(),action,...detail});state.events=state.events.slice(-250);persist();}
function status(text){$('status').textContent=text;$('status').hidden=!text;}
function time(value,short=false){if(!value)return '';const d=new Date(value);const parts=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}).format(d);return short?parts.slice(-8):parts.replace(' ',' (+8) ');}
function scrollEnd(){$('reader').scrollTop=$('reader').scrollHeight;}
function svgHtml(turn){
 if(turn.answerHtml)return turn.answerHtml;
 const bundled=BUNDLED_TURNS.find(t=>t.id===turn.id&&t.answer===turn.answer);if(bundled)return bundled.answerHtml;
 const source=[DATA.playback,...BUNDLED_TURNS].find(t=>t.answer===turn.answer);
 return source?source.answerHtml.replace(/id="([^"]+)"/g,(_,id)=>'id="'+turn.id+'-'+id+'"').replace(/data-block="([^"]+)"/g,(_,id)=>'data-block="'+turn.id+'-'+id+'"').replace(/href="#([^"]+)"/g,(_,id)=>'href="#'+turn.id+'-'+id+'"'):'';
}
function activity(s){return '<details class="activity '+(!s.details?'no-details':'')+'"><summary><span>'+esc(s.text)+'</span><time>'+time(s.time,true)+'</time></summary>'+(s.details?'<pre>'+esc(s.details)+'</pre>':'')+'</details>';}
const ICON_CLOSE='<svg class="icon" viewBox="0 0 20 20"><path d="M5 5L15 15M15 5L5 15"/></svg>';
const ICON_REFILL='<svg class="icon" viewBox="0 0 20 20"><path d="M8 4L4 8L8 12M4 8H12A4 4 0 0 1 12 16"/></svg>';
const ICON_SEND='<svg class="icon" viewBox="0 0 20 20"><path d="M10 17V3M4 9L10 3L16 9"/></svg>';
let restoring=false,scrollTimer;
function readingPosition(){const area=$('reader'),top=area.getBoundingClientRect().top;const nodes=[...area.querySelectorAll('.passage,.question')].filter(n=>n.getClientRects().length);const anchor=nodes.find(n=>n.getBoundingClientRect().bottom>top+2);return {top:area.scrollTop,anchor:anchor?.id||anchor?.closest('.turn')?.id,offset:anchor?anchor.getBoundingClientRect().top-top:0};}
function saveReading(){if(!restoring){current().reading=readingPosition();persist();}}
function restoreReading(pos){const area=$('reader');restoring=true;area.scrollTop=pos?.top??area.scrollHeight;if(pos?.anchor){const anchor=readingAnchor(pos.anchor);if(anchor?.getClientRects().length)area.scrollTop+=anchor.getBoundingClientRect().top-area.getBoundingClientRect().top-pos.offset;}requestAnimationFrame(()=>{restoring=false;updateReadingContext();positionEditor();});}
function questionHtml(t){if(!t.annotations?.length)return esc(t.question);return esc(t.prompt||'')+'<div class="sent-summaries">'+t.annotations.map(e=>'<span class="sent-summary" title="'+esc(e.question)+'">#'+e.number+' · '+esc(e.summary)+'</span>').join('')+'</div><details class="snapshot-view"><summary><svg class="snapshot-chevron" viewBox="0 0 20 20" aria-hidden="true"><path d="M7 4L13 10L7 16"/></svg><span>查看发送原件</span></summary><div class="snapshot-scroll" tabindex="0" role="region" aria-label="发送原件内容">'+t.annotations.map(e=>'<div><strong>#'+e.number+' '+esc(e.question)+'</strong>'+contextHtml({...e,state:'发送时快照'})+'</div>').join('')+'</div></details>';}
function renderTurns(){
 const pos=$('reader').scrollTop;
 $('thread').innerHTML=current().turns.length?current().turns.map(t=>'<article class="turn" id="'+t.id+'"><div class="turn-tools"><div class="timestamp"><span>发送时间：'+(time(t.sentAt)||'样例，无实际发送时间')+'</span>'+(t.collapsed&&t.answeredAt?'<span>答复时间：'+time(t.answeredAt)+'</span>':'')+'</div><button data-refill="'+t.id+'" title="将问题重新放入编辑栏" aria-label="将问题重新放入编辑栏">'+ICON_REFILL+'</button><button data-delete-turn="'+t.id+'" title="删除此轮问题和回答" aria-label="删除此轮问题和回答">'+ICON_CLOSE+'</button></div><div class="question" id="'+t.id+'-question">'+questionHtml(t)+'</div><div class="turn-controls"><button data-process="'+t.id+'" aria-expanded="'+!t.processCollapsed+'">'+(t.processCollapsed?'展开过程':'收起过程')+'</button><button data-collapse="'+t.id+'" aria-expanded="'+!t.collapsed+'">'+(t.collapsed?'展开全部':'收起全部')+'</button></div><div class="segments" '+(t.collapsed?'hidden':'')+'><div class="process" '+(t.processCollapsed?'hidden':'')+'>'+(t.segments||[]).filter(s=>s.kind==='activity'&&!s.text.startsWith('公式排版检查通过')).map(activity).join('')+'</div>'+(svgHtml(t)?'<div class="final-label">答复</div><div class="answer" data-turn="'+t.id+'">'+svgHtml(t)+'</div>':'')+'<div class="process" '+(t.processCollapsed?'hidden':'')+'>'+(t.segments||[]).filter(s=>s.kind==='activity'&&s.text.startsWith('公式排版检查通过')).map(activity).join('')+'</div></div>'+(t.answeredAt&&!t.collapsed?'<div class="answered">答复时间：'+time(t.answeredAt)+'</div>':'')+'</article>').join(''):'<div class="empty"><h2>AI 对话</h2><p>从一个问题，或一处圈点开始。</p></div>';
 enhanceAnswers();$('reader').scrollTop=pos;renderBubbles();requestAnimationFrame(updateReadingContext);
}
function enhanceAnswers(){
 document.querySelectorAll('.math').forEach(m=>m.removeAttribute('tabindex'));
 document.querySelectorAll('.answer code').forEach(code=>{const text=code.textContent.trim();if(/^(?:main\.tex|qa\.md)(?::\d+(?:[–—-]\d+)?)?$/.test(text)){const b=document.createElement('button');b.className='file-link';b.dataset.file=text;b.textContent=text;b.title='打开源码快照'+(text.includes(':')?'并跳转到行号':'');code.replaceChildren(b);}});
 document.querySelectorAll('.answer').forEach(answer=>{prepareReadingLayout(answer);const t=current().turns.find(t=>t.id===answer.dataset.turn);let hiding=[];for(const p of answer.querySelectorAll(':scope>.passage')){const h=p.querySelector('h2,h3,h4');if(h){const level=Number(h.tagName.slice(1));hiding=hiding.filter(x=>x<level);p.hidden=!!hiding.length||p.dataset.readingHidden==='true';const collapsed=!!t.sections?.[p.id],b=document.createElement('button');b.className='heading-toggle';b.dataset.section=p.id;b.setAttribute('aria-expanded',String(!collapsed));b.innerHTML='<span class="heading-label">'+h.innerHTML+'</span>';h.replaceChildren(b);if(collapsed)hiding.push(level);}else p.hidden=!!hiding.length||p.dataset.readingHidden==='true';}});
}
function toggleTurn(t,field){const node=$(t.id),offset=node.getBoundingClientRect().top;t[field]=!t[field];if(field==='processCollapsed'&&!t[field])t.collapsed=false;renderTurns();$('reader').scrollTop+=$(t.id).getBoundingClientRect().top-offset;saveReading();}
function openSource(ref){const m=/^([^:]+)(?::(\d+)(?:[–—-](\d+))?)?$/.exec(ref),file=m[1],start=Number(m[2]||1),end=Number(m[3]||start);const text=DATA.sourceFiles[file];if(text===undefined){status('未收集此文件的源码。');return;}$('source-title').textContent=ref;$('source-lines').innerHTML=text.split('\n').map((line,i)=>'<div class="source-line '+(i+1>=start&&i+1<=end?'selected':'')+'" id="source-line-'+(i+1)+'"><span class="source-number">'+(i+1)+'</span><span class="source-code">'+esc(line||' ')+'</span></div>').join('');$('source-dialog').showModal();requestAnimationFrame(()=>$('source-line-'+start)?.scrollIntoView({block:'center'}));}

function contextHtml(e){return (e.quote?'<div>'+esc(e.quote)+'</div>':'')+(e.sources||[]).map(s=>'<div>'+esc(s.file)+'</div><pre>'+esc(s.text)+'</pre>').join('')+(e.pdf||[]).map(p=>'<div>PDF 第 '+p.page+' 页</div><img alt="PDF 圈选区域" src="data:image/png;base64,'+p.image+'">').join('')+'<div class="muted">'+esc(e.state)+'</div>';}
function contextText(e){return e.quote||(e.sources||[]).map(s=>s.text).join('\n')||(e.pdf?.length?'PDF 圈选快照 · '+e.title:'');}
function renderEntries(){
 const scroll=$('entries').scrollTop;
 $('entries-button').textContent='圈点 · '+current().entries.length;
 $('entries').innerHTML=current().entries.map(e=>'<section class="entry" data-entry="'+e.id+'"><div class="entry-head"><input type="checkbox" aria-label="选择圈点 '+e.number+'" data-select="'+e.id+'" '+(e.selected?'checked':'')+(running?' disabled':'')+'><span class="entry-title">#'+e.number+' · '+esc(e.title)+'</span><button class="entry-remove" data-remove="'+e.id+'" aria-label="移除圈点 '+e.number+'" '+(running?'disabled':'')+'>×</button></div><button class="entry-context-preview '+(e.contextExpanded?'expanded':'')+'" data-preview="'+e.id+'" aria-expanded="'+!!e.contextExpanded+'" aria-label="圈点 '+e.number+' 原文；双击展开或收起">'+esc(contextText(e))+'</button>'+(e.pdf?.length?'<div class="context-body">'+e.pdf.map(p=>'<img alt="PDF 圈选快照" src="data:image/png;base64,'+p.image+'">').join('')+'</div>':'')+'<textarea class="entry-question" rows="2" data-question="'+e.id+'" aria-label="圈点 '+e.number+' 的单独说明" '+(running?'disabled':'')+'>'+esc(e.question)+'</textarea></section>').join('')||'<p class="muted">本次尚未添加圈点。开启 AI 圈点后选择回答内容。</p>';
 mountEditors($('entries'));$('entries').scrollTop=scroll;renderBubbles();
}

function renderBubbles(){document.querySelectorAll('.margin-notes').forEach(e=>e.replaceChildren());for(const e of current().entries){if(!e.block||['已完成','提交中'].includes(e.state))continue;let p=$(e.block);if(p?.dataset.joinedInto)p=$(p.dataset.joinedInto);if(!p)continue;const b=document.createElement('button');b.className='bubble';b.textContent=e.number;b.dataset.bubble=e.id;b.setAttribute('aria-label','编辑批注 '+e.number);p.querySelector('.margin-notes').append(b);}}
function renderConversation(){closePopups();$('title').textContent=shortTitle(current().title);$('title').title=shortTitle(current().title);$('composer').value=current().draft;$('permission').value=current().permission||'完全访问';restoring=true;renderTurns();renderEntries();refreshEditors();status('');requestAnimationFrame(()=>restoreReading(current().reading));}

function removeEntry(id){const e=current().entries.find(e=>e.id===id);if(!e||e.state==='提交中')return;current().entries=current().entries.filter(e=>e.id!==id);if(editing===id){editing=null;$('annotation-editor').hidden=true;}renderEntries();record('remove-entry',{number:e.number});}
function serialize(node){if(node.nodeType===3)return node.nodeValue;if(node.nodeType!==1&&node.nodeType!==11)return '';if(node.nodeType===1){if(node.matches('.margin-notes,.equation-number'))return '';if(node.matches('.math'))return node.dataset.display==='true'?'$$\n'+node.dataset.tex+'$$\n':' $'+node.dataset.tex+'$ ';if(node.matches('svg'))return '';}let text=Array.from(node.childNodes).map(serialize).join('');if(node.nodeType===1&&node.matches('p,li,h3,h4,blockquote,.equation,.passage'))text+='\n';return text;}
function targetContext(target){const p=target.closest('.passage'),answer=target.closest('.answer');if(!p||!answer)return null;const sel=window.getSelection();let quote='',selected=false;if(sel?.rangeCount&&!sel.isCollapsed){const r=sel.getRangeAt(0),start=r.startContainer.nodeType===1?r.startContainer:r.startContainer.parentElement;if(start.closest('.answer')===answer){quote=serialize(r.cloneContents());selected=true;}}if(!quote.trim())quote=serialize(p.querySelector('.passage-body')).trim();return {block:p.id,quote,selected,turn:answer.dataset.turn};}
function addAnnotation(ctx){if(!ctx||!ctx.quote.trim())return;const c=current();const e={id:'a'+Date.now(),number:c.next++,title:'AI 回答批注',question:'',selected:true,state:'待提交',sources:[],pdf:[],block:ctx.block,quote:ctx.quote};c.entries.push(e);renderEntries();record('add-answer-annotation',{number:e.number,quote:e.quote});openEditor(e.id);}
function openEditor(id){const e=current().entries.find(e=>e.id===id);if(!e)return;editing=id;$('annotation-title').innerHTML=esc('#'+e.number+' · '+e.title)+'<span class="anchor-state"></span>';$('annotation-input').value=e.question;$('annotation-editor').hidden=false;refreshEditors();positionEditor();$('annotation-input').focus({preventScroll:true});}
function positionEditor(){if(!editing)return;const e=current().entries.find(e=>e.id===editing),p=e&&readingAnchor(e.block),box=$('annotation-editor');if(!e)return;const area=$('reader').getBoundingClientRect(),r=p?.getBoundingClientRect();box.hidden=false;const visible=r&&p.getClientRects().length&&r.bottom>=area.top&&r.top<=area.bottom;const label=$('annotation-title').querySelector('.anchor-state');if(label)label.textContent=visible?'':'原位置在视野外';box.style.left=Math.max(12,Math.min((r?.right||area.right)-28,innerWidth-box.offsetWidth-12))+'px';box.style.top=Math.max(area.top,Math.min(r?.top+28||area.top,area.bottom-box.offsetHeight-8))+'px';}

function closePopups(){for(const id of ['options','answer-menu','history','annotation-editor'])$(id).hidden=true;editing=null;}
function placePopup(id,anchor,above=false){const el=$(id);el.hidden=false;const r=anchor.getBoundingClientRect();el.style.left=Math.max(6,Math.min(r.left,innerWidth-el.offsetWidth-8))+'px';el.style.top=Math.max(6,Math.min(above?r.top-el.offsetHeight:r.bottom+4,innerHeight-el.offsetHeight-8))+'px';}
function setBusy(value){$('send').hidden=value;$('stop').hidden=!value;for(const id of ['permission','model','effort'])$(id).disabled=value;renderEntries();}
function send(entryId=null){
 if(running)return;const c=current(),prompt=entryId?'':$('composer').value.trim(),selected=c.entries.filter(e=>entryId?e.id===entryId:e.selected);
 if(!selected.length&&!prompt){status('请输入问题，或填写并勾选条目。');return;}
 if(!prompt&&selected.some(e=>!e.question.trim())){status('请在编辑栏描述要问的问题。');$('composer').focus();return;}
 if(selected.length>20){status('每批最多 20 个问题，请分批提交。');return;}
 closePopups();c.updatedAt=new Date().toISOString();const question=[prompt,...selected.map(e=>'#'+e.number+' · '+e.title+'：'+e.question)].filter(Boolean).join('\n');
 const annotations=clone(selected).map(e=>({...e,summary:summarizeSnapshot(e),block:null}));
 const t={id:'sim-'+Date.now(),question,prompt,annotations,request:{prompt,annotations:clone(selected),displayInstructions:DATA.prompts.answer,titleInstructions:DATA.prompts.title},answer:'',answerHtml:'',sentAt:new Date().toISOString(),segments:[{kind:'activity',text:'原型回放 · 以下为现编 transcript，不调用实时模型。',details:'',time:new Date().toISOString()}]};
 c.turns.push(t);if(c.title==='新对话')c.title=shortTitle(question);$('title').textContent=c.title;if(!entryId){c.draft='';$('composer').value='';refreshEditors();}if($('entries-dialog').open)$('entries-dialog').close();selected.forEach(e=>e.state='提交中');running={turn:t,selected};status('');setBusy(true);renderTurns();scrollEnd();record('send',{question,entries:selected.map(e=>e.number)});
 const mode=$('playback-mode').value;
 const finish=()=>{
  if(!running)return;const follow=$('reader').scrollHeight-$('reader').scrollTop-$('reader').clientHeight<40;
  if(mode==='failure'){selected.forEach(e=>e.state='未完成，可重试');t.segments.push({kind:'activity',text:'本轮未完成：网络连接失败。',details:'原型预编失败事件。',time:new Date().toISOString()});}
  else {t.answer=DATA.playback.answer;t.answerHtml=DATA.playback.answerHtml.replace(/id="([^"]+)"/g,(_,id)=>'id="'+t.id+'-'+id+'"').replace(/data-block="([^"]+)"/g,(_,id)=>'data-block="'+t.id+'-'+id+'"').replace(/href="#([^"]+)"/g,(_,id)=>'href="#'+t.id+'-'+id+'"');t.answeredAt=new Date().toISOString();c.entries=c.entries.filter(e=>!selected.some(sent=>sent.id===e.id));t.segments.push({kind:'activity',text:'公式排版检查通过 · 回放完成',time:t.answeredAt});}
  running=null;setBusy(false);renderTurns();if(follow)scrollEnd();record('finish',{mode});
 };
 if(mode==='repair')timers.push(setTimeout(()=>{if(running){t.segments.push({kind:'activity',text:'正在校对公式 · 第 1 次自动修正',details:'现编回放：公式定界符未闭合，已请求修正。',time:new Date().toISOString()});renderTurns();}},900));
 timers.push(setTimeout(finish,mode==='repair'?3200:1700));
}
function stop(){if(!running)return;timers.forEach(clearTimeout);timers=[];running.selected.forEach(e=>e.state='未完成，可重试');running.turn.segments.push({kind:'activity',text:'已停止。已完成的修改保留；可继续提问。',time:new Date().toISOString()});running=null;setBusy(false);renderTurns();status('已停止；未完成的条目可重新提交。');record('stop');}
function relativeTime(c){if(c.sample&&!c.updatedAt)return '样例';const minutes=Math.max(0,(Date.now()-Date.parse(c.updatedAt||new Date().toISOString()))/60000);return minutes<1?'刚刚':minutes<60?Math.floor(minutes)+' 分钟':minutes<1440?Math.floor(minutes/60)+' 小时':Math.floor(minutes/1440)+' 天';}
function history(){const query=$('history-search').value.toLowerCase();$('history-list').innerHTML=state.conversations.filter(c=>[c.title,...c.turns.map(t=>t.question+' '+t.answer)].join(' ').toLowerCase().includes(query)).map(c=>'<div class="history-row '+(c.id===state.active?'active':'')+'"><button class="history-open" data-history="'+c.id+'">'+esc(c.title)+'</button><small>'+relativeTime(c)+'</small><button data-delete-history="'+c.id+'" aria-label="删除对话">×</button></div>').join('')||'<div class="muted">没有匹配的对话</div>';}
function newConversation(){saveReading();stop();const c={id:'c'+Date.now(),title:'新对话',turns:[],entries:[],next:1,draft:'',permission:'完全访问'};state.conversations.push(c);state.active=c.id;renderConversation();record('new-conversation');$('composer').focus();}
function search(){const q=$('search-input').value.trim().toLowerCase();matches=q?current().turns.filter(t=>(t.question+' '+t.answer).toLowerCase().includes(q)):[];matchIndex=0;showMatch();}
function showMatch(){if(!matches.length){$('search-count').textContent='0';return;}matchIndex=(matchIndex+matches.length)%matches.length;$('search-count').textContent=(matchIndex+1)+'/'+matches.length;$(matches[matchIndex].id)?.scrollIntoView({block:'start'});}
function showSettings(){closePopups();$('settings').hidden=false;$('settings-tab').hidden=false;$('chat').hidden=true;$('settings-effort').value=$('effort').value;}
function showChat(){$('settings').hidden=true;$('settings-tab').hidden=true;$('chat').hidden=false;$('closed').hidden=true;$('chat-tab').hidden=false;}
function openLab(){$('provenance').textContent=DATA.provenance;$('lab-transcripts').textContent='收集的真实问题：\n'+DATA.turns.map(t=>t.question).join('\n\n')+'\n\n现编续答：\n'+DATA.playback.answer;$('lab-events').textContent=JSON.stringify(state.events,null,2);$('lab').showModal();}
async function copy(text){try{await navigator.clipboard.writeText(text);}catch{$('copy-text').value=text;$('copy-dialog').showModal();$('copy-text').select();}closePopups();}
function mdMath(text){return text.replace(/\\\[([\s\S]*?)\\\]/g,(_,s)=>'$$\n'+s.trim()+'$$\n').replace(/\\\(([\s\S]*?)\\\)/g,(_,s)=>' $'+s.trim()+'$ ');}
$('entries').addEventListener('input',e=>{const id=e.target.dataset.question;if(id){const n=current().entries.find(e=>e.id===id);n.question=e.target.value;if(editing===id)$('annotation-input').value=n.question;persist();}});
$('entries').addEventListener('change',e=>{const id=e.target.dataset.select;if(id){current().entries.find(e=>e.id===id).selected=e.target.checked;record('select-entry',{id,selected:e.target.checked});}});
$('entries').addEventListener('click',e=>{const b=e.target.closest('[data-remove]');if(b)removeEntry(b.dataset.remove);});
$('annotation-input').oninput=()=>{const e=current().entries.find(e=>e.id===editing);if(!e)return;e.question=$('annotation-input').value;const field=$('entries').querySelector('[data-question="'+e.id+'"]');if(field)field.value=e.question;persist();};
$('collapse-annotation').onclick=()=>{record('collapse-annotation',{id:editing});editing=null;$('annotation-editor').hidden=true;};
$('delete-annotation').onclick=()=>removeEntry(editing);
$('thread').addEventListener('contextmenu',e=>{const ctx=targetContext(e.target);if(!ctx)return;e.preventDefault();context=ctx;closePopups();const menu=$('answer-menu');menu.hidden=false;menu.style.left=Math.min(e.clientX,innerWidth-menu.offsetWidth-6)+'px';menu.style.top=Math.min(e.clientY,innerHeight-menu.offsetHeight-6)+'px';$('copy-selection').disabled=!ctx.selected;});
$('thread').addEventListener('mouseup',e=>{if(marking&&e.button===0&&!e.target.closest('button,a,input,textarea')&&!running){const ctx=targetContext(e.target);setTimeout(()=>addAnnotation(ctx),0);}});
$('thread').addEventListener('click',e=>{const b=e.target.closest('button');if(!b)return;if(b.dataset.bubble)openEditor(b.dataset.bubble);else if(b.dataset.refill){const t=current().turns.find(t=>t.id===b.dataset.refill);current().draft=[current().draft,t.question].filter(Boolean).join('\n');$('composer').value=current().draft;refreshEditors();$('composer').focus();record('refill-question');}else if(b.dataset.deleteTurn&&!running){current().turns=current().turns.filter(t=>t.id!==b.dataset.deleteTurn);renderTurns();record('delete-turn');}});
$('annotate').onclick=()=>{const ctx=context;closePopups();addAnnotation(ctx);};$('copy-selection').onclick=()=>context&&copy(context.quote);$('copy-answer').onclick=()=>{const t=current().turns.find(t=>t.id===context?.turn);if(t)copy(mdMath(t.answer));};
$('reader').onscroll=()=>{updateReadingContext();positionEditor();$('answer-menu').hidden=true;if(!restoring){current().reading=readingPosition();clearTimeout(scrollTimer);scrollTimer=setTimeout(persist,160);}};
window.addEventListener('pagehide',saveReading);
$('composer').oninput=()=>{current().draft=$('composer').value;persist();};$('composer').onkeydown=e=>{if(e.key==='Enter'&&e.ctrlKey&&!e.shiftKey&&!e.altKey){e.preventDefault();send();}};
$('send').onclick=()=>send();$('stop').onclick=stop;
$('options-button').onclick=()=>{const was=$('options').hidden;closePopups();if(was)placePopup('options',$('options-button'),true);};
$('options').onclick=e=>{const op=e.target.dataset.option;if(!op)return;closePopups();if(op==='add'){const c=current();c.entries.push({id:'e'+Date.now(),number:c.next++,title:'问题',question:'',selected:true,state:'待提交',sources:[],pdf:[],quote:''});renderEntries();record('add-entry');$('entries-dialog').showModal();refreshEditors();}if(op==='all'){current().entries.forEach(e=>e.selected=true);renderEntries();record('select-all');}if(op==='clear'){current().entries=current().entries.filter(e=>e.state!=='已完成');renderEntries();record('clear-done');}if(op==='settings')showSettings();if(op==='refresh')status('原型未连接账号；模型列表保持截图中的 GPT-6.1-Sol。');};
$('permission').onchange=()=>{current().permission=$('permission').value;record('permission',{value:current().permission});};
$('effort').onchange=()=>record('effort',{value:$('effort').value});
$('settings-effort').onchange=()=>{$('effort').value=$('settings-effort').value;};
$('settings-button').onclick=showSettings;$('settings-tab').onclick=showChat;$('chat-tab').onclick=e=>{if(!e.target.closest('button'))showChat();};
document.querySelectorAll('[data-setting]').forEach(b=>b.onclick=()=>{$('settings').querySelector('.note').textContent='此 HTML 原型未接入账号、网络或正式应用设置。';});
$('search-button').onclick=()=>{$('search').hidden=!$('search').hidden;if(!$('search').hidden)$('search-input').focus();};$('search-input').oninput=search;$('search-prev').onclick=()=>{matchIndex--;showMatch();};$('search-next').onclick=()=>{matchIndex++;showMatch();};
$('history-button').onclick=()=>{const was=$('history').hidden;closePopups();if(was){$('history-search').value='';history();placePopup('history',$('history-button'));$('history-search').focus();}};
$('history-search').oninput=history;$('history-list').onclick=e=>{const b=e.target.closest('button');if(!b)return;if(b.dataset.history){saveReading();stop();state.active=b.dataset.history;renderConversation();record('open-conversation');}if(b.dataset.deleteHistory){stop();state.conversations=state.conversations.filter(c=>c.id!==b.dataset.deleteHistory);if(!state.conversations.length)newConversation();else{if(state.active===b.dataset.deleteHistory)state.active=state.conversations[0].id;renderConversation();persist();}}};
$('new-button').onclick=newConversation;$('close-chat').onclick=()=>{stop();$('chat').hidden=true;$('chat-tab').hidden=true;$('closed').hidden=false;};$('reopen').onclick=showChat;
document.addEventListener('pointerdown',e=>{if(!e.target.closest('#options,#options-button'))$('options').hidden=true;if(!e.target.closest('#history,#history-button'))$('history').hidden=true;if(!e.target.closest('#answer-menu'))$('answer-menu').hidden=true;});
document.addEventListener('keydown',e=>{if(e.ctrlKey&&e.altKey&&e.key.toLowerCase()==='t'){e.preventDefault();openLab();}if(e.ctrlKey&&!e.altKey&&e.key.toLowerCase()==='f'){e.preventDefault();$('search').hidden=false;$('search-input').focus();}if(e.key==='Escape'){setMarking(false);closePopups();$('search').hidden=true;}});
document.addEventListener('copy',e=>{if(e.target.closest('textarea,input'))return;const sel=window.getSelection();if(!sel?.rangeCount||sel.isCollapsed)return;const r=sel.getRangeAt(0),start=r.startContainer.nodeType===1?r.startContainer:r.startContainer.parentElement;if(!start.closest('.answer'))return;e.clipboardData.setData('text/plain',serialize(r.cloneContents()));e.preventDefault();});
function setMarking(value){marking=value;$('marking').checked=value;$('mark-button').setAttribute('aria-pressed',String(value));record('external-marking',{enabled:value});}
$('marking').onchange=()=>setMarking($('marking').checked);$('mark-button').onclick=()=>setMarking(!marking);
$('restore').onclick=()=>{stop();state=fresh();migrateSnapshots();persist();$('lab').close();showChat();renderConversation();requestAnimationFrame(()=>$('entries').scrollTop=$('entries').scrollHeight);};
$('close-lab').onclick=()=>$('lab').close();$('close-copy').onclick=()=>$('copy-dialog').close();
$('export').onclick=()=>{const blob=new Blob([JSON.stringify({basis:DATA.basis,provenance:DATA.provenance,scripted:DATA.playback,...state},null,2)],{type:'application/json'});const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='texeed-baseline-transcript.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};

// Text inputs share the source editor font, wrapping, logical line numbers and shortcuts.
function mountEditors(root){for(const field of root.querySelectorAll('textarea')){if(field.closest('.code-editor'))continue;const box=document.createElement('div');box.className='code-editor';field.before(box);const gutter=document.createElement('div');gutter.className='line-numbers';gutter.setAttribute('aria-hidden','true');gutter.innerHTML='<div></div>';box.append(gutter,field);field.wrap='soft';field.spellcheck=false;
 if(field.dataset.question||field.id==='annotation-input'){const b=document.createElement('button');b.type='button';b.className='editor-action';b.title='发送此条 · Ctrl+Enter';b.setAttribute('aria-label',field.dataset.question?'发送圈点 '+current().entries.find(e=>e.id===field.dataset.question)?.number:'发送此条批注');b.innerHTML=ICON_SEND;b.onclick=()=>send(field.dataset.question||editing);box.append(b);}
 field.addEventListener('input',()=>updateEditor(field));field.addEventListener('scroll',()=>{gutter.firstElementChild.style.transform='translateY(-'+field.scrollTop+'px)';});
 field.addEventListener('keydown',event=>{if(event.key==='Enter'&&event.ctrlKey&&(field.dataset.question||field.id==='annotation-input')){event.preventDefault();send(field.dataset.question||editing);}else if(event.key==='Tab'){event.preventDefault();field.setRangeText('    ',field.selectionStart,field.selectionEnd,'end');field.dispatchEvent(new Event('input',{bubbles:true}));}});updateEditor(field);
 }}
function updateEditor(field){const box=field.closest('.code-editor');if(!box||!field.clientWidth)return;const style=getComputedStyle(field),measure=document.createElement('div');measure.className='editor-measure';measure.style.font=style.font;measure.style.lineHeight=style.lineHeight;measure.style.width=(field.clientWidth-parseFloat(style.paddingLeft)-parseFloat(style.paddingRight))+'px';measure.style.tabSize=style.tabSize;box.append(measure);const heights=field.value.split('\n').map(line=>{measure.textContent=line||' ';return Math.max(24,measure.getBoundingClientRect().height);});measure.remove();box.querySelector('.line-numbers>div').innerHTML=heights.map((height,i)=>'<span style="height:'+height+'px">'+(i+1)+'</span>').join('');box.querySelector('.line-numbers>div').style.transform='translateY(-'+field.scrollTop+'px)';}
function refreshEditors(){document.querySelectorAll('.code-editor textarea').forEach(updateEditor);}
function summarizeSnapshot(e){const text=contextText(e);if(/平均.*码长|平均信息量|1\.5/.test(text))return '平均信息量与编码长度';if(/碰撞|信息损失/.test(text))return '碰撞与信息损失';if(/Poisson|泊松|固定.*k/.test(text))return '泊松近似与极限条件';return e.summary||text.replace(/\s+/g,' ').slice(0,48)||e.question.trim().slice(0,48)||'圈选内容';}
function migrateSnapshots(){if(state.snapshotVersion)return;for(const c of state.conversations){for(const t of c.turns){const matched=c.entries.filter(e=>t.question.includes('#'+e.number+' ·')||t.question.includes(e.title+'：'));if(matched.length){t.prompt=t.question.split('\n').filter(line=>!matched.some(e=>line.includes(e.title+'：')||line.startsWith('#'+e.number+' ·'))).join('\n');t.annotations=clone(matched).map(e=>({...e,block:null,summary:DATA.entrySummaries[e.number]||e.question}));}}c.entries=c.entries.filter(e=>e.state!=='已完成');}state.snapshotVersion=1;}
$('entries-button').onclick=()=>{closePopups();renderEntries();$('entries-dialog').showModal();refreshEditors();};$('close-entries').onclick=()=>$('entries-dialog').close();$('close-source').onclick=()=>$('source-dialog').close();
$('entries').addEventListener('dblclick',event=>{const b=event.target.closest('[data-preview]');if(!b)return;const e=current().entries.find(e=>e.id===b.dataset.preview);e.contextExpanded=!e.contextExpanded;b.classList.toggle('expanded',e.contextExpanded);b.setAttribute('aria-expanded',String(e.contextExpanded));persist();});
$('entries').addEventListener('keydown',event=>{if(event.key==='Enter'&&event.target.dataset.preview){event.preventDefault();event.target.dispatchEvent(new MouseEvent('dblclick',{bubbles:true}));}});
let tooltipTimer;
$('entries').addEventListener('pointerover',event=>{const b=event.target.closest('[data-preview]');if(!b)return;clearTimeout(tooltipTimer);tooltipTimer=setTimeout(()=>{const tooltip=$('context-tooltip'),r=b.getBoundingClientRect();tooltip.textContent=b.textContent;tooltip.hidden=false;tooltip.style.left=Math.max(10,Math.min(r.left,innerWidth-tooltip.offsetWidth-10))+'px';tooltip.style.top=Math.max(10,Math.min(r.bottom+5,innerHeight-tooltip.offsetHeight-10))+'px';// Keep the tooltip in the modal top layer.
 if($('entries-dialog').open)$('entries-dialog').append(tooltip);},350);});
$('entries').addEventListener('pointerout',()=>{clearTimeout(tooltipTimer);$('context-tooltip').hidden=true;});
$('entries-dialog').addEventListener('close',()=>{clearTimeout(tooltipTimer);$('context-tooltip').hidden=true;document.body.append($('context-tooltip'));});
$('thread').addEventListener('click',event=>{const b=event.target.closest('button');if(!b)return;if(b.dataset.file){openSource(b.dataset.file);return;}const t=current().turns.find(t=>t.id===(b.dataset.process||b.dataset.collapse));if(t){toggleTurn(t,b.dataset.process?'processCollapsed':'collapsed');return;}if(b.dataset.section){const answer=b.closest('.answer'),turn=current().turns.find(t=>t.id===answer.dataset.turn),p=$(b.dataset.section),offset=p.getBoundingClientRect().top;turn.sections||={};turn.sections[p.id]=!turn.sections[p.id];renderTurns();$('reader').scrollTop+=$(p.id).getBoundingClientRect().top-offset;saveReading();}});
// SVG is display content, never a focus or navigation target.
$('thread').addEventListener('click',event=>{if(event.target.closest('.math'))event.preventDefault();});
$('annotation-input').addEventListener('input',()=>{const field=$('entries').querySelector('[data-question="'+editing+'"]');if(field)updateEditor(field);});
$('entries').addEventListener('input',event=>{if(event.target.dataset.question===editing)updateEditor($('annotation-input'));});
window.addEventListener('resize',()=>{refreshEditors();updateReadingContext();});


// Layout transforms are for the prototype's reading view; raw transcript/snapshots remain intact.
function shortTitle(value){return [...value.replace(/\s+/g,' ').trim()].slice(0,20).join('')||'新对话';}
function migrateReadingDesign(){if(state.readingDesignVersion===4)return;for(const c of state.conversations){c.title=c.id==='collected'?DATA.title:shortTitle(c.title);}state.readingDesignVersion=4;}
let headerOpen=!!state.headerPinned,headerClickTimer;
function applyHeader(){const button=$('heading-peek');$('heading-content').hidden=!headerOpen;button.setAttribute('aria-expanded',String(headerOpen));button.classList.toggle('pinned',!!state.headerPinned);button.setAttribute('aria-label',headerOpen?'收起对话标题':'展开对话标题');button.title=state.headerPinned?'已固定 · 双击解除固定':'自动收起 · 单击展开/收起，展开后双击固定';}
function setHeader(open){const pos=readingPosition();headerOpen=open;applyHeader();restoreReading(pos);}
$('heading-peek').onclick=event=>{if(event.detail>1)return;clearTimeout(headerClickTimer);headerClickTimer=setTimeout(()=>{if(state.headerPinned&&headerOpen)state.headerPinned=false;setHeader(!headerOpen);persist();},240);};
$('heading-peek').ondblclick=()=>{clearTimeout(headerClickTimer);if(!headerOpen){setHeader(true);return;}state.headerPinned=!state.headerPinned;applyHeader();persist();};
$('heading').addEventListener('pointerleave',()=>{if(headerOpen&&!state.headerPinned&&!$('heading').querySelector(':focus-visible'))setHeader(false);});
$('reader').addEventListener('pointerdown',()=>{if(headerOpen&&!state.headerPinned)setHeader(false);});
function updateReadingContext(){const shell=$('reading-shell'),reader=$('reader'),box=$('reading-context');const top=reader.getBoundingClientRect().top;let turn;
 if(shell.clientHeight>=420)turn=current().turns.find(t=>{const node=$(t.id),q=$(t.id+'-question');return !t.collapsed&&node&&q&&node.getBoundingClientRect().bottom>top+12&&q.getBoundingClientRect().bottom<top;});
 box.hidden=!turn;if(!turn){box.dataset.turn='';return;}box.dataset.turn=turn.id;
 const first=(turn.prompt??turn.question).split('\n').map(x=>x.trim()).find(Boolean)||'圈点提问';$('reading-context-title').textContent=first;$('reading-context-title').title=first;
 $('reading-context-notes').innerHTML=(turn.annotations||[]).slice(0,3).map(e=>'<span title="'+esc(e.summary)+'">#'+e.number+' · '+esc(e.summary)+'</span>').join('')||'<span>本轮没有圈点</span>';
}
function readingAnchor(id){let node=$(id);const seen=new Set();while(node?.dataset.joinedInto&&!seen.has(node.id)){seen.add(node.id);node=$(node.dataset.joinedInto);}return node;}
function prepareReadingLayout(answer){
 const tid=answer.dataset.turn;
 for(const edit of DATA.readingEdits.filter(e=>e.turn===tid)){const p=$(edit.block);if(!p)continue;const temp=document.createElement('div');temp.innerHTML=edit.html;p.querySelector('.passage-body').innerHTML=temp.querySelector('.passage-body').innerHTML;for(const id of edit.omit){const old=$(id);if(old){old.dataset.readingHidden='true';old.dataset.joinedInto=edit.block;}}}
 const bodySize=parseFloat(getComputedStyle(answer).fontSize);
 for(const eq of [...answer.querySelectorAll('.equation')]){const number=eq.querySelector('.equation-number'),n=Number(number.textContent.replace(/\D/g,'')),math=eq.querySelector('.math');eq.id=tid+'-eq-'+n;number.textContent='Eq. '+n;
  const tex=math.dataset.tex,width=parseFloat(math.querySelector('svg').getAttribute('width'))*18.25;
  if(width>bodySize*5||/=|\\(?:eq|neq|equiv|leq|geq)/.test(tex))continue;
  const passage=eq.closest('.passage'),previous=passage.previousElementSibling,following=passage.nextElementSibling,prevP=previous?.querySelector('.passage-body>p'),nextP=following?.querySelector('.passage-body>p');
  const anchor=eq.id;eq.removeAttribute('id');math.id=anchor;math.dataset.display='false';
  if(prevP&&!previous.dataset.readingHidden){prevP.append(' ',math,' ');passage.dataset.readingHidden='true';passage.dataset.joinedInto=previous.id;if(nextP&&!following.dataset.readingHidden){prevP.append(...nextP.childNodes);following.dataset.readingHidden='true';following.dataset.joinedInto=previous.id;}}
  else {const p=document.createElement('p');p.append(math);eq.replaceWith(p);}
 }
 answer.querySelectorAll('.math').forEach(m=>m.removeAttribute('tabindex'));
 const walker=document.createTreeWalker(answer,NodeFilter.SHOW_TEXT),nodes=[];while(walker.nextNode()){const node=walker.currentNode;if(!node.parentElement.closest('.math,code,button,.equation-number')&&/公式\s*[（(]\d+[)）]/.test(node.textContent))nodes.push(node);}
 for(const node of nodes){const fragment=document.createDocumentFragment();let start=0;for(const match of node.textContent.matchAll(/公式\s*[（(](\d+)[)）]/g)){fragment.append(node.textContent.slice(start,match.index));const target=tid+'-eq-'+match[1];if($(target)){const b=document.createElement('button');b.className='equation-ref';b.dataset.equationRef=target;b.textContent='Eq. '+match[1];b.title='跳转到公式 '+match[1];fragment.append(b);}else fragment.append(match[0]);start=match.index+match[0].length;}fragment.append(node.textContent.slice(start));node.replaceWith(fragment);}
}
$('thread').addEventListener('click',event=>{const b=event.target.closest('[data-equation-ref]');if(!b)return;const id=b.dataset.equationRef,turn=current().turns.find(t=>t.id===b.closest('.turn').id);turn.collapsed=false;
 // Expand any collapsed heading that contains the target, without altering other headings.
 const answer=b.closest('.answer');let stack=[];for(const p of answer.querySelectorAll(':scope>.passage')){const h=p.querySelector('h2,h3,h4');if(h){const level=Number(h.tagName.slice(1));stack=stack.filter(x=>x.level<level);stack.push({level,id:p.id});}if(p.contains($(id))){for(const h of stack)if(turn.sections)turn.sections[h.id]=false;break;}}
 renderTurns();requestAnimationFrame(()=>{const target=$(id);if(target){target.scrollIntoView({block:'center'});target.classList.add('equation-target');saveReading();}});
});

window.addEventListener('resize',positionEditor);
for(const c of state.conversations)for(const e of c.entries)if(e.state==='提交中')e.state='上次提交未完成，可重试';
mountEditors(document);migrateSnapshots();migrateReadingDesign();applyHeader();renderConversation();persist();document.fonts.ready.then(()=>{refreshEditors();restoreReading(current().reading);});
if(location.search.includes('tools=1'))openLab();

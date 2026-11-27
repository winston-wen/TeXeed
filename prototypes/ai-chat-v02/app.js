
'use strict';
const DATA=/*__TRANSCRIPTS__*/;
const $=id=>document.getElementById(id);
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const VISUAL_PREVIEW=new URLSearchParams(location.search).get('preview')==='visual';
const REVIEW_PREVIEW=new URLSearchParams(location.search).get('preview')==='review';
const KEY='texeed-ai-baseline-v02'+(VISUAL_PREVIEW?'-visual':REVIEW_PREVIEW?'-review':'')+(location.search.includes('test=1')?'-test':'');
const clone=x=>JSON.parse(JSON.stringify(x));
const BUNDLED_TURNS=[...DATA.turns,...DATA.sampleConversations.flatMap(c=>c.turns),...DATA.visualPreview.conversations.flatMap(c=>c.turns)];
function fresh(){const conversations=VISUAL_PREVIEW?clone(DATA.visualPreview.conversations):[{id:'collected',title:DATA.title,updatedAt:DATA.updatedAt,turns:clone(DATA.turns),entries:clone(DATA.entries),next:DATA.next,draft:DATA.draft,permission:'完全访问'},...clone(DATA.sampleConversations)];return {sampleHistoryVersion:1,visualMediaVersion:1,active:conversations[0].id,conversations,events:[]};}
let state;try{state=JSON.parse(localStorage.getItem(KEY)||'null');}catch{}
if(state?.conversations?.length){
 for(const conversation of state.conversations)for(const turn of conversation.turns||[]){
  if(turn.answer && turn.answerHtml?.includes('<span data-inline-number="'))turn.answerHtml=restoreCachedFormulaMarkup(turn.answerHtml,turn.answer);
 }
}else state=fresh();
if(REVIEW_PREVIEW&&!state.reviewDemoVersion){const prefix='一次修改可以保留 ',old='AI 回答的原文',added='AI 原始回答',suffix='，同时让新的表述更准确。',answer=prefix+old+suffix+'\n\n这份历史修改稿只读；可以选择文字添加评论，并回复已有评论。';const turn={id:'review-demo-turn',question:'如何在 AI 回答中保留我的修改和评论？',answer,sentAt:'2026-11-06T10:00:00+08:00',answeredAt:'2026-11-06T10:00:12+08:00',segments:[],reviewDraft:{blocks:{'review-demo-turn-p0':{original:prefix+old+suffix,runs:[{kind:'ai',text:prefix},{kind:'deleted',text:old},{kind:'user',text:added},{kind:'ai',text:suffix}]}},comments:[{id:'review-demo-comment',turn:'review-demo-turn',block:'review-demo-turn-p0',start:prefix.length,end:prefix.length+added.length,quote:added,text:'这里的说法是否更准确？',replies:['可以，这样更容易看出原件会被保留。'],resolved:false}]}};const c={id:'review-demo',title:'回答与评论示例',turns:[turn],entries:[],next:1,draft:'',permission:'完全访问'};state={sampleHistoryVersion:1,visualMediaVersion:1,reviewDemoVersion:1,active:c.id,conversations:[c],events:[]};}
// Add sample history once to existing browser storage.
if(!state.sampleHistoryVersion){for(const c of DATA.sampleConversations)if(!state.conversations.some(existing=>existing.id===c.id))state.conversations.push(clone(c));state.sampleHistoryVersion=1;}
if(VISUAL_PREVIEW&&!state.visualMediaVersion){for(const c of DATA.visualPreview.conversations.filter(c=>c.id==='visual-media'))if(!state.conversations.some(existing=>existing.id===c.id))state.conversations.push(clone(c));state.visualMediaVersion=1;}
const requests=new Map(),composerHeights=new Map();
let activeView=state.active,openTabs=(state.openTabs||[state.active]).filter(id=>['settings','history'].includes(id)||state.conversations.some(c=>c.id===id));
let running=null,editing=null,context=null,matchIndex=0,matches=[];
function current(){return state.conversations.find(c=>c.id===state.active)||state.conversations[0];}
function defaultModel(){return state.defaultModel||'gpt-6.1-sol';}
function defaultEffort(){return state.defaultEffort||'高';}
function conversationModel(c=current()){return c.model||defaultModel();}
function conversationEffort(c=current()){return c.effort||defaultEffort();}
function selectStoredValue(select,value){
 if(!Array.from(select.options).some(option=>option.value===value)){const option=document.createElement('option');option.value=value;option.textContent=value+'（当前不可用）';select.appendChild(option);}
 select.value=value;
}
function persist(){try{localStorage.setItem(KEY,JSON.stringify(state,function(key,value){return key==='processCollapsed'?undefined:key==='answerHtml'&&[...BUNDLED_TURNS,DATA.playback].some(t=>t.answer===this.answer)?undefined:value;}));return true;}catch{status('浏览器不允许持久保存；本次页面内仍可试用。');return false;}}
function record(action,detail={}){state.events.push({at:new Date().toISOString(),action,...detail});state.events=state.events.slice(-250);persist();}
let answerMarkingStatusTimer=null;
function status(text){if(answerMarkingStatusTimer!==null){clearTimeout(answerMarkingStatusTimer);answerMarkingStatusTimer=null;}$('status').textContent=text;$('status').hidden=!text;}
function time(value,short=false){if(!value)return '';const d=new Date(value);const parts=new Intl.DateTimeFormat('sv-SE',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hour12:false}).format(d);return short?parts.slice(-8):parts.replace(' ',' (+8) ');}
function scrollEnd(){$('reader').scrollTop=$('reader').scrollHeight;}
function svgHtml(turn){
 if(turn.answerHtml){if(turn.answerHeadingVersion!==1){turn.answerHtml=restoreAnswerHeadings(turn.answerHtml,turn.answer);turn.answerHeadingVersion=1;}return turn.answerHtml;}
 const bundled=BUNDLED_TURNS.find(t=>t.id===turn.id&&t.answer===turn.answer);if(bundled)return bundled.answerHtml;
 const source=[DATA.playback,...BUNDLED_TURNS].find(t=>t.answer===turn.answer);
 if(source)return source.answerHtml.replace(/id="([^"]+)"/g,(_,id)=>'id="'+turn.id+'-'+id+'"').replace(/data-block="([^"]+)"/g,(_,id)=>'data-block="'+turn.id+'-'+id+'"').replace(/href="#([^"]+)"/g,(_,id)=>'href="#'+turn.id+'-'+id+'"');
 if(!turn.answer)return '';turn.answerHeadingVersion=1;return turn.answerHtml=renderSavedAnswer(turn.answer,turn.id+(turn.id.startsWith('sim-')?'-playback':''));
}
function activity(s){const details=s.details||s.text,hasDetails=/[\r\n]/.test(details)||details!==s.text,summary='<span>'+esc(s.text)+'</span>',stamp=s.time?'<time datetime="'+esc(s.time)+'">'+esc(time(s.time,true))+'</time>':'';return !hasDetails&&!stamp?'<div class="activity no-details"><div class="activity-summary">'+summary+'</div></div>':'<details class="activity"><summary>'+summary+'</summary>'+(stamp?'<div class="activity-time">'+stamp+'</div>':'')+(hasDetails?'<template class="activity-source">'+esc(details)+'</template>':'')+'</details>';}
function mountActivityCode(details){
 if(details.querySelector('.activity-code'))return;
 const source=details.querySelector('.activity-source');if(!source)return;
 const text=source.content.textContent,lines=text.split(/\r\n|\r|\n/).length;
 const box=document.createElement('div');box.className='activity-code';
 const numbers=document.createElement('div');numbers.className='activity-numbers';numbers.setAttribute('aria-hidden','true');
 const labels=document.createElement('pre');labels.textContent=Array.from({length:lines},(_,i)=>i+1).join('\n');numbers.append(labels);
 const code=document.createElement('textarea');code.readOnly=true;code.wrap='off';code.spellcheck=false;code.value=text;code.setAttribute('aria-label','只读工具详情');
 code.onscroll=()=>{labels.style.transform='translateY(-'+code.scrollTop+'px)';};box.append(numbers,code);details.append(box);
 const style=getComputedStyle(code);box.style.height=Math.min(320,lines*parseFloat(style.lineHeight)+parseFloat(style.paddingTop)+parseFloat(style.paddingBottom))+'px';
}
const ICON_CLOSE='<svg class="icon" viewBox="0 0 20 20"><path d="M5 5L15 15M15 5L5 15"/></svg>';
const ICON_SEND='<svg class="icon" viewBox="0 0 20 20"><path d="M10 17V3M4 9L10 3L16 9"/></svg>';
let restoring=false,scrollTimer,readingRestoreVersion=0;
function readingPosition(){const area=$('reader'),top=area.getBoundingClientRect().top;const nodes=[...area.querySelectorAll('.passage,.question,.fold-line')].filter(n=>n.getClientRects().length);const anchor=nodes.find(n=>n.getBoundingClientRect().bottom>top+2);return {top:area.scrollTop,anchor:anchor?.id||anchor?.closest('.turn')?.id,offset:anchor?anchor.getBoundingClientRect().top-top:0};}
function saveReading(){if(!restoring&&!$('chat').hidden){current().reading=readingPosition();persist();}}
function restoreReading(pos,version){if(version!==readingRestoreVersion)return;const area=$('reader');restoring=true;area.scrollTop=pos?.top??area.scrollHeight;if(pos?.anchor){const anchor=readingAnchor(pos.anchor);if(anchor?.getClientRects().length)area.scrollTop+=anchor.getBoundingClientRect().top-area.getBoundingClientRect().top-pos.offset;}requestAnimationFrame(()=>{if(version!==readingRestoreVersion)return;lastReaderBounds=area.getBoundingClientRect();readerRounding=0;restoring=false;positionEditor();});}
function cancelReadingRestore(){readingRestoreVersion++;restoring=false;}
function scrollToReadingTarget(target,block){
 if(!target)return;
 cancelReadingRestore();
 // This navigation uses the current viewport, even if ResizeObserver has not delivered its new bounds yet.
 lastReaderBounds=readingViewport.getBoundingClientRect();readerRounding=0;chromeScrollOffset=null;
 target.scrollIntoView({block});
}
function turnAnnotations(t){return (t.annotations||[]).map((e,i)=>({...e,number:i+1}));}
function questionHtml(t,index){
 const annotations=turnAnnotations(t),prompt=annotations.length?t.prompt||'':t.question;
 const back=index===0&&current().branchSource?'<button type="button" data-back-origin>返回原文</button>':'';
 const question=back+'<div class="question-text rich-text">'+renderRichText(prompt)+'</div>';
 if(!annotations.length)return question;
 const items=annotations.map(e=>{
  const id=esc(t.id),source=e.citation,attrs=' data-snapshot-turn="'+id+'" data-snapshot-number="'+e.number+'"';
  const destination=source?' href="#'+esc(source.rowId)+'" title="返回当前原文"':' href="#'+id+'-preview" aria-controls="'+id+'-preview" aria-expanded="false"';
  const link='<a class="sent-summary"'+destination+attrs+'>'+e.number+'. '+renderReferenceSummary(contextText(e)||e.summary||e.question)+'</a>';
  const changed=source&&window.turnRevision?.citationChanged(e,t.id)?'<button type="button" class="source-updated" title="查看批注时版本" aria-haspopup="dialog"'+attrs+'>原文已更新</button>':'';
  return '<li>'+link+changed+(source&&e.question?'<div class="citation-comment">'+renderRichText(e.question)+'</div>':'')+'</li>';
 });
 const preview=annotations.some(e=>!e.citation)?'<section class="snapshot-preview" id="'+t.id+'-preview" hidden aria-label="原文预览"><div class="preview-heading"><span class="preview-label"></span><button data-close-preview="'+t.id+'" aria-label="关闭原文预览">'+ICON_CLOSE+'</button></div><div class="snapshot-scroll rich-text" tabindex="0"></div></section>':'';
 return question+'<ol class="sent-summaries">'+items.join('')+'</ol>'+preview;
}
function snapshotHtml(e){
 return (e.quote?renderRichText(e.quote):'')+(e.sources||[]).map(source=>'<div class="preview-source">'+esc(source.file)+'</div>'+(/\.(?:md|tex)$/i.test(source.file)?renderRichText(source.text):'<pre><code>'+esc(source.text)+'</code></pre>')).join('')+(e.pdf||[]).map(p=>'<figure class="pdf-snapshot"><figcaption>PDF 第 '+esc(p.page)+' 页 · 圈选区域快照</figcaption><img class="zoomable-image" tabindex="0" role="button" aria-haspopup="dialog" alt="PDF 第 '+esc(p.page)+' 页圈选区域" src="data:image/png;base64,'+p.image+'"></figure>').join('');
}
function hideSnapshot(preview){preview.hidden=true;preview.querySelector('.snapshot-scroll').replaceChildren();delete preview.dataset.number;document.querySelectorAll('[aria-controls="'+preview.id+'"]').forEach(link=>link.setAttribute('aria-expanded','false'));}
function showSnapshot(link){
 const turn=current().turns.find(t=>t.id===link.dataset.snapshotTurn),number=Number(link.dataset.snapshotNumber),entry=turnAnnotations(turn).find(e=>e.number===number),preview=$(turn.id+'-preview');
 if(entry.citation){window.turnRevision.openCitation(entry,link.classList.contains('source-updated'),turn.id);return;}
 if(!preview.hidden&&preview.dataset.number===String(number)){hideSnapshot(preview);return;}
 document.querySelectorAll('.snapshot-preview:not([hidden])').forEach(hideSnapshot);
 preview.querySelector('.preview-label').textContent='原文 · '+number;
 preview.querySelector('.snapshot-scroll').innerHTML=snapshotHtml(entry)||'<p class="muted">此条没有保存原文。</p>';
 preview.dataset.number=String(number);preview.hidden=false;link.setAttribute('aria-expanded','true');
 prepareReadingMath(preview);
 requestAnimationFrame(()=>{scrollToReadingTarget(preview,'nearest');preview.querySelector('.snapshot-scroll').focus({preventScroll:true});});
}
function installImageViewer(){
 const dialog=$('image-viewer'),viewport=$('image-viewport'),preview=$('image-preview');let source=null,zoom=1,fitMode=true;
 function scale(value){
  zoom=value;
  if(!preview.naturalWidth||!preview.naturalHeight)return;
  const minimum=Math.min(.05,viewport.clientWidth/preview.naturalWidth,viewport.clientHeight/preview.naturalHeight);
  zoom=Math.max(minimum,Math.min(8,value));preview.style.width=preview.naturalWidth*zoom+'px';preview.style.height=preview.naturalHeight*zoom+'px';
  $('image-scale').textContent=Math.round(zoom*100)+'%';$('image-zoom-out').disabled=zoom<=minimum;$('image-zoom-in').disabled=zoom>=8;
 }
 function fit(){scale(Math.min(1,viewport.clientWidth/preview.naturalWidth,viewport.clientHeight/preview.naturalHeight));}
 function open(image){
  source=image;zoom=1;fitMode=true;preview.alt=image.alt;$('image-title').textContent=image.alt||'图片预览';$('image-scale').textContent='加载中';
  preview.style.width=preview.style.height='';preview.src=image.currentSrc||image.src;source.focus({preventScroll:true});dialog.showModal();$('image-close').focus();
  if(preview.complete&&preview.naturalWidth)fit();
 }
 preview.onload=()=>{if(dialog.open){if(fitMode)fit();else scale(zoom);}};preview.onerror=()=>{if(dialog.open)$('image-scale').textContent='图片加载失败';};
 document.addEventListener('click',event=>{const image=event.target.closest('.zoomable-image');if(image){event.preventDefault();open(image);}});
 document.addEventListener('keydown',event=>{if((event.key==='Enter'||event.key===' ')&&event.target.matches('.zoomable-image')){event.preventDefault();if(!event.repeat)open(event.target);}});
 dialog.addEventListener('click',event=>{
  const id=event.target.closest('button')?.id;
  if(id==='image-close')dialog.close();
  else if(id==='image-fit'){fitMode=true;fit();}
  else if(id==='image-actual'){fitMode=false;scale(1);}
  else if(id==='image-zoom-in'||id==='image-zoom-out'){fitMode=false;scale(zoom*(id==='image-zoom-in'?1.25:.8));}
 });
 dialog.addEventListener('close',()=>{preview.removeAttribute('src');if(source?.isConnected)source.focus({preventScroll:true});source=null;});
 window.addEventListener('resize',()=>{if(dialog.open&&fitMode)fit();});
}
function turnEnd(t){
 if(t.decision)return {incomplete:false,action:'等待你确认答案',at:null};
 const terminal=(t.segments||[]).findLast(s=>s.kind==='activity'&&/^(已停止[。.]|本轮未完成[:：]|公式排版检查通过\s*·\s*回放完成)/.test(s.text));
 const incomplete=typeof t.incomplete==='boolean'?t.incomplete:terminal?!terminal.text.startsWith('公式排版检查通过'):!t.answeredAt&&!t.answer;
 const reason=t.endReason||(terminal?.text.startsWith('已停止')?'user':'program');
 return {incomplete,at:(incomplete?t.endedAt||terminal?.time||t.answeredAt:t.answeredAt)||null,action:incomplete?(reason==='user'?'用户中止':'程序中止'):'答复'};
}
function pendingStatusHtml(job){
 return '<span class="thinking-label" role="status"><span class="pending-label-text">'+esc(job.pendingStatus||'等待 AI 响应')+'</span><span class="thinking-dot" aria-hidden="true"></span></span>';
}
function turnHtml(t,index=0,turns=[t]){
 const thinking=running?.turn.id===t.id,canFold=!thinking,end=turnEnd(t);
 const activities=(t.segments||[]).filter(s=>s.kind==='activity'&&!isConnectionStatus(s));
 const proofIndex=activities.findIndex(s=>/^公式排版检查通过/.test(s.text));
 const after=proofIndex<0?[]:activities.splice(proofIndex);
 const processAfter=after.length?'<div class="process-log after-answer" aria-label="过程记录">'+after.map(activity).join('')+'</div>':'';
 const process=(t.segments||[]).some(s=>s.kind==='text'&&s.text)?(t.segments||[]).filter(s=>s.kind==='text'||s.kind==='activity'&&!isConnectionStatus(s)&&!/^公式排版检查通过/.test(s.text)).map(s=>s.kind==='text'?'<div class="streaming-answer">'+esc(s.text)+'</div>':'<div class="process-log" aria-label="过程记录">'+activity(s)+'</div>').join(''):(activities.length?'<div class="process-log" aria-label="过程记录">'+activities.map(activity).join('')+'</div>':'');
 const sent='<div class="turn-time sent-time"'+(canFold?' data-turn-divider="'+t.id+'" data-toggle-turn="'+t.id+'" tabindex="0" role="button" title="双击收起本轮"':'')+'>'+(t.sentAt?'<time datetime="'+esc(t.sentAt)+'">'+esc(time(t.sentAt))+'</time>':'未记录')+' 发送</div>';
 const answerTime=thinking?pendingStatusHtml(running):t.decision?'<span role="status">等待你确认答案</span>':(end.at?'<time datetime="'+esc(end.at)+'">'+esc(time(end.at))+'</time>':'未记录')+' <span'+(end.incomplete?' class="incomplete-label"':'')+'>'+end.action+'</span>';
 const retry=!running&&!t.decisions?.at(-1)?.cancelled&&end.incomplete&&turns.at(-1)?.id===t.id?'<button class="retry-turn" data-retry-turn="'+t.id+'" aria-label="重试本轮">↻  重试</button>':'';
 const revise=!thinking&&!t.decision&&t.answer?'<button type="button" class="revision-open" data-revise="'+esc(t.id)+'">整理本轮</button>':'';
 const answerHtml=globalThis.turnRevision?.html(t)??svgHtml(t);
 const footer='<div class="turn-footer" id="'+t.id+'-divider"'+(canFold?' data-turn-divider="'+t.id+'" data-toggle-turn="'+t.id+'" tabindex="0" role="button" aria-expanded="true" aria-controls="'+t.id+'-segments" title="双击收起本轮；键盘 Enter / 空格切换"':'')+'><div class="turn-time answer-time">'+answerTime+retry+revise+'</div><div class="answer-divider" aria-hidden="true"></div></div>';
 return '<article class="turn" id="'+t.id+'"><div class="question" id="'+t.id+'-question"><span class="turn-number" aria-label="第 '+(index+1)+' 轮">['+(index+1)+']</span>'+questionHtml(t,index)+'</div>'+sent+'<div class="segments" id="'+t.id+'-segments">'+process+(globalThis.turnDecision?.html(t)||'')+(answerHtml?'<div class="answer-start-divider" aria-hidden="true"></div><div class="answer" data-turn="'+t.id+'">'+answerHtml+'</div>':'')+processAfter+'</div>'+footer+'</article>';
}
function transcriptRows(turns){
 const rows=[];
 const folded=i=>turns[i]?.collapsed&&turns[i].id!==running?.turn.id;
 const foldGroup=i=>{
  const group=turns[i].foldGroup;
  return group&&(folded(i-1)&&turns[i-1].foldGroup===group||folded(i+1)&&turns[i+1].foldGroup===group)?group:'';
 };
 for(let i=0;i<turns.length;i++){
  if(!folded(i)){rows.push({index:i,folded:false});continue;}
  const first=i,group=foldGroup(i);
  while(folded(i+1)&&foldGroup(i+1)===group)i++;
  if(group&&i>first){rows.push({index:first,folded:true,first,last:i,compact:true});continue;}
  for(let index=first;index<=i;index++)
   rows.push({index,folded:true,first,last:i,hint:first<i&&(index===first||index===i)});
 }
 return rows;
}
function transcriptRowHtml(turns,row){
 const {index,folded,first,last,compact,hint}=row,turn=turns[index];
 if(!folded)return turnHtml(turn,index,turns);
 const id=esc(turn.id),begin=first+1,end=last+1;
 const label=compact?begin+(end===begin+1?', ':' ~ ')+end:String(index+1);
 const action=compact?'展开':'收纳';
 const button=compact||hint?'<button type="button" class="fold-run-hint" data-fold-run="'+(compact?'expand':'compact')+
  '" data-fold-first="'+esc(turns[first].id)+'" data-fold-last="'+esc(turns[last].id)+'" aria-label="'+action+'第 '+begin+' 至 '+end+
  ' 轮的折叠区间" title="双击'+action+'折叠区间">'+action+'</button>':'';
 const number='<span class="fold-number"'+(compact?'':' data-toggle-turn="'+id+'" role="button" tabindex="0" aria-expanded="false" aria-label="展开第 '+(index+1)+' 轮"')+'>['+label+']</span>';
 return '<article class="turn collapsed-turn'+(!compact&&index<last?' within-fold-run':'')+'" id="'+id+'"><div class="fold-line'+(button?' has-hint':'')+'" id="'+id+'-divider"'+
  (compact?' data-fold-run-line title="双击展开折叠区间"':' data-turn-divider="'+id+'" title="双击展开本轮"')+'>'+
  number+(button?'<span class="fold-track">'+button+'</span>':'')+'</div></article>';
}
function transcriptHtml(turns){
 return transcriptRows(turns).map(row=>transcriptRowHtml(turns,row)).join('');
}
function renderTurns(){
 closeEquationPreview();
 const pos=$('reader').scrollTop,revisionFocus=window.turnRevision?.focusState();
 $('thread').innerHTML=current().turns.length?transcriptHtml(current().turns):current().branchSource?branchOriginHtml(current().branchSource):'<div class="empty"><h2>AI 对话</h2><p>从一个问题，或一处圈点开始。</p></div>';
 enhanceAnswers();addSameTurnLinks();window.alignReadingMedia?.($('thread'));alignReadingInk();alignPendingIndicator();$('reader').scrollTop=pos;window.turnRevision?.resume(revisionFocus);renderBubbles();window.refreshUserBookmarkMarkers?.();refreshReferenceClips();
}
function sourceTextNodes(root){const walker=document.createTreeWalker(root,NodeFilter.SHOW_TEXT),nodes=[];while(walker.nextNode()){const node=walker.currentNode;if(!node.parentElement.closest('.same-turn-link,.heading-marker,.margin-notes,.math,svg'))nodes.push(node);}return nodes;}
function sourceText(root){return sourceTextNodes(root).map(node=>node.nodeValue).join('');}
function addSameTurnLinks(){
 const turns=current().turns,positions=new Map();
 const incoming=turns.flatMap((turn,index)=>(turn.request?.annotations||turn.annotations||[]).map((entry,i)=>({turn,index,entry,key:'turn:'+turn.id+':'+(entry.id||i)})));
 for(const c of state.conversations)if(c.branchSource?.parentId===current().id)incoming.push({child:c,entry:c.branchSource,key:'branch:'+c.id});
 if(!incoming.length)return;
 const placed=new Set(Array.from($('thread').querySelectorAll('[data-revision-ref]'),el=>el.dataset.revisionRef));
 for(const spec of incoming){
  const {entry,turn,index,child,key}=spec;
  if(placed.has(key))continue;
  const id=entry.endBlock||entry.block,source=entry.block&&$(entry.block),end=id&&$(id);if(!source||!end||source.closest('.answer')!==end.closest('.answer'))continue;
  const sourceIndex=turns.findIndex(t=>t.id===source.closest('.answer')?.dataset.turn);if(sourceIndex<0||(!child&&sourceIndex>=index))continue;
  const body=end.querySelector('.passage-body');if(!body)continue;
  if(Number.isInteger(entry.endMediaIndex)){const media=body.querySelectorAll('.math,img,table')[entry.endMediaIndex];if(media){if(!positions.has(id))positions.set(id,[]);positions.get(id).push({media,...spec});}continue;}
  const text=sourceText(body);let offset=entry.endTextOffset;
  if(!Number.isInteger(offset)){const quote=String(entry.quote||'').trim();if(!quote||source!==end)continue;
   const matches=[...body.querySelectorAll('.math,img,table')].filter(media=>(media.matches('img')?media.alt:serialize(media).trim())===quote);
   if(matches.length===1){if(!positions.has(id))positions.set(id,[]);positions.get(id).push({media:matches[0],...spec});continue;}
   const at=text.indexOf(quote);if(at<0||text.indexOf(quote,at+1)>=0)continue;offset=at+quote.length;}
  if(offset<0||offset>text.length)continue;if(!positions.has(id))positions.set(id,[]);positions.get(id).push({offset,...spec});
 }
 for(const [id,links] of positions){const body=$(id)?.querySelector('.passage-body');if(!body)continue;links.sort((a,b)=>(b.offset??-1)-(a.offset??-1)||b.index-a.index);
  for(const item of links){const nodes=sourceTextNodes(body);let n=item.offset,target=null;if(!item.media){for(const node of nodes){if(n<=node.length){target=node;break;}n-=node.length;}if(!target)continue;}
   const link=document.createElement('button');link.type='button';link.className='same-turn-link';link.dataset.revisionRef=item.key;link.dataset.sourceBlock=id;if(item.media)link.dataset.sourceMedia=[...body.querySelectorAll('.math,img,table')].indexOf(item.media);else link.dataset.sourceOffset=item.offset;if(item.child){link.dataset.openChild=item.child.id;link.title=item.child.title;}else{link.dataset.openTurn=item.turn.id;link.title='跳转到第 '+(item.index+1)+' 轮';}
   const icon=document.createElement('span');icon.className='reading-bubble';icon.textContent='🗨️';link.append(icon,item.child?' '+item.child.title:' ['+(item.index+1)+']');
   if(item.media)item.media.after(link);else{const tail=target.splitText(n);tail.parentNode.insertBefore(link,tail);}
  }
 }
}
function enhanceAnswers(scope=document){
 prepareReadingMath(scope);
 scope.querySelectorAll('.math').forEach(m=>m.removeAttribute('tabindex'));
 scope.querySelectorAll('.answer code').forEach(code=>{const turn=current().turns.find(t=>t.id===code.closest('.answer').dataset.turn);if(!BUNDLED_TURNS.some(t=>t.id===turn.id&&t.answer===turn.answer))return;const text=code.textContent.trim();if(/^(?:main\.tex|qa\.md)(?::\d+(?:[–—-]\d+)?)?$/.test(text)){const b=document.createElement('button');b.className='file-link';b.dataset.file=text;b.textContent=text;b.title='打开源码快照'+(text.includes(':')?'并跳转到行号':'');code.replaceChildren(b);}});
 const answerNumbers=new Map();
 scope.querySelectorAll('.answer').forEach(answer=>{
  prepareReadingLayout(answer);const index=current().turns.findIndex(t=>t.id===answer.dataset.turn),t=current().turns[index],numbers=answerNumbers.get(answer.dataset.turn)||[0,0,0,0,0,0];answerNumbers.set(answer.dataset.turn,numbers);let hiding=[];
  for(const p of answer.querySelectorAll(':scope>.passage')){
   const headings=[...p.querySelectorAll('h1,h2,h3,h4,h5,h6')],h=headings[0];
   if(!h){p.hidden=!!hiding.length||p.dataset.readingHidden==='true';continue;}
   const level=Number(h.tagName.slice(1));hiding=hiding.filter(x=>x<level);p.hidden=!!hiding.length||p.dataset.readingHidden==='true';
   let firstLabel;
   for(const heading of headings){const depth=Number(heading.tagName.slice(1));numbers[depth-1]++;numbers.fill(0,depth);const label='<span class="heading-marker" aria-hidden="true">['+(index+1)+':'+numbers.slice(0,depth).join('.')+']</span><span class="heading-label">'+heading.innerHTML+'</span>';if(heading===h)firstLabel=label;else heading.innerHTML=label;}
   const collapsed=!!t.sections?.[p.id],b=document.createElement('button');b.className='heading-toggle';b.dataset.section=p.id;b.setAttribute('aria-expanded',String(!collapsed));
   b.innerHTML=firstLabel+(collapsed?'<span class="heading-collapsed">（已折叠）</span>':'');h.replaceChildren(b);if(collapsed)hiding.push(level);
  }
 });
}
function isConnectionStatus(segment){return /^(request-state-|network-retry$)/.test(segment.id||'')||/^(已连接 AI|正在连接 AI|正在准备请求)(?:$| ·)/.test(segment.text||'');}
function setPendingStatus(job,text){job.pendingStatus=text;if(running===job){const label=$(job.turn.id+'-divider')?.querySelector('.pending-label-text');if(label){label.textContent=text;alignPendingIndicator();}}}
function alignPendingIndicator(){
 for(const status of document.querySelectorAll('.thinking-label')){
  const label=status.querySelector('.pending-label-text'),dot=status.querySelector('.thinking-dot');if(!label?.firstChild||!dot)continue;
  const style=getComputedStyle(label),ctx=document.createElement('canvas').getContext('2d');ctx.font=style.font||[style.fontStyle,style.fontWeight,style.fontSize,style.fontFamily].join(' ');const metrics=ctx.measureText(label.textContent);
  const range=document.createRange();range.selectNodeContents(label);const text=range.getBoundingClientRect(),box=dot.getBoundingClientRect(),transform=getComputedStyle(dot).transform;
  const previous=transform==='none'?0:new DOMMatrixReadOnly(transform).m42;
  dot.style.transform='translateY('+(previous+text.top+metrics.fontBoundingBoxAscent+(metrics.actualBoundingBoxDescent-metrics.actualBoundingBoxAscent)/2-box.top-box.height/2)+'px)';
 }
}
function alignReadingInk(headingsChanged=true,scope=document){
 const questions=[...scope.querySelectorAll('.question')],headings=headingsChanged?[...scope.querySelectorAll('.heading-label')]:[];
 if(!questions.length&&!headings.length)return;
 const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');
 const metrics=alignReadingInk.metrics||(alignReadingInk.metrics=new Map());
 const measure=(text,style,font)=>{font=font||style.font||[style.fontStyle,style.fontWeight,style.fontSize,style.fontFamily].join(' ');const key=font+'\n'+text;if(metrics.has(key))return metrics.get(key);ctx.font=font;const value=ctx.measureText(text);if(metrics.size>=512)metrics.clear();metrics.set(key,value);return value;};
 const axis=m=>(m.actualBoundingBoxDescent-m.actualBoundingBoxAscent)/2;
 const inkCanvas=headings.length?document.createElement('canvas'):null,inkContext=inkCanvas?.getContext('2d',{willReadFrequently:true}),inkCache=alignReadingInk.inkCache||(alignReadingInk.inkCache=new Map()),axisCache=alignReadingInk.axisCache||(alignReadingInk.axisCache=new Map());
 const ink=(text,style)=>{const font=style.font||[style.fontStyle,style.fontWeight,style.fontSize,style.fontFamily].join(' '),axisKey=font+'\n'+text.slice(0,64);if(axisCache.has(axisKey))return axisCache.get(axisKey);let weight=0,moment=0,count=0;
  for(const char of text){if(count++>=64)break;if(!char.trim())continue;const key=font+'\n'+char;let glyph=inkCache.get(key);
   if(!glyph){const bounds=measure(char,style,font),left=Math.max(0,bounds.actualBoundingBoxLeft),right=Math.max(0,bounds.actualBoundingBoxRight),ascent=Math.max(0,bounds.actualBoundingBoxAscent),descent=Math.max(0,bounds.actualBoundingBoxDescent),scale=2,baseline=2+ascent;
    if(!inkContext||!Number.isFinite(left+right+ascent+descent)||left+right===0||ascent+descent===0)glyph={weight:1,moment:axis(bounds)};
    else{inkCanvas.width=Math.max(1,Math.ceil((left+right+4)*scale));inkCanvas.height=Math.max(1,Math.ceil((ascent+descent+4)*scale));inkContext.setTransform(scale,0,0,scale,0,0);inkContext.font=font;inkContext.fillStyle='#fff';inkContext.fillText(char,2+left,baseline);const pixels=inkContext.getImageData(0,0,inkCanvas.width,inkCanvas.height).data;let area=0,sum=0;for(let y=0;y<inkCanvas.height;y++)for(let x=0;x<inkCanvas.width;x++){const alpha=pixels[(y*inkCanvas.width+x)*4+3];area+=alpha;sum+=alpha*((y+.5)/scale-baseline);}glyph=area?{weight:area,moment:sum}:{weight:1,moment:axis(bounds)};}
    if(inkCache.size>=1024)inkCache.clear();inkCache.set(key,glyph);
   }weight+=glyph.weight;moment+=glyph.moment;
  }const value=weight?moment/weight:axis(measure(text,style,font));if(axisCache.size>=512)axisCache.clear();axisCache.set(axisKey,value);return value;
 };
 const ordinalEdits=[],headingEdits=[],headingStyles=new WeakMap();
 for(const label of headings){
  const marker=label.previousElementSibling;if(!marker?.classList.contains('heading-marker'))continue;
  let node=label.firstChild;if(!node||node.nodeType!==Node.TEXT_NODE||!node.textContent.trimStart()){
   const walker=document.createTreeWalker(label,NodeFilter.SHOW_TEXT);node=null;let candidate;
   while((candidate=walker.nextNode()))if(candidate.textContent.trimStart()&&!candidate.parentElement.closest('.math,svg')){node=candidate;break;}
  }
  if(!node)continue;
  const scope=label.closest('.answer')||label,styles=headingStyles.get(scope)||{body:getComputedStyle(label)};headingStyles.set(scope,styles);
  const bodyStyle=node.parentElement===label?styles.body:getComputedStyle(node.parentElement);
  const text=node.textContent.trimStart();headingEdits.push(marker,ink(text,bodyStyle)-ink(marker.textContent,styles.body));
 }
 for(const question of questions){
  const number=question.querySelector('.turn-number');if(!number||!question.getClientRects().length)continue;
  const walker=document.createTreeWalker(question,NodeFilter.SHOW_TEXT),range=document.createRange();let node,top=null,inkTop=Infinity,inkBottom=-Infinity;
  while((node=walker.nextNode())){
   if(!node.textContent.trim()||node.parentElement.closest('.turn-number,.math'))continue;
   range.selectNodeContents(node);const rect=range.getClientRects()[0];if(!rect)continue;if(top===null)top=rect.top;if(rect.top>top+2)break;
   let lo=1,hi=node.length;while(lo<hi){const mid=Math.ceil((lo+hi)/2);range.setStart(node,mid-1);range.setEnd(node,mid);if(range.getBoundingClientRect().top<=top+2)lo=mid;else hi=mid-1;}
   const style=getComputedStyle(node.parentElement),m=measure(node.textContent.slice(0,lo),style),baseline=rect.top+m.fontBoundingBoxAscent;
   inkTop=Math.min(inkTop,baseline-m.actualBoundingBoxAscent);inkBottom=Math.max(inkBottom,baseline+m.actualBoundingBoxDescent);
  }
  if(Number.isFinite(inkTop)){
   const style=getComputedStyle(number),m=measure(number.textContent,style),rect=number.getBoundingClientRect(),previous=style.transform==='none'?0:new DOMMatrixReadOnly(style.transform).m42,baseline=rect.top-previous+(parseFloat(style.lineHeight)-m.fontBoundingBoxAscent-m.fontBoundingBoxDescent)/2+m.fontBoundingBoxAscent;
   ordinalEdits.push(()=>{number.style.transform='translateY('+((inkTop+inkBottom)/2-baseline-axis(m))+'px)';});
  }
 }
 for(let i=0;i<headingEdits.length;i+=2)headingEdits[i].style.transform='translateY('+headingEdits[i+1]+'px)';
 ordinalEdits.forEach(apply=>apply());
}
function toggleTurn(t,control){
 const turns=current().turns,index=turns.indexOf(t);
 if(index<0||running?.turn.id===t.id)return;
 const expanding=t.collapsed;
 const offset=(control.closest('.fold-line')||control).getBoundingClientRect().top;
 let first=index;
 if(expanding)revealFoldedTurn(t);
 else{
  const left=turns[index-1],right=turns[index+1];
  const upper=left?.collapsed&&left.foldGroup&&turns[index-2]?.collapsed&&turns[index-2].foldGroup===left.foldGroup?left.foldGroup:null;
  const lower=right?.collapsed&&right.foldGroup&&turns[index+2]?.collapsed&&turns[index+2].foldGroup===right.foldGroup?right.foldGroup:null;
  t.collapsed=true;delete t.foldGroup;
  if(upper||lower)t.foldGroup=upper||lower;
  if(upper&&lower&&upper!==lower)
   for(let i=index+1;turns[i]?.collapsed&&turns[i].foldGroup===lower;i++)turns[i].foldGroup=upper;
  while(t.foldGroup&&turns[first-1]?.collapsed&&turns[first-1].foldGroup===t.foldGroup)first--;
 }
 cancelReadingRestore();renderTurns();
 const anchor=expanding?$(t.id+'-question'):$(turns[first].id+'-divider');
 $('reader').scrollTop+=anchor.getBoundingClientRect().top-offset;
 saveReading();
 return expanding?anchor.nextElementSibling:anchor.querySelector(t.foldGroup?'.fold-run-hint':'.fold-number');
}
function revealFoldedTurn(turn,turns=current().turns){
 if(!turn.collapsed)return false;
 const group=turn.foldGroup,index=turns.indexOf(turn);
 if(group&&index>=0){
  let first=index,last=index;
  const matches=i=>turns[i]?.collapsed&&turns[i].foldGroup===group;
  while(matches(first-1))first--;
  while(matches(last+1))last++;
  for(let i=first;i<=last;i++)delete turns[i].foldGroup;
 }
 turn.collapsed=false;
 return true;
}
function changeFoldRun(control,restoreFocus){
 const turns=current().turns,first=turns.findIndex(t=>t.id===control.dataset.foldFirst),last=turns.findIndex(t=>t.id===control.dataset.foldLast);
 if(first<0||last<=first)return;
 const compact=control.dataset.foldRun==='compact';
 const group=compact?crypto.randomUUID():null;
 const row=control.closest('.fold-line'),y=row.getBoundingClientRect().top;
 const reader=$('reader'),pos=reader.scrollTop;
 for(let i=first;i<=last;i++){if(compact)turns[i].foldGroup=group;else delete turns[i].foldGroup;}
 const rows=transcriptRows(turns);
 const changed=rows.find(item=>item.index===first);
 const start=changed.first,end=changed.last;
 cancelReadingRestore();
 const fragment=document.createElement('template');
 fragment.innerHTML=rows.filter(info=>info.index>=start&&info.index<=end).map(info=>transcriptRowHtml(turns,info)).join('');
 const anchor=$(turns[start].id);
 for(let i=start+1;i<=end;i++)$(turns[i].id)?.remove();
 anchor.replaceWith(fragment.content);
 const replacement=$(turns[first].id+'-divider');
 reader.scrollTop=pos;
 reader.scrollTop+=replacement.getBoundingClientRect().top-y;
 if(restoreFocus)(replacement.querySelector('.fold-run-hint')||replacement.querySelector('.fold-number'))?.focus({preventScroll:true});
 saveReading();
}

function openSource(ref){const m=/^([^:]+)(?::(\d+)(?:[–—-](\d+))?)?$/.exec(ref),file=m[1],start=Number(m[2]||1),end=Number(m[3]||start);const text=DATA.sourceFiles[file];if(text===undefined){status('未收集此文件的源码。');return;}$('source-title').textContent=ref;$('source-lines').innerHTML=text.split('\n').map((line,i)=>'<div class="source-line '+(i+1>=start&&i+1<=end?'selected':'')+'" id="source-line-'+(i+1)+'"><span class="source-number">'+(i+1)+'</span><span class="source-code">'+esc(line||' ')+'</span></div>').join('');$('source-dialog').showModal();requestAnimationFrame(()=>$('source-line-'+start)?.scrollIntoView({block:'center'}));}

function contextText(e){return e.quote||(e.sources||[]).map(s=>s.text).join('\n')||(e.pdf?.length?'PDF 圈选快照 · '+e.title:'');}
function renderEntries(){
 current().entries.forEach((e,i)=>e.number=i+1);current().next=current().entries.length+1;
 const scroll=$('entries').scrollTop;
 $('entries-button').textContent='圈点 · '+current().entries.length;
 $('entries').innerHTML=current().entries.map(e=>'<section class="entry" data-entry="'+e.id+'"><div class="entry-head"><input type="checkbox" aria-label="选择圈点 '+e.number+'" data-select="'+e.id+'" '+(e.selected?'checked':'')+(running||e.sourceLocked?' disabled':'')+'><span class="entry-title">#'+e.number+' · '+esc(e.title)+'</span><button class="entry-remove" data-remove="'+e.id+'" aria-label="移除圈点 '+e.number+'" '+(running||e.sourceLocked?'disabled':'')+'>×</button></div><button class="entry-context-preview '+(e.contextExpanded?'expanded':'')+'" data-preview="'+e.id+'" aria-expanded="'+!!e.contextExpanded+'" aria-label="圈点 '+e.number+' 原文；双击展开或收起">'+esc(contextText(e))+'</button>'+(e.pdf?.length?'<div class="context-body">'+e.pdf.map(p=>'<img class="zoomable-image" tabindex="0" role="button" aria-haspopup="dialog" alt="PDF 圈选快照" src="data:image/png;base64,'+p.image+'">').join('')+'</div>':'')+'<textarea class="entry-question" rows="2" data-question="'+e.id+'" aria-label="圈点 '+e.number+' 的单独说明" '+(running?'disabled':'')+'>'+esc(e.question)+'</textarea></section>').join('')||'<p class="muted">本次尚未添加圈点。右键选择回答内容可添加。</p>';
 mountEditors($('entries'));$('entries').scrollTop=scroll;renderBubbles();
}

function renderBubbles(){document.querySelectorAll('.margin-notes').forEach(e=>e.replaceChildren());for(const e of current().entries){if(!e.block||['已完成','提交中'].includes(e.state))continue;let p=$(e.block);if(p?.dataset.joinedInto)p=$(p.dataset.joinedInto);if(!p)continue;const b=document.createElement('button');b.className='bubble';b.textContent=e.number;b.dataset.bubble=e.id;b.setAttribute('aria-label','编辑批注 '+e.number);p.querySelector('.margin-notes').append(b);}}
function renderConversation(){setAnswerMarking(false);running=requests.get(state.active)||turnDecision.restore(current())||null;closePopups();$('composer').value=current().draft;$('permission').value=current().permission||'完全访问';selectStoredValue($('model'),conversationModel());selectStoredValue($('effort'),conversationEffort());const version=++readingRestoreVersion;restoring=true;renderTurns();setBusy(!!running);restoreComposerHeight();refreshEditors();status('');renderTabs();requestAnimationFrame(()=>restoreReading(current().reading,version));}

function protectedAnnotation(e){return !!(e.sourceLocked||e.isBranchOrigin||e.state==='提交中');}
function removeEntry(id){const e=current().entries.find(e=>e.id===id);if(!e||protectedAnnotation(e))return;current().entries=current().entries.filter(e=>e.id!==id);if(editing===id){editing=null;resetAnnotationDelete();annotationPosition=null;annotationDrag=null;annotationCanDrag=false;$('annotation-editor').hidden=true;}renderEntries();record('remove-entry',{number:e.number});}
function serialize(node){if(node.nodeType===3)return node.nodeValue;if(node.nodeType!==1&&node.nodeType!==11)return '';if(node.nodeType===1){if(node.matches('.margin-notes,.equation-number,.heading-marker,.same-turn-link'))return '';if(node.matches('.math'))return node.dataset.display==='true'?'$$\n'+node.dataset.tex+'$$\n':' $'+node.dataset.tex+'$ ';if(node.matches('svg'))return '';}let text=Array.from(node.childNodes).map(serialize).join('');if(node.nodeType===1&&node.matches('p,li,h1,h2,h3,h4,h5,h6,blockquote,.equation,.passage'))text+='\n';return text;}
function serializeReadingSelection(range){
 const formula=node=>(node.nodeType===Node.ELEMENT_NODE?node:node.parentElement)?.closest('.math');
 const from=formula(range.startContainer),to=formula(range.endContainer);
 if(!from&&!to)return serialize(range.cloneContents());
 const expanded=range.cloneRange();
 if(from)expanded.setStartBefore(from);
 if(to)expanded.setEndAfter(to);
 return serialize(expanded.cloneContents());
}
function targetContext(target,event){
 const p=target.closest('.passage'),answer=target.closest('.answer');if(!p||!answer)return null;
 const sel=window.getSelection(),media=target.closest('.math,img,table'),body=p.querySelector('.passage-body');
 let quote='',selected=false,startBlock=p.id,endBlock,endTextOffset,endMediaIndex,endInMedia=false;
 if(sel?.rangeCount&&!sel.isCollapsed){
  const r=sel.getRangeAt(0),start=r.startContainer.nodeType===1?r.startContainer:r.startContainer.parentElement,end=r.endContainer.nodeType===1?r.endContainer:r.endContainer.parentElement;
  const inside=!event||Array.from(r.getClientRects()).some(rect=>event.clientX>=rect.left&&event.clientX<=rect.right&&event.clientY>=rect.top&&event.clientY<=rect.bottom);
  if(!inside)sel.removeAllRanges();
  else if(start.closest('.answer')===answer&&end.closest('.answer')===answer){
   quote=serializeReadingSelection(r);selected=true;startBlock=start.closest('.passage')?.id||p.id;endInMedia=!!media?.contains(r.endContainer);
   const block=end.closest('.passage'),endBody=block?.querySelector('.passage-body');
   if(endBody?.contains(r.endContainer)){const prefix=document.createRange();prefix.selectNodeContents(endBody);prefix.setEnd(r.endContainer,r.endOffset);const fragment=prefix.cloneContents();fragment.querySelectorAll('.same-turn-link,.heading-marker,.margin-notes,.math,svg').forEach(el=>el.remove());endBlock=block.id;endTextOffset=fragment.textContent.length;}
  }
 }
 const unit=media?.matches('table')?media:target.closest('p,li,blockquote,h1,h2,h3,h4,h5,h6,.equation')||body;
 const cell=target.closest('td,th');let paragraph=cell?(cell.querySelector('input,textarea,select')?.value??serialize(cell).trim()):media?.matches('table')?[...media.rows].map(row=>[...row.cells].map(cell=>cell.querySelector('input,textarea,select')?.value??serialize(cell).trim()).join('\t')).join('\n'):serialize(unit).trim();if(!paragraph&&media?.matches('img'))paragraph=media.alt||'图片';
 if(!selected&&media)quote=media.matches('img')?media.alt||sourceText(p.previousElementSibling||p.nextElementSibling||p).trim().slice(-80)||'图片':serialize(media).trim();
 if(!quote.trim())quote=serialize(body).trim();
 if(media&&body?.contains(media)&&(!selected||endInMedia)){endBlock=p.id;endMediaIndex=[...body.querySelectorAll('.math,img,table')].indexOf(media);}
 const preview=media?.matches('.math')?{kind:'formula',tex:media.dataset.tex||'',display:media.dataset.display==='true'}:media?.matches('img')?{kind:'image',alt:media.alt||'',src:media.getAttribute('src')||''}:media?.matches('table')?{kind:'table',rows:[...media.rows].map(row=>[...row.cells].map(cell=>cell.querySelector('input,textarea,select')?.value??cell.textContent))}:null;
 const child=target.closest('.passage-body > *'),index=Array.from(body.children).indexOf(child);
 const revisionBlock=index>0?p.id+'-sub'+index:p.id;
 const equationId=media&&(!selected||quote.trim()===serialize(media).trim())?media.closest('.equation')?.id:undefined;
 return {block:startBlock,revisionBlock,equationId,tableObject:!selected&&media?.matches('table'),quote,selected:selected||!!media,turn:answer.dataset.turn,endBlock,endTextOffset,endMediaIndex,paragraph,preview};
}
function addAnnotation(ctx){if(!ctx?.selected||!ctx.quote.trim())return;const c=current();const e={id:crypto.randomUUID(),number:c.next++,title:'AI 回答批注',question:'',selected:true,state:'待提交',sources:[],pdf:[],block:ctx.block,endBlock:ctx.endBlock,endTextOffset:ctx.endTextOffset,endMediaIndex:ctx.endMediaIndex,quote:ctx.quote,citation:window.turnRevision?.citationAt(ctx)};c.entries.push(e);renderEntries();record('add-answer-annotation',{number:e.number,quote:e.quote});openEditor(e.id);}
let answerMarking=false;
function setAnswerMarking(active){
 const was=answerMarking;answerMarking=active;$('thread').classList.toggle('answer-marking',active);
 if(active)status('回答批注模式：选择文字或内容区域；Esc 退出。');
 else if(was&&(!$('status').textContent||$('status').textContent==='回答批注模式：选择文字或内容区域；Esc 退出。')){
  const message='已退出回答批注模式。';status(message);answerMarkingStatusTimer=setTimeout(()=>{answerMarkingStatusTimer=null;if($('status').textContent===message)status('');},3000);
 }
}
function branchOriginHtml(source){
 const preview=source.preview;let body;
 if(preview?.kind==='formula')body=renderRichText((preview.display?'$$':'$')+preview.tex+(preview.display?'$$':'$'),true);
 else if(preview?.kind==='image'&&/^(?:https?:|data:image\/(?:png|jpeg|gif|webp);|[^a-zA-Z][^:]*|[a-zA-Z0-9_.-]+\/)/i.test(preview.src))body='<img class="zoomable-image" src="'+esc(preview.src)+'" alt="'+esc(preview.alt)+'">';
 else if(preview?.kind==='table'&&Array.isArray(preview.rows))body='<div class="table-wrap"><table>'+preview.rows.map((row,i)=>'<tr>'+row.map(cell=>'<'+(i?'td':'th')+'>'+esc(cell)+'</'+(i?'td':'th')+'>').join('')+'</tr>').join('')+'</table></div>';
 else body=renderRichText(source.quote||'');
 return '<section class="branch-origin-view"><button type="button" id="branch-origin-return">返回原文</button><div class="rich-text">'+body+'</div></section>';
}
function openBranchFrom(ctx){
 if(!ctx||!ctx.quote.trim())return;const parent=current(),index=parent.turns.findIndex(turn=>turn.id===ctx.turn);if(index<0)return;
 const source={parentId:parent.id,turn:ctx.turn,block:ctx.block,endBlock:ctx.endBlock,endTextOffset:ctx.endTextOffset,endMediaIndex:ctx.endMediaIndex,quote:ctx.quote,preview:ctx.preview};
 const inherited=[...(parent.inheritedContext||[]),...parent.turns.slice(0,index+1).map(turn=>({question:turn.question,answer:window.turnRevision?.snapshot(turn)??turn.answer,annotations:turn.annotations||[],...(turn.decisions?.length?{decisions:turn.decisions}:{})}))];
 const entry={id:crypto.randomUUID(),number:1,title:'追问来源',question:'',selected:true,state:'待提交',sources:[],pdf:[],quote:ctx.quote,block:null,sourceLocked:true};
 const child={id:crypto.randomUUID(),title:shortTitle(ctx.quote),updatedAt:new Date().toISOString(),turns:[],entries:[entry],next:2,draft:'',permission:parent.permission||'完全访问',model:conversationModel(parent),effort:conversationEffort(parent),branchSource:source,inheritedContext:clone(inherited)};
 state.conversations.push(child);if(!persist()){state.conversations.pop();return;}openConversation(child.id);record('open-branch',{parent:parent.id,source:ctx.block});
}
function returnBranchOrigin(){
 const child=current(),source=child.branchSource,parent=source&&state.conversations.find(c=>c.id===source.parentId),turn=parent?.turns.find(t=>t.id===source.turn);if(!parent||!turn){status('原会话或来源已不存在。');return;}
 revealFoldedTurn(turn,parent.turns);openConversation(parent.id);cancelReadingRestore();const marker=()=>document.querySelector('[data-revision-ref="'+CSS.escape('branch:'+child.id)+'"]'),owner=()=>marker()?.closest('.passage')||$(source.block);let target=owner(),answer=target?.closest('.answer'),stack=[];
 if(answer)for(const p of answer.querySelectorAll(':scope>.passage')){const h=p.querySelector('h1,h2,h3,h4,h5,h6');if(h){const level=Number(h.tagName.slice(1));stack=stack.filter(x=>x.level<level);stack.push({level,id:p.id});}if(p===target){for(const item of stack)if(turn.sections)turn.sections[item.id]=false;break;}}
 renderTurns();target=marker()||owner();scrollToReadingTarget(target,'center');saveReading();
}
let annotationPosition=null,annotationDrag=null,annotationCanDrag=false,annotationDeleteArmed=false;
function resetAnnotationDelete(){annotationDeleteArmed=false;$('annotation-delete-hint').hidden=true;}
function updateAnnotationDeleteHint(){const e=current().entries.find(entry=>entry.id===editing),button=$('delete-annotation');if(!e)return;button.disabled=protectedAnnotation(e);button.title=button.disabled?'受保护的来源不能删除':annotationCanDrag?'删除批注':'双击删除此条批注';button.setAttribute('aria-label',button.title);}
function openEditor(id){
 const e=current().entries.find(e=>e.id===id);if(!e)return;
 commitAnnotation();deleteKeyboardAt=0;editing=id;annotationPosition=null;
 annotationCanDrag=!!(e.block&&$(e.block)?.closest('.answer'));
 const popup=$('annotation-editor'),input=$('annotation-input');
 popup.querySelector('.annotation-header').dataset.draggable=String(annotationCanDrag);
 $('annotation-title').innerHTML=esc('#'+e.number+' · '+e.title)+'<span class="anchor-state"></span>';
 input.value=e.question;popup.hidden=false;resetAnnotationDelete();updateAnnotationDeleteHint();refreshEditors();positionEditor();
 input.focus({preventScroll:true});
 input.setSelectionRange(input.value.length,input.value.length);
}
function commitAnnotation(){if(!editing)return;const e=current().entries.find(e=>e.id===editing);if(e)e.question=$('annotation-input').value;record('commit-annotation',{id:editing});editing=null;resetAnnotationDelete();annotationPosition=null;annotationDrag=null;annotationCanDrag=false;$('annotation-editor').hidden=true;}
function confirmAnnotation(id){
 const e=current().entries.find(e=>e.id===id);if(!e)return;
 const fromPopup=editing===id;
 if(fromPopup)commitAnnotation();e.selected=true;renderEntries();
 if($('entries-dialog').open)$('entries-dialog').close();
 if(fromPopup&&e.block){
  const bubble=$('thread').querySelector('[data-bubble="'+CSS.escape(id)+'"]');
  (bubble?.getClientRects().length?bubble:$('reader')).focus({preventScroll:true});
 }
 record('confirm-annotation',{id});
}
function positionEditor(){if(!editing)return;const e=current().entries.find(e=>e.id===editing),p=e&&readingAnchor(e.block),box=$('annotation-editor');if(!e)return;const area=$('reader').getBoundingClientRect(),r=p?.getBoundingClientRect();box.hidden=false;const visible=r&&p.getClientRects().length&&r.bottom>=area.top&&r.top<=area.bottom;const label=$('annotation-title').querySelector('.anchor-state');if(label)label.textContent=visible?'':'原位置在视野外';if(annotationPosition){annotationPosition.x=Math.max(0,Math.min(annotationPosition.x,innerWidth-box.offsetWidth));annotationPosition.y=Math.max(0,Math.min(annotationPosition.y,innerHeight-box.offsetHeight));box.style.left=annotationPosition.x+'px';box.style.top=annotationPosition.y+'px';}else{box.style.left=Math.max(12,Math.min((r?.right||area.right)-28,innerWidth-box.offsetWidth-12))+'px';box.style.top=Math.max(area.top,Math.min(r?.top+28||area.top,area.bottom-box.offsetHeight-8))+'px';}}
const annotationHeader=$('annotation-editor').querySelector('.annotation-header');
annotationHeader.addEventListener('pointerdown',event=>{if(!annotationCanDrag||event.button!==0||event.target.closest('button'))return;const rect=$('annotation-editor').getBoundingClientRect();annotationDrag={pointer:event.pointerId,x:event.clientX,y:event.clientY,left:rect.left,top:rect.top};annotationHeader.setPointerCapture(event.pointerId);event.preventDefault();});
annotationHeader.addEventListener('pointermove',event=>{if(!annotationDrag||annotationDrag.pointer!==event.pointerId)return;annotationPosition={x:annotationDrag.left+event.clientX-annotationDrag.x,y:annotationDrag.top+event.clientY-annotationDrag.y};positionEditor();});
annotationHeader.addEventListener('pointerup',event=>{if(annotationDrag?.pointer===event.pointerId){annotationDrag=null;annotationHeader.releasePointerCapture(event.pointerId);}});
annotationHeader.addEventListener('pointercancel',()=>{annotationDrag=null;});
$('annotation-editor').addEventListener('pointerleave',resetAnnotationDelete);
$('annotation-editor').addEventListener('focusout',()=>queueMicrotask(()=>{if(!$('annotation-editor').contains(document.activeElement))resetAnnotationDelete();}));

function closePopups(closeEditor=false){for(const id of ['options','answer-menu'])$(id).hidden=true;if(closeEditor)commitAnnotation();}
function placePopup(id,anchor,above=false){const el=$(id);el.hidden=false;const r=anchor.getBoundingClientRect();el.style.left=Math.max(6,Math.min(r.left,innerWidth-el.offsetWidth-8))+'px';el.style.top=Math.max(6,Math.min(above?r.top-el.offsetHeight:r.bottom+4,innerHeight-el.offsetHeight-8))+'px';}
function setBusy(value){$('send').hidden=value;$('stop').hidden=!value;for(const id of ['permission','model','effort'])$(id).disabled=value;renderEntries();}
const playbackResponseMs=1700;
function send(retryTurn=null){
 if(running)return;const c=current(),prompt=retryTurn?(retryTurn.prompt??retryTurn.question):$('composer').value.trim(),selected=retryTurn?clone(retryTurn.request?.annotations||retryTurn.annotations||[]):c.entries.filter(e=>e.selected);
 if(editing)commitAnnotation();
 if(!selected.length&&!prompt){status('请输入问题，或填写并勾选条目。');return;}
 if(!prompt&&!selected.some(e=>e.question.trim())){status('请在编辑栏描述要问的问题。');$('composer').focus();return;}
 if(selected.length>20){status('每批最多 20 个问题，请分批提交。');return;}
 const notes=retryTurn?[]:annotationRevisionRequests(prompt,selected,c.turns);
 const explicit=!retryTurn&&prototypeRevisionRequest(prompt,c.turns);
 if(new Set(notes.map(note=>note.target)).size>1||(notes.length&&explicit&&explicit.target!==notes[0].target)){
  status('本次有多个改稿目标，请分别发送对应的批注。');return;
 }
 const revision=notes[0]||explicit;
 const outgoing=clone(selected).map((e,i)=>({...e,number:i+1}));
 closePopups(true);const beforeSend={entries:c.entries,draft:c.draft,title:c.title,updatedAt:c.updatedAt,lastSentAt:c.lastSentAt};c.updatedAt=new Date().toISOString();const question=[prompt,...outgoing.map(e=>'#'+e.number+' · '+e.title+'：'+e.question)].filter(Boolean).join('\n');
 const annotations=outgoing.map(e=>({...e,summary:summarizeSnapshot(e),block:null}));
 const t={id:'sim-'+Date.now(),question,prompt,annotations,request:{prompt,annotations:clone(outgoing),model:retryTurn?.request?.model||conversationModel(c),effort:retryTurn?.request?.effort||conversationEffort(c),displayInstructions:DATA.prompts.answer,titleInstructions:DATA.prompts.title,...(c.inheritedContext?{inheritedContext:clone(c.inheritedContext)}:{})},answer:'',answerHtml:'',incomplete:true,sentAt:new Date().toISOString(),segments:[]};
 if(revision){
  Object.assign(t,{id:crypto.randomUUID(),incomplete:false,answeredAt:t.sentAt});
  c.turns.push(t);c.draft='';c.entries=c.entries.filter(e=>!selected.includes(e));c.lastSentAt=t.sentAt;
  if(!persist()){c.turns.pop();Object.assign(c,beforeSend);return;}
  $('composer').value='';refreshEditors();renderTurns();renderEntries();
  window.turnRevision.askAi(revision.target,revision.instruction,{replyId:t.id,entryId:revision.entry?.id,remaining:notes.slice(1).map(note=>note.entry.id)});return;
 }
 c.lastSentAt=t.sentAt;c.turns.push(t);if(!retryTurn)c.entries=c.entries.filter(e=>!selected.includes(e));if(c.title==='新对话')c.title=shortTitle(question);if(!retryTurn)c.draft='';if(!persist()){c.turns.pop();Object.assign(c,beforeSend);return;}if(!retryTurn){$('composer').value='';refreshEditors();}if($('entries-dialog').open)$('entries-dialog').close();selected.forEach(e=>e.state='提交中');const job={turn:t,selected,timers:[]};requests.set(c.id,job);running=job;renderTabs();status('');setBusy(true);renderTurns();scrollEnd();record('send',{question,entries:selected.map(e=>e.number)});
 const mode=$('playback-mode').value;
 const finish=()=>{
  if(requests.get(c.id)!==job)return;const visible=state.active===c.id;const follow=visible&&$('reader').scrollHeight-$('reader').scrollTop-$('reader').clientHeight<40;
  if(mode==='failure'){job.retryCount=(job.retryCount||0)+1;const wait=Math.min(30,2**Math.min(job.retryCount-1,5));const countdown=remaining=>{if(requests.get(c.id)!==job)return;if(!remaining){finish();return;}setPendingStatus(job,'网络连接中断，'+remaining+' 秒后重试 · 第 '+job.retryCount+' 次');job.timers.push(setTimeout(()=>countdown(remaining-1),1000));};countdown(wait);record('network-retry',{attempt:job.retryCount});return;}
  if(mode==='program-failure'){t.incomplete=true;t.endReason='program';t.endedAt=new Date().toISOString();selected.forEach(e=>e.state='未完成，可重试');t.segments.push({kind:'activity',text:'本轮未完成：程序无法继续处理。',details:'原型预编失败事件。',time:t.endedAt});}
  else if(mode==='decision'){
   t.incomplete=false;
   turnDecision.wait(t,{id:crypto.randomUUID(),question:'你想先看哪一种说明？',options:['一步步推导','先看例子'],draft:'',
    results:['正方形的四条边长度相同。周长是四条边之和，所以周长等于边长的四倍。','取一个具体例子：正方形边长为 3，周长就是 4 × 3 = 12。']});
  }
  else {t.answer=DATA.playback.answer;t.answerHtml=DATA.playback.answerHtml.replace(/id="([^"]+)"/g,(_,id)=>'id="'+t.id+'-'+id+'"').replace(/data-block="([^"]+)"/g,(_,id)=>'data-block="'+t.id+'-'+id+'"').replace(/href="#([^"]+)"/g,(_,id)=>'href="#'+t.id+'-'+id+'"');t.incomplete=false;t.answeredAt=new Date().toISOString();t.segments.push({kind:'activity',text:'公式排版检查通过 · 回放完成',time:t.answeredAt});}
  requests.delete(c.id);if(visible){running=null;setBusy(false);renderTurns();if(follow)scrollEnd();}renderTabs();if(activeView==='history')historyDialog.refresh();record('finish',{mode});
 };
 if(mode==='repair')job.timers.push(setTimeout(()=>{if(requests.get(c.id)===job){t.segments.push({kind:'activity',text:'正在校对公式 · 第 1 次自动修正',details:'现编回放：公式定界符未闭合，已请求修正。',time:new Date().toISOString()});if(state.active===c.id)renderTurns();}},900));
 job.timers.push(setTimeout(finish,mode==='repair'?3200:playbackResponseMs));
}
function stopConversation(id=state.active){const job=requests.get(id);if(!job)return;job.timers.forEach(clearTimeout);if(job.turn.decision?.confirmed){turnDecision.finish(job.turn);delete job.turn.answeredAt;}job.turn.incomplete=true;job.turn.endReason='user';job.turn.endedAt=new Date().toISOString();job.turn.segments.push({kind:'activity',text:'已停止。已完成的修改保留；可继续提问。',time:job.turn.endedAt});requests.delete(id);if(id===state.active){running=null;setBusy(false);renderTurns();status('已停止；本轮已保留，可继续提问。');}record('stop',{id});}
function newConversation(){saveReading();const c={id:crypto.randomUUID(),title:'新对话',updatedAt:new Date().toISOString(),turns:[],entries:[],next:1,draft:'',permission:'完全访问',model:defaultModel(),effort:defaultEffort()};state.conversations.push(c);openConversation(c.id);record('new-conversation');$('composer').focus();}
function search(){const q=$('search-input').value.trim().toLowerCase();matches=q?current().turns.filter(t=>(t.question+' '+t.answer).toLowerCase().includes(q)):[];matchIndex=0;showMatch();}
function showMatch(){if(!matches.length){$('search-count').textContent='0';return;}matchIndex=(matchIndex+matches.length)%matches.length;$('search-count').textContent=(matchIndex+1)+'/'+matches.length;const turn=matches[matchIndex];if(revealFoldedTurn(turn))renderTurns();scrollToReadingTarget($(turn.id),'start');saveReading();}
function tabTitle(title){const chars=[...new Intl.Segmenter('zh',{granularity:'grapheme'}).segment(title)].map(x=>x.segment);return chars.slice(0,15).join('')+(chars.length>15?'…':'');}
function renderTabs(){
 state.openTabs=openTabs;const root=$('workspace-tabs');
 for(const tab of [...root.children])if(!openTabs.includes(tab.dataset.tab))tab.remove();
 openTabs.forEach((id,index)=>{
  const c=state.conversations.find(c=>c.id===id),title=c?.title||(id==='settings'?'AI 设置':'AI 会话历史');
  let tab=[...root.children].find(t=>t.dataset.tab===id);
  if(!tab){tab=document.createElement('div');tab.className='app-tab';tab.setAttribute('role','tab');tab.tabIndex=0;tab.dataset.tab=id;tab.innerHTML='<span class="tab-label"></span><button data-close-tab="'+esc(id)+'">'+ICON_CLOSE+'</button>';}
  tab.setAttribute('aria-selected',String(activeView===id));tab.title=title;
  const label=tab.querySelector('.tab-label');if(label.textContent!==tabTitle(title))label.textContent=tabTitle(title);
  tab.querySelector('button').setAttribute('aria-label','关闭 '+title);
  if(root.children[index]!==tab)root.insertBefore(tab,root.children[index]||null);
 });
}
function showView(id){stopReaderAutoScroll();if(activeView==='history')historyDialog.commit();if(activeView===state.active)saveReading();activeView=id;if(!openTabs.includes(id))openTabs.push(id);$('chat').hidden=['settings','history'].includes(id);$('settings').hidden=id!=='settings';$('history').hidden=id!=='history';$('closed').hidden=!!openTabs.length;if(id==='history')historyDialog.open();else if(id==='settings'){selectStoredValue($('settings-model'),defaultModel());selectStoredValue($('settings-effort'),defaultEffort());}renderTabs();persist();}
function openConversation(id){if(!state.conversations.some(c=>c.id===id))return;commitAnnotation();saveReading();state.active=id;showView(id);renderConversation();record('open-conversation',{id});}
function closeTab(id){if(id===activeView&&id===state.active)saveReading();if(id==='history')historyDialog.commit();stopConversation(id);openTabs=openTabs.filter(x=>x!==id);if(activeView===id){const next=openTabs.at(-1);if(next){if(['settings','history'].includes(next))showView(next);else openConversation(next);}else{activeView=null;$('chat').hidden=$('history').hidden=$('settings').hidden=true;$('closed').hidden=false;}}renderTabs();if(activeView==='history')historyDialog.refresh();persist();}
function showSettings(){closePopups(true);showView('settings');}
function showChat(){openConversation(state.active);}
function showHistory(){closePopups(true);showView('history');}
function showAiMenu(){const b=$('ai-menu-button'),menu=$('ai-menu');menu.hidden=!menu.hidden;b.setAttribute('aria-expanded',String(!menu.hidden));$('ai-history-menu').hidden=true;if(!menu.hidden){const r=b.getBoundingClientRect();menu.style.top=r.bottom+'px';menu.style.left=r.left+'px';menu.querySelector('button').focus();}}
function hideAiMenu(){$('ai-menu').hidden=$('ai-history-menu').hidden=true;$('ai-menu-button').setAttribute('aria-expanded','false');$('ai-recent').setAttribute('aria-expanded','false');}
function showRecent(){const menu=$('ai-history-menu'),b=$('ai-recent');menu.innerHTML=recentConversations(state.conversations).map(c=>'<button role="menuitem" data-recent="'+esc(c.id)+'" title="'+esc(c.title)+' · 最后发送：'+esc(time(lastSend(c)))+'">'+esc(tabTitle(c.title))+'</button>').join('')||'<button disabled>暂无发送记录</button>';menu.innerHTML+='<hr><button role="menuitem" data-manage>管理 AI 会话历史</button>';const r=b.getBoundingClientRect();menu.hidden=false;menu.style.left=Math.min(r.right,innerWidth-menu.offsetWidth-6)+'px';menu.style.top=Math.min(r.top,innerHeight-menu.offsetHeight-6)+'px';b.setAttribute('aria-expanded','true');}
$('workspace-tabs').onclick=e=>{const close=e.target.closest('[data-close-tab]');if(close){closeTab(close.dataset.closeTab);return;}const tab=e.target.closest('[data-tab]');if(tab){const id=tab.dataset.tab;if(['settings','history'].includes(id))showView(id);else openConversation(id);}};
$('workspace-tabs').onmousedown=e=>{if(e.button===1&&e.target.closest('[data-tab]'))e.preventDefault();};
$('workspace-tabs').onauxclick=e=>{if(e.button!==1)return;const tab=e.target.closest('[data-tab]');if(tab){e.preventDefault();closeTab(tab.dataset.tab);}};
$('workspace-tabs').onkeydown=e=>{if((e.key==='Enter'||e.key===' ')&&e.target.dataset.tab){e.preventDefault();e.target.click();}};
let renamingConversation=null;
$('workspace-tabs').ondblclick=e=>{
 const tab=e.target.closest('[data-tab]');if(!tab||e.target.closest('[data-close-tab]'))return;
 const conversation=state.conversations.find(c=>c.id===tab.dataset.tab);if(!conversation)return;
 e.preventDefault();renamingConversation=conversation.id;$('rename-title').value=conversation.title;$('rename-save').disabled=!conversation.title.trim();
 $('rename-dialog').showModal();$('rename-title').focus();$('rename-title').select();
};
$('rename-title').oninput=()=>{$('rename-save').disabled=!$('rename-title').value.trim();};
$('rename-cancel').onclick=()=>$('rename-dialog').close();
$('rename-form').onsubmit=e=>{
 e.preventDefault();const title=$('rename-title').value.trim();if(!title)return;
 const conversation=state.conversations.find(c=>c.id===renamingConversation);if(conversation){conversation.title=title;renderTabs();record('rename-conversation',{id:conversation.id,title});}
 $('rename-dialog').close();
};
$('ai-menu-button').onclick=showAiMenu;$('ai-new').onclick=()=>{hideAiMenu();newConversation();};$('ai-settings').onclick=()=>{hideAiMenu();showSettings();};$('ai-recent').onclick=showRecent;
$('ai-history-menu').onclick=e=>{const b=e.target.closest('button');if(!b)return;hideAiMenu();if(b.hasAttribute('data-manage'))showHistory();else if(b.dataset.recent)openConversation(b.dataset.recent);};
function openLab(){$('provenance').textContent=DATA.provenance;$('lab-transcripts').textContent='合成示例问题：\n'+DATA.turns.map(t=>t.question).join('\n\n')+'\n\n预编续答：\n'+DATA.playback.answer;$('lab-events').textContent=JSON.stringify(state.events,null,2);$('lab').showModal();}
async function copy(text){try{await navigator.clipboard.writeText(text);}catch{$('copy-text').value=text;$('copy-dialog').showModal();$('copy-text').select();}closePopups();}
function mdMath(text){return text.replace(/\\\[([\s\S]*?)\\\]/g,(_,s)=>'$$\n'+s.trim()+'$$\n').replace(/\\\(([\s\S]*?)\\\)/g,(_,s)=>' $'+s.trim()+'$ ');}
$('entries').addEventListener('input',e=>{const id=e.target.dataset.question;if(id){const n=current().entries.find(e=>e.id===id);n.question=e.target.value;if(editing===id)$('annotation-input').value=n.question;persist();}});
$('entries').addEventListener('change',e=>{const id=e.target.dataset.select;if(id){current().entries.find(e=>e.id===id).selected=e.target.checked;record('select-entry',{id,selected:e.target.checked});}});
$('entries').addEventListener('click',e=>{const b=e.target.closest('[data-remove]');if(b)removeEntry(b.dataset.remove);});
$('annotation-input').oninput=()=>{const e=current().entries.find(e=>e.id===editing);if(!e)return;e.question=$('annotation-input').value;const field=$('entries').querySelector('[data-question="'+e.id+'"]');if(field)field.value=e.question;updateAnnotationDeleteHint();persist();};
$('send-annotation').onclick=()=>confirmAnnotation(editing);
let deleteKeyboardAt=0;
$('delete-annotation').onclick=event=>{const e=current().entries.find(entry=>entry.id===editing);if(!e||protectedAnnotation(e))return;if(annotationCanDrag){if(annotationDeleteArmed)removeEntry(editing);else{annotationDeleteArmed=true;$('annotation-delete-hint').hidden=false;}return;}if(event.detail!==0){deleteKeyboardAt=0;return;}const now=Date.now();if(now-deleteKeyboardAt<=500){deleteKeyboardAt=0;removeEntry(editing);}else deleteKeyboardAt=now;};
$('delete-annotation').onblur=()=>{deleteKeyboardAt=0;};
$('delete-annotation').ondblclick=()=>{if(annotationCanDrag)return;deleteKeyboardAt=0;removeEntry(editing);};
$('thread').addEventListener('contextmenu',e=>{const ctx=targetContext(e.target,e);if(!ctx)return;e.preventDefault();context=ctx;closePopups();const menu=$('answer-menu');$('stop-answer-marking').hidden=!answerMarking;menu.hidden=false;menu.style.left=Math.min(e.clientX,innerWidth-menu.offsetWidth-6)+'px';menu.style.top=Math.min(e.clientY,innerHeight-menu.offsetHeight-6)+'px';$('copy-selection').disabled=!ctx.selected;});
$('thread').addEventListener('click',e=>{const b=e.target.closest('[data-bubble]');if(b)openEditor(b.dataset.bubble);});
$('annotate').onclick=()=>{const ctx=context;closePopups();if(ctx?.selected)addAnnotation(ctx);else setAnswerMarking(true);};
$('stop-answer-marking').onclick=()=>{closePopups();setAnswerMarking(false);};
$('thread').addEventListener('pointerup',e=>{if(!answerMarking||e.button!==0||e.target.closest('input,textarea,button,a,img'))return;const sel=window.getSelection();if(!sel?.rangeCount||sel.isCollapsed)return;const ctx=targetContext(e.target);if(ctx?.selected)addAnnotation(ctx);});
$('branch-conversation').onclick=()=>{const ctx=context;closePopups();openBranchFrom(ctx);};
$('copy-selection').onclick=()=>{if(context)copy(context.quote);closePopups();};
$('copy-answer').onclick=()=>{if(context)copy(context.paragraph);closePopups();};
const stopReaderAutoScroll=installReaderAutoScroll($('reader'));
$('reader').addEventListener('wheel',cancelReadingRestore,{passive:true});
$('reader').addEventListener('pointerdown',event=>{if(event.isTrusted)cancelReadingRestore();},{passive:true});
$('reader').addEventListener('keydown',e=>{if(!e.altKey&&!e.ctrlKey&&!e.metaKey&&!e.target.closest('input,textarea,select,[contenteditable="true"]')&&['ArrowUp','ArrowDown','PageUp','PageDown','Home','End',' '].includes(e.key))cancelReadingRestore();});
$('reader').onscroll=()=>{
 const correction=chromeScrollOffset;chromeScrollOffset=null;
 if(correction===$('reader').scrollTop)return;
 commitAnnotation();$('answer-menu').hidden=true;
 if(!restoring){current().reading=readingPosition();clearTimeout(scrollTimer);scrollTimer=setTimeout(persist,160);}
};
window.addEventListener('pagehide',saveReading);
$('composer').oninput=()=>{current().draft=$('composer').value;persist();};$('composer').onkeydown=e=>{if(e.key==='Enter'&&e.ctrlKey&&!e.shiftKey&&!e.altKey){e.preventDefault();e.stopPropagation();if(!e.repeat&&!e.isComposing)send();}};
$('send').onclick=()=>send();$('stop').onclick=()=>stopConversation();
$('options-button').onclick=()=>{const was=$('options').hidden;closePopups();if(was)placePopup('options',$('options-button'),true);};
$('options').onclick=e=>{const op=e.target.dataset.option;if(!op)return;closePopups();if(op==='add'){const c=current();c.entries.push({id:'e'+Date.now(),number:c.next++,title:'问题',question:'',selected:true,state:'待提交',sources:[],pdf:[],quote:''});renderEntries();record('add-entry');$('entries-dialog').showModal();refreshEditors();}if(op==='all'){current().entries.forEach(e=>e.selected=true);renderEntries();record('select-all');}if(op==='clear'){current().entries=current().entries.filter(e=>e.state!=='已完成');renderEntries();record('clear-done');}if(op==='settings')showSettings();if(op==='refresh')status('原型未连接账号；可用演示模型：GPT-6.1-Sol。');};
$('permission').onchange=()=>{current().permission=$('permission').value;record('permission',{value:current().permission});};
$('model').onchange=()=>{const c=current(),before=c.model;c.model=$('model').value;if(!persist()){c.model=before;selectStoredValue($('model'),conversationModel(c));return;}record('model',{value:c.model});};
$('effort').onchange=()=>{const c=current(),before=c.effort;c.effort=$('effort').value;if(!persist()){c.effort=before;selectStoredValue($('effort'),conversationEffort(c));return;}record('effort',{value:c.effort});};
$('settings-model').onchange=()=>{const before=state.defaultModel;state.defaultModel=$('settings-model').value;if(!persist()){state.defaultModel=before;selectStoredValue($('settings-model'),defaultModel());}};
$('settings-effort').onchange=()=>{const before=state.defaultEffort;state.defaultEffort=$('settings-effort').value;if(!persist()){state.defaultEffort=before;selectStoredValue($('settings-effort'),defaultEffort());}};

let settingsOperation=0,settingsTimer=null;
function cancelSettingsOperation(message){settingsOperation++;clearTimeout(settingsTimer);settingsTimer=null;$('settings-cancel').disabled=true;$('settings-status').textContent=message;}
function beginSettingsOperation(kind){
 const operation=++settingsOperation;clearTimeout(settingsTimer);$('settings-cancel').disabled=false;
 $('settings-status').textContent=kind==='login'||kind==='add'?'正在模拟浏览器授权；其他设置仍可使用。':'正在模拟连接操作；其他设置仍可使用。';
 settingsTimer=setTimeout(()=>{if(operation!==settingsOperation)return;settingsTimer=null;$('settings-cancel').disabled=true;$('settings-status').textContent=kind==='login'||kind==='add'?'模拟登录失败，可直接重试或修改设置。':'模拟操作已完成；未连接账号或网络。';},1600);
}
document.querySelectorAll('[data-setting]').forEach(button=>button.onclick=()=>{const kind=button.dataset.setting;if(kind==='clear-key'){$('settings-key').value='';cancelSettingsOperation('已清除示例输入；原型不保存密钥。');return;}beginSettingsOperation(kind);});
$('settings-cancel').onclick=()=>cancelSettingsOperation('已取消模拟连接，可直接重试。');
$('settings-account').onchange=()=>beginSettingsOperation('switch');
$('settings-use-key').onchange=()=>cancelSettingsOperation('已切换示例连接方式；原型不连接账号或网络。');
$('search-input').oninput=search;$('search-prev').onclick=()=>{matchIndex--;showMatch();};$('search-next').onclick=()=>{matchIndex++;showMatch();};
const historyDialog=createHistoryManager({getConversations:()=>state.conversations,getActive:()=>state.active,isOpen:id=>openTabs.includes(id),
 rename:(id,title)=>{const c=state.conversations.find(c=>c.id===id);if(c)c.title=title;renderTabs();record('rename-conversation',{id,title});},
 remove:ids=>{if(ids.some(id=>openTabs.includes(id)))throw new Error('会话窗口已打开，关闭后才能删除。');state.conversations=state.conversations.filter(c=>!ids.includes(c.id));if(!state.conversations.length){const c={id:crypto.randomUUID(),title:'新对话',turns:[],entries:[],next:1,draft:''};state.conversations.push(c);}if(ids.includes(state.active))state.active=state.conversations[0].id;record('delete-conversations',{ids});},
 open:openConversation
});
$('reopen').onclick=showChat;
document.addEventListener('pointerdown',e=>{if(!e.target.closest('#ai-menu,#ai-history-menu,#ai-menu-button'))hideAiMenu();});
document.addEventListener('pointerdown',e=>{if(!e.target.closest('#options,#options-button'))$('options').hidden=true;if(!e.target.closest('#answer-menu'))$('answer-menu').hidden=true;if(answerMarking&&!e.target.closest('#reader,#answer-menu,#annotation-editor'))setAnswerMarking(false);});
let searchPreviousFocus=null;
document.addEventListener('keydown',e=>{
 const key=e.key.toLowerCase();
 if(e.ctrlKey&&e.altKey&&key==='t'){e.preventDefault();openLab();}
 if(e.ctrlKey&&!e.altKey&&key==='f'){
  e.preventDefault();
  if(activeView==='history')$('history-search').focus();
  else if(!$('chat').hidden){
   cancelReadingRestore();
   if($('search').hidden)searchPreviousFocus=document.activeElement;
   $('search').hidden=false;$('search-input').focus({preventScroll:true});
  }
 }
 if(e.altKey&&key==='a'){e.preventDefault();showAiMenu();}
 if(e.key==='Escape'){
  setAnswerMarking(false);hideAiMenu();closePopups();
  const returnFocus=$('search').contains(document.activeElement);
  $('search').hidden=true;
  if(returnFocus)(searchPreviousFocus?.isConnected&&searchPreviousFocus!==document.body?searchPreviousFocus:$('reader')).focus({preventScroll:true});
  searchPreviousFocus=null;
 }
});
document.addEventListener('copy',e=>{if(e.target.closest('textarea,input'))return;const sel=window.getSelection();if(!sel?.rangeCount||sel.isCollapsed)return;const r=sel.getRangeAt(0),start=r.startContainer.nodeType===1?r.startContainer:r.startContainer.parentElement;if(!start.closest('.answer'))return;e.clipboardData.setData('text/plain',serializeReadingSelection(r));e.preventDefault();});
$('restore').onclick=()=>{for(const id of [...requests.keys()])stopConversation(id);state=fresh();openTabs=[state.active];migrateSnapshots();persist();$('lab').close();showChat();renderConversation();requestAnimationFrame(()=>$('entries').scrollTop=$('entries').scrollHeight);};
$('close-lab').onclick=()=>$('lab').close();$('close-copy').onclick=()=>$('copy-dialog').close();
$('export').onclick=()=>{const blob=new Blob([JSON.stringify({basis:DATA.basis,provenance:DATA.provenance,scripted:DATA.playback,...state},null,2)],{type:'application/json'});const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download='texeed-baseline-transcript.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};

// Text inputs share the source editor font, wrapping, logical line numbers and shortcuts.
function mountEditors(root){for(const field of root.querySelectorAll('textarea')){if(field.closest('.code-editor'))continue;const box=document.createElement('div');box.className='code-editor';field.before(box);const gutter=document.createElement('div');gutter.className='line-numbers';gutter.setAttribute('aria-hidden','true');gutter.innerHTML='<div></div>';box.append(gutter,field);field.wrap='soft';field.spellcheck=false;
 if(field.dataset.question){const b=document.createElement('button');b.type='button';b.className='editor-action';b.title='加入 AI 会话待发区 · Ctrl+Enter';b.setAttribute('aria-label','确认圈点 '+current().entries.find(e=>e.id===field.dataset.question)?.number);b.innerHTML=ICON_SEND;b.onclick=()=>confirmAnnotation(field.dataset.question);box.append(b);}
 field.addEventListener('input',()=>updateEditor(field));field.addEventListener('scroll',()=>{gutter.firstElementChild.style.transform='translateY(-'+field.scrollTop+'px)';});
 field.addEventListener('keydown',event=>{if(event.key==='Enter'&&event.ctrlKey&&!event.shiftKey&&!event.altKey&&(field.dataset.question||field.id==='annotation-input')){event.preventDefault();event.stopPropagation();const id=field.dataset.question||editing;if(!event.repeat&&!event.isComposing&&id)confirmAnnotation(id);}else if(event.key==='Tab'){event.preventDefault();field.setRangeText('    ',field.selectionStart,field.selectionEnd,'end');field.dispatchEvent(new Event('input',{bubbles:true}));}});updateEditor(field);
 }}
function updateEditor(field){const box=field.closest('.code-editor');if(!box||!field.clientWidth)return;const style=getComputedStyle(field),measure=document.createElement('div');measure.className='editor-measure';measure.style.font=style.font;measure.style.lineHeight=style.lineHeight;measure.style.width=(field.clientWidth-parseFloat(style.paddingLeft)-parseFloat(style.paddingRight))+'px';measure.style.tabSize=style.tabSize;box.append(measure);const lineHeight=parseFloat(style.lineHeight)||24,lines=field.value.split('\n');if(field.id==='composer')while(lines.length<3)lines.push('');const heights=lines.map(line=>{measure.textContent=line||' ';return Math.max(lineHeight,measure.getBoundingClientRect().height);});measure.remove();if(field.id==='composer'||field.id==='annotation-input')box.querySelector('.line-numbers').style.width='calc('+String(heights.length).length+'ch + 8px)';box.querySelector('.line-numbers>div').innerHTML=heights.map((height,i)=>'<span style="height:'+height+'px">'+(i+1)+'</span>').join('');box.querySelector('.line-numbers>div').style.transform='translateY(-'+field.scrollTop+'px)';}
function restoreComposerHeight(){const field=$('composer');field.style.height=Math.max(41,Math.min(composerHeights.get(current().id)||81,Math.max(41,$('chat').clientHeight*.65)))+'px';}
function resizeComposer(height){composerHeights.set(current().id,height);restoreComposerHeight();updateEditor($('composer'));}
function installComposerResize(){const grip=document.createElement('div');grip.className='composer-resize';grip.setAttribute('role','separator');grip.setAttribute('aria-orientation','horizontal');grip.setAttribute('aria-label','调整编辑栏高度');grip.tabIndex=0;$('composer').closest('.composer-shell').prepend(grip);let start;
 grip.onpointerdown=e=>{if(e.button!==0)return;e.preventDefault();start={y:e.clientY,height:$('composer').clientHeight};grip.setPointerCapture(e.pointerId);};
 grip.onpointermove=e=>{if(start)resizeComposer(start.height+start.y-e.clientY);};
 grip.onpointerup=grip.onpointercancel=()=>{start=null;};
 grip.onkeydown=e=>{if(['ArrowUp','ArrowDown','Home'].includes(e.key)){e.preventDefault();resizeComposer(e.key==='Home'?81:$('composer').clientHeight+(e.key==='ArrowUp'?20:-20));}};
}
$('thread').addEventListener('toggle',e=>{if(e.target.matches('.activity')&&e.target.open)mountActivityCode(e.target);},true);
function refreshEditors(){document.querySelectorAll('.code-editor textarea').forEach(updateEditor);}
function summarizeSnapshot(e){return e.summary||contextText(e).replace(/\s+/g,' ').slice(0,48)||e.question.trim().slice(0,48)||'圈选内容';}
function migrateSnapshots(){
 if(state.snapshotVersion)return;
 for(const c of state.conversations)for(const t of c.turns){
  if(t.annotations?.length||t.prompt!==undefined)continue;
  let prompt='\n'+t.question+'\n';
  const labels=e=>['#'+e.number+' · '+e.title,e.title];
  const matched=c.entries.filter(e=>['已完成','提交中','未完成，可重试','上次提交未完成，可重试'].includes(e.state)&&labels(e).some(label=>prompt.includes('\n'+label+'：'+e.question+'\n')));
  if(!matched.length)continue;
  for(const e of matched)for(const label of labels(e))prompt=prompt.replace('\n'+label+'：'+e.question+'\n','\n');
  t.prompt=prompt.replace(/^\n|\n$/g,'');t.annotations=clone(matched).map(e=>({...e,block:null,summary:e.summary||e.question}));
 }
 state.snapshotVersion=1;
}
function reconcileSentEntries(){for(const c of state.conversations){const sent=new Map(c.turns.flatMap(t=>t.annotations||[]).map(e=>[e.id,e]));c.entries=c.entries.filter(e=>{const old=sent.get(e.id);return !old||['question','title','sources','pdf','quote'].some(k=>JSON.stringify(e[k])!==JSON.stringify(old[k]));});}}
$('entries-button').onclick=()=>{closePopups(true);renderEntries();$('entries-dialog').showModal();refreshEditors();};$('close-entries').onclick=()=>$('entries-dialog').close();$('close-source').onclick=()=>$('source-dialog').close();
$('entries').addEventListener('dblclick',event=>{const b=event.target.closest('[data-preview]');if(!b)return;const e=current().entries.find(e=>e.id===b.dataset.preview);e.contextExpanded=!e.contextExpanded;b.classList.toggle('expanded',e.contextExpanded);b.setAttribute('aria-expanded',String(e.contextExpanded));persist();});
$('entries').addEventListener('keydown',event=>{if(event.key==='Enter'&&event.target.dataset.preview){event.preventDefault();event.target.dispatchEvent(new MouseEvent('dblclick',{bubbles:true}));}});
let tooltipTimer;
$('entries').addEventListener('pointerover',event=>{const b=event.target.closest('[data-preview]');if(!b)return;clearTimeout(tooltipTimer);tooltipTimer=setTimeout(()=>{const tooltip=$('context-tooltip'),r=b.getBoundingClientRect();tooltip.textContent=b.textContent;tooltip.hidden=false;tooltip.style.left=Math.max(10,Math.min(r.left,innerWidth-tooltip.offsetWidth-10))+'px';tooltip.style.top=Math.max(10,Math.min(r.bottom+5,innerHeight-tooltip.offsetHeight-10))+'px';// Keep the tooltip in the modal top layer.
 if($('entries-dialog').open)$('entries-dialog').append(tooltip);},350);});
$('entries').addEventListener('pointerout',()=>{clearTimeout(tooltipTimer);$('context-tooltip').hidden=true;});
$('entries-dialog').addEventListener('close',()=>{clearTimeout(tooltipTimer);$('context-tooltip').hidden=true;document.body.append($('context-tooltip'));});
$('thread').addEventListener('click',event=>{const b=event.target.closest('button');if(!b)return;if(b.dataset.openChild){openConversation(b.dataset.openChild);return;}if(b.id==='branch-origin-return'||b.hasAttribute('data-back-origin')){returnBranchOrigin();return;}if(b.dataset.retryTurn){const t=current().turns.at(-1);if(!running&&t?.id===b.dataset.retryTurn&&turnEnd(t).incomplete)send(t);return;}if(b.dataset.file){openSource(b.dataset.file);return;}if(b.dataset.section){const answer=b.closest('.answer'),turn=current().turns.find(t=>t.id===answer.dataset.turn),p=$(b.dataset.section),offset=p.getBoundingClientRect().top;turn.sections||={};turn.sections[p.id]=!turn.sections[p.id];renderTurns();$('reader').scrollTop+=$(p.id).getBoundingClientRect().top-offset;saveReading();}});
// Persist answer and heading folds independently of activity disclosures.
$('thread').addEventListener('dblclick',event=>{
 const hint=event.target.closest('[data-fold-run]')||event.target.closest('[data-fold-run-line]')?.querySelector('[data-fold-run]');
 if(hint){event.preventDefault();changeFoldRun(hint,false);return;}
 const divider=event.target.closest('[data-turn-divider]');
 if(!divider||event.target.closest('button,a'))return;
 event.preventDefault();
 const t=current().turns.find(t=>t.id===divider.dataset.turnDivider);if(t)toggleTurn(t,divider);
});
$('thread').addEventListener('click',event=>{
 const hint=event.target.closest('[data-fold-run]');
 if(hint&&event.detail===0)changeFoldRun(hint,true);
});
$('thread').addEventListener('keydown',event=>{
 if(event.key!=='Enter'&&event.key!==' ')return;
 const hint=event.target.closest('[data-fold-run]'),id=event.target.dataset.toggleTurn;
 if(!hint&&!id)return;
 event.preventDefault();
 if(event.repeat)return;
 if(hint){changeFoldRun(hint,true);return;}
 const t=current().turns.find(t=>t.id===id);
 if(t)toggleTurn(t,event.target)?.focus({preventScroll:true});
});
$('thread').addEventListener('click',event=>{const link=event.target.closest('[data-open-turn]');if(!link)return;const turn=current().turns.find(t=>t.id===link.dataset.openTurn);if(!turn)return;if(revealFoldedTurn(turn))renderTurns();scrollToReadingTarget($(turn.id),'center');saveReading();});
$('thread').addEventListener('click',event=>{const link=event.target.closest('[data-snapshot-turn]');if(link){event.preventDefault();showSnapshot(link);return;}const close=event.target.closest('[data-close-preview]');if(close){const preview=$(close.dataset.closePreview+'-preview'),number=preview.dataset.number;hideSnapshot(preview);document.querySelector('[data-snapshot-turn="'+close.dataset.closePreview+'"][data-snapshot-number="'+number+'"]').focus({preventScroll:true});}});
$('thread').addEventListener('keydown',event=>{if(event.key==='Escape'){const preview=event.target.closest('.snapshot-preview');if(preview){event.preventDefault();preview.querySelector('[data-close-preview]').click();}}});
// Keep formula clicks in the reading surface.
$('thread').addEventListener('click',event=>{if(event.target.closest('.math'))event.preventDefault();});
$('annotation-input').addEventListener('input',()=>{const field=$('entries').querySelector('[data-question="'+editing+'"]');if(field)updateEditor(field);});
$('entries').addEventListener('input',event=>{if(event.target.dataset.question===editing)updateEditor($('annotation-input'));});
window.addEventListener('resize',()=>{restoreComposerHeight();refreshEditors();alignReadingInk(false);alignPendingIndicator();});


// Restore saved titles and reading anchors.
function shortTitle(value){return [...value.replace(/\s+/g,' ').trim()].slice(0,20).join('')||'新对话';}
function migrateReadingDesign(){if(state.readingDesignVersion===4)return;for(const c of state.conversations){c.title=shortTitle(c.title);}state.readingDesignVersion=4;}
function readingAnchor(id){let node=$(id);const seen=new Set();while(node?.dataset.joinedInto&&!seen.has(node.id)){seen.add(node.id);node=$(node.dataset.joinedInto);}return node;}
function equationIndex(turnId,nodes){
 const keys=new Map();
 const entries=Array.from(nodes,(math,i)=>{
  const source=math.dataset.tex||'',label=/\\label\{([^{}]+)\}/.exec(source)?.[1];
  const tag=/\\tag\*?\{([^{}]+)\}/.exec(source)?.[1]||String(i+1);
  const formula={id:turnId+'-eq-'+(i+1),turnId,tag,math};
  for(const key of new Set([label,tag,'eq-'+tag]))if(key)keys.set(key,keys.has(key)?null:formula);
  return formula;
 });
 return {entries,find(key){return keys.get(key);}};
}
// A folded turn has no DOM. Read its current published version only on a reference to it.
const referencedEquationIndexes=new WeakMap();
function publishedEquationIndex(turn){
 const version=turn.readingRevisions?.at(-1);
 const cached=referencedEquationIndexes.get(turn);
 if(cached&&cached.version===version&&cached.answer===turn.answer)return cached.index;
 const template=document.createElement('template');
 template.innerHTML=window.turnRevision?.html(turn)??svgHtml(turn);
 const index=equationIndex(turn.id,template.content.querySelectorAll('.equation .math'));
 referencedEquationIndexes.set(turn,{version,answer:turn.answer,index});return index;
}
function resolveEquationReference(key,sourceId,local){
 const qualified=/^([1-9]\d*):(.+)$/.exec(key);
 if(!qualified)return local.find(key);
 const turns=current().turns;
 const bound=turns.find(t=>t.id===sourceId)?.equationReferenceTurns?.[key];
 const destination=bound?turns.find(t=>t.id===bound):turns[Number(qualified[1])-1];
 if(!destination)return null;
 const same=destination.id===sourceId;
 const found=(same?local:publishedEquationIndex(destination)).find(qualified[2]);
 return found&&{...found,tag:same?found.tag:(turns.indexOf(destination)+1)+':'+found.tag};
}
function prepareReadingLayout(answer){
 const tid=answer.dataset.turn;
 const equations=answer.querySelectorAll('.equation'),bodySize=equations.length?parseFloat(getComputedStyle(answer).fontSize):0;
 const local=equationIndex(tid,Array.from(equations,eq=>eq.querySelector('.math')));
 for(const [i,eq] of equations.entries()){const number=eq.querySelector('.equation-number'),math=eq.querySelector('.math');
  const source=math.dataset.tex,target=local.entries[i],tag=target.tag;
  eq.id=target.id;number.textContent='('+tag+')';
  if(math.querySelector('.render-fallback')&&/\\(?:label|tag\*?)\{/.test(source)){
   const clean=source.replace(/\\(?:label|tag\*?)\{[^{}]+\}/g,'').trim();
   math.innerHTML=renderRichText('$$'+clean+'$$',true);
  }
  const tex=math.dataset.tex,svg=math.querySelector('svg'),width=svg?parseFloat(svg.getAttribute('width'))*bodySize:math.getBoundingClientRect().width*bodySize/parseFloat(getComputedStyle(math).fontSize);
  if(width>bodySize*5||/=|\\(?:eq|neq|equiv|leq|geq)/.test(tex))continue;
  const passage=eq.closest('.passage'),previous=passage.previousElementSibling,following=passage.nextElementSibling,prevP=previous?.querySelector('.passage-body>p'),nextP=following?.querySelector('.passage-body>p');
  const anchor=eq.id;eq.removeAttribute('id');math.id=anchor;math.dataset.display='false';
  if(prevP&&!previous.dataset.readingHidden){prevP.append(' ',math,' ');passage.dataset.readingHidden='true';passage.dataset.joinedInto=previous.id;if(nextP&&!following.dataset.readingHidden){prevP.append(...nextP.childNodes);following.dataset.readingHidden='true';following.dataset.joinedInto=previous.id;}}
  else {const p=document.createElement('p');p.append(math);eq.replaceWith(p);}
 }
 answer.querySelectorAll('.math').forEach(m=>m.removeAttribute('tabindex'));
 if(!equations.length&&!/\\(?:eqref|ref)\{|公式\s*[（(]\d+:/.test(answer.textContent))return;
 const pattern=/(?<!\\)\\(eqref|ref)\{([^{}\r\n]+)\}|公式\s*[（(](\d+(?::\d+)?)[)）]/g;
 const walker=document.createTreeWalker(answer,NodeFilter.SHOW_TEXT),nodes=[];
 while(walker.nextNode()){
  const node=walker.currentNode;
  if(/\\(?:eqref|ref)\{|公式\s*[（(]/.test(node.data)&&!node.parentElement.closest('.math,code,button,a,.equation-number'))nodes.push(node);
 }
 for(const node of nodes){
  const fragment=document.createDocumentFragment();let start=0;
  for(const match of node.data.matchAll(pattern)){
   const target=resolveEquationReference(match[2]||match[3],tid,local);
   if(!target)continue;
   const link=document.createElement('button'),direct=match[1]!=='eqref';
   link.type='button';link.className='equation-ref';link.dataset.equationRef=target.id;link.dataset.equationTurn=target.turnId;
   if(direct)link.dataset.direct='true';
   link.textContent=match[3]?match[0]:direct?target.tag:'（'+target.tag+'）';
   link.title=direct?'跳转到公式 '+target.tag:'单击预览公式，双击跳转。Enter 预览，Ctrl+Enter 跳转。';
   if(!direct)link.setAttribute('aria-expanded','false');
   fragment.append(node.data.slice(start,match.index),link);start=match.index+match[0].length;
  }
  if(start){fragment.append(node.data.slice(start));node.replaceWith(fragment);}
 }
}
function jumpToEquation(b){const id=b.dataset.equationRef,turn=current().turns.find(t=>t.id===b.dataset.equationTurn);
 if(!turn)return;
 closeEquationPreview();cancelReadingRestore();
 if(revealFoldedTurn(turn))renderTurns();
 const target=$(id),answer=target?.closest('.answer');if(!answer)return;
 let stack=[],changed=false;
 for(const p of answer.querySelectorAll(':scope>.passage')){
  const h=p.querySelector('h1,h2,h3,h4,h5,h6');if(h){const level=Number(h.tagName.slice(1));stack=stack.filter(x=>x.level<level);stack.push({level,id:p.id});}
  if(p.contains(target)){for(const h of stack)if(turn.sections?.[h.id]){turn.sections[h.id]=false;changed=true;}break;}
 }
 if(changed)renderTurns();
 requestAnimationFrame(()=>{const target=$(id);if(target){scrollToReadingTarget(target,'center');target.classList.add('equation-target');saveReading();}});
}
let equationPreviewLink=null,equationPreviewAnchor=null;
function closeEquationPreview(){
 if(!equationPreviewLink)return;
 equationPreviewLink.setAttribute('aria-expanded','false');equationPreviewLink=null;equationPreviewAnchor=null;
 $('equation-preview')?.remove();
}
function previewEquation(link){
 if(equationPreviewLink===link){closeEquationPreview();return;}
 closeEquationPreview();
 const target=$(link.dataset.equationRef),turn=current().turns.find(t=>t.id===link.dataset.equationTurn);
 const original=(target?.matches('.math')?target:target?.querySelector('.math'))||
  (turn&&publishedEquationIndex(turn).entries.find(eq=>eq.id===link.dataset.equationRef)?.math);
 if(!original)return;
 const popup=document.createElement('aside'),content=original.cloneNode(true);
 popup.id='equation-preview';popup.setAttribute('aria-label','公式预览');
 content.removeAttribute('id');content.removeAttribute('tabindex');content.dataset.display='true';
 if(content.querySelector('.render-fallback'))content.innerHTML=renderRichText('$$'+content.dataset.tex.replace(/\\(?:label|tag\*?)\{[^{}]+\}/g,'').trim()+'$$',true);
 // SVG glyphs must resolve within the preview even when their original heading is folded.
 for(const def of content.querySelectorAll('svg [id]'))def.id='equation-preview-'+def.id;
 for(const use of content.querySelectorAll('svg use'))for(const attr of use.attributes)
  if(attr.localName==='href'&&attr.value.startsWith('#'))attr.value='#equation-preview-'+attr.value.slice(1);
 const body=document.createElement('div'),number=document.createElement('span');
 body.className='equation-preview-body';body.append(content);
 number.className='equation-number';number.textContent=link.textContent.replace('（','(').replace('）',')');
 popup.append(body,number);document.body.append(popup);
 prepareReadingMath(body);
 const anchor=link.getBoundingClientRect(),reader=$('reader').getBoundingClientRect(),gap=6;
 popup.style.maxWidth=Math.max(1,reader.width-2*gap)+'px';
 const above=anchor.top-reader.top-gap,below=reader.bottom-anchor.bottom-gap;
 const showAbove=popup.offsetHeight>below&&above>below;
 popup.style.maxHeight=Math.max(32,showAbove?above:below)+'px';
 popup.style.top=(showAbove?anchor.top-gap-popup.offsetHeight:anchor.bottom+gap)+'px';
 popup.style.left=Math.max(reader.left+gap,Math.min(anchor.left,reader.right-popup.offsetWidth-gap))+'px';
 link.setAttribute('aria-expanded','true');equationPreviewLink=link;equationPreviewAnchor=anchor;
}
$('thread').addEventListener('click',event=>{
 const link=event.target.closest('[data-equation-ref]');if(!link)return;
 if(link.dataset.direct)jumpToEquation(link);
 else if(event.detail<=1)previewEquation(link);
});
$('thread').addEventListener('dblclick',event=>{
 const link=event.target.closest('[data-equation-ref]');if(!link||link.dataset.direct)return;
 event.preventDefault();jumpToEquation(link);
});
$('thread').addEventListener('keydown',event=>{
 const link=event.target.closest('[data-equation-ref]');if(link&&event.ctrlKey&&event.key==='Enter'){event.preventDefault();jumpToEquation(link);}
});
document.addEventListener('pointerdown',event=>{
 if(equationPreviewLink&&!event.target.closest('#equation-preview,[data-equation-ref]'))closeEquationPreview();
});
document.addEventListener('focusin',event=>{
 if(equationPreviewLink&&event.target!==equationPreviewLink&&!event.target.closest('#equation-preview'))closeEquationPreview();
});
document.addEventListener('keydown',event=>{if(event.key==='Escape')closeEquationPreview();});
document.addEventListener('scroll',event=>{
 if(!equationPreviewLink||!event.target.contains?.(equationPreviewLink))return;
 const bounds=equationPreviewLink.getBoundingClientRect();
 // A focus or reveal scroll from before the click can still be waiting for paint.
 if(bounds.top!==equationPreviewAnchor.top||bounds.left!==equationPreviewAnchor.left)closeEquationPreview();
},true);
window.addEventListener('resize',closeEquationPreview);

function annotationLayoutChanged(){if(annotationCanDrag&&editing)positionEditor();else commitAnnotation();}
window.addEventListener('resize',annotationLayoutChanged);
const readingViewport=$('reader');
let lastReaderBounds=null,readerRounding=0,chromeScrollOffset=null;
new ResizeObserver(()=>{
 const bounds=readingViewport.getBoundingClientRect(),previous=lastReaderBounds;
 lastReaderBounds=bounds.height?bounds:null;
 // Opening a toolbar above the transcript changes the screen origin of every visible row.
 // Read the current offset so that an intervening wheel movement is preserved.
 if(previous&&lastReaderBounds&&previous.width===bounds.width&&!restoring&&readingViewport.scrollTop>0){
  if(previous.top!==bounds.top){
   const previousOffset=readingViewport.scrollTop,wanted=previousOffset+bounds.top-previous.top+readerRounding;
   readingViewport.scrollTop=wanted;
   const offset=readingViewport.scrollTop,remainder=wanted-offset;
   if(offset!==previousOffset)chromeScrollOffset=offset;
   // Preserve fractional pixels across repeated opening and closing, but discard a clamped overscroll.
   readerRounding=Math.abs(remainder)<1?remainder:0;
  }
 }else readerRounding=0;
 annotationLayoutChanged();
}).observe(readingViewport);
for(const c of state.conversations)for(const e of c.entries)if(e.state==='提交中')e.state='上次提交未完成，可重试';

mountEditors(document);installComposerResize();installImageViewer();migrateSnapshots();reconcileSentEntries();migrateReadingDesign();renderConversation();showView(state.active);persist();const fontsRestoreVersion=readingRestoreVersion;document.fonts.ready.then(()=>{refreshEditors();alignReadingInk.metrics?.clear();alignReadingInk.inkCache?.clear();alignReadingInk.axisCache?.clear();alignReadingInk();alignPendingIndicator();restoreReading(current().reading,fontsRestoreVersion);});
if(location.search.includes('tools=1'))openLab();

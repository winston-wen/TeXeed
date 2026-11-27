'use strict';
(() => {
 const catalogs = new WeakMap();
 const textPart = part => typeof part === 'string';
 const atomic = ref => ref.startsWith('media:') || ref.startsWith('block:') || ref.startsWith('equation-ref:');
 const live = turn => Array.isArray(turn.readingRevisions)&&Array.isArray(turn.readingRevisions.at(-1)?.rows)?turn.readingRevisions.at(-1).rows:null;
 const make = (tag, className, text) => {
   const el=document.createElement(tag);
   if(className)el.className=className;
   if(text!==undefined)el.textContent=text;
   return el;
 };
 const rootOf = (html) => { const template=document.createElement('template'); template.innerHTML=html; return template.content; };
 const equationRefs = /(?<!\\)\\(eqref|ref)\{([^{}\r\n]+)\}/g;
 function originalInlineSource(node,available){
   if(node.matches('code.render-fallback')){
     const formula=revisionFormulaSpans(node.textContent)[0];
     if(formula&&!formula.display&&formula.raw===node.textContent)return formula.raw;
   }
   if(!node.matches('.math[data-display="false"]'))return null;
   const source=node.dataset.tex;
   const index=available.findIndex(formula=>formula.tex.trim()===source.trim());
   return index>=0?available.splice(index,1)[0].raw:'$'+source+'$';
 }
 const compact = parts => {
   const result=[];
   for(const p of parts) if(p!==''){
     if(textPart(p)&&textPart(result.at(-1)))result[result.length-1]+=p;
     else result.push(p);
   }
   return result;
 };
 function catalog(turn){
   const saved=catalogs.get(turn);
   if(saved&&saved.answer===turn.answer)return saved;
   const markup=svgHtml(turn),root=rootOf(markup),atoms=new Map(),rows=[],sources=new Map(),tables=new Map(),inlineSources=new Map(),inlineMarkup=new Map(),equations=new Map();
   for(const [index,node] of Array.from(root.querySelectorAll('.equation .math')).entries()){
     const tex=node.dataset.tex,tag=/\\tag\*?\{([^{}]+)\}/.exec(tex)?.[1]||String(index+1),label=/\\label\{([^{}]+)\}/.exec(tex)?.[1];
     const number=node.closest('.equation').querySelector('.equation-number');
     if(number)number.textContent='('+tag+')';
     if(label&&!equations.has(label))equations.set(label,tag);
     if(!equations.has('eq-'+tag))equations.set('eq-'+tag,tag);
   }
   const chunks=answerSourceBlocks(turn.answer,/<\/(?:h[1-6]|p|pre|ul|ol|blockquote|div)>\r?\n<\/div>/.test(markup));
   let sectionIndex=0;
   for(const section of Array.from(root.children).filter(el=>el.matches('.passage'))){
     const body=section.querySelector('.passage-body'),source=chunks[sectionIndex++];
     if(!body)continue;
     const available=revisionFormulaSpans(source||'').filter(formula=>!formula.display&&formula.closed);
     let tableIndex=0;
     for(const [childIndex,block] of Array.from(body.children).entries()){
       const id=section.id+(childIndex?'-sub'+childIndex:''),level=/^H[1-6]$/.test(block.tagName)?Number(block.tagName[1]):0;
       const parts=[];
       if(!level&&block.tagName!=='P'){
         const ref='block:'+id,wrapper=make('div');wrapper.append(block.cloneNode(true));atoms.set(ref,wrapper);
         if(block.matches('.table-wrap')&&source){
           const table=body.children.length===1?source:answerPipeTables(source)[tableIndex++];
           if(table){tables.set(ref,table);sources.set(ref,table);}
         }
         else if(body.children.length===1&&source!==undefined)sources.set(ref,source);
         rows.push({id,level:0,parts:[{ref}]});continue;
       }
       let count=0;
       function visit(node){
         if(node.nodeType===Node.TEXT_NODE){parts.push(node.data);return;}
         if(node.nodeType!==Node.ELEMENT_NODE)return;
         if(node.matches('strong,b,em,i')){
           const mark=node.matches('strong,b')?'**':'*';parts.push(mark);node.childNodes.forEach(visit);parts.push(mark);return;
         }
         const ref='media:'+id+':'+count++;atoms.set(ref,node);parts.push({ref});
         const formula=originalInlineSource(node,available);
         if(formula!==null){
           inlineSources.set(ref,formula);
           if(node.matches('.math')&&!inlineMarkup.has(formula))inlineMarkup.set(formula,node);
         }
       }
       block.childNodes.forEach(visit);rows.push({id,level,parts:compact(parts)});
     }
   }
   const result={turn,answer:turn.answer,atoms,rows,sources,tables,inlineSources,inlineMarkup,equations};catalogs.set(turn,result);return result;
 }
 const protectedBlock=row=>row?.parts.some(p=>!textPart(p)&&p.ref.startsWith('block:'));
 const equationText=(part,cat)=>{
   const tag=resolveEquationReference(part.label,cat.turn.id,publishedEquationIndex(cat.turn))?.tag;
   return tag===undefined?part.equation:part.parentheses?'（'+tag+'）':tag;
 };
 function editableRows(rows,cat){
   return rows.map(row=>{
     const parts=[];
     const ids=new Set(refs(row.parts).map(part=>part.ref));
     let next=0;
     for(const part of compact(row.parts.map(p=>p?.ref&&cat.inlineSources.has(p.ref)?cat.inlineSources.get(p.ref):p))){
       if(!isText(part)){parts.push(part);continue;}
       let start=0;
       const formulas=revisionFormulaSpans(part);
       for(const match of part.matchAll(equationRefs)){
         if(formulas.some(formula=>match.index>=formula.start&&match.index<formula.end))continue;
         let ref;
         do{ref='equation-ref:'+row.id+':'+next++;}while(ids.has(ref));
         parts.push(part.slice(start,match.index),{ref,equation:match[0],label:match[2],parentheses:match[1]==='eqref'});
         start=match.index+match[0].length;
       }
       parts.push(part.slice(start));
     }
     return {...row,parts:compact(parts)};
   });
 }
 function checkFormulas(rows,original,allowDisplay=false){
   const existing=new Map(),failures=[],cache=new Map();
   for(const row of original)for(const part of row.parts)if(isText(part))
     for(const formula of revisionFormulaSpans(part))existing.set(formula.raw,(existing.get(formula.raw)||0)+1);
   for(const [rowIndex,row] of rows.entries())for(const [partIndex,part] of row.parts.entries()){
     if(!isText(part))continue;
     for(const formula of revisionFormulaSpans(part)){
       const count=existing.get(formula.raw)||0;
       if(count){existing.set(formula.raw,count-1);continue;}
       let error;
       if(formula.display&&!allowDisplay)error='行间公式在整理区中保留为只读。';
       else if(!formula.closed)error='公式的结束定界符缺失。';
       else{
         if(!cache.has(formula.raw)){
           let failure='';
           const source=formula.display?formula.tex.replace(/\\(?:label|tag\*?)\{[^{}]+\}/g,''):formula.tex;
           try{compileRevisionFormula(source,formula.display);}catch(e){failure=e.message;}
           cache.set(formula.raw,failure);
         }
         error=cache.get(formula.raw);
       }
       if(error)failures.push({rowIndex,partIndex,formula,error});
     }
   }
   return failures;
 }
 function simulateFormulaRepair(rows,failures){
   const fixed=structuredClone(rows);
   for(const {rowIndex,partIndex,formula} of [...failures].reverse()){
     if(formula.display||!formula.closed)return null;
     const tex=formula.tex.replace(/\\(?:frca|farc|sqtr)\b/g,token=>token==='\\sqtr'?'\\sqrt':'\\frac');
     if(tex===formula.tex)return null;
     const parts=fixed[rowIndex].parts,text=parts[partIndex];
     parts[partIndex]=text.slice(0,formula.start)+formula.left+tex+formula.right+text.slice(formula.end);
   }
   return fixed;
 }
 function appendReadingParts(root,parts,cat,locations,children){
   if(parts.some((p,i)=>i&&textPart(p)&&textPart(parts[i-1])))parts=compact(parts);
   // A protected inline occupies exactly one position in the source editor.
   const marks=parts.some(p=>textPart(p)&&/\*|__/.test(p))
     ?revisionEmphasisMarks(parts.map(p=>textPart(p)?p:'\ufffc').join('')):[];
   let target=root,markIndex=0,offset=0;
   function appendText(text,at){
     if(!text)return;
     const node=document.createTextNode(text);
     locations?.set(node,{offset:at});target.append(node);
   }
   for(const part of parts){
     if(!textPart(part)){
       const node=locations&&part.equation!==undefined?make('span','equation-ref',equationText(part,cat)):partNode(part,cat,false);
       if(part.child)children?.push([part.child,node.title]);
       locations?.set(node,{offset,size:1});
       if(!locations&&part.ref.startsWith('block:'))target.append(...node.childNodes);else target.append(node);
       offset++;
       continue;
     }
     const events=revisionFormulaSpans(part).filter(span=>!span.display&&span.closed);
     const firstMark=markIndex;
     while(markIndex<marks.length&&marks[markIndex].at<offset+part.length){
       const mark=marks[markIndex++],start=mark.at-offset;
       events.push({start,end:start+mark.size,mark});
     }
     if(markIndex!==firstMark&&events.length>1)events.sort((a,b)=>a.start-b.start);
     let from=0;
     for(const event of events){
       appendText(part.slice(from,event.start),offset+from);
       if(event.mark){
         if(event.mark.open){const el=make(event.mark.tag);target.append(el);target=el;}
         else target=target.parentNode;
       }else{
         const node=cat.inlineMarkup.get(event.raw)?.cloneNode(true)??rootOf(renderRevisionFormula(event)).firstElementChild;
         locations?.set(node,{offset:offset+event.start,size:event.raw.length});target.append(node);
       }
       from=event.end;
     }
     appendText(part.slice(from),offset+from);
     offset+=part.length;
   }
 }
 function partNode(part,cat,editing){
   if(part.tableSource){
     const body=rootOf(renderSavedAnswer(part.tableSource,'revision-table')).querySelector('.passage-body');
     return body||make('div');
   }
   // Reconstruct rich tokens from source, including original protected objects.
   if(part.displaySource){
     const wrapper=make('div'),equation=make('div','equation');
     const formula=revisionFormulaSpans(part.displaySource)[0];
     if(!formula?.closed||!formula.display)return wrapper;
     equation.append(rootOf(renderRevisionFormula(formula)),make('span','equation-number'));
     wrapper.append(equation);return wrapper;
   }
   if(part.ref.startsWith('equation-ref:')){
     if(!editing)return document.createTextNode(part.equation);
     const atom=make('span','revision-object revision-equation-ref',part.equation);
     atom.dataset.reference=part.ref;atom.contentEditable='false';return atom;
   }
   if(atomic(part.ref)){
     const original=cat.atoms.get(part.ref);if(!original)return document.createTextNode('');
     const block=part.ref.startsWith('block:'), el=make(block?'div':'span',editing?'revision-object':'');
     if(block)el.append(...Array.from(original.childNodes,n=>n.cloneNode(true)));else el.appendChild(original.cloneNode(true));
     if(editing){el.dataset.reference=part.ref;el.contentEditable='false';}
     return el;
   }
   const link=make('button','same-turn-link');link.type='button';link.dataset.revisionRef=part.ref;
   if(part.child){link.dataset.openChild=part.child;link.title=state.conversations.find(c=>c.id===part.child)?.title||part.label;}
   else {link.dataset.openTurn=part.turn;link.title=part.label;}
   link.append(make('span','reading-bubble','🗨️'), ' '+(part.child?link.title:part.label));
   if(editing){link.dataset.reference=part.ref;link.contentEditable='false';link.tabIndex=-1;}
   return link;
 }
 const rendered=new WeakMap();
 function html(turn,rows=live(turn)){
   if(!rows)return null;
   if(!rows.length)return '<p class="revision-empty">正文为空。</p>';
   const cached=rendered.get(rows);
   if(cached&&cached.children.every(([id,name])=>state.conversations.find(c=>c.id===id)?.title===name))return cached.markup;
   const children=[],cat=catalog(turn), container=make('div');
   for(const row of rows){
     const section=make('section','passage');section.id=row.id;section.dataset.block=row.id;
     const body=make('div','passage-body'),root=protectedBlock(row)?body:make(row.level?'h'+row.level:'p');
     appendReadingParts(root,row.parts,cat,null,children);
     if(root!==body)body.append(root);
     section.append(body,make('div','margin-notes'));container.append(section);
   }
   const markup=container.innerHTML;rendered.set(rows,{markup,children});return markup;
 }
 function snapshot(turn){
   if(!live(turn))return turn.answer;
   const cat=catalog(turn);
   return live(turn).map(row=>(row.level?'#'.repeat(row.level)+' ':'')+row.parts.map(part=>{
     if(textPart(part))return part;
     if(part.tableSource)return part.tableSource;
     if(part.displaySource)return part.displaySource;
     if(part.equation!==undefined)return part.equation;
     if(cat.sources.has(part.ref))return cat.sources.get(part.ref);
     const node=cat.atoms.get(part.ref);if(!node)return '';
     const math=node.matches('.math')?node:node.querySelector('.math');
     if(math&& (node===math||node.querySelector('.equation')))return (math.dataset.display==='true'?'$$':'$')+math.dataset.tex+(math.dataset.display==='true'?'$$':'$');
     const img=node.matches('img')?node:node.querySelector('img');if(img)return '!['+img.alt+']('+img.getAttribute('src')+')';
     if(node.querySelector('table'))return [...node.querySelectorAll('tr')].map(tr=>'| '+[...tr.cells].map(c=>c.innerText||c.textContent).join(' | ')+' |').join('\n');
     return node.textContent;
   }).join('')).join('\n\n');
 }
 function capture(turn){
   const cat=catalog(turn),rows=editableRows(structuredClone(live(turn)??cat.rows),cat),known=new Set(rows.flatMap(r=>r.parts.filter(p=>!textPart(p)).map(p=>p.ref)));
   if(!live(turn))for(const row of rows){
     const old=turn.reviewDraft?.blocks?.[row.id]?.runs;
     if(Array.isArray(old)&&row.parts.every(p=>isText(p)&&!revisionFormulaSpans(p).length))
       row.parts=compact(old.filter(r=>r.kind!=='deleted').map(r=>r.text));
   }
   const additions=new Map();
   for(const link of document.getElementById(turn.id)?.querySelectorAll('.answer .same-turn-link[data-revision-ref]')||[]){
     const ref=link.dataset.revisionRef;if(known.has(ref))continue;
     const base=link.dataset.sourceBlock,body=document.getElementById(base)?.querySelector('.passage-body');
     let child=link;
     while(child.parentElement&&child.parentElement!==body)child=child.parentElement;
     const childIndex=body?Array.from(body.children).indexOf(child):0,id=childIndex>0&&rows.some(r=>r.id===base+'-sub'+childIndex)?base+'-sub'+childIndex:base;
     const row=rows.find(r=>r.id===id);if(!row)continue;
     const label=link.textContent.replace(/^🗨️\s*/,'');
     const part=link.dataset.openChild?{ref,child:link.dataset.openChild,label}:{ref,turn:link.dataset.openTurn,label};
     let position=0,remaining=Number(link.dataset.sourceOffset??Infinity);
     if(id!==base&&Number.isFinite(remaining)){
       const before=document.createRange();before.selectNodeContents(child);before.setEndBefore(link);
       const fragment=before.cloneContents();fragment.querySelectorAll('.same-turn-link,.heading-marker,.math,svg').forEach(el=>el.remove());remaining=fragment.textContent.length;
     }
     if(link.dataset.sourceMedia!==undefined){
       const mediaIndex=Number(link.dataset.sourceMedia);let seen=-1;
       for(const p of row.parts){
         if(textPart(p)){
           const formulas=revisionFormulaSpans(p).filter(span=>span.closed&&!span.display);
           const formula=formulas[mediaIndex-seen-1];
           if(formula){position+=formula.end;break;}
           seen+=formulas.length;
           position+=p.length;
         }else if(atomic(p.ref)){
           const node=cat.atoms.get(p.ref);
           if(node&&(node.matches('.math,img,table')||node.querySelector('.math,img,table')))seen++;
           position++;
           if(seen===mediaIndex||protectedBlock(row))break;
         }else position++;
       }
     }else{
       for(const p of row.parts){
         if(!textPart(p)){
           const node=cat.atoms.get(p.ref),length=p.equation!==undefined?equationText(p,cat).length:
             node&&!node.matches('.math,svg')&&!node.querySelector('.math')?node.textContent.length:0;
           remaining=Math.max(0,remaining-length);
           position++;continue;
         }
         let used=0;const spans=revisionFormulaSpans(p).filter(span=>span.closed&&!span.display);
         for(let i=0,formula=0;i<p.length&&remaining>0;i++){
           if(spans[formula]?.start===i){i=spans[formula++].end-1;used=i+1;continue;}
           used++;if(p[i]!=='*')remaining--;
         }
         position+=used;if(remaining===0)break;
       }
     }
     if(!additions.has(row.id))additions.set(row.id,[]);
     additions.get(row.id).push({position,part});known.add(ref);
   }
   for(const row of rows){
     const next=additions.get(row.id);if(!next)continue;
     for(const {position,part} of next.reverse().sort((a,b)=>b.position-a.position))
       row.parts=compact([...slice(row.parts,0,position),part,...slice(row.parts,position)]);
   }
   return rows;
 }
 function resumeDraft(draft,original){
   if(!Array.isArray(draft?.rows))return structuredClone(original);
   const rows=structuredClone(draft.rows),known=new Set(rows.flatMap(r=>refs(r.parts).map(p=>p.ref)));
   // A new follow-up may have been sent between an unfinished edit and reopening the turn.
   for(const [i,source] of original.entries()){
     let at=0;
     for(const part of source.parts){
       if(isText(part)){at+=part.length;continue;}
       if(known.has(part.ref))continue;
       let row=rows.find(r=>r.id===source.id);
       if(!row){
         row=original.slice(0,i).reverse().map(r=>rows.find(p=>p.id===r.id)).find(Boolean);
         if(!row){row={id:source.id,level:source.level,parts:[]};rows.unshift(row);}
         row.parts.push(part);
       }else{
         let size=0,position=0;
         for(const p of row.parts){if(isText(p)){const n=Math.min(p.length,Math.max(0,at-size));position+=n;size+=n;if(n<p.length)break;}
           else position++;}
         row.parts=compact([...slice(row.parts,0,position),part,...slice(row.parts,position)]);
       }
       known.add(part.ref);
     }
   }
   return rows;
 }
 const isText=textPart;
 const blank=s=>!s.replace(/[\s\u200b\u2060]/gu,'');
 const textOf=parts=>parts.filter(isText).join('');
 const refs=parts=>parts.filter(p=>!isText(p));
 const width=p=>isText(p)?p.length:1;
 const lengthOf=parts=>parts.reduce((n,p)=>n+width(p),0);
 const newRow=parts=>({id:'para-'+crypto.randomUUID(),level:0,parts});
 function slice(parts,start=0,end=Infinity){
   const out=[];let i=0;
   for(const p of parts){const next=i+width(p);if(start<next&&end>i)out.push(isText(p)?p.slice(Math.max(0,start-i),end-i):p);i=next;}
   return compact(out);
 }
 function button(label,action,cls){
   const b=make('button',cls,label);b.type='button';b.onclick=action;return b;
 }
 function readingRow(id){
   if(!id)return null;
   const row=document.getElementById(id);if(row)return row;
   const child=/-sub(\d+)$/.exec(id);
   return child&&document.getElementById(id.slice(0,child.index))?.querySelector('.passage-body')?.children[Number(child[1])];
 }
 function anchoredRefresh(id,editedRowId){
   const article=document.getElementById(id),answer=article?.querySelector('.answer'),turn=current().turns.find(t=>t.id===id);
   if(!answer||!turn)return;
   const area=$('reader'),bounds=area.getBoundingClientRect();
   const visible=Array.from(answer.querySelectorAll('.revision-row,.passage')).map(el=>({id:el.dataset.id||el.id,rect:el.getBoundingClientRect()})).filter(({rect})=>rect.bottom>bounds.top&&rect.top<bounds.bottom);
   const edited=visible.findIndex(item=>item.id===editedRowId);
   if(edited>0)visible.unshift(visible.splice(edited,1)[0]);
   closeEquationPreview();answer.innerHTML=html(turn)??svgHtml(turn);
   enhanceAnswers(article);addSameTurnLinks();window.mountTurnReview?.(answer);window.alignReadingMedia?.(answer);alignReadingInk(true,article);
   renderBubbles();window.refreshUserBookmarkMarkers?.();
   for(const item of visible){const node=readingRow(item.id);if(node&&node.getClientRects().length){area.scrollTop+=node.getBoundingClientRect().top-item.rect.top;break;}}
 }
 let active=null;
 function begin(turn,sourceId,readingAnchor,restoredFocus){
   const entry=document.getElementById(turn.id)?.querySelector('.answer');if(!entry)return;
   if(turn.readingRevisions!==undefined&&(!Array.isArray(turn.readingRevisions)||turn.readingRevisions.some(v=>!Array.isArray(v?.rows)))){status('本轮包含当前原型无法编辑的版本记录。');return;}
   if(active?.root.isConnected){if(active.turn===turn){if(sourceId)active.focus(sourceId);return;}active.pause();}else active=null;
   const original=capture(turn),cat=catalog(turn);
   const draft=turn.readingRevisionDraft;
   const compatible=Array.isArray(draft?.rows)?{...draft,rows:editableRows(draft.rows,cat)}:draft;
   let paragraphs=resumeDraft(compatible,original),painting=false,selecting=false,activeId=sourceId||paragraphs[0]?.id;
   const selected=new Set(),history=[];
   const tokenMap=new Map(paragraphs.flatMap(r=>refs(r.parts).map(p=>[p.ref,p])));
   const root=make('section','revision-editor'),body=make('div','revision-body'),notice=make('span','revision-notice');
   notice.setAttribute('role','status');
   function undo(){
     if(!history.length)return;
     const previous=history.pop();paragraphs=previous.rows;render();
     const pos=previous.position;
     const row=pos&&Array.from(body.children).find(r=>r.dataset.id===pos.id);
     if(row)setCaret(row.querySelector('.words'),pos.span[0],true,pos.span[1]);
     save();notice.textContent='已撤销。';
   }
   function toggleSelection(){
     selecting=!selecting;selected.clear();render();root.focus({preventScroll:true});
   }
   root.append(body,notice);
   const readingTop=restoredFocus?null:(readingAnchor||document.getElementById(sourceId))?.getBoundingClientRect().top;
   entry.replaceChildren(root);
   let lastPosition=null;
   function remember(position){
     const span=sourceWords&&selectionOf(sourceWords);
     history.push({rows:paragraphs.slice(),position:position??(span?{id:sourceWords.parentElement.dataset.id,span}:lastPosition)});
   }
   let pendingWrite=null;
   const save=()=>{
     turn.readingRevisionDraft={...turn.readingRevisionDraft,rows:paragraphs};
     clearTimeout(pendingWrite);
     pendingWrite=setTimeout(persist,180);
   };
   const linkFor=ref=>partNode(tokenMap.get(ref)??{ref},cat,true);
   const stored=row=>{
     let text='';const links=[],offsets=[];
     for(const p of row.parts){if(isText(p))text+=p;else{links.push(p.ref);offsets.push(text.length);}}
     return {text,links,offsets};
   };
  function readParts(node){
    const parts=[];
    function walk(n){
      if(n.nodeType===Node.TEXT_NODE){parts.push(n.data);return}
      if(n.dataset?.reference){parts.push(tokenMap.get(n.dataset.reference)??{ref:n.dataset.reference});return}
      for(const child of n.childNodes)walk(child);
    }
    walk(node);return compact(parts);
  }
  // Chrome's End and Home can leave a DOM range inside a protected button label.
  function outsideLink(words,node,offset){
    const el=node.nodeType===Node.ELEMENT_NODE?node:node.parentElement;
    const link=el.closest('[data-reference]');
    if(!link||!words.contains(link))return [node,offset];
    const range=document.createRange();range.selectNodeContents(link);range.setEnd(node,offset);
    const after=range.toString().length*2>=link.textContent.length;
    return [link.parentNode,Array.from(link.parentNode.childNodes).indexOf(link)+Number(after)];
  }
  function normalizeSelection(words){
    const s=getSelection();
    if(!s.rangeCount||!words.contains(s.anchorNode)||!words.contains(s.focusNode))return false;
    const a=outsideLink(words,s.anchorNode,s.anchorOffset),f=outsideLink(words,s.focusNode,s.focusOffset);
    if(a[0]===s.anchorNode&&f[0]===s.focusNode)return false;
    s.setBaseAndExtent(...a,...f);return true;
  }
  function offsetOf(words,node,offset){
    const range=document.createRange();range.selectNodeContents(words);range.setEnd(...outsideLink(words,node,offset));
    return lengthOf(readParts(range.cloneContents()));
  }
  function spanOf(words,range){
    if(!range||!words.contains(range.startContainer)||!words.contains(range.endContainer))return null;
    return [offsetOf(words,range.startContainer,range.startOffset),offsetOf(words,range.endContainer,range.endOffset)];
  }
  function selectionOf(words){
    const s=getSelection();
    return s.rangeCount?spanOf(words,s.getRangeAt(0)):null;
  }
  function setCaret(words,at,reveal=true,end=at){
    if(document.activeElement!==words)words.focus({preventScroll:true});
    function point(remaining){
      for(const [i,node] of Array.from(words.childNodes).entries()){
        if(node.nodeType===Node.TEXT_NODE&&remaining<=node.length)return [node,remaining];
        if(remaining===0)return [words,i];
        remaining-=node.nodeType===Node.TEXT_NODE?node.length:1;
      }
      return [words,words.childNodes.length];
    }
    const start=point(at);getSelection().setBaseAndExtent(...start,...(at===end?start:point(end)));
    if(reveal)words.scrollIntoView({block:'nearest',inline:'nearest'});
    restartCaret();
  }
  function paintWords(words,parts){
    words.replaceChildren(...parts.map(p=>isText(p)?document.createTextNode(p):linkFor(p.ref)));
  }
  const locations=new WeakMap();
  function paintReading(words,row){
    const fragment=document.createDocumentFragment();
    appendReadingParts(fragment,row.parts,cat,locations);
    words.replaceChildren(fragment);
  }
  function pointerOffset(words,x,y){
    const range=document.caretRangeFromPoint(x,y);
    if(!range||!words.contains(range.startContainer))return null;
    const start=range.startContainer;
    for(let node=start;node&&node!==words;node=node.parentNode){
      const position=locations.get(node);
      if(!position)continue;
      if(position.size===undefined)return position.offset+range.startOffset;
      if(node.classList?.contains('wrapped-math')){
        const lines=[...node.getClientRects()],hit=lines.find(rect=>y>=rect.top&&y<=rect.bottom);
        if(hit){
          const before=lines.slice(0,lines.indexOf(hit)).reduce((w,rect)=>w+rect.width,0);
          const width=lines.reduce((w,rect)=>w+rect.width,0);
          return position.offset+(before+x-hit.left>=width/2?position.size:0);
        }
      }
      const rect=node.getBoundingClientRect();
      return position.offset+(x>=rect.left+rect.width/2?position.size:0);
    }
    return null;
  }
  // The browser paints its usual caret whenever the IME or a selection owns it.
  const caret=make('span','revision-caret');
  caret.setAttribute('aria-hidden','true');
  let sourceWords=null,caretFrame=0,resetBlink=false;
  function queueCaret(){
    if(sourceWords&&!caretFrame)caretFrame=requestAnimationFrame(updateCaret);
  }
  function restartCaret(){
    resetBlink=true;queueCaret();
  }
  function updateCaret(){
    caretFrame=0;
    const words=sourceWords,selection=getSelection();
    if(!words)return;
    const row=words.parentElement;
    let valid=words.isConnected&&document.hasFocus()&&document.activeElement===words&&!words.classList.contains('composing')&&selection.isCollapsed&&selection.rangeCount&&words.contains(selection.anchorNode);
    if(valid){
      const range=selection.getRangeAt(0),box=row.getBoundingClientRect(),rect=range.getBoundingClientRect();
      const style=getComputedStyle(words),height=parseFloat(style.fontSize)*1.15;
      let left=rect.left,top=rect.top+(rect.height-height)/2;
      if(!rect.height){
        const child=range.startContainer===words?words.childNodes[range.startOffset]??words.lastChild:null;
        if(child){const at=document.createRange();at.selectNodeContents(child);const rects=at.getClientRects();
          const before=child===words.childNodes[range.startOffset],edge=before?rects[0]:rects[rects.length-1];
          if(edge){left=before?edge.left:edge.right;top=edge.top+(edge.height-height)/2;}else valid=false;
        }else if(!words.textContent){const bounds=words.getBoundingClientRect();left=bounds.left;top=bounds.top+(bounds.height-height)/2;}
        else valid=false;
      }
      if(valid){
        if(resetBlink&&caret.parentNode===row&&!caret.hidden){
          const blink=caret.getAnimations()[0];
          if(blink)blink.currentTime=0;
        }
        caret.style.left=(left-box.left)+'px';caret.style.top=(top-box.top)+'px';caret.style.height=height+'px';
        if(caret.parentNode!==row)row.append(caret);
      }
    }
    resetBlink=false;caret.hidden=!valid;words.classList.toggle('drawn-caret',!!valid);
  }
  const resized=new ResizeObserver(queueCaret);
  function endCaret(){
    if(!sourceWords)return;
    sourceWords.classList.remove('drawn-caret','composing');sourceWords=null;
    document.removeEventListener('selectionchange',restartCaret);resized.disconnect();
    window.removeEventListener('blur',queueCaret);window.removeEventListener('focus',restartCaret);
    cancelAnimationFrame(caretFrame);caretFrame=0;caret.remove();
  }
  function caretIn(i,at){setCaret(body.querySelectorAll('.words')[i],at)}
  function removeEmpty(i,forward){
    const row=paragraphs[i],links=refs(row.parts);
    if((i===0&&links.length)||links.some(p=>atomic(p.ref))||paragraphs.length===1){
      paragraphs[i]={...row,parts:links};
      const editor=body.querySelectorAll('.words')[i];paintWords(editor,links);setCaret(editor,0);
      notice.textContent=links.length?'引用保留在开头。':'';
      return;
    }
    if(links.length){
      const prev=paragraphs[i-1];paragraphs[i-1]={...prev,parts:[...prev.parts,...links]};
    }
    paragraphs.splice(i,1);render();
    if(forward||i===0)caretIn(i,0);
    else {const previous=paragraphs[i-1].parts;let end=previous.length;while(end&&!isText(previous[end-1])&&!atomic(previous[end-1].ref))end--;caretIn(i-1,lengthOf(previous.slice(0,end)));}
    notice.textContent=links.length?'引用已移至上一段。':'空白段落已删除。';
  }
  function deferDraftCleanup(id){
    setTimeout(()=>{
      const i=paragraphs.findIndex(p=>p.id===id),node=[...body.children].find(p=>p.dataset.id===id);
      if(!root.isConnected||i<0||!node||node.contains(document.activeElement)||paragraphs.length===1||
        refs(paragraphs[i].parts).length||!blank(textOf(paragraphs[i].parts))||!blank(textOf(readParts(node.querySelector('.words')))))return;
      paragraphs.splice(i,1);node.remove();selected.delete(id);save();
    },0);
  }
  function bindEditor(words,id){
    const index=()=>paragraphs.findIndex(p=>p.id===id);
    let composing=false,compositionRange=null,pendingSelection=null;
    words.tabIndex=0;
    words.onfocus=()=>{
      const row=paragraphs[index()];if(!row)return;
      endCaret();activeId=id;words.contentEditable='true';paintWords(words,row.parts);
      const number=words.parentElement.querySelector('.revision-number');
      if(number)number.textContent='#'.repeat(row.level)+' ';
      sourceWords=words;resized.observe(words.parentElement);document.addEventListener('selectionchange',restartCaret);
      window.addEventListener('blur',queueCaret);window.addEventListener('focus',restartCaret);
      setCaret(words,0,false);
    };
    words.onmousedown=e=>{
      if(e.button!==0||sourceWords===words)return;
      const at=pointerOffset(words,e.clientX,e.clientY);
      e.preventDefault();setCaret(words,at??0,false);
    };
    function sync(){
      const i=index();
      if(i<0||painting||composing||sourceWords!==words||!words.isConnected)return;
      const row=paragraphs[i];
      let next=readParts(words);
      const oldIds=refs(row.parts).map(p=>p.ref),newIds=refs(next).map(p=>p.ref);
      // Also protect occurrences for native edits that have no cancellable beforeinput (such as IME).
      if(JSON.stringify(oldIds)!==JSON.stringify(newIds)){
        const oldText=textOf(row.parts),newText=textOf(next);
        let prefix=0,suffix=0;
        while(prefix<Math.min(oldText.length,newText.length)&&oldText[prefix]===newText[prefix])prefix++;
        while(suffix<Math.min(oldText.length,newText.length)-prefix&&oldText.at(-1-suffix)===newText.at(-1-suffix))suffix++;
        const before=stored(row);
        let start=0;
        next=[];
        before.links.forEach((ref,n)=>{
          const offset=before.offsets[n];
          const at=offset<=prefix?offset:Math.max(prefix,newText.length-suffix+Math.max(0,offset-(oldText.length-suffix)));
          next.push(newText.slice(start,at),tokenMap.get(ref)??{ref});start=at;
        });
        next=compact([...next,newText.slice(start)]);paintWords(words,next);
        setCaret(words,lengthOf(next)-suffix);
      }
      if(JSON.stringify(next)===JSON.stringify(row.parts))return;
      remember(pendingSelection?{id,span:pendingSelection}:null);pendingSelection=null;paragraphs[i]={...row,parts:next};
      if(words.parentElement.classList.contains('revision-invalid')){
        words.parentElement.classList.remove('revision-invalid');notice.textContent='';
      }
      if(blank(textOf(next))&&!blank(textOf(row.parts)))removeEmpty(i,false);
      save();
    }
    function replace(start,end,value){
      const i=index(),row=paragraphs[i];
      if(!row)return;
      const left=slice(row.parts,0,start),right=slice(row.parts,end);
      const held=refs(slice(row.parts,start,end)),lines=value.split('\n');
      if(lines.length===1){
        const next=compact([...left,value,...held,...right]);
        if(JSON.stringify(next)===JSON.stringify(row.parts)){setCaret(words,start+value.length);return}
        remember({id,span:[start,end]});paragraphs[i]={...row,parts:next};
        if(words.parentElement.classList.contains('revision-invalid')){
          words.parentElement.classList.remove('revision-invalid');notice.textContent='';
        }
        if(blank(textOf(next))&&!blank(textOf(row.parts)))removeEmpty(i,false);
        else{paintWords(words,next);setCaret(words,start+value.length)}
      }else{
        remember({id,span:[start,end]});
        const rows=lines.map(line=>newRow(compact([line]))),last=rows.at(-1);
        rows[0]={...row,parts:compact([...left,lines[0]])};
        last.parts=compact([...last.parts,...held,...right]);
        paragraphs.splice(i,1,...rows);render();caretIn(i+rows.length-1,lines.at(-1).length);
        for(const [j,r] of rows.slice(0,-1).entries())if(!r.parts.length){
          if(j===0&&rows.length===2&&right.length)
            body.querySelectorAll('.words')[i+1].addEventListener('blur',()=>deferDraftCleanup(r.id),{once:true});
          else deferDraftCleanup(r.id);
        }
        notice.textContent='';
      }
      save();
    }
    function boundary(forward){
      const pos=selectionOf(words),i=index();
      if(!pos||pos[0]!==pos[1]||i<0)return false;
      const at=pos[0],row=paragraphs[i],size=lengthOf(row.parts);
      const empty=blank(textOf(row.parts))&&!refs(row.parts).some(p=>atomic(p.ref));
      if(!empty&&at!==(forward?size:0)){
        const near=forward?slice(row.parts,at,at+1):slice(row.parts,at-1,at);
        if(!refs(near).length)return false;
        setCaret(words,at+(forward?1:-1));return true;
      }
      if(forward?i===paragraphs.length-1:i===0)return true;
      const adjacent=paragraphs[forward?i+1:i-1];
      if(!empty&&protectedBlock(adjacent))return true;
      remember();
      if(empty&&!(i===0&&row.parts.length&&forward))removeEmpty(i,forward);
      else{
        const l=forward?i:i-1,left=paragraphs[l],right=paragraphs[l+1];
        paragraphs.splice(l,2,{...(forward?right:left),parts:compact([...left.parts,...right.parts])});
        render();caretIn(l,empty?0:lengthOf(left.parts));notice.textContent='';
      }
      save();return true;
    }
    words.onkeydown=e=>{
      if(composing||e.isComposing||e.keyCode===229)return;
      normalizeSelection(words);
      if(e.key==='Enter'&&e.target===words){const pos=selectionOf(words);if(pos){e.preventDefault();replace(...pos,'\n')}return}
      if(!e.shiftKey&&!e.altKey&&!e.ctrlKey&&!e.metaKey&&['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key)){
        const i=index(),pos=selectionOf(words),forward=e.key==='ArrowRight'||e.key==='ArrowDown';
        if(pos&&pos[0]===pos[1]){
          let boundary=pos[0]===(forward?lengthOf(paragraphs[i].parts):0);
          if(!boundary&&(e.key==='ArrowUp'||e.key==='ArrowDown')){
            const rect=getSelection().getRangeAt(0).getBoundingClientRect(),box=words.getBoundingClientRect();
            const halfLine=parseFloat(getComputedStyle(words).lineHeight)/2;
            boundary=rect.height&&(forward?box.bottom-rect.bottom:rect.top-box.top)<halfLine;
          }
          const next=forward?i+1:i-1;
          if(boundary&&paragraphs[next]){
            e.preventDefault();
            if(protectedBlock(paragraphs[next]))body.children[next].querySelector('.words').focus();
            else caretIn(next,forward?0:lengthOf(paragraphs[next].parts));
          }
        }
      }
      if(['Backspace','Delete'].includes(e.key)&&!e.altKey&&!e.ctrlKey&&!e.metaKey&&boundary(e.key==='Delete'))e.preventDefault();
    };
    words.onkeyup=e=>{if(!composing&&['End','Home','ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(e.key))normalizeSelection(words)};
    words.onbeforeinput=e=>{
      if(!composing)pendingSelection=selectionOf(words);
      if(composing||e.isComposing||!e.cancelable)return;
      if(e.inputType==='historyUndo'){e.preventDefault();undo();return}
      const insideLink=normalizeSelection(words);
      const selection=selectionOf(words);
      if(!selection)return;
      if(insideLink&&e.inputType==='insertText'){e.preventDefault();replace(...selection,e.data??'');return}
      if(e.inputType==='insertParagraph'||e.inputType==='insertLineBreak'){e.preventDefault();replace(...selection,'\n');return}
      if(['deleteContentBackward','deleteContentForward'].includes(e.inputType)&&boundary(e.inputType==='deleteContentForward')){e.preventDefault();return}
      const pos=spanOf(words,e.getTargetRanges?.()[0])??selection;
      if(refs(slice(paragraphs[index()].parts,...pos)).length){
        if(e.inputType.startsWith('delete')){e.preventDefault();replace(...pos,'')}
        else if(e.inputType.startsWith('insert')){e.preventDefault();replace(...pos,e.data??e.dataTransfer?.getData('text/plain')??'')}
      }
    };
    words.oninput=e=>{if(!e.isComposing){sync();restartCaret();}};
    words.addEventListener('compositionstart',()=>{
      words.classList.add('composing');queueCaret();
      normalizeSelection(words);
      const pos=selectionOf(words);
      compositionRange=null;
      if(pos&&refs(slice(paragraphs[index()].parts,...pos)).length){
        // Give the IME a plain insertion point. The model keeps the original selection until confirmation.
        const [start,end]=pos,row=paragraphs[index()];
        compositionRange=pos;
        const next=compact([...slice(row.parts,0,start),...refs(slice(row.parts,start,end)),...slice(row.parts,end)]);
        paintWords(words,next);setCaret(words,start);
      }
      composing=true;
    });
    words.addEventListener('compositionend',e=>{
      composing=false;words.classList.remove('composing');queueCaret();
      if(!compositionRange){sync();return}
      const pos=compositionRange;compositionRange=null;
      if(e.data){replace(...pos,e.data);return}
      paintWords(words,paragraphs[index()].parts);setCaret(words,pos[0]);
    });
    words.onblur=()=>{
      if(painting||sourceWords!==words)return;
      if(!words.isConnected){endCaret();return;}
      const span=selectionOf(words);
      if(span)lastPosition={id,span};
      sync();
      if(sourceWords!==words)return;
      endCaret();words.removeAttribute('contenteditable');
      const row=paragraphs[index()];
      if(row){paintReading(words,row);prepareReadingMath(words);}
      const number=words.parentElement.querySelector('.revision-number');
      if(number)number.textContent=number.dataset.number;
      if(row&&!row.parts.length)deferDraftCleanup(id);
    };
    words.onpaste=e=>{
      const pos=selectionOf(words);
      if(!pos)return;
      e.preventDefault();replace(...pos,(e.clipboardData?.getData('text/plain')??'').replace(/\r\n?/g,'\n'));
    };
    words.oncut=e=>{
      const pos=selectionOf(words);
      if(!pos||pos[0]===pos[1])return;
      e.preventDefault();e.clipboardData?.setData('text/plain',textOf(slice(paragraphs[index()].parts,...pos)));replace(...pos,'');
    };
    words.ondragstart=e=>{
      const pos=selectionOf(words);
      if(e.target.closest?.('[data-reference]')||pos&&refs(slice(paragraphs[index()].parts,...pos)).length)e.preventDefault();
    };
    words.ondrop=e=>{
      e.preventDefault();
      const range=document.caretRangeFromPoint(e.clientX,e.clientY),pos=spanOf(words,range);
      const text=e.dataTransfer?.getData('text/plain');
      if(pos&&text)replace(...pos,text.replace(/\r\n?/g,'\n'));
    };
  }
   function render(){
     painting=true;endCaret();body.replaceChildren();
     if(!paragraphs.length)paragraphs.push(newRow([]));
     const numbering=Array(6).fill(0),turnNumber=current().turns.indexOf(turn)+1;
     for(const [i,row] of paragraphs.entries()){
       const p=make('div','revision-row');p.dataset.id=row.id;
       const locked=protectedBlock(row);
       if(row.level){
         numbering[row.level-1]++;numbering.fill(0,row.level);
         const label='['+turnNumber+':'+numbering.slice(0,row.level).join('.')+'] ',number=make('span','revision-number',label);
         number.dataset.number=label;p.append(number);
       }
       const words=make('div','words');words.style.display=row.level&&!locked?'inline':'block';
       if(selecting&&!locked){
         p.classList.add('revision-choice');p.setAttribute('role','button');p.tabIndex=0;p.setAttribute('aria-pressed',String(selected.has(row.id)));
         const choose=()=>{if(selected.has(row.id))selected.delete(row.id);else selected.add(row.id);p.setAttribute('aria-pressed',String(selected.has(row.id)));};
         p.onclick=choose;p.onkeydown=e=>{if(e.key===' '||e.key==='Enter'){e.preventDefault();choose();}};
       }else if(!locked&&!selecting){
          words.setAttribute('role','textbox');words.setAttribute('aria-label','修改第 '+(i+1)+' 段');bindEditor(words,row.id);
       }else {words.tabIndex=0;words.onfocus=()=>{activeId=row.id;};words.setAttribute('aria-label','受保护的原文');}
        paintReading(words,row);
       if(locked){
         words.classList.add('revision-locked');
         words.onkeydown=e=>{if(e.key==='Enter'&&!selecting){e.preventDefault();remember();const at=i+(e.shiftKey?0:1);paragraphs.splice(at,0,newRow([]));render();caretIn(at,0);save();}};
       }
       p.append(words);body.append(p);
     }
     prepareReadingMath(body);
     painting=false;
   }
   function deleteSelected(){
     if(!selected.size)return;
     const kept=[];
     for(const row of paragraphs){
       if(!selected.has(row.id)||protectedBlock(row)){kept.push(row);continue;}
       const tokens=refs(row.parts);
       if(tokens.some(x=>atomic(x.ref))||(!kept.length&&tokens.length)){kept.push({...row,parts:tokens});continue;}
       if(tokens.length){const prev=kept.pop();kept.push({...prev,parts:[...prev.parts,...tokens]});}
     }
     remember();paragraphs=kept;selecting=false;selected.clear();render();save();root.focus({preventScroll:true});
   }
   root.oncontextmenu=e=>{
     const id=e.target.closest('.revision-row')?.dataset.id;
     if(!id)return;
     e.preventDefault();e.stopPropagation();
     root.querySelector('.revision-menu')?.remove();
     const menu=make('div','menu revision-menu');menu.popover='auto';menu.setAttribute('role','menu');menu.setAttribute('aria-label','整理段落');
     function item(label,action,disabled=false){
       const el=button(label,()=>{menu.hidePopover();action();});
       el.setAttribute('role','menuitem');el.disabled=disabled;menu.append(el);return el;
     }
     const row=paragraphs.find(r=>r.id===id);
     if(!selecting&&!protectedBlock(row)){
       const style=button('段落类型',()=>{
         menu.replaceChildren();
         for(let level=0;level<=6;level++){
           const label=level===0?'正文':level+' 级标题';
           const el=item(label,()=>{
             const i=paragraphs.findIndex(r=>r.id===id);if(i<0)return;
             const caret=lastPosition?.id===id?lastPosition.span[0]:0;
             if(paragraphs[i].level!==level){remember();paragraphs[i]={...paragraphs[i],level};render();save();}
             caretIn(i,caret);
           });
           el.setAttribute('role','menuitemradio');el.setAttribute('aria-checked',String(row.level===level));
         }
         position();menu.querySelector('[aria-checked=true]').focus();
       });
       style.setAttribute('role','menuitem');style.setAttribute('aria-haspopup','menu');menu.append(style);
     }
     item(selecting?'取消选择':'选择段落',toggleSelection);
     if(selecting)item('删除所选 '+selected.size+' 段',deleteSelected,!selected.size);
     item('取消整理',()=>closeEditor());
     function position(){
       const rect=e.target.getBoundingClientRect(),x=e.clientX||rect.left,y=e.clientY||rect.bottom;
       menu.style.left=Math.max(0,Math.min(x,innerWidth-menu.offsetWidth))+'px';
       menu.style.top=Math.max(0,Math.min(y,innerHeight-menu.offsetHeight))+'px';
     }
     root.append(menu);menu.showPopover();position();menu.querySelector('button:enabled')?.focus({preventScroll:true});
     menu.ontoggle=event=>{if(event.newState==='closed')menu.remove();};
     menu.onkeydown=event=>{
       if(event.key==='Escape'){event.preventDefault();event.stopPropagation();menu.hidePopover();root.focus({preventScroll:true});}
       if(event.key==='ArrowDown'||event.key==='ArrowUp'){
         event.preventDefault();
         const choices=[...menu.querySelectorAll('button:enabled')],index=choices.indexOf(document.activeElement);
         choices[(index+(event.key==='ArrowDown'?1:-1)+choices.length)%choices.length].focus();
       }
     };
   };
   root.oncopy=e=>{
     const s=getSelection();if(!s.rangeCount||s.isCollapsed)return;
     const fragment=s.getRangeAt(0).cloneContents(),links=fragment.querySelectorAll('.same-turn-link');
     if(links.length){links.forEach(link=>link.remove());e.preventDefault();e.clipboardData?.setData('text/plain',fragment.textContent);}
   };
   root.tabIndex=-1;
   root.onkeydown=e=>{
     if(e.isComposing||e.keyCode===229||sourceWords?.classList.contains('composing'))return;
     if((e.ctrlKey||e.metaKey)&&!e.shiftKey&&!e.altKey&&e.key.toLowerCase()==='s'){
       e.preventDefault();e.stopPropagation();
       if(!e.repeat)finish();
     }else if((e.ctrlKey||e.metaKey)&&!e.shiftKey&&!e.altKey&&e.key.toLowerCase()==='z'){
       e.preventDefault();e.stopPropagation();undo();
     }else if(e.key==='Escape'){
       e.preventDefault();e.stopPropagation();
       if(selecting)toggleSelection();
       root.focus({preventScroll:true});
     }
   };
   function finish(){
     let rows=editableRows(paragraphs.filter(p=>refs(p.parts).length||!blank(textOf(p.parts))),cat);
     let repaired=false;
     const errors=checkFormulas(rows,original);
     if(errors.length){
       const candidate=simulateFormulaRepair(rows,errors);
       const remaining=candidate&&checkFormulas(candidate,original);
       if(!candidate||remaining.length){
         const {rowIndex,error}=(remaining?.[0]??errors[0]);
         if(selecting){selecting=false;selected.clear();render();}
         const row=Array.from(body.children).find(el=>el.dataset.id===rows[rowIndex].id);
         row?.classList.add('revision-invalid');
         row?.querySelector('.words').focus({preventScroll:true});
         row?.scrollIntoView({block:'nearest'});
         const result=error==='行间公式在整理区中保留为只读。'?error:
           '行内公式未通过 KaTeX 校验。AI 模拟修正未能修复。'+error.slice(0,180);
         notice.textContent=result;
         save();clearTimeout(pendingWrite);persist();return false;
       }
       rows=candidate;repaired=true;
     }
     if(!closeEditor(JSON.stringify(rows)!==JSON.stringify(original)?rows:null))return false;
     if(repaired)status('行内公式初次校验失败，AI 模拟修正后重新通过校验，已保存。');
     return true;
   }
   function closeEditor(rows){
     const draft=turn.readingRevisionDraft;
     delete turn.readingRevisionDraft;
     if(!(rows?commit(turn,rows,'用户'):persist())){
       if(draft!==undefined)turn.readingRevisionDraft=draft;
       notice.textContent='无法保存，请重试。';return false;
     }
     clearTimeout(pendingWrite);endCaret();active=null;anchoredRefresh(turn.id,activeId);return true;
   }
   function focusRow(id){Array.from(body.children).find(r=>r.dataset.id===id)?.querySelector('.words')?.focus({preventScroll:true});}
   active={turn,root,focus:focusRow,endCaret,
     focusState:()=>({id:activeId,span:sourceWords&&selectionOf(sourceWords),within:root.contains(document.activeElement)}),
     pause(){clearTimeout(pendingWrite);endCaret();persist();active=null;anchoredRefresh(turn.id,activeId);}};
   render();
   const start=Array.from(body.children).find(r=>r.dataset.id===activeId)||body.firstElementChild;
   if(start&&readingTop!=null)$('reader').scrollTop+=start.getBoundingClientRect().top-readingTop;
   if(start&&restoredFocus?.span)setCaret(start.querySelector('.words'),restoredFocus.span[0],false,restoredFocus.span[1]);
   else if(!restoredFocus)start?.querySelector('.words').focus({preventScroll:true});
   else if(restoredFocus.within)root.focus({preventScroll:true});
 }
 function commit(turn,rows,author){
   return commitBatch([{turn,rows}],author);
 }
 function commitBatch(edits,author){
   const previous=edits.map(({turn})=>turn.readingRevisions);
   if(previous.some(history=>history!==undefined&&(!Array.isArray(history)||history.some(version=>!Array.isArray(version?.rows))))){
     status('该轮的版本记录来自其他版本，无法保存。');return false;
   }
   for(const {turn,rows} of edits)turn.readingRevisions=[...(turn.readingRevisions||[]),
     {id:crypto.randomUUID(),author,at:new Date().toISOString(),rows:structuredClone(rows)}];
   if(persist())return true;
   edits.forEach(({turn},i)=>{if(previous[i]===undefined)delete turn.readingRevisions;else turn.readingRevisions=previous[i];});
   return false;
 }
 function modal(name){
   const box=make('dialog','revision-dialog');box.setAttribute('aria-label',name);const head=make('header');
   head.append(make('h2','',name),button('关闭',()=>box.close()));box.append(head);document.body.append(box);box.onclose=()=>box.remove();return box;
 }
 function showHistory(turn,version,anchor){
   const dialog=modal('本轮版本历史'),select=make('select'),preview=make('article','revision-history-body');select.setAttribute('aria-label','选择本轮版本');
   const revisions=turn.readingRevisions||[];
   select.add(new Option('原答 · AI','0'));
   revisions.forEach((v,i)=>select.add(new Option((i+1)+' · '+v.author+' · '+time(v.at),String(i+1))));
   select.value=String(version??revisions.length);
   function display(){const i=Number(select.value);preview.innerHTML=i?html(turn,revisions[i-1].rows):svgHtml(turn);const nums=Array(6).fill(0);for(const h of preview.querySelectorAll('h1,h2,h3,h4,h5,h6')){const depth=Number(h.tagName[1]);nums[depth-1]++;nums.fill(0,depth);h.prepend(make('span','revision-number','['+(current().turns.indexOf(turn)+1)+':'+nums.slice(0,depth).join('.')+'] '));}
   if(anchor&&i===version){
     const passage=[...preview.querySelectorAll('.passage')].find(el=>el.id===anchor.rowId);
     const formula=anchor.formula&&[...preview.querySelectorAll('.equation .math')].find(el=>el.dataset.tex===anchor.formula.tex&&(!passage||passage.contains(el)));
     const target=formula?.closest('.equation')||passage;
     if(target){target.classList.add('citation-history-target');requestAnimationFrame(()=>{if(dialog.open)target.scrollIntoView({block:'center'});});}
   }
   preview.querySelectorAll('[id]').forEach(el=>el.removeAttribute('id'));preview.querySelectorAll('button,input,textarea,select').forEach(el=>el.disabled=true);
   prepareReadingMath(preview);}
   select.onchange=display;dialog.append(select,preview);dialog.showModal();display();
 }
 // The prototype models an exact replacement. Give this writer the same TeX
 // source as the native tool while leaving the human editor's atoms untouched.
 function aiSourceRows(turn,captured){
   const cat=catalog(turn);
   return captured.map(row=>({...row,parts:compact(row.parts.map(part=>{
     if(isText(part))return part;
     if(part.equation!==undefined)return part.equation;
     if(part.displaySource)return part.displaySource;
     const table=tableSource(part,cat);
     if(table!==undefined)return table;
     const math=cat.atoms.get(part.ref)?.querySelector(':scope > .equation > .math');
     return math?(cat.sources.get(part.ref)??'$$'+math.dataset.tex+'$$'):part;
   }))}));
 }
 function tableSource(part,cat){
   return part.tableSource??cat.tables.get(part.ref);
 }
 function mapEquationReferences(part,cat,visit){
   if(isText(part))return part;
   if(part.equation)return visit(part);
   const source=tableSource(part,cat);
   if(!source||!source.includes('\\'))return part;
   const opaque=[...revisionFormulaSpans(source),...Array.from(source.matchAll(/(`+)[\s\S]*?\1(?!`)/g),m=>({start:m.index,end:m.index+m[0].length}))];
   const updated=source.replace(equationRefs,(raw,command,label,index)=>{
     if(opaque.some(span=>index>=span.start&&index<span.end))return raw;
     return visit({equation:raw,label,parentheses:command==='eqref'}).equation;
   });
   return updated===source?part:{...part,tableSource:updated};
 }
 function protectedTableContent(source){
   if(!source||!/[!`{]/.test(source))return [];
   const body=rootOf(renderSavedAnswer(source,'table-objects')),objects=[];
   const visit=node=>{
     if(node.nodeType===Node.TEXT_NODE){
       for(const match of node.data.matchAll(/\{\{(?:text|textarea|checkbox|radio|file):[^{}\r\n]+\}\}/g))objects.push(match[0]);
     }else if(node.nodeType!==Node.ELEMENT_NODE&&node.nodeType!==Node.DOCUMENT_FRAGMENT_NODE)return;
     else if(node.matches?.('.math'))return;
     else if(node.matches?.('code'))objects.push(['code',node.textContent]);
     else if(node.matches?.('img'))objects.push(['image',node.getAttribute('src'),node.alt,node.title]);
     else node.childNodes.forEach(visit);
   };
   visit(body);return objects;
 }
 function aiPublishedRows(turn,source,captured,before){
   const rows=[],cat=catalog(turn);
   const original=new Map(before.map((row,index)=>[row.id,{source:row,published:captured[index]}]));
   const retained=new Set(source.map(row=>row.id));
   for(const row of captured)if(!retained.has(row.id)&&row.parts.some(p=>!isText(p)&&protectedTableContent(tableSource(p,cat)).length))
     throw Error('请保留表格中已有的表单、图片及行内代码。');
   for(const row of source){
     const old=original.get(row.id);
     if(old&&old.source.level===row.level&&JSON.stringify(old.source.parts)===JSON.stringify(row.parts)){
       rows.push(old.published);continue;
     }
     const content=textOf(row.parts).trim(),prior=old?.published.parts.find(p=>!isText(p)&&tableSource(p,cat)!==undefined);
     if(prior&&JSON.stringify(protectedTableContent(tableSource(prior,cat)))!==JSON.stringify(protectedTableContent(content)))
       throw Error('请保留表格中已有的表单、图片及行内代码。');
     const tables=content.includes('|')&&content.includes('\n')?answerPipeTables(content):[];
     if(tables.length===1&&tables[0]===content){
       if(row.level||refs(row.parts).some(p=>atomic(p.ref)))throw Error('请将管道表格作为独立正文段落。');
       rows.push({...row,parts:[{ref:prior?.ref||'block:'+row.id+':table',tableSource:content},...refs(row.parts)]});
       continue;
     }
     if(content.includes('\n')&&(content.startsWith('|')||prior&&content.split(/\r?\n/).filter(line=>line.includes('|')).length>1))
       throw Error('请提交完整的管道表格，包括表头和分隔行。');
     let parts=[],first=true;
     function append(){
       if(!parts.length)return;
       if(!first&&refs(parts).length===parts.length&&parts.every(p=>!atomic(p.ref))&&rows.at(-1).parts.some(p=>p.displaySource)){
         rows.at(-1).parts.push(...parts);parts=[];return;
       }
       const next=first?{...row,parts}:newRow(parts);
       if(protectedBlock(next))next.level=0;
       rows.push(next);parts=[];first=false;
     }
     for(const part of row.parts){
       if(!isText(part)){parts.push(part);continue;}
       let start=0;
       for(const formula of revisionFormulaSpans(part).filter(f=>f.display&&f.closed)){
         const prefix=part.slice(start,formula.start);
         if(prefix.trim())parts.push(prefix);
         append();parts.push({ref:'block:'+row.id+':math:'+formula.start,displaySource:formula.raw});append();start=formula.end;
       }
       if(start<part.length&&(start===0||part.slice(start).trim()))parts.push(part.slice(start));
     }
     append();
   }
   return editableRows(rows,catalog(turn));
 }
 function revisionEquationIndex(turn,rows){
   const doc=rootOf(html(turn,rows));
   return equationIndex(turn.id,doc.querySelectorAll('.equation .math'));
 }
 const formulaLabel=tex=>/\\label\{([^{}]+)\}/.exec(tex)?.[1];
 function citationAt(ctx){
   const turn=current().turns.find(t=>t.id===ctx.turn);
   if(!turn)return;
   const anchor={turnId:turn.id,rowId:ctx.revisionBlock,version:turn.readingRevisions?.length||0};
   if(ctx.equationId){
     const equation=publishedEquationIndex(turn).entries.find(eq=>eq.id===ctx.equationId);
     if(equation){
       const tex=equation.math.dataset.tex;
       anchor.formula={label:formulaLabel(tex),tex,tag:equation.tag};
     }
   }
   if(ctx.tableObject){
     const row=(live(turn)??catalog(turn).rows).find(row=>row.id===anchor.rowId);
     const part=row?.parts.find(p=>!isText(p)&&tableSource(p,catalog(turn))!==undefined);
     if(part)anchor.tableSource=tableSource(part,catalog(turn));
   }
   return anchor;
 }
 function citationEquation(turn,anchor){
   const index=publishedEquationIndex(turn),label=anchor.formula.label;
   const named=label&&index.find(label);
   if(named)return named;
   const candidates=index.entries.filter(eq=>eq.math.closest('.passage')?.id===anchor.rowId);
   return candidates.length===1?candidates[0]:null;
 }
 function citationRow(turn,entry,replyId){
   const rows=live(turn)??catalog(turn).rows,ref=replyId&&'turn:'+replyId+':'+entry.id;
   const moved=ref&&live(turn)&&rows.find(row=>row.parts.some(part=>part.ref===ref));
   return moved||rows.find(row=>row.id===entry.citation.rowId);
 }
 function citationChanged(entry,replyId){
   const anchor=entry.citation,turn=anchor&&current().turns.find(t=>t.id===anchor.turnId);
   if(!turn||anchor.version===(turn.readingRevisions?.length||0))return false;
   if(anchor.formula)return citationEquation(turn,anchor)?.math.dataset.tex!==anchor.formula.tex;
   const row=citationRow(turn,entry,replyId);
   return !row||!textOf(aiSourceRows(turn,[row])[0].parts).includes(anchor.tableSource??entry.quote.trim());
 }
 function openCitation(entry,historical,replyId){
   const anchor=entry.citation,turn=current().turns.find(t=>t.id===anchor?.turnId);
   if(!turn)return status('来源轮次已不存在。');
   if(historical)return showHistory(turn,anchor.version,anchor);
   if(anchor.formula){
     const equation=citationEquation(turn,anchor);
     if(!equation)return status('当前稿中无法唯一确定这道公式，可查看批注时版本。');
     jumpToEquation({dataset:{equationTurn:turn.id,equationRef:equation.id}});return;
   }
   if(revealFoldedTurn(turn))renderTurns();
   const id=citationRow(turn,entry,replyId)?.id,row=readingRow(id),passage=row?.closest('.passage');
   if(!passage)return status('当前稿中的段落已变化，可查看批注时版本。');
   let parents=[];
   for(const item of passage.closest('.answer').querySelectorAll(':scope>.passage')){
     const h=item.querySelector('h1,h2,h3,h4,h5,h6');
     if(h){const level=Number(h.tagName[1]);parents=parents.filter(p=>p.level<level);parents.push({level,id:item.id});}
     if(item===passage)break;
   }
   if(parents.some(p=>turn.sections?.[p.id])){for(const p of parents)turn.sections[p.id]=false;renderTurns();}
   scrollToReadingTarget(readingRow(id),'center');saveReading();
 }
 function prepareAiEdits(turn,rows,before,captured,reviewEquation){
   const errors=checkFormulas(rows,before,true);
   if(errors.length)throw Error('替换内容中的公式未通过 KaTeX 校验。'+errors[0].error.slice(0,180));
   const edits=[{turn,rows:aiPublishedRows(turn,rows,captured,before)}],pending=new Set(),review=new Set();
   const oldIndex=publishedEquationIndex(turn),newIndex=revisionEquationIndex(turn,edits[0].rows);
   const rowOf=eq=>eq.math.closest('.passage').id;
   const retained=new Map(newIndex.entries.map(eq=>[rowOf(eq),eq]));
   function fixReferences(source,sourceRows){
     let changed=false;
     const original=publishedEquationIndex(source);
     const cat=catalog(source);
     const fixed=sourceRows.map(row=>({...row,parts:row.parts.map(p=>mapEquationReferences(p,cat,part=>{
       const match=/^([1-9]\d*):(.+)$/u.exec(part.label);
       const prior=resolveEquationReference(part.label,source.id,original);
       if(!prior||prior.turnId!==turn.id)return part;
       if(source!==turn&&prior.id===reviewEquation?.id){
       const containsMath=row.parts.some(p=>revisionFormulaSpans(isText(p)?p:tableSource(p,cat)||'').length);
         if(containsMath)review.add(current().turns.indexOf(source)+1);
       }
       const key=match?match[2]:part.label;
       const same=newIndex.find(key),oldLabel=formulaLabel(prior.math.dataset.tex);
       const dest=(oldLabel&&newIndex.find(oldLabel))||retained.get(rowOf(prior));
       if(same&&(dest?rowOf(same)===rowOf(dest):key===/\\label\{([^{}]+)\}/.exec(same.math.dataset.tex)?.[1]))return part;
       if(!dest){pending.add(current().turns.indexOf(source)+1);return part;}
       const label=/\\label\{([^{}]+)\}/.exec(dest.math.dataset.tex)?.[1]||'eq-'+dest.tag;
       const qualified=(match?match[1]+':':'')+label;
       changed=true;return {...part,label:qualified,equation:'\\'+(part.parentheses?'eqref':'ref')+'{'+qualified+'}'};
     }))}));
     return changed?fixed:null;
   }
   edits[0].rows=fixReferences(turn,edits[0].rows)??edits[0].rows;
   const existingMissing=new Map();
   function check(rowList,local,old){
     const cat=catalog(turn);
     for(const row of rowList)for(const part of row.parts)mapEquationReferences(part,cat,reference=>{
       if(resolveEquationReference(reference.label,turn.id,local))return reference;
       const count=existingMissing.get(reference.label)||0;
       if(old)existingMissing.set(reference.label,count+1);
       else if(count)existingMissing.set(reference.label,count-1);
       else throw Error('公式引用找不到目标：'+reference.equation);
       return reference;
     });
   }
   check(captured,oldIndex,true);check(edits[0].rows,newIndex,false);
   for(const other of current().turns){
     if(other===turn||other.incomplete||!snapshot(other)?.includes(':'))continue;
     const fixed=fixReferences(other,capture(other));
     if(fixed)edits.push({turn:other,rows:fixed});
   }
   return {edits,pending,review};
 }
 function askAi(turn,prompt,requestedBinding){
   const conversation=current();
   const prior=turn.readingAiTask;
   const binding=requestedBinding||prior?.binding;
   const reply=binding&&conversation.turns.find(t=>t.id===binding.replyId);
   const entry=(reply?.request?.annotations??reply?.annotations)?.find(a=>a.id===binding.entryId&&a.citation?.turnId===turn.id);
   function setReply(text){
     if(reply){reply.answer=text;delete reply.answerHtml;}
   }
   function report(text){
     setReply(reply?.annotationEdits?.length?reply.answer+' '+text:text);if(reply){persist();renderTurns();}status(text);
   }
   if(binding&&!entry){report('无法确定这条批注的来源，请重新选择原文。');return;}
   if(entry&&citationChanged(entry,reply.id)){report('所选原文已更新，请核对最新版本后重新批注。');return;}
   if(conversation.permission==='只读'||turn.readingRevisionDraft){report(conversation.permission==='只读'?'当前为只读，改稿未写入。':'本轮有手动草稿，请先完成或取消整理。');return;}
   const namedTurn=entry?.question.match(/(?:改写|修改|重写|整理|改|把|将)\s*第\s*(\d+)\s*轮/u)?.[1];
   if(namedTurn&&Number(namedTurn)!==conversation.turns.indexOf(turn)+1){report('批注所选的轮次和指令不同。请重新选择要修改的原文。');return;}
   const namedFormula=entry?.question.normalize('NFKC')
     .match(/^(?:请(?:你)?(?:帮我)?|帮我|麻烦你)?\s*(?:直接|就地|原地)?\s*(?:修改|改写|改|重写|整理|把|将|给)\s*(?:第\s*\d+\s*轮的?)?\s*(?:这个|这条|该)?(?:公式|式子|等式)\s*\(?\s*(\d+(?:\s*:\s*\d+)*)/u)?.[1].replace(/\s/g,'');
   if(namedFormula&&entry.citation.formula&&!([entry.citation.formula.tag,(conversation.turns.indexOf(turn)+1)+':'+entry.citation.formula.tag].includes(namedFormula))){
     report('批注所选的公式和指令中的编号不同。请重新选择要修改的公式。');return;
   }
   if(entry?.endBlock&&entry.endBlock!==entry.block&&entry.endBlock!==entry.citation.rowId){
     report('这条批注跨越多个段落，请分别选中要修改的内容。');return;
   }
   const dialog=modal('AI 改稿 · 原型模拟'),label=make('label'),input=make('textarea'),feedback=make('p','revision-feedback');
   let task=(!requestedBinding&&prior)||{turnId:turn.id,phase:'request',messages:[],binding};
   const messages={request:'希望 AI 怎样修改本轮？',source:'请指出要改写的原句。',replacement:'这处换成什么文字？'};
   if(entry?.citation.formula)messages.replacement='这道公式应改成什么？请输入新的 TeX 公式。';
   label.textContent=messages[task.phase];input.placeholder=task.phase==='request'?'例如：把「原句」改为「新句」':'';label.append(input);
   const send=button('提交',()=>{
      const text=input.value.trim();if(!text){input.focus();return;}
      const captured=capture(turn),rows=aiSourceRows(turn,captured),before=structuredClone(rows);
      let scope,excerpt;
      if(entry){
        const anchor=entry.citation,eq=anchor.formula&&citationEquation(turn,anchor);
        scope=rows.find(row=>row.id===(eq?.math.closest('.passage')?.id??citationRow(turn,entry,reply.id)?.id));
        const value=scope&&textOf(scope.parts);
        excerpt=anchor.formula&&eq&&value?revisionFormulaSpans(value).find(f=>f.display&&f.closed)?.raw:anchor.tableSource??entry.quote.trim();
        if(!scope||!excerpt||(anchor.formula&&!eq)||!value.includes(excerpt)){
          feedback.textContent='无法把批注唯一定位到当前稿，请在最新段落重新批注。';return;
        }
      }
      task={...task,messages:[...task.messages,{author:'用户',text}]};
      if(task.phase==='request'){
        const match=/(?:把|将)?\s*[「“『]([^」”』]+)[」”』]\s*(?:改[成为]|替换[成为])\s*[「“『]([^」”』]*)[」”』]/u.exec(text);
        if(match){task.from=match[1];task.to=match[2];}
        else if(entry){task.from=excerpt;task.phase='replacement';}
        else task.phase='source';
      }else if(task.phase==='source'){task.from=text;task.phase='replacement';}
      else task.to=text==='删除'?'':text;
      if(task.from!==undefined){
        if(entry&&!excerpt.includes(task.from)){
          feedback.textContent='替换原句超出了这条批注选中的内容。请在选区内指定修改。';delete task.from;delete task.to;task.phase='source';
          label.firstChild.textContent=messages.source;input.value='';input.focus();return;
        }
        const found=[];
        for(const [i,row] of rows.entries()){
          if(scope&&row.id!==scope.id)continue;
          const visible=row.parts.map(p=>isText(p)?p:atomic(p.ref)?'\ufffc':'').join('');
          for(let at=visible.indexOf(task.from);at>=0;at=visible.indexOf(task.from,at+1))found.push({i,at});
        }
        if(found.length!==1){delete task.from;delete task.to;task.phase='source';feedback.textContent=found.length?'多处文字相同，请补充相邻文字。':'没找到这句，请从当前稿复制原句。';}
        else if(task.to!==undefined){
          const {i,at}=found[0],row=rows[i];
          if(entry?.citation.formula&&task.from===excerpt){
            let replacement=task.to.trim();
            if(replacement&&!revisionFormulaSpans(replacement).some(f=>f.display))replacement='$$'+replacement+'$$';
            const equations=revisionFormulaSpans(replacement).filter(f=>f.display&&f.closed);
            const oldLabel=formulaLabel(excerpt);
            if(!equations.length||(equations.length>1&&(!oldLabel||equations.filter(f=>formulaLabel(f.raw)===oldLabel).length!==1))){
              feedback.textContent='删除或拆分后，哪道公式承接原来的引用？请给出完整公式，并在承接公式上保留原标签。';
              task.from=excerpt;delete task.to;task.phase='replacement';label.firstChild.textContent=messages.replacement;input.value='';input.focus();return;
            }
            if(equations.length===1&&oldLabel&&!formulaLabel(replacement)){
              const f=equations[0],end=f.end-2;
              replacement=replacement.slice(0,end)+'\\label{'+oldLabel+'}'+replacement.slice(end);
            }
            task.to=replacement;
          }
          let pos=0,offset=0,begin,end;
          for(const p of row.parts){if(!isText(p)){if(atomic(p.ref))offset++;pos++;continue;}
            if(begin===undefined&&at>=offset&&at<offset+p.length)begin=pos+at-offset;
            if(at+task.from.length>offset&&at+task.from.length<=offset+p.length)end=pos+at+task.from.length-offset;
            pos+=p.length;offset+=p.length;
          }
          if(begin===undefined||end===undefined){feedback.textContent='请选择正文、公式或公式引用。';return;}
          if(refs(slice(row.parts,begin,end)).some(p=>atomic(p.ref))){feedback.textContent='选中的范围包含受保护的对象。';return;}
          row.parts=compact([...slice(row.parts,0,begin),task.to,...refs(slice(row.parts,begin,end)),...slice(row.parts,end)]);
          if(blank(textOf(row.parts))&&!refs(row.parts).some(p=>atomic(p.ref))){const links=refs(row.parts);if(i>0){rows[i-1].parts.push(...links);rows.splice(i,1);}else if(!links.length)rows.splice(i,1);}
          let plan;
          try{
            const inspect=entry?.citation.formula&&/(?:前后文|上下文|统一.{0,12}(?:符号|术语)|不一致的(?:符号|术语))/u.test(entry.question);
            plan=prepareAiEdits(turn,rows,before,captured,inspect?citationEquation(turn,entry.citation):null);
            if(entry&&plan.pending.size){plan=null;throw Error('仍有引用无法确定目标。请保留所引公式的标签，或给出它的替代公式。');}
          }
          catch(error){
            feedback.textContent=error.message;
            delete task.to;task.phase='replacement';
          }
          if(plan){
            const {edits,pending}=plan,versions=edits.map(({turn})=>turn.readingRevisions?.at(-1));
            const numbers=edits.map(item=>conversation.turns.indexOf(item.turn)+1);
            const previousEdits=reply?.annotationEdits??[],change={entryId:entry?.id,numbers};
            if(entry?.citation.formula){
              const index=revisionEquationIndex(turn,edits[0].rows),oldLabel=entry.citation.formula.label;
              const rowTargets=index.entries.filter(eq=>eq.math.closest('.passage')?.id===scope.id);
              const eq=(oldLabel&&index.find(oldLabel))||(rowTargets.length===1&&rowTargets[0]);
              if(!eq){feedback.textContent='请明确哪道公式承接原来的引用。';input.value='';return;}
              change.reference='\\eqref{'+numbers[0]+':'+(formulaLabel(eq.math.dataset.tex)||'eq-'+eq.tag)+'}';
            }
            const allEdits=[...previousEdits,change];
            const revisedTurns=[...new Set(allEdits.flatMap(edit=>edit.numbers))].sort((a,b)=>a-b);
            const references=[...new Set(allEdits.map(edit=>edit.reference).filter(Boolean))];
            let receipt='已修改第 '+revisedTurns.join('、')+' 轮。'+(references.length?'见公式 '+references.join('、')+'。':'');
            if(plan.review.size)receipt+='第 '+[...plan.review].join('、')+' 轮引用这道公式，段中的符号也需核对。';
            if(entry&&state.conversations.some(c=>c.branchSource?.parentId===conversation.id&&edits.some(e=>e.turn.id===c.branchSource.turn)))
              receipt+='相关分支保留了分叉时的引文。';
            function apply(){
              if(current()!==conversation||conversation.permission==='只读'||edits.some(({turn},i)=>
                !conversation.turns.includes(turn)||turn.readingRevisionDraft||versions[i]!==turn.readingRevisions?.at(-1))){
                dialog.close();status('会话、稿件或权限已变化，请重新核对。');return;
              }
              const previousReply=reply?.answer;
              if(reply){reply.annotationEdits=allEdits;setReply(receipt);}
              if(!commitBatch(edits,'AI')){if(reply){reply.annotationEdits=previousEdits;setReply(previousReply);}return;}
              delete turn.readingAiTask;persist();dialog.close();
              renderTurns();
              const related=edits.slice(1).map(item=>conversation.turns.indexOf(item.turn)+1);
              status('AI 模拟改稿已保存。'+(related.length?'已同步第 '+related.join('、')+' 轮的引用。':'')+
                (pending.size?'第 '+[...pending].join('、')+' 轮引用的公式已删除，请核对。':''));
              if(binding?.remaining?.length){
                const [next,...remaining]=binding.remaining,note=reply.annotations.find(e=>e.id===next);
                if(note)askAi(turn,note.question,{replyId:reply.id,entryId:next,remaining});
              }
            }
            if(conversation.permission==='执行前询问'){
              label.hidden=true;send.hidden=true;
              feedback.textContent='允许 AI 修改第 '+edits.map(item=>conversation.turns.indexOf(item.turn)+1).join('、')+' 轮吗？';
              dialog.append(button('同意修改',apply,'revision-primary'),button('拒绝',()=>{dialog.close();delete turn.readingAiTask;report('已拒绝改稿。');}));
            }else apply();
            return;
          }
        }
      }
      task.messages.push({author:'AI',text:messages[task.phase]});turn.readingAiTask=task;persist();label.firstChild.textContent=messages[task.phase];input.value='';input.placeholder='';input.focus();
   },'revision-primary');
   dialog.append(label,feedback,send);dialog.showModal();input.focus();
   if(prompt){input.value=prompt;send.click();}
 }
 document.addEventListener('click',e=>{
   const open=e.target.closest('[data-revise],#revise-here,#revision-history,#revision-ai');if(!open)return;
   e.preventDefault();e.stopPropagation();
   const turn=current().turns.find(t=>t.id===(open.dataset.revise||context?.turn));
   if(!turn||running?.turn.id===turn.id)return;
   if(open.id==='revision-history'){closePopups();showHistory(turn);return;}
   if(open.id==='revision-ai'){closePopups();askAi(turn);return;}
   const savedId=open.id==='revise-here'?context?.revisionBlock:null;
   closePopups();begin(turn,savedId,readingRow(savedId));
 },true);
 window.addEventListener('pagehide',()=>{if(active?.root.isConnected)persist();});
 window.turnRevision={html,snapshot,askAi,citationAt,citationChanged,openCitation,focusState:()=>active?.root.isConnected?active.focusState():null,resume(focus){
   if(!active||active.root.isConnected)return;
   active.endCaret();
   if(current().turns.includes(active.turn)&&document.getElementById(active.turn.id)?.querySelector('.answer'))begin(active.turn,focus?.id,null,focus);
   else active=null;
 }};
})();

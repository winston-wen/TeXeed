'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'), path=require('node:path'), {pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');

async function checkEditedStrong(page){
 const source=String.raw`要点： \eqref{eq:recover} 用的是 $w'$，却能恢复从 $w_1$ 提取的位。 看公式 \eqref{eq:products} 的第一行和最后一行。`;
 await page.evaluate(source=>{
   current().turns=[{id:'bold',question:'怎样恢复？',segments:[],
     answer:source+'\n\n'+String.raw`$$w=x\label{eq:recover}$$`+'\n\n'+String.raw`$$v=x\label{eq:products}$$`}];
   renderConversation();persist();
 },source);
 const field=page.locator('[data-id="bold-p0"] .words'),reading=page.locator('#bold-p0 p');
 await page.locator('[data-revise="bold"]').click();
 assert.equal(await field.locator('.revision-equation-ref[contenteditable=false]').count(),2);
 async function insertAfter(text){
   await field.evaluate((el,text)=>{
     const node=[...el.childNodes].find(n=>n.nodeType===3&&n.data.includes(text));
     getSelection().collapse(node,node.data.indexOf(text)+text.length);
   },text);
   await page.keyboard.insertText('**');
 }
 await insertAfter('要点：');await insertAfter('提取的位。');
 const edited=source.replace('要点：','要点：**').replace('提取的位。','提取的位。**');
 assert.equal(await field.textContent(),edited);
 await page.keyboard.press('Escape');
 async function check(root){
   const strong=root.locator('strong');
   assert.equal(await strong.count(),1,'两端星号跨过受保护的公式引用和行内公式时应渲染为一个强调范围');
   assert.equal(await strong.locator('.equation-ref').count(),1);
   assert.equal(await root.locator('.equation-ref').count(),2);
   assert.deepEqual(await strong.locator('.math').evaluateAll(nodes=>nodes.map(n=>n.dataset.tex)),["w'",'w_1']);
   assert.equal(await strong.evaluate(el=>el.nextSibling.textContent.startsWith(' 看公式')),true);
   assert.equal((await root.textContent()).includes('**'),false);
   assert.notEqual(await strong.evaluate(el=>getComputedStyle(el).color),await root.evaluate(el=>getComputedStyle(el).color));
 }
 await check(field);
 const point=await field.locator('strong').evaluate(el=>{
   const text=[...el.childNodes].find(n=>n.nodeType===3&&n.data.includes('提取的位'));
   const range=document.createRange(),at=text.data.indexOf('提取的位');
   range.setStart(text,at);range.setEnd(text,at+1);
   const rect=range.getBoundingClientRect();return {x:rect.left+1,y:rect.top+rect.height/2};
 });
 await page.mouse.click(point.x,point.y);
 assert.equal(await field.evaluate(el=>{
   const range=getSelection().getRangeAt(0).cloneRange();range.setStart(el,0);return range.cloneContents().textContent;
 }),edited.slice(0,edited.indexOf('提取的位')),'点击引用和两条公式之后的强调文字应回到该字的源码位置');
 await page.keyboard.press('Escape');
 await page.reload();
 await page.locator('[data-revise="bold"]').click();
 assert.equal(await field.textContent(),edited,'草稿重开可继续修改原始标记');
 await page.keyboard.press('Control+s');
 await check(reading);
 await page.reload();await check(reading);
 // Some existing revisions store a whole mixed Markdown paragraph in a single string.
 await page.evaluate(edited=>{
   current().turns[0].readingRevisions.at(-1).rows[0].parts=[edited];persist();
 },edited);
 await page.reload();await check(reading);
 if(process.env.TEXEED_REVISION_ARTIFACTS){
   fs.mkdirSync(process.env.TEXEED_REVISION_ARTIFACTS,{recursive:true});
   await reading.screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'edited-strong.png')});
 }
 await reading.locator('strong .equation-ref').click();
 await page.locator('#equation-preview .math').waitFor();
 assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),/eq:recover/);
 await page.keyboard.press('Escape');
 await reading.locator('strong .equation-ref').dblclick();
 await page.locator('#bold-eq-1.equation-target').waitFor();
}

async function checkRevisionFinish(page){
 await page.evaluate(()=>{
   current().turns=[
     {id:'first-edit',question:'整理前一轮',answer:'保留这一轮的草稿。',segments:[]},
     {id:'second-edit',question:'整理后一轮',answer:Array.from({length:48},(_,i)=>
       '第 '+(i+1)+' 段，核对符号、公式和上一步的结果。').join('\n\n'),segments:[]}];
   renderConversation();persist();
 });
 await page.waitForFunction(()=>!restoring);
 await page.locator('[data-revise="first-edit"]').click();
 await page.keyboard.press('End');await page.keyboard.insertText('尚未完成。');
 const id='second-edit-p26',reading=page.locator('#'+id+' p');
 await reading.evaluate(p=>p.scrollIntoView({block:'center'}));
 await reading.click({button:'right'});await page.locator('#revise-here').click();
 const field=page.locator('[data-id="'+id+'"] .words'),editor=page.locator('#second-edit .revision-editor');
 assert.equal(await field.evaluate(el=>el===document.activeElement),true);
 await page.keyboard.press('End');await page.keyboard.insertText('已订正。');
 await page.keyboard.press('Escape');assert.equal(await editor.locator('[contenteditable=true]').count(),0);
 const y=await field.evaluate(node=>node.getBoundingClientRect().y);
 if(process.env.TEXEED_REVISION_ARTIFACTS)
   await page.locator('#reader').screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'shortcut-reader.png')});
 // Losing durable storage must leave the editor and both turns' drafts available for retry.
 await page.evaluate(()=>{window.originalSetItem=Storage.prototype.setItem;Storage.prototype.setItem=()=>{throw new Error('fixture: storage full');};});
 await page.keyboard.press('Control+s');
 assert.equal(await editor.count(),1);assert.match(await editor.locator('.revision-notice').innerText(),/无法保存/);
 assert.equal(await page.evaluate(()=>current().turns[1].readingRevisions?.length||0),0);
 await page.evaluate(()=>{Storage.prototype.setItem=window.originalSetItem;delete window.originalSetItem;});
 await page.keyboard.press('Control+s');assert.equal(await editor.count(),0);
 await page.evaluate(()=>new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
 const after=await reading.evaluate(node=>node.getBoundingClientRect().y);
 assert(Math.abs(after-y)<=2,'完成整理时正在读的段落移动了 '+(after-y)+' px');
 console.log('Active paragraph displacement after finishing:',(after-y).toFixed(2),'px');
 await page.reload();assert.match(await reading.textContent(),/已订正/);
 assert.deepEqual(await page.evaluate(()=>current().turns.map(t=>[t.readingRevisions?.length||0,!!t.readingRevisionDraft])),[[0,true],[1,false]]);
}

async function checkCaretBlink(page,editor,dir){
 const indicator=editor.locator('.revision-caret');
 const cycle=await indicator.evaluate(async caret=>{
   const animation=caret.getAnimations()[0];
   if(!animation)throw new Error('Focused paragraph has no caret animation');
   const duration=animation.effect.getTiming().duration;
   const edges=[];let previous;
   const start=performance.now();
   do{
     await new Promise(requestAnimationFrame);
     const time=animation.currentTime;
     const visible=getComputedStyle(caret).opacity==='1';
     if(visible!==previous){edges.push({time,visible});previous=visible;}
   }while(edges.length<6&&performance.now()-start<4000);
   return {duration,edges};
 });
 assert.equal(cycle.duration,500);
 assert(cycle.edges.length>=5,'Fewer than two complete caret blinks observed');
 for(const {time,visible} of cycle.edges.slice(1)){
   const phase=time%500;
   if(Math.min(phase,Math.abs(phase-250),500-phase)>1)
     assert.equal(visible,phase<250,'Rendered opacity should change with each 250 ms half-cycle');
 }
 await page.emulateMedia({reducedMotion:'no-preference'});
 assert.equal(await indicator.evaluate(el=>el.getAnimations()[0].effect.getTiming().duration),500);
 // Inspect opposite phases of that same animation without racing a screenshot against the next frame.
 const setPhase=async time=>indicator.evaluate((caret,time)=>{
   const animation=caret.getAnimations()[0];animation.pause();animation.currentTime=time;
   return Number(getComputedStyle(caret).opacity);
 },time);
 for(const [time,expected] of [[0,1],[249,1],[250,0],[499,0],[500,1]])
   assert.equal(await setPhase(time),expected,'Unexpected caret opacity at '+time+' ms');
 assert.equal(await setPhase(125),1);
 if(dir)await editor.screenshot({path:path.join(dir,'focused-paragraph.png')});
 const bright=await indicator.screenshot();
 assert.equal(await setPhase(375),0);
 if(dir)await editor.screenshot({path:path.join(dir,'caret-off.png')});
 const dark=await indicator.screenshot();
 assert(!bright.equals(dark),'Painted caret pixels should change between bright and dark phases');
 await page.keyboard.press('ArrowLeft');
 await page.waitForFunction(()=>getComputedStyle(document.querySelector('#focus .revision-caret')).opacity==='1');
 assert.equal(await setPhase(375),0);
 await page.keyboard.insertText('再');
 await page.waitForFunction(()=>getComputedStyle(document.querySelector('#focus .revision-caret')).opacity==='1');
 await page.keyboard.press('Backspace');
 await page.keyboard.press('Shift+ArrowRight');
 await indicator.waitFor({state:'hidden'});
 assert.equal(await indicator.evaluate(caret=>caret.getAnimations().length),0);
 await page.keyboard.press('ArrowLeft');
 await indicator.waitFor({state:'visible'});
 const ime=await page.context().newCDPSession(page);
 try{
   await ime.send('Input.imeSetComposition',{text:'校',selectionStart:1,selectionEnd:1});
   await indicator.waitFor({state:'hidden'});
   await ime.send('Input.imeSetComposition',{text:'',selectionStart:0,selectionEnd:0});
   await indicator.waitFor({state:'visible'});
 }finally{await ime.detach();}
 console.log('Caret half-cycle transitions (ms):',cycle.edges.slice(1).map(x=>Math.round(x.time)));
}

(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 try{
  const page=await browser.newPage({viewport:{width:1250,height:900}});
  page.setDefaultTimeout(5500);
  const errors=[];page.on('pageerror',e=>{errors.push(e.message);console.error(e.message);});
  await page.goto(pathToFileURL(path.join(__dirname,'index.html')).href+'?test=1');
  await require('./annotation-revision-browser.cjs')(page);
  await checkEditedStrong(page);
  await checkRevisionFinish(page);
  const original='# 密钥恢复\n\n先算接收值，再核对。\n\n## 进一步计算\n\n复查结论。\n\n行内 $x+1$ 仍在。\n\n$$w=x+1$$\n\n| 输入 | 输出 |\n| --- | --- |\n| 1 | 2 |';
  await page.evaluate(original=>{
    const c=current();c.turns=[{id:'up',answer:original,question:'如何恢复密钥？',segments:[],reviewDraft:{blocks:{},comments:[],oldComments:{preserve:true}}},{id:'follow',question:'何时核对？',answer:'保留原注。',segments:[],annotations:[{id:'q1',block:'up-p1',quote:'接收值',endTextOffset:5}]}];
    c.draft='我的未发草稿';c.entries=[];
    state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];state.reviewAutoSeen={};
    renderConversation();persist();
  },original);
  async function editAt(target){
    await target.scrollIntoViewIfNeeded();await page.waitForFunction(()=>!restoring);
    await page.evaluate(()=>new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
    await target.click({button:'right'});await page.getByRole('button',{name:'整理这一段',exact:true}).click();
  }
  const row=id=>page.locator('.revision-row[data-id="'+id+'"] .words');
  const save=()=>page.keyboard.press('Control+s');
  async function actionAt(target,label){
    await target.click({button:'right'});await page.getByRole('menuitem',{name:label,exact:true}).click();
  }
  async function readingAction(id,label){
    await page.locator('#'+id+' .answer .passage-body').first().click({button:'right'});
    await page.getByRole('button',{name:label,exact:true}).click();
  }
  async function caret(locator,side,selectAll=false){await locator.evaluate((el,{side,selectAll})=>{
    el.focus({preventScroll:true});const range=document.createRange();range.selectNodeContents(el);if(!selectAll)range.collapse(side==='start');
    const s=getSelection();s.removeAllRanges();s.addRange(range);
  },{side,selectAll});}
  // Start a branch using the ordinary context menu and come back.
  const source=page.locator('#up-p1 p');
  await page.evaluate(()=>document.fonts.ready);
  await source.scrollIntoViewIfNeeded();
  await source.evaluate(el=>{const n=el.firstChild,r=document.createRange();r.setStart(n,0);r.setEnd(n,2);const s=getSelection();s.removeAllRanges();s.addRange(r);const b=r.getBoundingClientRect();el.dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true,clientX:b.x+1,clientY:b.y+1}));});
  await page.locator('#branch-conversation').click();const child=await page.evaluate(()=>current().id);
  await page.locator('#branch-origin-return').click();assert.equal(await source.locator('.same-turn-link').count(),2);
  const baseSnapshot=await page.evaluate(()=>structuredClone(state.conversations.at(-1).inheritedContext));
  await editAt(source);
  const first=row('up-p1');
  assert.equal(await first.evaluate(el=>el===document.activeElement),true);
  assert.equal(await page.locator('.revision-toolbar,.revision-editor select,.revision-editor > button').count(),0);
  assert.equal(await page.locator('.revision-editor').evaluate(el=>el.firstElementChild===el.querySelector('.revision-body')),true,'整理模式应直接从正文开始');
  assert.equal(await first.locator('[contenteditable=false]').count(),2);
  await first.evaluate(el=>{const ref=el.querySelector('[data-open-child]');el.focus();const r=document.createRange();r.setStartAfter(ref);r.collapse(true);const s=getSelection();s.removeAllRanges();s.addRange(r);});
  await page.keyboard.insertText('顺序');assert.match(await first.innerText(),/顺序/);
  await caret(first,'start',true);
  const ime=await page.context().newCDPSession(page);await ime.send('Input.imeSetComposition',{text:'zheng',selectionStart:5,selectionEnd:5});
  await ime.send('Input.insertText',{text:'整句重写'});await ime.detach();
  assert.equal(await first.locator('[contenteditable=false]').count(),2);assert.match(await first.textContent(),/整句重写/);
  await page.keyboard.press('Control+z');assert.match(await first.textContent(),/顺序/);
  await caret(first,'start',true);await page.keyboard.press('Backspace');
  assert.equal(await row('up-p0').locator('.same-turn-link').count(),2);
  assert.equal(await first.count(),0);
  await page.keyboard.press('Control+z');assert.equal(await first.locator('.same-turn-link').count(),2);
  await caret(first,'start',true);await page.keyboard.press('Backspace');
  await caret(row('up-p0'),'end');await page.keyboard.press('Enter');await page.keyboard.insertText('恢复条件');
  const newId=await page.evaluate(()=>document.activeElement.closest('.revision-row').dataset.id);
  await actionAt(row(newId),'段落类型');await page.getByRole('menuitemradio',{name:'2 级标题',exact:true}).click();
  const editedHeading=page.locator('.revision-row[data-id="'+newId+'"] .revision-number');
  assert.equal(await editedHeading.textContent(),'## ');
  assert.equal(await row(newId).textContent(),'恢复条件','标题层级标记由界面绘制，独立于可编辑正文');
  await page.keyboard.press('Escape');
  assert.equal(await editedHeading.textContent(),'[1:1.1] ');
  await caret(row(newId),'end');await page.keyboard.press('Shift+Enter');await page.keyboard.insertText('按提示恢复。');
  await caret(row('up-p3'),'start');await page.keyboard.press('Backspace');
  assert.equal(await row('up-p3').count(),0);
  // Reversibility for whole-paragraph selection. Inline source can be deleted; display math is locked.
  await actionAt(row('up-p0'),'选择段落');
  await page.locator('.revision-row[data-id="up-p0"]').click();await page.locator('.revision-row[data-id="up-p4"]').click();
  await actionAt(page.locator('.revision-row[data-id="up-p4"]'),'删除所选 2 段');
  assert.equal(await page.locator('.revision-editor .same-turn-link').count(),2);
  assert.equal(await page.locator('.revision-editor .math').count(),1);
  assert.equal(await row('up-p4').count(),0);
  await page.keyboard.press('Control+z');
  assert.equal(await row('up-p4').locator('.math').getAttribute('data-tex'),'x+1');
  await row('up-p4').focus();
  assert.match(await row('up-p4').textContent(),/\$x\+1\$/);
  const dir=process.env.TEXEED_REVISION_ARTIFACTS;if(dir){fs.mkdirSync(dir,{recursive:true});await page.locator('.revision-editor').screenshot({path:path.join(dir,'editing.png')});}
  await save();
  assert.equal(await page.locator('#up .answer table').count(),1);
  assert.equal(await page.locator('#up .answer .math').count(),2);
  assert.equal(await page.locator('#up-p0 .same-turn-link').count(),2);
  assert.equal(await page.evaluate(()=>current().turns[0].readingRevisions[0].author),'用户');
  assert.equal(await page.evaluate(()=>current().turns[0].answer),original);
  await page.reload();await page.waitForSelector('#up-p0 .same-turn-link');
  assert.equal(await page.locator('#composer').inputValue(),'我的未发草稿');
  assert.equal(await page.locator('#up-p0 .same-turn-link').count(),2);
  await page.locator('#thread [data-open-child="'+child+'"]').click();await page.locator('#branch-origin-return').click();
  assert.equal(await page.locator('#up-p0 .same-turn-link').count(),2);
  assert.deepEqual(await page.evaluate(()=>state.conversations.at(-1).inheritedContext),baseSnapshot);
  await readingAction('up','请 AI 改这一轮');
  const dialog=page.getByRole('dialog',{name:'AI 改稿 · 原型模拟'});
  await dialog.locator('textarea').fill('帮我把结论写具体');await dialog.getByRole('button',{name:'提交'}).click();
  assert.match(await dialog.locator('label').textContent(),/原句/);
  await dialog.locator('textarea').fill('按提示恢复。');await dialog.getByRole('button',{name:'提交'}).click();
  await dialog.getByRole('button',{name:'关闭'}).click();
  await page.reload();await readingAction('up','请 AI 改这一轮');
  assert.match(await dialog.locator('label').textContent(),/什么文字/);
  await dialog.locator('textarea').fill('核对提示后恢复密钥。');await dialog.getByRole('button',{name:'提交'}).click();
  assert.match(await page.locator('#up .answer').innerText(),/核对提示后恢复密钥/);
  assert.deepEqual(await page.evaluate(()=>current().turns[0].readingRevisions.map(v=>v.author)),['用户','AI']);
  await readingAction('up','编辑历史');
  const versions=page.getByRole('dialog',{name:'本轮版本历史'});
  await versions.locator('select').selectOption('0');assert.match(await versions.locator('article').innerText(),/先算接收值/);
  await versions.locator('select').selectOption('1');assert.match(await versions.locator('article').innerText(),/按提示恢复/);
  await versions.locator('select').selectOption('2');assert.match(await versions.locator('article').innerText(),/核对提示后恢复密钥/);
  assert.equal(await page.evaluate(()=>current().turns[0].reviewDraft.oldComments.preserve),true);
  if(dir)await versions.screenshot({path:path.join(dir,'history.png')});
  await versions.getByRole('button',{name:'关闭'}).click();await versions.waitFor({state:'detached'});
  await page.locator('#thread [data-open-child="'+child+'"]').click();await page.locator('#composer').fill('请继续这处追问');await page.locator('#send').click();
  await page.waitForSelector('[data-back-origin]');await page.locator('[data-back-origin]').click();
  assert.match(await page.locator('#up .answer').innerText(),/核对提示后恢复密钥/);
  assert.deepEqual(await page.evaluate(()=>state.conversations.find(c=>c.branchSource).inheritedContext),baseSnapshot);
  await page.evaluate(()=>{
    const c=current();c.turns.push({id:'edges',question:'边界输入',answer:'# 第一节\n相邻正文\n## 子节\n\n中段文字\n\n末段文字',segments:[]});renderTurns();
  });
  await editAt(page.locator('#edges-p0 h2'));
  const edgeEditor=page.locator('#edges .revision-editor'),words=edgeEditor.locator('.words');
  assert.equal(await page.evaluate(()=>document.activeElement.textContent),'子节','同一 Markdown 块中右键子标题应编辑该小段');
  assert.equal(await words.count(),5,'相邻 Markdown 标题与段落均可整理');
  await caret(words.first(),'start');await page.keyboard.press('Enter');
  await caret(words.first(),'start');await page.keyboard.press('Backspace');assert.equal(await words.count(),6,'首空段 Backspace 保留');
  await page.keyboard.press('Delete');assert.equal(await words.count(),5);assert.equal(await page.evaluate(()=>document.activeElement.textContent),'第一节');
  await caret(words.last(),'end');await page.keyboard.press('Enter');await page.keyboard.press('Delete');assert.equal(await words.count(),6,'尾空段 Delete 保留');
  await page.keyboard.press('Backspace');assert.equal(await words.count(),5);assert.equal(await page.evaluate(()=>document.activeElement.textContent),'末段文字');
  await caret(words.nth(1),'end');await page.keyboard.press('Enter');await page.keyboard.press('Delete');
  assert.equal(await page.evaluate(()=>document.activeElement.textContent),'子节');
  await caret(words.nth(1),'end');await page.keyboard.press('Delete');assert.equal(await words.count(),4);
  assert.equal(await page.evaluate(()=>document.activeElement.textContent),'相邻正文子节');
  assert.equal(await page.evaluate(()=>getSelection().anchorOffset),'相邻正文'.length,'合并后光标停在接缝');
  await caret(words.last(),'end');await page.keyboard.insertText('新增草稿');await page.keyboard.press('Control+s');
  assert.equal(await edgeEditor.count(),0,'Ctrl+S 应完成整理并恢复阅读');
  assert.deepEqual(await page.evaluate(()=>[current().turns.at(-1).readingRevisions.map(v=>v.author),current().turns.at(-1).readingRevisionDraft]),[['用户'],undefined]);
  await page.reload();await page.locator('[data-revise="edges"]').click();assert.match(await words.last().textContent(),/新增草稿/);
  await save();
  assert.equal(await page.evaluate(()=>current().turns.at(-1).readingRevisions.length),1,'未改动时完成整理不重复写入');
  await page.locator('[data-revise="edges"]').click();await caret(words.last(),'end');await page.keyboard.insertText('放弃改动');
  await actionAt(words.last(),'取消整理');assert.equal(await edgeEditor.count(),0);assert.doesNotMatch(await page.locator('#edges .answer').innerText(),/放弃改动/);
  assert.equal(await page.evaluate(()=>current().turns.at(-1).readingRevisionDraft),undefined);
  if(dir)await page.locator('#edges').screenshot({path:path.join(dir,'reading.png')});
  await page.evaluate(()=>{
    current().turns.push({id:'tex-edit',question:'如何校验行内公式？',
      answer:'计算 $x+1$，再核对 \\(y-2\\)。\n\n并且依照公式 \\eqref{editable-eq} 处理；参考编号 \\ref{editable-eq}。\n\n$$w=x+1\\label{editable-eq}\\tag{1234567890123456789012345678901234567890}$$\n\n|甲|乙|\n|---|---|\n|1|2|',segments:[]});
    renderTurns();
  });
  const editTex=()=>page.locator('[data-revise="tex-edit"]').click();
  const measureCompiles=()=>page.evaluate(()=>{
    const compile=window.originalCompileRevisionFormula??=window.compileRevisionFormula;
    window.validationCalls=[];
    window.compileRevisionFormula=tex=>{window.validationCalls.push(tex);return compile(tex);};
  });
  const formula=row('tex-edit-p0');
  await editTex();
  assert.match(await formula.textContent(),/\$x\+1\$.*\\\(y-2\\\)/, 'Original inline delimiters are preserved in the editor');
  assert.equal(await formula.locator('.math').count(),0);
  await row('tex-edit-p1').focus();
  assert.equal(await formula.locator('.math').count(),2,'离开段落后两种 TeX 定界符都恢复排版');
  const refs=row('tex-edit-p1').locator('.revision-equation-ref');
  assert.equal(await refs.count(),2);
  assert.deepEqual(await refs.allTextContents(),['\\eqref{editable-eq}','\\ref{editable-eq}']);
  assert.equal(await page.locator('#tex-edit .revision-editor .equation-number').textContent(),'(1234567890123456789012345678901234567890)');
  assert.equal(await refs.first().getAttribute('contenteditable'),'false');
  const wrapped=await refs.first().evaluate(node=>{
    const words=node.closest('.words');words.style.width='92px';
    const range=document.createRange();range.selectNodeContents(node);
    return {lines:new Set([...range.getClientRects()].map(rect=>rect.top)).size,align:getComputedStyle(node).verticalAlign};
  });
  assert(wrapped.lines>1 && wrapped.align==='baseline','The reference wraps and shares the text baseline');
  if(dir)await page.locator('#tex-edit .revision-editor').screenshot({path:path.join(dir,'inline-source.png')});
  await caret(row('tex-edit-p1'),'start',true);await page.keyboard.insertText('保留两个引用。');
  assert.equal(await refs.count(),2,'Deleting across references must retain both protected tokens');
  await measureCompiles();
  await formula.focus();await formula.fill('计算 $x+2$，再核对 \\(y-2\\)。');await save();
  assert.deepEqual(await page.evaluate(()=>window.validationCalls),['x+2'],'A valid edit is compiled once; the untouched expression is reused');
  assert.match(await page.locator('#tex-edit-p0 .math').first().getAttribute('data-tex'),/^x\+2$/);
  assert.equal(await page.locator('#tex-edit .equation-ref').count(),2);
  assert.equal(await page.locator('#tex-edit .equation-ref').first().textContent(),'（1234567890123456789012345678901234567890）');
  assert.equal(await page.locator('#tex-edit .answer table').count(),1);
  await editTex();await measureCompiles();
  await formula.fill('计算 $\\frca{1}{2}$，再核对 \\(y-2\\)。');await save();
  assert.deepEqual(await page.evaluate(()=>window.validationCalls),['\\frca{1}{2}','\\frac{1}{2}'],
    'The simulated repair candidate is compiled only after the initial expression fails');
  assert.match(await page.locator('#status').textContent(),/AI 模拟修正后重新通过校验/);
  assert.equal(await page.locator('#tex-edit-p0 .math').first().getAttribute('data-tex'),'\\frac{1}{2}');
  await editTex();await measureCompiles();
  await formula.fill('换成 $\\unknownPrototypeMacro{x}$。');await save();
  assert.deepEqual(await page.evaluate(()=>window.validationCalls),['\\unknownPrototypeMacro{x}']);
  assert.match(await page.locator('#tex-edit .revision-notice').textContent(),/KaTeX 校验.*未能修复/);
  assert.equal(await page.evaluate(()=>current().turns.at(-1).readingRevisions.length),2);
  await page.reload();await editTex();
  assert.match(await formula.textContent(),/\\unknownPrototypeMacro/,'A failed formula remains in the draft after reloading');
  await formula.fill('缺少 $x 的结束符。');await save();
  assert.match(await page.locator('#tex-edit .revision-notice').textContent(),/结束定界符缺失/);
  await formula.fill('恢复为 $z^2$。');await save();
  assert.equal(await page.locator('#tex-edit-p0 .math').getAttribute('data-tex'),'z^2');
  assert.equal(await page.locator('#tex-edit .equation-ref').count(),2);
  if(dir)await page.locator('#tex-edit .answer').screenshot({path:path.join(dir,'inline-rendered.png')});
  await page.evaluate(()=>{
    current().turns.push({id:'focus',question:'怎样检查每一段的计算？',segments:[],
      answer:'# 校验步骤\n\n先计算 $x_1$，再核对**符号**。\n\n从 $x_2$ 开始，然后检查**下标**。\n\n结束时确认 $x_3$。'});
    renderTurns();
  });
  await page.emulateMedia({reducedMotion:'reduce'});
  await editAt(page.locator('#focus-p2 p'));
  const editor=page.locator('#focus .revision-editor'),fields=editor.locator('.words[contenteditable=true]');
  async function onlySource(id){
    assert.deepEqual(await fields.evaluateAll(nodes=>nodes.map(node=>[node.parentElement.dataset.id,node===document.activeElement])),[[id,true]]);
  }
  await onlySource('focus-p2');
  assert.equal(await row('focus-p2').textContent(),'从 $x_2$ 开始，然后检查**下标**。');
  assert.equal(await row('focus-p1').locator('strong').innerText(),'符号');
  await caret(row('focus-p2'),'start');await page.keyboard.press('ArrowLeft');await onlySource('focus-p1');
  await page.keyboard.press('ArrowRight');await onlySource('focus-p2');
  await page.keyboard.press('Backspace');await onlySource('focus-p1');
  await page.keyboard.press('Control+z');await onlySource('focus-p2');
  assert.equal(await page.evaluate(()=>getSelection().anchorOffset),0,'撤销段首合并应回到原来段首');
  // Even a browser edit that empties the source node must activate the new caret owner.
  await caret(row('focus-p2'),'start',true);await page.keyboard.press('Backspace');
  await onlySource('focus-p1');
  assert.equal(await page.evaluate(()=>getSelection().anchorOffset),'先计算 $x_1$，再核对**符号**。'.length);
  await page.keyboard.press('Control+z');await onlySource('focus-p2');
  assert.equal(await page.evaluate(()=>getSelection().toString()),'从 $x_2$ 开始，然后检查**下标**。','撤销应恢复被删段落的选区');
  await page.keyboard.press('Backspace');await onlySource('focus-p1');
  await page.keyboard.insertText('已核对。');
  await page.keyboard.press('Escape');assert.equal(await fields.count(),0);
  assert.equal(await row('focus-p1').locator('strong').innerText(),'符号');
  assert.equal(await row('focus-p1').locator('.math').getAttribute('data-tex'),'x_1');
  // The pending edit also survives a reload without using the finish shortcut.
  await page.waitForFunction(()=>JSON.parse(localStorage.getItem(KEY)).conversations.find(c=>c.id===current().id).turns.at(-1).readingRevisionDraft?.rows[1].parts.includes('先计算 $x_1$，再核对**符号**。已核对。'));
  assert.deepEqual(await page.evaluate(()=>{
    const turn=JSON.parse(localStorage.getItem(KEY)).conversations.find(c=>c.id===current().id).turns.at(-1);
    return [turn.readingRevisions?.length||0,turn.readingRevisionDraft.rows.map(r=>r.id),turn.readingRevisionDraft.rows[1].parts];
  }),[0,['focus-p0','focus-p1','focus-p3'],['先计算 $x_1$，再核对**符号**。已核对。']]);
  const point=await row('focus-p1').locator('strong').evaluate(el=>{
    const r=document.createRange();r.setStart(el.firstChild,1);r.setEnd(el.firstChild,2);const box=r.getBoundingClientRect();
    return {x:box.left+1,y:box.top+box.height/2};
  });
  await page.mouse.click(point.x,point.y);
  await onlySource('focus-p1');
  assert.equal(await page.evaluate(()=>getSelection().anchorOffset),'先计算 $x_1$，再核对**符'.length,'点击强调字应映射到该字的源码偏移');
  await page.evaluate(()=>renderTurns());await onlySource('focus-p1');
  assert.equal(await page.evaluate(()=>getSelection().anchorOffset),'先计算 $x_1$，再核对**符'.length,'同会话更新不应抢走编辑段的光标');
  await page.waitForFunction(()=>document.querySelector('#focus .revision-caret')?.getBoundingClientRect().width===3);
  const geometry=await editor.locator('.revision-caret').evaluate(el=>{
    const caret=el.getBoundingClientRect(),range=getSelection().getRangeAt(0).getBoundingClientRect();
    return {x:Math.abs(caret.x-range.x),mid:Math.abs((caret.top+caret.bottom-range.top-range.bottom)/2),color:getComputedStyle(el).backgroundColor};
  });
  assert(geometry.x<1&&geometry.mid<1&&geometry.color==='rgb(98, 229, 255)',JSON.stringify(geometry));
  await checkCaretBlink(page,editor,dir);
  await page.locator('#composer').focus();assert.equal(await fields.count(),0);
  await page.keyboard.press('Control+s');
  assert.equal(await page.evaluate(()=>current().turns.at(-1).readingRevisions?.length||0),0,'主输入框里的 Ctrl+S 不应提交失焦的整理稿');
  await page.evaluate(()=>renderTurns());assert.equal(await fields.count(),0);
  assert.equal(await page.evaluate(()=>document.activeElement.id),'composer');
  assert.equal(await editor.locator('.revision-caret').count(),0);assert.equal(await editor.locator('.math').count(),2);
  await page.reload();await page.locator('[data-revise="focus"]').click();
  assert.match(await row('focus-p1').textContent(),/已核对/);
  await save();assert.deepEqual(await page.evaluate(()=>current().turns.at(-1).readingRevisions.map(v=>v.author)),['用户']);
  await page.evaluate(()=>{
    current().turns=[
      {id:'source-math',question:'定义',answer:'先计算。\n\n$$w=x\\label{eq:old}$$\n\n参见公式 \\eqref{eq:old}。',segments:[]},
      {id:'uses-math',question:'复用',answer:'使用公式 \\eqref{1:eq:old}。',segments:[]},
      {id:'unrelated',question:'其他',answer:'无关。',segments:[]}];
    current().permission='完全访问';renderConversation();persist();
  });
  const startAi=()=>readingAction('source-math','请 AI 改这一轮');
  await startAi();
  await dialog.locator('textarea').fill('把「$$w=x\\label{eq:old}$$」改为「$$\n\\begin{aligned}a&=b\\\\c&=d\\end{aligned}\\label{eq:new}\n$$」');
  await dialog.getByRole('button',{name:'提交'}).click();
  assert.equal(await dialog.count(),0,'完全访问直接保存');
  assert.equal(await page.locator('#source-math .equation .katex').count(),1);
  const sources=()=>page.evaluate(()=>current().turns.map(t=>window.turnRevision.snapshot(t)));
  assert.match((await sources())[0],/\\eqref\{eq:new\}/);
  assert.match((await sources())[1],/\\eqref\{1:eq:new\}/);
  assert.deepEqual(await page.evaluate(()=>current().turns.map(t=>t.readingRevisions?.length??0)),[1,1,0]);
  await page.locator('[data-revise="uses-math"]').click();
  await page.keyboard.press('Escape');
  assert.match(await page.locator('#uses-math .revision-editor .equation-ref').textContent(),/1:1/);
  await save();
  await page.locator('#permission').selectOption('执行前询问');
  const askToReplace=async()=>{
    await startAi();
    await dialog.locator('textarea').fill('把「先计算。」改为「先计算 $w_1$，见公式 \\eqref{eq:new}。」');
    await dialog.getByRole('button',{name:'提交'}).click();
  };
  await askToReplace();await dialog.getByRole('button',{name:'拒绝'}).click();
  assert.doesNotMatch((await sources())[0],/\$w_1\$/);
  await askToReplace();await dialog.getByRole('button',{name:'同意修改'}).click();
  assert.equal(await page.locator('#source-math .math[data-display=false] .katex').count(),1);
  await page.reload();
  assert.match((await sources())[0],/\$w_1\$/);
  assert.deepEqual(await page.evaluate(()=>current().turns.map(t=>t.readingRevisions?.length??0)),[2,1,0]);
  await page.locator('#permission').selectOption('完全访问');
  await startAi();
  await dialog.locator('textarea').fill('把「\\label{eq:new}」改为「\\tag{17}\\label{eq:new}」');
  await dialog.getByRole('button',{name:'提交'}).click();
  const cross=page.locator('#uses-math .equation-ref');
  assert.match(await cross.textContent(),/1:17/);
  await cross.click();
  assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),/\\tag\{17\}/);
  assert.equal(await page.locator('#equation-preview .katex').count(),1);
  assert.deepEqual(await page.evaluate(()=>current().turns.map(t=>t.readingRevisions?.length??0)),[3,1,0],
    '重排后仅刷新可见跨轮编号和预览，不必写入引用轮');
  await page.evaluate(()=>{
    newConversation();
    state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];
    current().turns=[
      {id:'original',question:'原题',answer:'原句。',segments:[]},
      {id:'discussion',question:'第 1 轮那句可以怎样改？',answer:'可以把「原句」改为「简明的 $x$」',segments:[]}
    ];
    current().permission='完全访问';renderConversation();persist();
  });
  const command=async text=>{await page.locator('#composer').fill(text);await page.locator('#send').click();};
  await command('你倒是写过去啊');
  assert.equal(await page.locator('#original .math .katex').count(),1,await page.evaluate(()=>JSON.stringify({
    status:$('status').textContent,feedback:document.querySelector('.revision-feedback')?.textContent,
    draft:current().draft,questions:current().turns.map(t=>t.question),answer:window.turnRevision.snapshot(current().turns[0])})));
  assert.equal((await sources())[0],'简明的 $x$。');
  assert.equal(await page.locator('#composer').inputValue(),'');
  assert.equal(await page.evaluate(()=>current().turns.at(-1).question),'你倒是写过去啊');
  await page.locator('#permission').selectOption('执行前询问');
  await command('请改写第 1 轮，把「简明」改为「准确」');
  await dialog.getByRole('button',{name:'拒绝'}).click();
  assert.equal((await sources())[0],'简明的 $x$。');
  await command('请改写第 1 轮，把「简明」改为「准确」');
  await dialog.getByRole('button',{name:'同意修改'}).click();
  await page.reload();
  assert.equal((await sources())[0],'准确的 $x$。');
  await page.locator('#permission').selectOption('只读');
  await command('请改写第 1 轮，把「准确」改为「精确」');
  assert.equal(await dialog.count(),0);
  assert.equal((await sources())[0],'准确的 $x$。');
  assert.deepEqual(errors,[]);
  console.log('PASS 原位整理、标题编号、跨段合并、撤销、受保护的公式表格和多个引用、最新稿回源、草稿与历史恢复、跨轮模拟 AI 改稿');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

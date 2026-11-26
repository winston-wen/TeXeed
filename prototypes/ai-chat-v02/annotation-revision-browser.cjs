'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');

module.exports=async function checkAnnotationRevision(page){
 const dialog=page.getByRole('dialog',{name:'AI 改稿 · 原型模拟'});
 const old='d=b-a',label='eq:gap',note='改公式6，加入我设立的 $\\delta_0$。检查一下前后文，自动修改不一致的符号。';
 async function setup(permission='完全访问'){
   await page.evaluate(({permission,old,label})=>{
     const c=current();c.entries=[];c.draft='';c.permission=permission;
     state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];state.reviewAutoSeen={};
     c.turns=Array.from({length:15},(_,i)=>({id:'ann-'+(i+1),question:'第 '+(i+1)+' 轮问题',answer:'第 '+(i+1)+' 轮说明。',segments:[]}));
     const equations=Array.from({length:5},(_,i)=>'$$a_'+i+'='+i+'\\label{eq:k'+i+'}$$');
     equations.push('$$'+old+'\\label{'+label+'}$$');
     c.turns[8].answer='先写出定义。\n\n'+equations.join('\n\n')+'\n\n继续计算。';
     c.turns[9].answer='$$'+old+'\\label{'+label+'}$$';
     c.turns[10].answer='根据公式 \\eqref{9:eq-6} 计算 $d$。';
     renderConversation();persist();
   },{permission,old,label});
   await page.waitForFunction(()=>!restoring);
 }
 async function attach(target,instruction){
   await target.evaluate(el=>el.scrollIntoView({block:'center'}));
   if(await target.evaluate(el=>el.tagName==='P')){
     const point=await target.evaluate(el=>{
       const range=document.createRange();range.selectNodeContents(el);const sel=getSelection();
       sel.removeAllRanges();sel.addRange(range);const r=range.getBoundingClientRect(),p=el.getBoundingClientRect();
       return {x:r.x+r.width/2-p.x,y:r.y+r.height/2-p.y};
     });
     await target.click({position:point,button:'right'});
   }else await target.click({button:'right'});
   await page.locator('#annotate').click();
   await page.locator('#annotation-input').fill(instruction);
   await page.locator('#send-annotation').click();
 }
 const source=page.locator('#ann-9 .equation .math').nth(5);
 const snapshot=()=>page.evaluate(()=>current().turns.map(t=>window.turnRevision.snapshot(t)));
 const histories=()=>page.evaluate(()=>current().turns.slice(0,15).map(t=>t.readingRevisions?.length||0));
 await setup();
 await attach(source,note);
 assert.equal(await page.locator('#composer').inputValue(),'');
 await page.locator('#send').click();
 await dialog.waitFor();assert.match(await dialog.locator('label').innerText(),/这道公式/);
 assert.equal(await page.evaluate(()=>current().turns.length),16);
 await dialog.locator('textarea').fill('d=b-a+\\delta_0');await dialog.getByRole('button',{name:'提交'}).click();
 await dialog.waitFor({state:'hidden'});const all=await snapshot();
 assert.match(all[8],/d=b-a\+\\delta_0\\label\{eq:gap\}/);
 assert.doesNotMatch(all[9],/delta/);
 assert.match(all[15],/第 9 轮.*\\eqref\{9:eq:gap\}/);
 assert.match(all[15],/第 11 轮.*符号也需核对/);
 assert.doesNotMatch(all[15],/b-a|delta/);
 assert.equal((await histories()).filter(Boolean).length,1);
 const receipt=page.locator('#thread article.turn').last();
 assert.equal(await receipt.locator('.source-updated').innerText(),'原文已更新');
 assert.match(await receipt.locator('.citation-comment').innerText(),/加入我设立的/);
 await receipt.locator('.equation-ref').click();
 assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),/delta_0/);
 await page.keyboard.press('Escape');
 await page.reload();
 assert.equal((await receipt.locator('.sent-summary .katex-mathml annotation').textContent()).trim(),old);
 await receipt.getByRole('button',{name:'原文已更新'}).click();
 const history=page.getByRole('dialog',{name:'本轮版本历史'});
 assert.equal(await history.locator('select').inputValue(),'0');
 assert.equal(await history.locator('.citation-history-target .math').getAttribute('data-tex'),old+'\\label{'+label+'}');
 if(process.env.TEXEED_REVISION_ARTIFACTS){
   fs.mkdirSync(process.env.TEXEED_REVISION_ARTIFACTS,{recursive:true});
   await history.screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'annotated-history.png')});
 }
 await history.getByRole('button',{name:'关闭'}).click();
 await receipt.locator('.sent-summary').click();
 assert.match(await page.locator('#ann-9 .equation-target .math').getAttribute('data-tex'),/delta_0/);
 if(process.env.TEXEED_REVISION_ARTIFACTS){
   const dir=process.env.TEXEED_REVISION_ARTIFACTS;fs.mkdirSync(dir,{recursive:true});
   await receipt.scrollIntoViewIfNeeded();await receipt.screenshot({path:path.join(dir,'annotated-revision.png')});
 }
 await setup('执行前询问');await attach(source,'请把「d=b-a」改为「d=b+a」');await page.locator('#send').click();
 assert.match(await dialog.locator('.revision-feedback').innerText(),/允许 AI 修改第 9 轮/);
 assert.deepEqual((await histories()).filter(Boolean),[]);
 await dialog.getByRole('button',{name:'拒绝'}).click();
 assert.match(await receipt.locator('.answer').innerText(),/拒绝/);assert.equal(await receipt.locator('.source-updated').count(),0);
 await setup('只读');await attach(source,'请把「d=b-a」改为「d=b+a」');await page.locator('#send').click();
 assert.match(await receipt.locator('.answer').innerText(),/只读/);assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'修改公式6，把「d=b-a」改为「d=b-a+x」');await page.locator('#send').click();
 assert.match((await snapshot())[8],/d=b-a\+x/);
 for(const number of ['7','（1:8）','(1:6)','（9：7）','（6：8）']){
   await setup();await attach(source,'改公式'+number+'。');await page.locator('#send').click();
   assert.match(await receipt.locator('.answer').innerText(),/编号不同/);assert.deepEqual((await histories()).filter(Boolean),[]);
 }
 await setup();await attach(source,'改公式（９：６），把「d=b-a」改为「d=b-a+x」');await page.locator('#send').click();
 assert.match((await snapshot())[8],/d=b-a\+x/);
 await setup();await attach(source,'改公式6，拆成两条。');await page.locator('#send').click();
 await dialog.locator('textarea').fill('$$d=b$$\n\n$$d=a$$');await dialog.getByRole('button',{name:'提交'}).click();
 assert.match(await dialog.locator('.revision-feedback').innerText(),/哪道公式承接/);assert.deepEqual((await histories()).filter(Boolean),[]);
 await dialog.locator('textarea').fill('$$d=b$$\n\n$$d=a\\label{eq:gap}$$');await dialog.getByRole('button',{name:'提交'}).click();
 await dialog.waitFor({state:'hidden'});
 const split=await snapshot();
 assert.match(split[10],/\\eqref\{9:eq:gap\}/);
 assert.match(split[15],/第 9、11 轮/);
 assert.equal(await page.locator('#ann-11 .equation-ref').innerText(),'（9:7）');
 await page.locator('#ann-11 .equation-ref').click();
 assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),/^d=a/);
 await page.keyboard.press('Escape');
 await setup();
 await attach(source,'请把「d=b-a」改为「d=b+a」');
 await attach(page.locator('#ann-9 .equation .math').first(),'把「a_0=0」改为「a_0=1」');
 await attach(page.locator('#ann-10 .equation .math'),'');
 await page.locator('#send').click();
 assert.equal((await histories())[8],2);
 assert.equal((await histories())[9],0);
 assert.equal(await receipt.locator('.equation-ref').count(),2);
 assert.equal(await receipt.locator('.source-updated').count(),2);
 await setup();await attach(source,'解释这一步，别修改。');await page.locator('#send').click();
 await page.locator('#stop').click();assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'请修改第10轮的公式。');await page.locator('#send').click();
 assert.match(await receipt.locator('.answer').innerText(),/所选的轮次和指令不同/);assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'请把「d=b-a」改为「d=b+a」');
 await attach(page.locator('#ann-10 .equation .math'),'请把「d=b-a」改为「d=b+1」');
 await page.locator('#send').click();
 assert.equal(await page.evaluate(()=>current().turns.length),15);
 assert.match(await page.locator('#status').innerText(),/多个改稿目标/);
 assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'请把「d=b-a」改为「d=b+a」');
 await page.locator('#composer').fill('这次先别改，看看批注就好。');await page.locator('#send').click();
 await page.locator('#stop').click();assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'请把「d=b-a」改为「d=b+a」');
 await page.locator('#composer').fill('请先审阅，不要修改。');await page.locator('#send').click();
 await page.locator('#stop').click();assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(source,'公式6无需修改。');await attach(source,'这段不用改。');
 await attach(source,'这公式改得不错。');await attach(source,'这段加了符号以后更清楚。');
 await page.locator('#send').click();await page.locator('#stop').click();
 assert.deepEqual((await histories()).filter(Boolean),[]);
 await setup();await attach(page.locator('#ann-9-p0 p'),'把「写出定义」改为「给出定义」');
 await page.locator('#send').click();
 assert.match((await snapshot())[8],/先给出定义/);assert.equal(await receipt.locator('.source-updated').count(),1);
 await receipt.locator('.sent-summary').click();
 assert.equal(await page.locator('#ann-9-p0').evaluate(el=>{
   const r=el.getBoundingClientRect(),v=$('reader').getBoundingClientRect();return r.top>=v.top&&r.bottom<=v.bottom;
 }),true);
 console.log('PASS: annotation-bound writeback, same-number isolation, permissions, historic quote, current equation, cross-turn receipt and ambiguous split');
};

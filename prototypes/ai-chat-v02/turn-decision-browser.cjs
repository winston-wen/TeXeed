'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');

module.exports=async function checkTurnDecision(page){
  await page.evaluate(()=>{
    state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];state.reviewAutoSeen={};
    const c=current();c.turns=[];c.entries=[];c.draft='';renderConversation();persist();
  });
  await page.keyboard.press('Control+Alt+t');
  await page.locator('#playback-mode').selectOption('decision');
  await page.locator('#close-lab').click();
  await page.locator('#composer').fill('解释你将怎样计算正方形的周长。');await page.locator('#send').click();
  const turn=page.locator('#thread article.turn').first(),form=turn.locator('.decision-question');
  const confirm=form.getByRole('button',{name:'确认并继续'});
  const lamp=turn.locator('.answer-time .thinking-dot');
  await lamp.waitFor();
  assert.equal(await lamp.evaluate(el=>getComputedStyle(el).animationName),'thinking-breathe');
  await form.waitFor();assert(await confirm.isDisabled());
  assert.equal(await turn.locator('.answer-time').innerText(),'等待你确认答案');
  assert.equal(await lamp.count(),0);
  const id=await turn.getAttribute('id');
  await form.getByLabel('一步步推导').check();await form.getByLabel('先看例子').check();
  assert.equal(await page.evaluate(()=>current().turns[0].decisions),undefined);
  await page.locator('#composer').fill('这是另一个尚未发送的问题');
  await page.reload();
  assert(await form.getByLabel('先看例子').isChecked());assert(await confirm.isEnabled());
  assert.equal(await page.locator('#composer').inputValue(),'这是另一个尚未发送的问题');
  if(process.env.TEXEED_REVISION_ARTIFACTS){
    fs.mkdirSync(process.env.TEXEED_REVISION_ARTIFACTS,{recursive:true});
    await turn.screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'decision-wait.png')});
  }
  const questionId=await form.getAttribute('data-decision');
  await page.locator('#send').click();
  await page.waitForFunction(()=>current().turns[1]?.answeredAt);
  assert.equal(await form.getAttribute('data-decision'),questionId);
  assert(await form.getByLabel('先看例子').isChecked());
  await page.locator('#composer').fill('保留第二份主草稿');
  const ids=await page.evaluate(()=>current().turns.map(t=>t.id));
  await confirm.dblclick();await lamp.waitFor();
  assert.equal(await turn.locator('[role=status]').innerText(),'AI 正在继续本轮');
  const light=await lamp.evaluate(el=>{
    const rect=el.getBoundingClientRect(),style=getComputedStyle(el);
    return {width:rect.width,height:rect.height,font:parseFloat(style.fontSize),animation:style.animationName};
  });
  assert.deepEqual(light,{width:13,height:13,font:13,animation:'thinking-breathe'});
  if(process.env.TEXEED_REVISION_ARTIFACTS)await page.locator('#reader').screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'decision-resuming.png')});
  await page.emulateMedia({reducedMotion:'reduce'});await page.reload();await lamp.waitFor();
  const blink=await lamp.evaluate(el=>{
    const animation=el.getAnimations()[0];animation.pause();
    return [0,499,500,999,1000].map(time=>{
      animation.currentTime=time;
      const style=getComputedStyle(el);
      return [style.backgroundColor,style.borderTopColor,style.opacity];
    });
  });
  const solid=['rgb(168, 188, 156)','rgb(168, 188, 156)','1'],hollow=['rgba(0, 0, 0, 0)',solid[0],'1'];
  assert.deepEqual(blink,[solid,solid,hollow,hollow,solid]);
  await page.evaluate(()=>renderTurns());assert.equal(await lamp.count(),1);
  await lamp.waitFor({state:'detached'});await page.emulateMedia({reducedMotion:'no-preference'});await page.reload();
  assert.equal(ids[0],id);assert.deepEqual(await page.evaluate(()=>current().turns.map(t=>t.id)),ids);
  assert.match(await turn.locator('.answer').innerText(),/周长就是/);
  assert.equal(await turn.locator('.decision-record').count(),1);
  assert.match(await turn.locator('.decision-record').innerText(),/你的回答　先看例子/);
  assert.equal(await page.locator('#composer').inputValue(),'保留第二份主草稿');
  assert.equal(await lamp.count(),0);

  assert.equal(await page.locator('#thread article.turn').nth(1).locator('.decision-question').count(),0);
  await page.evaluate(()=>{
    const t=current().turns[0];
    turnDecision.wait(t,{id:'continue-then-stop',question:'还要看一个例子吗？',options:['继续'],draft:''});
    turnDecision.showWait(t);
  });
  await form.getByLabel('继续').check();await confirm.click();
  const [owner,other]=await page.evaluate(()=>[current().id,state.conversations.find(c=>c.id!==current().id).id]);
  await page.evaluate(id=>openConversation(id),other);
  assert.equal(await page.locator('#thread .thinking-dot').count(),0);
  await page.evaluate(id=>openConversation(id),owner);await lamp.waitFor();
  await page.locator('#stop').click();await page.reload();
  assert.equal(await lamp.count(),0);
  assert.match(await turn.locator('.answer-time').innerText(),/用户中止/);
  assert.equal(await turn.locator('.decision-record').count(),2);
  assert.match(await turn.locator('.decision-record').last().innerText(),/你的回答　继续/);
  console.log('PASS: blocking direction question, explicit confirmation, persistent choices, same-turn continuation and independent draft');
};

// Compare the same composer command through the published 0.6.11 prototype and
// the current one. Both eventually ask for the original text to replace.
if(require.main===module)(async()=>{
  const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
  const {pathToFileURL}=require('node:url');
  const [oldIndex,output]=process.argv.slice(2);
  const browser=await chromium.launch({headless:true,executablePath:process.env.TEXEED_BROWSER_PATH});
  async function measure(index){
    const page=await browser.newPage({viewport:{width:1250,height:900}});
    const session=await page.context().newCDPSession(page),samples=[];
    const url=pathToFileURL(path.resolve(index)).href+'?test=1';
    try{
      await page.goto(url);
      for(let i=0;i<9;i++){
        await page.evaluate(()=>{
          for(const d of document.querySelectorAll('dialog[open]'))d.close();
          state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];state.reviewAutoSeen={};
          const c=current();c.permission='完全访问';c.entries=[];c.draft='改写第 1 轮，让计算过程更清楚。';
          c.turns=[{id:'example',question:'周长怎样计算？',answer:'周长等于四边长度之和。',segments:[]}];
          renderConversation();persist();
        });
        await page.waitForFunction(()=>!restoring);
        await session.send('HeapProfiler.collectGarbage');
        await session.send('HeapProfiler.startSampling',{samplingInterval:4096,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
        const sample=await page.evaluate(async()=>{
          const start=performance.now();$('send').click();
          const writeMs=performance.now()-start;
          await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
          const settledMs=performance.now()-start;
          const question=document.querySelector('.decision-question,dialog.revision-dialog[open]');
          if(!question?.innerText.includes('原句')||current().turns.length!==2)throw Error('未出现预期的改稿澄清题');
          const visibleControls=[...question.querySelectorAll('input,button,textarea,select')].filter(el=>el.getClientRects().length).length;
          return {writeMs,settledMs,visibleControls};
        });
        const {profile}=await session.send('HeapProfiler.stopSampling');
        if(i>=2)samples.push({...sample,sampledBytes:profile.samples.reduce((sum,x)=>sum+x.size,0)});
      }
    }finally{await page.close();}
    return samples;
  }
  try{
    const report={baseline:'a3e798d',chromium:browser.version(),viewport:[1250,900],
      scenario:'Send a revision command for turn 1, wait for the clarification about the original text, 2 warm-ups and 7 samples per build, include 2 animation frames.',
      old:await measure(oldIndex),current:await measure(path.join(__dirname,'index.html'))};
    fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

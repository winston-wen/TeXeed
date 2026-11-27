'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path');
const {pathToFileURL}=require('node:url');

const oldTable='| 问题 | 要求攻击者输出什么 |\n| :--- | :--- |\n| 搜索型 | 原列甲 |\n| 判定型 | 原列乙 |';
const newTable=String.raw`| 问题 | 攻击者求解什么 |
| :--- | :--- |
| 搜索型 | 秘密向量 $\boldsymbol{s}$ |
| 判定型 | 表示向量 $\boldsymbol{b}$ 来自真实计算还是随机生成 |`;
const intro='两种问题的求解目标不同。';
async function setup(page,permission='完全访问',table=oldTable){
  await page.evaluate(({permission,table,intro})=>{
    const c=current();
    c.turns=[{id:'table-turn',question:'怎样区分两类问题？',segments:[],
      answer:'# 两类问题\n\n'+intro+'\n\n'+table+'\n\n公式如下。\n\n$$b=As+e\\label{eq:sample}$$'},
      {id:'adjacent',question:'下一轮问题',answer:'这一轮原封保留。',segments:[]}];
    c.entries=[];c.permission=permission;c.draft='未发送的草稿';
    state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];state.reviewAutoSeen={};
    renderConversation();persist();
  },{permission,table,intro});
  await page.waitForFunction(()=>!restoring);
}
async function openWriter(page){
  await page.locator('#table-turn table th').first().click({button:'right'});
  await page.locator('#revision-ai').click();
  return page.getByRole('dialog',{name:'AI 改稿 · 原型模拟'});
}
async function submit(dialog,text){
  await dialog.locator('textarea').fill(text);
  await dialog.getByRole('button',{name:'提交'}).click();
}
const snapshot=page=>page.evaluate(()=>window.turnRevision.snapshot(current().turns[0]));
const getRevisions=page=>page.evaluate(()=>current().turns[0].readingRevisions||[]);
module.exports=async function checkTableRevision(page){
  await setup(page);
  let dialog=await openWriter(page);
  await submit(dialog,'把「'+oldTable+'」改为「'+newTable+'」');await dialog.waitFor({state:'hidden'});
  const body=page.locator('#table-turn'),table=body.locator('table');
  assert.deepEqual(await table.locator('thead th').allTextContents(),['问题','攻击者求解什么']);
  assert.deepEqual(await table.locator('.math').evaluateAll(nodes=>nodes.map(n=>n.dataset.tex)),['\\boldsymbol{s}','\\boldsymbol{b}']);
  assert.equal(await body.locator('table').count(),1);
  assert((await snapshot(page)).includes(newTable));assert(!(await snapshot(page)).includes('要求攻击者'));
  assert.deepEqual((await getRevisions(page)).map(r=>r.author),['AI']);
  assert.equal(await page.locator('#composer').inputValue(),'未发送的草稿');
  await page.reload();
  assert((await snapshot(page)).includes(newTable));assert.equal(await table.locator('td').count(),4);
  if(process.env.TEXEED_REVISION_ARTIFACTS){
    fs.mkdirSync(process.env.TEXEED_REVISION_ARTIFACTS,{recursive:true});
    await body.screenshot({path:path.join(process.env.TEXEED_REVISION_ARTIFACTS,'ai-table.png')});
  }
  await table.locator('td').first().click({button:'right'});await page.locator('#revision-history').click();
  const history=page.getByRole('dialog',{name:'本轮版本历史'});
  await history.locator('select').selectOption('0');assert((await history.locator('th').last().textContent()).includes('要求攻击者'));
  await history.getByRole('button',{name:'关闭'}).click();
  await page.locator('[data-revise="table-turn"]').click();
  assert.equal(await body.locator('.revision-locked table').count(),1);assert.equal(await body.locator('table [contenteditable=true]').count(),0);
  await page.keyboard.press('Control+s');await page.reload();
  dialog=await openWriter(page);
  await submit(dialog,'把「秘密向量」改为「私有向量」');await dialog.waitFor({state:'hidden'});
  assert((await snapshot(page)).includes('私有向量'));
  const expanded=newTable.replace('秘密向量','私有向量')+'\n| 其他 | 已知输入 |';
  dialog=await openWriter(page);await submit(dialog,'把「'+newTable.replace('秘密向量','私有向量')+'」改为「'+expanded+'」');
  await dialog.waitFor({state:'hidden'});assert.equal(await table.locator('tbody tr').count(),3);
  const additional=expanded.split('\n').map((line,i)=>line+(i===0?' 备注 |':i===1?' --- |':' 甲\\|乙 |')).join('\n');
  dialog=await openWriter(page);await submit(dialog,'把「'+expanded+'」改为「'+additional+'」');
  await dialog.waitFor({state:'hidden'});assert.equal(await table.locator('th').count(),3);assert.equal(await table.locator('td').last().innerText(),'甲|乙');
  dialog=await openWriter(page);await submit(dialog,'把「'+additional+'」改为「」');await dialog.waitFor({state:'hidden'});
  assert.equal(await body.locator('table').count(),0);assert.equal(await body.locator('.equation').count(),1);
  await body.locator('.answer .passage p').first().click({button:'right'});await page.locator('#revision-ai').click();
  dialog=page.getByRole('dialog',{name:'AI 改稿 · 原型模拟'});
  await submit(dialog,'把「'+intro+'」改为「'+newTable+'」');await dialog.waitFor({state:'hidden'});
  assert.equal(await table.locator('td').count(),4);
  await setup(page,'执行前询问');
  dialog=await openWriter(page);await submit(dialog,'把「要求攻击者输出什么」改为「攻击者求解什么」');
  assert.deepEqual(await getRevisions(page),[]);await dialog.getByRole('button',{name:'拒绝'}).click();
  assert((await snapshot(page)).includes(oldTable));
  dialog=await openWriter(page);await submit(dialog,'把「要求攻击者输出什么」改为「攻击者求解什么」');
  await dialog.getByRole('button',{name:'同意修改'}).click();assert((await snapshot(page)).includes('攻击者求解什么'));
  await setup(page,'只读');await openWriter(page);assert.deepEqual(await getRevisions(page),[]);assert.match(await page.locator('#status').innerText(),/只读/);
  await setup(page,'完全访问',oldTable.replace('原列甲','{{text:answer}}'));
  dialog=await openWriter(page);await submit(dialog,'把「{{text:answer}}」改为「其他」');
  assert.match(await dialog.locator('.revision-feedback').innerText(),/表单/);assert.deepEqual(await getRevisions(page),[]);
  await dialog.getByRole('button',{name:'关闭'}).click();
  await setup(page);dialog=await openWriter(page);await submit(dialog,'把「原列甲」改为「见公式 \\eqref{eq:missing}」');
  assert.match(await dialog.locator('.revision-feedback').innerText(),/找不到目标/);
  await submit(dialog,'见公式 \\eqref{eq:sample}');await dialog.waitFor({state:'hidden'});
  await table.locator('.equation-ref').click();assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),/b=As/);
  await page.keyboard.press('Escape');
  await setup(page);
  await table.locator('td').first().click({button:'right'});await page.locator('#annotate').click();
  await page.locator('#annotation-input').fill('把「原列乙」改为「最新判定目标」');
  await page.locator('#send-annotation').click();await page.locator('#composer').fill('');await page.locator('#send').click();
  assert((await snapshot(page)).includes('最新判定目标'));
  const note=page.locator('#thread .turn').last();
  assert.equal(await note.locator('.source-updated').count(),1);
  await note.locator('.source-updated').click();assert.equal(await history.locator('td').last().textContent(),'原列乙');
  await history.getByRole('button',{name:'关闭'}).click();
  await setup(page);
  await page.locator('[data-revise="table-turn"]').click();
  const draft=page.locator('#table-turn [data-id="table-turn-p1"] .words');
  await draft.click();await page.keyboard.insertText('草稿');await page.reload();
  await openWriter(page);assert.match(await page.locator('#status').innerText(),/手动草稿/);
  assert.deepEqual(await getRevisions(page),[]);
  await setup(page);
  dialog=await openWriter(page);await submit(dialog,'把「'+oldTable+'」改为「| 表头 |\n| 没有分隔行 |」');
  assert.match(await dialog.locator('.revision-feedback').innerText(),/管道表格/);assert.deepEqual(await getRevisions(page),[]);
  await dialog.getByRole('button',{name:'关闭'}).click();
  await setup(page);dialog=await openWriter(page);
  await submit(dialog,'把「判定型 | 原列乙」改为「判定型 | 原列乙 | 第三个值」');await dialog.waitFor({state:'hidden'});
  await page.reload();
  assert.equal(await table.locator('th').count(),3,'长数据行需要一个空白的第三列表头');
  assert.deepEqual(await table.locator('tbody tr').first().locator('td').allTextContents(),['搜索型','原列甲','']);
  assert.equal(await table.locator('td').last().innerText(),'第三个值');
  assert((await snapshot(page)).includes('| 判定型 | 原列乙 | 第三个值 |'));
  console.log('PASS: AI table cells, TeX, row structure, reference preview, permissions, protected controls, manual lock, history and persisted draft');
};

async function measure(page,url){
  await page.goto(url);
  const session=await page.context().newCDPSession(page);
  const samples=[];
  for(let i=0;i<9;i++){
    await setup(page);
    await session.send('HeapProfiler.collectGarbage');
    await session.send('HeapProfiler.startSampling',{samplingInterval:4096,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
    const sample=await page.evaluate(async()=>{
      // Both versions can revise this sentence immediately before the two-column table.
      const start=performance.now();
      window.turnRevision.askAi(current().turns[0],'把「求解目标」改为「计算目标」');
      const afterSave=performance.now();
      await new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done)));
      const reader=$('reader').getBoundingClientRect(),controls=[...$('thread').querySelectorAll('button,input,textarea,select')];
      return {writeMs:afterSave-start,settledMs:performance.now()-start,
        visibleControls:controls.filter(el=>{const r=el.getBoundingClientRect();return r.width&&r.height&&r.top<reader.bottom&&r.bottom>reader.top;}).length,
        saved:current().turns[0].readingRevisions?.length,tableCells:document.querySelectorAll('#table-turn td').length};
    });
    const {profile}=await session.send('HeapProfiler.stopSampling');
    assert.equal(sample.saved,1);assert.equal(sample.tableCells,4);
    if(i>1)samples.push({...sample,sampledBytes:profile.samples.reduce((sum,s)=>sum+s.size,0)});
  }
  await session.detach();return samples;
}
if(require.main===module)(async()=>{
  const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
  const browser=await chromium.launch({headless:true,executablePath:process.env.TEXEED_BROWSER_PATH});
  try{
    const page=await browser.newPage({viewport:{width:1250,height:900}});
    page.setDefaultTimeout(5500);
    const url=pathToFileURL(path.join(__dirname,'index.html')).href+'?test=1';
    await page.goto(url);
    await module.exports(page);
    if(process.env.TEXEED_TABLE_REVISION_BASELINE){
      const baseline=pathToFileURL(path.resolve(process.env.TEXEED_TABLE_REVISION_BASELINE)).href+'?test=1';
      const file=process.env.TEXEED_TABLE_REVISION_SAMPLES;
      const data={chromium:browser.version(),viewport:[1250,900],baseline:'f2a2584',
        scenario:'AI revision of the paragraph immediately before a pipe table, two completed turns, two warm-ups, seven samples per build, two animation frames',
        old:await measure(page,baseline),current:await measure(page,url)};
      fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify(data,null,2)+'\n');
    }
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

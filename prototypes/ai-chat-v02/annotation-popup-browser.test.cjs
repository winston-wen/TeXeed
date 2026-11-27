'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP annotation popup browser: Playwright unavailable'); process.exit(0); }

async function checkOpeningFocus(page) {
  await page.evaluate(() => {
    const session=current();
    session.turns=[{id:'focus-check',question:'如何记录原文？',sections:{},
      answer:Array.from({length:45},(_,i)=>'第'+(i+1)+'段，选择这几个字，然后继续阅读。').join('\n\n')}];
    session.entries=[];
    session.draft='继续写主草稿';
    renderConversation();
  });
  const paragraph=page.locator('#focus-check-p26 p');
  await paragraph.evaluate(p=>p.scrollIntoView({block:'center'}));
  const anchorY=()=>paragraph.evaluate(p=>p.getBoundingClientRect().top);
  const top=await anchorY();
  const points=await paragraph.evaluate(p=>{
    const text=p.firstChild,value='选择这几个字',start=text.data.indexOf(value),end=start+value.length;
    const range=document.createRange();range.setStart(text,start);range.setEnd(text,end);
    const box=range.getBoundingClientRect();
    return {left:box.left+.25,right:box.right-.25,y:(box.top+box.bottom)/2};
  });
  await page.mouse.move(points.left,points.y);
  await page.mouse.down();await page.mouse.move(points.right,points.y,{steps:6});await page.mouse.up();
  assert.equal(await page.evaluate(()=>getSelection().toString()),'选择这几个字');
  await page.mouse.click((points.left+points.right)/2,points.y,{button:'right'});
  await page.locator('#annotate').click();
  const field=page.locator('#annotation-input');
  const caret=()=>field.evaluate(el=>[document.activeElement===el,el.selectionStart,el.selectionEnd]);
  assert.deepEqual(await caret(),[true,0,0]);
  await page.keyboard.insertText('第一条批注');
  assert.equal(await field.inputValue(),'第一条批注');
  assert.equal(await page.locator('#composer').inputValue(),'继续写主草稿');
  assert(Math.abs(await anchorY()-top)<1,'Opening the annotation moved the source paragraph');
  const quote=()=>page.evaluate(()=>{const e=current().entries[0];return [e.block,e.quote,e.question]});
  await page.keyboard.press('Control+Enter');
  assert(await page.locator('#annotation-editor').isHidden());
  assert.deepEqual(await quote(),['focus-check-p26','选择这几个字','第一条批注']);
  const bubble=paragraph.locator('xpath=ancestor::section[1]').locator('[data-bubble]');
  assert(await bubble.evaluate(el=>document.activeElement===el));
  await bubble.click();
  assert.deepEqual(await caret(),[true,'第一条批注'.length,'第一条批注'.length]);
  await page.keyboard.insertText('，补充说明');
  assert.equal(await field.inputValue(),'第一条批注，补充说明');
  if(process.env.TEXEED_ANNOTATION_FOCUS_SCREENSHOT)await page.screenshot({path:process.env.TEXEED_ANNOTATION_FOCUS_SCREENSHOT,caret:'initial'});
  await page.locator('#send-annotation').click();
  assert.deepEqual(await quote(),['focus-check-p26','选择这几个字','第一条批注，补充说明']);
  assert(await bubble.evaluate(el=>document.activeElement===el));
  assert.equal(await page.locator('#composer').inputValue(),'继续写主草稿');
  assert(Math.abs(await anchorY()-top)<1,'Completing the annotation moved the source paragraph');
}

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 820 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.waitForSelector('.answer .passage');
    await page.waitForTimeout(350);
    const create = async id => page.evaluate(id => {
      const block = document.querySelector('.answer .passage').id;
      current().draft = '保留主草稿'; $('composer').value = current().draft;
      current().entries.push({ id, number: current().next++, title: 'AI 回答批注', question: '尚未发送', selected: false, state: '待提交', sources: [], pdf: [], block, quote: '选中的文字' });
      renderEntries(); openEditor(id);
    }, id);
    await create('drag');
    const popup=page.locator('#annotation-editor'),header=page.locator('.annotation-header .popup-title');
    const original=await popup.boundingBox(),handle=await header.boundingBox();
    await page.mouse.move(handle.x+20,handle.y+handle.height/2);await page.mouse.down();await page.mouse.move(35,150,{steps:8});await page.mouse.up();
    const moved=await popup.boundingBox();assert.ok(Math.abs(moved.x-original.x)>30,'拖动标题栏应移动浮窗');
    if(process.env.TEXEED_DRAG_SCREENSHOT)await page.screenshot({path:process.env.TEXEED_DRAG_SCREENSHOT});
    await page.locator('#annotation-input').fill('拖动后保留的评论');assert.equal(await page.locator('#composer').inputValue(),'保留主草稿');
    await page.setViewportSize({width:420,height:620});await page.waitForTimeout(100);assert.equal(await popup.isVisible(),true,'窗口缩放不应关闭拖动中的浮窗');
    const bounded=await popup.boundingBox();assert.ok(bounded.x>=0&&bounded.y>=0&&bounded.x+bounded.width<=421&&bounded.y+bounded.height<=621,'缩放后浮窗应夹紧到窗口内');
    await page.setViewportSize({width:1000,height:820});await page.waitForTimeout(100);await page.locator('#send-annotation').click();
    assert.equal(await page.evaluate(()=>current().entries.find(e=>e.id==='drag').question),'拖动后保留的评论');assert.equal(await page.locator('#composer').inputValue(),'保留主草稿');
    await page.evaluate(()=>openEditor('drag'));const reopened=await popup.boundingBox();assert.ok(Math.abs(reopened.x-original.x)<2&&Math.abs(reopened.y-original.y)<2,'重开后按来源位置显示');
    await page.locator('#send-annotation').click();
    await create('blank');await page.locator('#annotation-input').fill('  ');
    assert.equal(await page.locator('#delete-annotation').getAttribute('title'),'删除批注');await page.locator('#delete-annotation').click();
    assert.equal(await page.locator('#annotation-delete-hint').innerText(),'再点一下删除');assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='blank')),true);
    await page.waitForTimeout(900);await page.locator('#delete-annotation').click();assert.equal(await page.locator('#annotation-editor').isHidden(),true);assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='blank')),false);assert.equal(await page.locator('#composer').inputValue(),'保留主草稿');
    await create('reopened-blank');await page.locator('#annotation-input').fill('');await page.locator('#send-annotation').click();await page.evaluate(()=>openEditor('reopened-blank'));await page.locator('#delete-annotation').click();
    await page.mouse.move(2,2);assert.equal(await page.locator('#annotation-delete-hint').isHidden(),true,'离开浮窗取消待确认');await page.locator('#delete-annotation').click();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='reopened-blank')),true);assert.equal(await page.locator('#annotation-delete-hint').isVisible(),true);await page.locator('#delete-annotation').click();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='reopened-blank')),false);
    await create('image');await page.evaluate(()=>{current().entries.find(e=>e.id==='image').images=['图片内容'];});await page.locator('#annotation-input').fill('');await page.locator('#delete-annotation').click();
    assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='image')),true,'有图片的批注不能单击删除');await page.waitForTimeout(900);await page.locator('#delete-annotation').click();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='image')),false);
    await create('protected');await page.evaluate(()=>{current().entries.find(e=>e.id==='protected').sourceLocked=true;openEditor('protected')});await page.locator('#annotation-input').fill('');assert.equal(await page.locator('#delete-annotation').isDisabled(),true,'受保护来源不能删除');await page.locator('#send-annotation').click();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='protected')),true);
    await page.evaluate(()=>{const c=current();c.entries.push({id:'non-answer',number:c.next++,title:'PDF 圈点',question:'',selected:false,state:'待提交',sources:[],pdf:[{page:1}],block:null});renderEntries();openEditor('non-answer')});
    await page.locator('#delete-annotation').click();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='non-answer')),true,'非回答批注保留双击规则');await page.locator('#delete-annotation').dblclick();assert.equal(await page.evaluate(()=>current().entries.some(e=>e.id==='non-answer')),false);
    await create('single-double');
    assert.equal(await page.locator('#send-annotation').getAttribute('title'), '保存并关闭浮窗\nCtrl+Enter');
    assert.equal(await page.locator('#send-annotation').innerText(), '完成');
    assert.equal(await page.locator('#delete-annotation').getAttribute('title'), '删除批注');
    await page.locator('#delete-annotation').click();
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'single-double')), true, 'single click must preserve annotation');
    assert.equal(await page.locator('#annotation-delete-hint').innerText(),'再点一下删除');
    assert.equal(await page.locator('#annotation-editor').isVisible(), true);
    if (process.env.TEXEED_ANNOTATION_SCREENSHOT) {
      await page.screenshot({ path: process.env.TEXEED_ANNOTATION_SCREENSHOT });
      await page.setViewportSize({ width: 420, height: 820 });
      await page.waitForTimeout(100);
      await page.evaluate(() => openEditor('single-double'));
      await page.screenshot({ path: process.env.TEXEED_ANNOTATION_SCREENSHOT.replace('.png', '-narrow.png') });
      await page.setViewportSize({ width: 1000, height: 820 });
      await page.waitForTimeout(100);
      await page.evaluate(() => openEditor('single-double'));
    }
    if (await page.locator('#annotation-delete-hint').isHidden()) await page.locator('#delete-annotation').click();
    await page.waitForTimeout(900);await page.locator('#delete-annotation').click();
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'single-double')), false, 'second click must remove annotation even after the double-click interval');
    await create('keyboard');
    await page.locator('#delete-annotation').focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'keyboard')), true, 'single keyboard activation must preserve annotation');
    await page.waitForTimeout(900);await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'keyboard')), false, 'two keyboard activations should remove annotation');
    await create('confirm');
    const turns = await page.evaluate(() => current().turns.length);
    await page.locator('#annotation-input').fill('确认后保留的评论');
    await page.locator('#send-annotation').click();
    assert.equal(await page.locator('#annotation-editor').isHidden(), true);
    assert.equal(await page.evaluate(() => current().entries.find(e => e.id === 'confirm')?.question), '确认后保留的评论');
    assert.equal(await page.evaluate(() => current().entries.find(e => e.id === 'confirm')?.selected), true);
    assert.equal(await page.locator('#composer').inputValue(), '保留主草稿');
    assert.equal(await page.evaluate(() => current().turns.length), turns);
    await page.reload();
    assert.equal(await page.evaluate(() => current().entries.find(e => e.id === 'confirm')?.question), '确认后保留的评论');
    await checkOpeningFocus(page);
    assert.deepEqual(errors, []);
    console.log('PASS popup: drag, bounded resize, two-stage delete, keyboard, protected source, confirmation and draft persistence');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

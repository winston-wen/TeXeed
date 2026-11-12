'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP annotation popup browser: Playwright unavailable'); process.exit(0); }

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 820 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.waitForSelector('.answer .passage');
    const create = async id => page.evaluate(id => {
      const block = document.querySelector('.answer .passage').id;
      current().draft = '保留主草稿'; $('composer').value = current().draft;
      current().entries.push({ id, number: current().next++, title: 'AI 回答批注', question: '尚未发送', selected: false, state: '待提交', sources: [], pdf: [], block, quote: '选中的文字' });
      renderEntries(); openEditor(id);
    }, id);
    await create('single-double');
    assert.equal(await page.locator('#send-annotation').getAttribute('title'), '保存并关闭浮窗\nCtrl+Enter');
    assert.equal(await page.locator('#send-annotation').innerText(), '完成');
    assert.equal(await page.locator('#delete-annotation').getAttribute('title'), '双击删除此条批注');
    await page.locator('#delete-annotation').click();
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'single-double')), true, 'single click must preserve annotation');
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
    await page.waitForTimeout(600);
    await page.locator('#delete-annotation').dblclick();
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'single-double')), false, 'double click must remove annotation');
    await create('keyboard');
    await page.locator('#delete-annotation').focus();
    await page.keyboard.press('Enter');
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'keyboard')), true, 'single keyboard activation must preserve annotation');
    await page.keyboard.press('Enter');
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
    assert.deepEqual(errors, []);
    console.log('PASS popup: exact tooltip, single/double mouse and keyboard, confirmation and draft persistence');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

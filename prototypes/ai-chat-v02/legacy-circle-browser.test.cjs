'use strict';
const assert = require('node:assert/strict'), path = require('node:path'), { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP legacy browser: Playwright unavailable'); process.exit(0); }
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1200, height: 850 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.waitForSelector('.answer .passage'); await pause(100);
    assert.equal(await page.locator('#mark-button, #marking, #reading-context').count(), 0, '没有顶部圈点入口或回答来源浮条');
    const original = await page.evaluate(() => {
      const conversation = current();
      conversation.draft = '保留的主草稿';
      conversation.entries.push({ id: 'qa-existing-entry', number: conversation.next++, title: '已有批注', question: '保留的已有批注', selected: false, state: '待提交', sources: [], pdf: [], quote: '旧来源' });
      const originalTurnIds = conversation.turns.map(turn => turn.id);
      persist(); return { id: conversation.id, turns: originalTurnIds };
    });
    await page.reload(); await pause(150);
    assert.equal(await page.locator('#composer').inputValue(), '保留的主草稿');
    assert.deepEqual(await page.evaluate(() => current().turns.map(turn => turn.id)), original.turns);
    assert.equal(await page.evaluate(() => current().entries.find(e => e.id === 'qa-existing-entry')?.question), '保留的已有批注');
    const target = page.locator('.answer .passage-body p:visible').first();
    await target.scrollIntoViewIfNeeded();
    const box = await target.boundingBox();
    await page.mouse.move(box.x + 4, box.y + 10); await page.mouse.down();
    await page.mouse.move(Math.min(box.x + 90, box.x + box.width - 4), box.y + 10, { steps: 10 }); await page.mouse.up();
    const selection = await page.evaluate(() => getSelection().toString().trim());
    assert.ok(selection.length > 0, '鼠标拖选已有回答');
    const rect = await page.evaluate(() => { const r = getSelection().getRangeAt(0).getBoundingClientRect(); return { x: r.left + Math.min(5, r.width / 2), y: r.top + r.height / 2 }; });
    await page.mouse.click(rect.x, rect.y, { button: 'right' });
    assert.equal(await page.locator('#answer-menu').isVisible(), true, '回答右键菜单保留');
    await page.locator('#annotate').click();
    assert.equal(await page.locator('#annotation-editor').isVisible(), true);
    await page.locator('#annotation-input').fill('新的右键批注');
    await page.locator('#send-annotation').click();
    assert.equal(await page.locator('#composer').inputValue(), '保留的主草稿');
    assert.equal(await page.evaluate(() => current().entries.some(e => e.question === '新的右键批注')), true);
    assert.equal(await page.evaluate(() => current().turns.length), original.turns.length, '批注确认不发送请求');
    await page.reload(); await pause(150);
    assert.equal(await page.locator('#composer').inputValue(), '保留的主草稿');
    assert.equal(await page.evaluate(() => current().entries.some(e => e.question === '新的右键批注')), true);
    assert.equal(await page.evaluate(() => current().entries.some(e => e.id === 'qa-existing-entry')), true);
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/legacy-ai-cleanup.png') });
    await page.setViewportSize({ width: 600, height: 850 }); await pause(100);
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/legacy-ai-cleanup-narrow.png') });
    assert.deepEqual(errors, []);
    console.log('PASS legacy cleanup, real right-click annotation, drafts and history');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

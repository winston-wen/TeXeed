'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP followups browser: Playwright unavailable'); process.exit(0); }

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const context = await browser.newContext({ viewport: { width: 1000, height: 800 } });
    const page = await context.newPage(), errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href);
    await page.waitForFunction(() => !!window.branchDemo);
    await page.evaluate(() => branchDemo.reset());
    assert(await page.locator('#steer').isDisabled());
    await page.locator('#demo-running').click();
    assert(await page.locator('#demo-running-status').isVisible());
    assert(await page.locator('#steer').isEnabled());
    assert(await page.locator('#send').isDisabled());
    if (process.env.TEXEED_FOLLOWUP_SCREENSHOT) {
      await page.locator('#composer').fill('编辑区字体预览 abc 123');
      await page.evaluate(() => { const probe = document.createElement('span'); probe.id = 'font-probe'; probe.textContent = '编辑区字体预览 abc 123'; probe.style.font = getComputedStyle(document.querySelector('#composer')).font; probe.style.position = 'fixed'; probe.style.top = '0'; probe.style.opacity = '.01'; document.body.appendChild(probe); });
      const client = await context.newCDPSession(page); await client.send('DOM.enable'); await client.send('CSS.enable');
      const { root } = await client.send('DOM.getDocument'); const { nodeId } = await client.send('DOM.querySelector', { nodeId: root.nodeId, selector: '#font-probe' });
      console.log('Composer rendered fonts:', JSON.stringify((await client.send('CSS.getPlatformFontsForNode', { nodeId })).fonts)); await client.detach();
      await page.locator('#font-probe').evaluate(el => el.remove());
      await page.screenshot({ path: process.env.TEXEED_FOLLOWUP_SCREENSHOT });
      await page.setViewportSize({ width: 390, height: 800 });
      await page.screenshot({ path: process.env.TEXEED_FOLLOWUP_SCREENSHOT.replace('.png', '-narrow.png') });
      await page.setViewportSize({ width: 1000, height: 800 }); await page.locator('#composer').fill('');
    }
    await page.locator('#composer').fill('稍后再问');
    await page.locator('#composer').press('Control+Enter');
    assert.equal(await page.locator('#composer').inputValue(), '稍后再问');
    await page.locator('#enqueue').click();
    assert.equal(await page.locator('#followup-queue input').inputValue(), '稍后再问');
    await page.locator('#followup-queue input').fill('稍后再问具体条件');
    await page.locator('#followup-queue input').blur();
    await page.locator('#composer').fill('请解释这个符号');
    const turns = await page.locator('.turn').count();
    await page.locator('#steer').click();
    assert.equal(await page.locator('.turn').count(), turns);
    assert(await page.locator('.example-steer').filter({ hasText: '引导：请解释这个符号' }).isVisible());
    await page.locator('#composer').fill('转为引导的消息'); await page.locator('#enqueue').click();
    if (process.env.TEXEED_FOLLOWUP_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_FOLLOWUP_SCREENSHOT.replace('.png', '-queue.png') });
    await page.getByRole('button', { name: '上移排队消息' }).nth(1).click();
    assert.equal(await page.locator('#followup-queue input').first().inputValue(), '转为引导的消息');
    await page.getByRole('button', { name: '转为引导' }).first().click();
    assert(await page.locator('.example-steer').filter({ hasText: '转为引导的消息' }).isVisible());
    await page.locator('#composer').fill('保留主草稿');
    await page.reload();
    assert.equal(await page.locator('#composer').inputValue(), '保留主草稿');
    assert.equal(await page.locator('#followup-queue input').inputValue(), '稍后再问具体条件');
    assert(await page.locator('#followup-queue').getByText('待处理消息 · 已暂停').isVisible());
    await page.getByRole('button', { name: '继续', exact: true }).click();
    assert(await page.locator('.turn-question').filter({ hasText: '稍后再问具体条件' }).isVisible());
    assert.equal(await page.locator('#composer').inputValue(), '保留主草稿');
    await page.locator('#composer').fill('中断后保留'); await page.locator('#enqueue').click();
    await page.locator('#demo-stop').click();
    assert(await page.locator('#followup-queue').getByText('待处理消息 · 已暂停').isVisible());
    assert(await page.locator('#send').isEnabled());
    await page.evaluate(() => {
      const snapshot = branchDemo.getState(), current = snapshot.sessions.find(s => s.id === snapshot.active);
      current.references = [{ id: 'picked', selected: true, quote: '选中引用', comment: '附注', attachments: [] }, { id: 'left', selected: false, quote: '未选引用', attachments: [] }];
      current.attachments = [{ id: 'file', name: 'example.txt', path: 'example.txt', type: 'text/plain', data: 'data:text/plain;base64,YQ==' }];
      branchDemo.setState(snapshot);
    });
    await page.locator('#demo-running').click(); await page.locator('#composer').fill('带材料的问题'); await page.locator('#enqueue').click();
    const snapshot = await page.evaluate(() => branchDemo.getState());
    const queued = snapshot.followups[snapshot.active].at(-1);
    assert.equal(queued.references[0].comment, '附注'); assert.equal(queued.attachments[0].name, 'example.txt');
    assert.deepEqual(snapshot.sessions.find(s => s.id === snapshot.active).references.map(r => r.id), ['left']);
    assert.equal(errors.length, 0, errors.join('\n'));
    console.log('PASS followups browser');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href);
    await page.evaluate(() => {
      const f = branchDemo.getState();
      f.sessions = [f.sessions.find(s => s.id === f.active)];
      const session = f.sessions[0]; session.draft = '保留未发送草稿'; session.references = [];
      session.turns = Array.from({ length: 12 }, (_, i) => ({ id: 'turn-' + i, question: '第 ' + (i + 1) + ' 轮', answer: [
        { id: 'anchor-' + i, kind: 'paragraph', text: '用于检查屏幕位置的正文，第 ' + (i + 1) + ' 轮。' },
        ...(i === 6 ? [{ id: 'form-target', kind: 'exercise', text: '' }] : []),
        { id: 'tail-' + i, kind: 'paragraph', text: '继续阅读的正文。'.repeat(8) }
      ] }));
      branchDemo.setState(f);
      const reader = document.getElementById('reader'), field = document.querySelector('[aria-label="A 的 a"]');
      reader.scrollTop += field.getBoundingClientRect().top - reader.getBoundingClientRect().top - 180;
    });
    const field = page.getByLabel('A 的 a');
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const snapshot = async () => page.evaluate(() => {
      const reader = document.getElementById('reader'), anchor = document.querySelector('[data-block-id="anchor-6"]'), field = document.querySelector('[aria-label="A 的 a"]');
      const range = document.createRange(); range.selectNodeContents(anchor);
      const ink = range.getBoundingClientRect(), bounds = field.getBoundingClientRect(), viewport = reader.getBoundingClientRect();
      return { textTop: ink.top, textBottom: ink.bottom, offset: reader.scrollTop, fieldTop: bounds.top, fieldBottom: bounds.bottom, viewportTop: viewport.top, viewportBottom: viewport.bottom };
    });
    const before = await snapshot();
    assert.ok(before.offset > 0 && before.fieldTop >= before.viewportTop && before.fieldBottom <= before.viewportBottom, '表格字段应在非顶部视口内完整可见');
    const stable = async label => {
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const now = await snapshot();
      assert.ok(Math.abs(now.textTop - before.textTop) < 1.5 && Math.abs(now.textBottom - before.textBottom) < 1.5,
        `${label}: 输入前后正文屏幕位置发生变化 ${JSON.stringify({before, now})}`);
      assert.ok(Math.abs(now.offset - before.offset) < 1.5, `${label}: 阅读视口发生变化 ${JSON.stringify({before, now})}`);
    };
    await field.click(); await stable('鼠标聚焦');
    for (const key of ['1', '4', '7', 'ArrowLeft', 'Backspace', '3']) { await page.keyboard.press(key); await stable('按键 ' + key); }
    assert.equal(await field.inputValue(), '137');
    await page.evaluate(() => document.querySelector('[aria-label="A 的 a"]').blur()); await stable('移出焦点');
    assert.equal(await page.locator('#composer').inputValue(), '保留未发送草稿');
    await page.reload();
    assert.equal(await page.getByLabel('A 的 a').inputValue(), '137');
    assert.equal(await page.locator('#composer').inputValue(), '保留未发送草稿');
    assert.deepEqual(errors, []);
    if (process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT });
    console.log('PASS: table field focus, typing, deletion and blur preserve actual on-screen text position, form value and draft.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

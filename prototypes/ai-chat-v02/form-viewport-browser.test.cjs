'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true, ignoreDefaultArgs: ['--hide-scrollbars'],
    ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
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
    const note = page.locator('.exercise textarea');
    await note.evaluate(el => {
      const reader = document.getElementById('reader');
      reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().top - 95;
      el.focus({ preventScroll: true });
    });
    const notePosition = () => note.evaluate(el => {
      const reader = document.getElementById('reader'), numbers = el.previousElementSibling.firstElementChild;
      return { top: el.getBoundingClientRect().top, outer: reader.scrollTop, inner: el.scrollTop, rows: (el.clientHeight - 6) / 24,
        overflow: el.scrollHeight - el.clientHeight, numberY: numbers.getBoundingClientRect().top - el.getBoundingClientRect().top,
        distance: reader.scrollHeight - reader.clientHeight - reader.scrollTop };
    });
    const noteStart = await notePosition();
    assert.equal(noteStart.rows, 3);
    assert.ok(noteStart.distance > 500, '长答题框应位于会话中段');
    const assertNoteStable = async label => {
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      const now = await notePosition();
      assert.ok(Math.abs(now.top - noteStart.top) < 1.5 && Math.abs(now.outer - noteStart.outer) < 1.5,
        `${label}: 长答题框引起外层跳转 ${JSON.stringify({ noteStart, now })}`);
      return now;
    };
    for (let i = 0; i < 8; i++) {
      await page.keyboard.insertText('第 ' + (i + 1) + ' 行');
      if (i < 7) await page.keyboard.press('Enter');
    }
    let sized = await assertNoteStable('逐行增加');
    assert.equal(sized.rows, 8);
    assert.equal(sized.overflow, 0, '恰好八个显示行时不出现滚动条');
    await page.keyboard.press('Enter');
    await page.keyboard.insertText('第九行');
    sized = await assertNoteStable('第九行');
    assert.equal(sized.rows, 8);
    assert.ok(sized.overflow > 0 && sized.inner > 0 && Math.abs(sized.numberY + sized.inner - 4) < 1, '仅框内滚动，行号同步');
    await page.keyboard.press('Control+Home');
    await assertNoteStable('回到首行');
    const box = await note.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, 18);
    await page.waitForFunction(() => document.querySelector('.exercise textarea').scrollTop > 0);
    sized = await assertNoteStable('框内滚轮');
    assert.ok(Math.abs(sized.numberY + sized.inner - 4) < 1, '滚轮移动后行号仍与文本同向');
    if (process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT });
    await page.keyboard.press('Control+a');
    await page.keyboard.press('Backspace');
    sized = await assertNoteStable('删除内容后缩回三行');
    assert.equal(sized.rows, 3);
    assert.equal(sized.overflow, 0);
    assert.equal(sized.inner, 0);
    assert.equal(await page.locator('#composer').inputValue(), '保留未发送草稿');
    await page.reload();
    assert.equal(await page.getByLabel('A 的 a').inputValue(), '137');
    assert.equal(await page.locator('#composer').inputValue(), '保留未发送草稿');
    await page.setViewportSize({ width: 390, height: 800 });
    const wrappedText = '这段长答也可以换成包含中文和 English 的混排示例。'.repeat(12);
    await note.fill(wrappedText);
    await note.evaluate(el => {
      el.setSelectionRange(0, 0);
      el.scrollTop = 0;
      const reader = document.getElementById('reader');
      reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().top - 75;
    });
    sized = await notePosition();
    assert.equal(sized.rows, 8);
    assert.ok(sized.overflow > 0, '没有换行符的长文在窄窗口也应产生框内滚动');
    if (process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT)
      await page.screenshot({ path: process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT.replace(/\.png$/i, '-narrow.png') });
    await page.setViewportSize({ width: 2600, height: 800 });
    await page.waitForFunction(() => {
      const el = document.querySelector('.exercise textarea');
      return el.scrollHeight <= el.clientHeight + 1;
    });
    assert.equal(await note.inputValue(), wrappedText, '窗口改变只影响折行，保留完整草稿');
    assert.deepEqual(errors, []);
    if (process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT });
    console.log('PASS: interactive table focus and long-answer growth, local scrolling, shrinkage and draft persistence retain the reading viewport.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

async function checkFirstTurnEditing(page) {
  await page.setViewportSize({ width: 900, height: 840 });
  await page.evaluate(async () => {
    const state = branchDemo.getState();
    const session = state.sessions.find(s => s.id === state.active);
    session.viewport = 0;
    session.turns = Array.from({ length: 5 }, (_, i) => ({
      id: 'mixed-turn-' + i, question: '第 ' + (i + 1) + ' 轮练习',
      answer: Array.from({ length: i ? 2 : 5 }, (_, j) => {
        const prefix = `mixed-${i}-${j}-`;
        return [
          { id: prefix + 'heading', kind: 'heading', text: '练习 ' + (j + 1) },
          { id: prefix + 'intro', kind: 'paragraph', text: '请先计算两组输入对应的输出，再说明二者的差别。'.repeat(4) },
          { id: prefix + 'math', kind: 'math', text: 'y = a x + b', number: '(' + (j + 1) + ')' },
          { id: prefix + 'form', kind: 'exercise', text: '' }
        ];
      }).flat()
    }));
    branchDemo.setState(state);
    await document.fonts.ready;
  });
  const note = page.locator('[data-block-id="mixed-0-3-form"] textarea');
  const frame = () => page.evaluate(() => new Promise(resolve => requestAnimationFrame(resolve)));
  await frame();
  await note.evaluate(el => {
    const reader = document.getElementById('reader');
    reader.scrollTop += el.getBoundingClientRect().top - reader.getBoundingClientRect().bottom + 225;
  });
  await note.click();
  await page.keyboard.insertText('发送方计算结果');
  await page.keyboard.press('Enter');
  await page.keyboard.press('Enter');
  await frame();
  await note.evaluate(el => {
    const reader = document.getElementById('reader'), turns = [...reader.querySelectorAll('.turn')];
    const field = el.getBoundingClientRect(), viewport = reader.getBoundingClientRect();
    if (reader.scrollTop < reader.clientHeight || field.top <= viewport.top || field.bottom >= viewport.bottom)
      throw new Error('预期首轮第四个练习在已下滚的视口内');
    const x = field.left + 10;
    const sample = () => {
      const boxes = turns.map(turn => turn.getBoundingClientRect());
      const top = el.getBoundingClientRect().top;
      const readTurn = y => document.elementFromPoint(x, y)?.closest('.turn')?.dataset.turnId;
      return {
        shift: top - field.top,
        rows: (el.clientHeight - 6) / 24,
        visibleTurn: readTurn(viewport.top + 20),
        hitsField: document.elementFromPoint(x, field.top + 12) === el,
        overlap: boxes.some((box, i) => i > 0 && box.top < boxes[i - 1].bottom - 1)
      };
    };
    const samples = [sample()];
    let id;
    const next = () => { samples.push(sample()); id = requestAnimationFrame(next); };
    id = requestAnimationFrame(next);
    window.finishEditFrames = () => { cancelAnimationFrame(id); samples.push(sample()); return samples; };
  });
  for (let i = 0; i < 3; i++) {
    await page.keyboard.press('Enter');
    await frame();
    if (i === 0 && process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT)
      await page.screenshot({ path: process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT.replace(/\.png$/i, '-four-rows.png') });
    await page.keyboard.press('Backspace');
    await frame();
  }
  for (let i = 0; i < 9; i++) await page.keyboard.press('Enter');
  await frame();
  if (process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT)
    await page.screenshot({ path: process.env.TEXEED_LONG_FORM_VIEWPORT_SCREENSHOT.replace(/\.png$/i, '-multi-turn.png') });
  for (let i = 0; i < 9; i++) await page.keyboard.press('Backspace');
  await frame();
  const samples = await page.evaluate(() => window.finishEditFrames());
  assert.ok([3, 4, 8].every(n => samples.some(s => s.rows === n)), '逐帧采样必须覆盖增高、缩短和八行上限');
  const displaced = samples.find(s => Math.abs(s.shift) >= 1.5 || s.visibleTurn !== 'mixed-turn-0' || !s.hitsField || s.overlap);
  assert.ok(!displaced, '编辑时视口跳转、控件被遮住或轮次重叠：' + JSON.stringify(displaced));
  assert.equal(await note.inputValue(), '发送方计算结果\n\n');
  assert.equal(await page.locator('#composer').inputValue(), '保留未发送草稿');
}

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
    if (process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_FORM_VIEWPORT_SCREENSHOT });
    await checkFirstTurnEditing(page);
    assert.deepEqual(errors, []);
    console.log('PASS: interactive form focus, long-answer editing, frame-by-frame multi-turn layout, local scrolling and draft persistence.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

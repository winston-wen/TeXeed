'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP inline branch links browser: Playwright unavailable'); process.exit(0); }
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href + '?test=1');
    await page.evaluate(() => {
      const f = branchDemo.getState();
      f.sessions[0].turns = [{ id: 'root-turn', question: '阅读问题', answer: [
        { id: 'first', kind: 'paragraph', text: '第一句。第二句接着写。第三句继续。' },
        { id: 'dup', kind: 'paragraph', text: '重复词在这里，重复词也在这里。' },
        { id: 'tail', kind: 'paragraph', text: '最后一段另有内容。' },
        { id: 'math', kind: 'math', text: 'x^2+1' },
        { id: 'image', kind: 'image', src: 'assets/flight-comparison.svg', alt: '示例图', text: '示例图' }
      ] }];
      f.sessions[0].draft = '原会话草稿';
      const child = (id, title, source) => ({ id, title, parent: 'root', source, draft: '', turns: [] });
      f.sessions.push(child('a', '追问甲', { blockId: 'first', textOffset: 0, endBlockId: 'first', endTextOffset: 4, quote: '第一句。' }));
      f.sessions.push(child('b', '追问乙', { blockId: 'first', textOffset: 0, endBlockId: 'first', endTextOffset: 4, quote: '第一句。' }));
      f.sessions.push(child('c', '追问丙', { blockId: 'first', textOffset: 4, endBlockId: 'first', endTextOffset: 10, quote: '第二句接着写' }));
      f.sessions.push(child('legacy-unique', '旧唯一', { blockId: 'tail', quote: '最后一段' }));
      f.sessions.push(child('legacy-cross', '旧跨段', { blockId: 'first', endBlockId: 'tail', textOffset: 4, quote: '第二句接着写。第三句继续。\n重复词在这里，重复词也在这里。\n最后一段' }));
      f.sessions.push(child('legacy-bad-cross', '旧跨段不确定', { blockId: 'first', endBlockId: 'tail', textOffset: 4, quote: '第二句接着写。第三句继续。\n不匹配的中间段\n最后一段' }));
      f.sessions.push(child('legacy-ambiguous', '旧不确定', { blockId: 'dup', quote: '重复词' }));
      f.sessions.push(child('math-branch', '公式追问', { blockId: 'math', kind: 'math', quote: '公式' }));
      f.sessions.push(child('image-branch', '图片追问', { blockId: 'image', kind: 'image', quote: '示例图' }));
      f.active = 'root'; f.selected = 'root'; branchDemo.setState(f);
    });
    const inline = page.locator('[data-block-id="first"]');
    assert.equal(await inline.locator('.associated').count(), 3);
    assert.equal(await page.locator('[data-open-session="a"]').evaluate(el => el.parentElement.dataset.blockId), 'first');
    assert.equal(await page.locator('[data-open-session="a"]').evaluate(el => el.previousSibling.textContent), '第一句。');
    assert.equal(await page.locator('[data-open-session="b"]').evaluate(el => el.nextSibling.textContent), '第二句接着写');
    assert.equal(await page.locator('[data-open-session="c"]').evaluate(el => el.nextSibling.textContent), '。第三句继续。');
    assert.equal(await page.locator('[data-open-session="legacy-unique"]').evaluate(el => el.previousSibling.textContent), '最后一段');
    assert.equal(await page.locator('[data-open-session="legacy-cross"]').evaluate(el => el.parentElement.dataset.blockId), 'tail', 'legacy cross-block end is recovered from source context');
    assert.equal(await page.locator('[data-open-session="legacy-bad-cross"]').evaluate(el => el.parentElement.className), 'unresolved-associated', 'mismatched intermediate context must not fabricate a position');
    assert.equal(await page.locator('[data-open-session="legacy-ambiguous"]').evaluate(el => el.parentElement.className), 'unresolved-associated');
    assert.equal(await page.locator('[data-open-session="math-branch"]').evaluate(el => el.parentElement.dataset.blockId), 'math');
    assert.equal(await page.locator('[data-open-session="image-branch"]').evaluate(el => el.parentElement.dataset.blockId), 'image');
    assert.match(await page.locator('[data-open-session="legacy-ambiguous"]').locator('..').innerText(), /来源位置待确认/);
    const display = await page.locator('[data-open-session="a"]').evaluate(el => ({ display: getComputedStyle(el).display, font: getComputedStyle(el).fontSize, bodyFont: getComputedStyle(el.parentElement).fontSize }));
    assert(['inline', 'inline-block'].includes(display.display)); assert.equal(display.font, display.bodyFont);
    const copied = await page.evaluate(() => {
      const p = document.querySelector('[data-block-id="first"]'), range = document.createRange(); range.selectNodeContents(p);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      const data = new DataTransfer(), event = new ClipboardEvent('copy', { bubbles: true, cancelable: true, clipboardData: data }); document.dispatchEvent(event);
      selection.removeAllRanges(); return { text: data.getData('text/plain'), prevented: event.defaultPrevented };
    });
    assert.deepEqual(copied, { text: '第一句。第二句接着写。第三句继续。', prevented: true });
    const copiedAcross = await page.evaluate(() => {
      const first = document.querySelector('[data-block-id="first"]'), last = document.querySelector('[data-block-id="tail"]'), range = document.createRange();
      range.setStart(first.firstChild, 0); range.setEndAfter(last.lastChild);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      const data = new DataTransfer(), event = new ClipboardEvent('copy', { bubbles: true, cancelable: true, clipboardData: data }); document.dispatchEvent(event);
      selection.removeAllRanges(); return data.getData('text/plain');
    });
    assert.equal(copiedAcross, '第一句。第二句接着写。第三句继续。\n重复词在这里，重复词也在这里。\n最后一段另有内容。');
    await page.evaluate(() => {
      const p = document.querySelector('[data-block-id="first"]'), nodes = [...p.childNodes].filter(node => node.nodeType === Node.TEXT_NODE && node.length);
      const range = document.createRange(); range.setStart(nodes[0], 0); range.setEnd(nodes[1], 3);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      p.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    });
    await page.waitForSelector('#selection-tools:not([hidden])');
    await page.locator('#selection-branch').click(); await page.locator('#composer').fill('选区跨过已有链接'); await page.locator('#send').click();
    let state = await page.evaluate(() => branchDemo.getState()); const created = state.sessions.find(s => s.id === state.active);
    assert.equal(created.source.quote, '第一句。第二句');
    assert.equal(created.source.endTextOffset, 7, 'new endpoint excludes existing link labels');
    await page.locator('#back-source').click(); await pause(120);
    assert.equal(await page.locator(`[data-open-session="${created.id}"]`).evaluate(el => el.nextSibling.textContent), '接着写');
    assert.equal(await page.locator('#composer').inputValue(), '原会话草稿');
    if (process.env.TEXEED_INLINE_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_INLINE_SCREENSHOT });
    await page.reload(); await pause(100);
    assert.equal(await page.locator(`[data-open-session="${created.id}"]`).count(), 1, 'inline endpoint survives reload');
    await page.evaluate(() => {
      const p = document.querySelector('[data-block-id="first"]'), tail = [...p.childNodes].find(node => node.nodeType === Node.TEXT_NODE && node.nodeValue.includes('第三句继续'));
      const range = document.createRange(); range.setStart(tail, 1); range.setEnd(tail, 4);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      p.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    });
    await page.waitForSelector('#selection-tools:not([hidden])');
    await page.locator('#selection-branch').click(); await page.locator('#composer').fill('此前有多个行内链接'); await page.locator('#send').click();
    state = await page.evaluate(() => branchDemo.getState()); const later = state.sessions.find(s => s.id === state.active);
    assert.equal(later.source.quote, '第三句'); assert.equal(later.source.textOffset, '第一句。第二句接着写。'.length);
    await page.locator('#back-source').click(); await page.locator('.source-flash').waitFor();
    assert.equal(await page.locator('.source-flash').getAttribute('data-block-id'), 'first');
    await page.locator(`[data-open-session="${created.id}"]`).click();
    assert.equal((await page.evaluate(() => branchDemo.getState())).active, created.id);
    await page.setViewportSize({ width: 420, height: 800 });
    await page.evaluate(() => {
      const f = branchDemo.getState(); f.active = 'root'; f.selected = 'root';
      f.sessions.push({ id: 'long-title', title: 'LONG_LINK_TITLE'.repeat(20), parent: 'root', source: { blockId: 'first', endBlockId: 'first', endTextOffset: 4, textOffset: 0, quote: '第一句。' }, draft: '', turns: [] });
      branchDemo.setState(f);
    });
    const longLayout = await page.locator('[data-open-session="long-title"]').evaluate(el => {
      const button = el.getBoundingClientRect(), block = el.parentElement.getBoundingClientRect(), following = document.createRange();
      following.selectNodeContents(el.nextSibling); const next = [...following.getClientRects()].map(r => ({ left: r.left, right: r.right, top: r.top, bottom: r.bottom }));
      return { button: { left: button.left, right: button.right, top: button.top, bottom: button.bottom }, block: { left: block.left, right: block.right }, next };
    });
    assert(longLayout.button.left >= longLayout.block.left - 1 && longLayout.button.right <= longLayout.block.right + 1, JSON.stringify(longLayout));
    assert(longLayout.next.every(rect => rect.bottom <= longLayout.button.top + 1 || rect.top >= longLayout.button.bottom - 1 || rect.right <= longLayout.button.left + 1 || rect.left >= longLayout.button.right - 1), JSON.stringify(longLayout));
    if (process.env.TEXEED_INLINE_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_INLINE_SCREENSHOT.replace('.png', '-narrow-long.png') });
    assert.deepEqual(errors, []);
    console.log('PASS inline links: exact endpoints, multiple links, legacy ambiguity, copy and durable navigation');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

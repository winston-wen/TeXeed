'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP branch origin browser: Playwright unavailable'); process.exit(0); }
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href);
    await page.evaluate(() => {
      const f = branchDemo.getState();
      f.sessions[0].turns = [{ id: 'turn', question: '原问题', answer: [
        { id: 'heading', kind: 'heading', text: '章节' },
        { id: 'first', kind: 'paragraph', text: '第一段需要追问的内容。' },
        { id: 'last', kind: 'paragraph', text: '第二段是引用结束处。' },
        { id: 'after', kind: 'paragraph', text: '继续阅读。' }
      ] }];
      f.sessions[0].draft = '保留根会话草稿'; branchDemo.setState(f);
    });
    await page.evaluate(() => {
      const first = document.querySelector('[data-block-id="first"]').firstChild;
      const last = document.querySelector('[data-block-id="last"]').firstChild;
      const range = document.createRange(); range.setStart(first, 0); range.setEnd(last, 6);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      document.querySelector('[data-block-id="first"]').dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    });
    await page.waitForSelector('#selection-tools:not([hidden])');
    await page.locator('#selection-branch').click(); await page.locator('#composer').fill('追问两段'); await page.locator('#send').click();
    await page.waitForSelector('.sent-reference');
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/branch-origin-sent.png') });
    let state = await page.evaluate(() => branchDemo.getState());
    const child = state.active;
    assert.equal(state.sessions.find(s => s.id === child).source.endBlockId, 'last');
    assert.equal(state.sessions.find(s => s.id === child).source.endTextOffset, 6);
    await page.locator('.sent-reference').click(); await pause(120);
    assert.equal((await page.evaluate(() => branchDemo.getState())).active, 'root');
    assert.equal(await page.locator('.source-flash').getAttribute('data-block-id'), 'first');
    assert.equal(await page.locator('#composer').inputValue(), '保留根会话草稿');
    assert.equal(await page.locator(`[data-open-session="${child}"]`).evaluate(el => el.parentElement.dataset.blockId), 'last', '跨段引用的链接位于最后一段内');
    assert.equal(await page.locator(`[data-open-session="${child}"]`).evaluate(el => el.previousSibling.textContent.length), 6, '链接紧随选区末尾');
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/branch-origin-parent.png') });

    await page.evaluate(() => {
      const f = branchDemo.getState();
      f.sessions.push({ id: 'legacy', title: '旧追问', parent: 'root', source: { blockId: 'first', textOffset: 0, quote: '第一段' }, draft: '旧草稿', turns: [{ id: 'old-turn', question: '旧提问', quote: '第一段', references: [{ id: 'old-ref', blockId: 'last', quote: '第二段', comment: '保留评论' }], answer: [{ id: 'old-answer', kind: 'paragraph', text: '旧回答' }] }] });
      f.active = 'legacy'; f.selected = 'legacy'; branchDemo.setState(f);
    });
    await page.reload(); await page.waitForSelector('.sent-origin');
    assert.equal(await page.locator('.sent-origin').count(), 1, '旧会话的来源展示一次');
    assert.equal(await page.locator('.quoted').count(), 0, '旧来源不重复展示');
    await page.locator('.sent-reference').click(); await pause(120);
    assert.equal((await page.evaluate(() => branchDemo.getState())).active, 'root');
    assert.equal(await page.locator('.source-flash').getAttribute('data-block-id'), 'last', '旧引用由唯一原文定位');
    await page.locator('[data-open-session="legacy"]').click();
    await page.locator('.sent-origin').click(); await pause(120);
    assert.equal((await page.evaluate(() => branchDemo.getState())).active, 'root');
    assert.equal(await page.locator('.source-flash').getAttribute('data-block-id'), 'first');
    assert.equal(await page.locator('[data-open-session="legacy"]').evaluate(el => el.parentElement.dataset.blockId), 'first', '旧分支根据原引用确定起点段落');
    assert.equal(await page.locator('[data-open-session="legacy"]').evaluate(el => el.previousSibling.textContent), '第一段', '旧分支精确恢复引用末尾');
    assert.deepEqual(errors, []);
    console.log('PASS branch source click, legacy restore, cross-paragraph link position and draft preservation');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

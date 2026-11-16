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
    await page.waitForFunction(() => !!window.branchDemo);
    const reset = async () => { await page.evaluate(() => branchDemo.reset()); await page.waitForTimeout(100); };
    const contextBranch = async locator => { await locator.scrollIntoViewIfNeeded(); await locator.click({ button: 'right' }); await page.locator('#context-branch').click(); };
    const state = () => page.evaluate(() => branchDemo.getState());
    await reset(); await page.locator('#composer').fill('上游尚未发送的草稿');
    const paragraph = page.locator('.passage').filter({ hasText: '先看一个较简单的模型' });
    const quote = await paragraph.evaluate(el => {
      const text = Array.from(el.childNodes).find(node => node.nodeType === Node.TEXT_NODE && node.textContent.trim());
      const range = document.createRange(); range.setStart(text, 0); range.setEnd(text, 18);
      const selected = getSelection(); selected.removeAllRanges(); selected.addRange(range);
      el.dispatchEvent(new PointerEvent('pointerup', { bubbles: true })); return selected.toString();
    });
    await page.locator('#selection-branch').click();
    assert.equal(await page.locator('.initial-source-text').innerText(), quote, 'new branch displays exactly the selected text');
    assert.equal(await page.locator('#references-toggle').innerText(), '引用 · 1');
    await page.locator('#composer').fill('下游尚未发送的草稿');
    await page.reload(); await page.waitForFunction(() => !!window.branchDemo);
    assert.equal(await page.locator('.initial-source-text').innerText(), quote, 'reopening retains source text');
    assert.equal(await page.locator('#composer').inputValue(), '下游尚未发送的草稿');
    await page.locator('.initial-source-heading button').click();
    assert.equal(await page.locator('#composer').inputValue(), '上游尚未发送的草稿');

    await reset(); await contextBranch(page.locator('.passage.formula').first());
    assert.equal(await page.locator('.initial-source .katex').count(), 1, 'formula source is typeset in reading area');
    assert.equal((await state()).sessions.find(s => s.parent)?.references[0]?.sourceLocked, true);
    await page.reload(); await page.waitForFunction(() => !!window.branchDemo);
    assert.equal(await page.locator('.initial-source .katex').count(), 1, 'formula rendering survives reload');
    if (process.env.TEXEED_SOURCE_SCREENSHOT_PATH) await page.screenshot({ path: process.env.TEXEED_SOURCE_SCREENSHOT_PATH });
    await page.locator('#composer').fill('问题'); await page.locator('#send').click();
    assert.equal(await page.locator('.initial-source').count(), 0, 'sent reference takes over after first message');
    assert.equal(await page.locator('.sent-reference .formula-svg').count(), 1);

    await page.setViewportSize({ width: 390, height: 800 }); await reset();
    await contextBranch(page.locator('.passage.formula').last());
    const longFormula = page.locator('.initial-source-formula');
    assert.equal(await longFormula.locator('.katex').count(), 1, 'long source formula is rendered');
    assert(await longFormula.evaluate(el => el.scrollWidth > el.clientWidth), 'long formula scrolls within its own area');
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'long formula does not widen the whole window');
    if (process.env.TEXEED_SOURCE_NARROW_SCREENSHOT_PATH) await page.screenshot({ path: process.env.TEXEED_SOURCE_NARROW_SCREENSHOT_PATH });
    await page.setViewportSize({ width: 1000, height: 800 });
    await reset(); await contextBranch(page.locator('.sample-image').first());
    const image = page.locator('.initial-source img'); assert.equal(await image.count(), 1);
    assert.equal(await image.getAttribute('alt'), '示例图：图表比较');
    assert((await image.evaluate(el => el.complete && el.naturalWidth)) > 0, 'source image is displayed');

    await page.evaluate(() => { const fixture = branchDemo.makeFixture(1, 0); fixture.sessions[0].turns[0].answer = [{ id: 'exercise-source', kind: 'exercise', text: '' }]; branchDemo.setState(fixture); });
    await page.locator('.exercise input[type=text]').first().fill('777');
    await contextBranch(page.locator('.exercise table'));
    assert.equal(await page.locator('.initial-source-table table').count(), 1, 'table source is displayed in the new conversation');
    assert.match(await page.locator('.initial-source-table').innerText(), /输入 y/);
    assert.match(await page.locator('.initial-source-table').innerText(), /777/, 'table source retains the visible cell value');
    assert.equal(await page.locator('.initial-source-table input').count(), 0, 'source snapshot is read-only');
    await page.reload(); await page.waitForFunction(() => !!window.branchDemo);
    assert.match(await page.locator('.initial-source-table').innerText(), /777/, 'table snapshot survives reload');
    await page.evaluate(() => { const fixture = branchDemo.makeFixture(1, 0); fixture.sessions[0].turns[0].answer = [{ id: 'long-source', kind: 'paragraph', text: '长选区'.repeat(750) }]; branchDemo.setState(fixture); });
    await page.locator('[data-block-id="long-source"]').evaluate(el => { const range = document.createRange(); range.selectNodeContents(el); const selected = getSelection(); selected.removeAllRanges(); selected.addRange(range); el.dispatchEvent(new PointerEvent('pointerup', { bubbles: true })); });
    await page.locator('#selection-branch').click();
    assert.equal((await page.locator('.initial-source-text').innerText()).length, 2250, 'long text selection is not truncated');
    assert.equal((await state()).sessions.find(s => s.parent).references[0].quote.length, 2250, 'full selection remains in the locked reference');
    assert.deepEqual(errors, []);
    console.log('PASS: branch source displays text, formula, image and read-only table; drafts and locked references survive');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

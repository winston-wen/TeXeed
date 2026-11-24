'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

async function run() {
  const input = process.argv[2] || 'prototypes/ai-chat-v02/index.html';
  const browser = await chromium.launch({ executablePath: process.env.TEXEED_BROWSER_PATH, headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 800, height: 920 } });
    await page.goto(pathToFileURL(path.resolve(input)).href + '?test=1');
    await page.evaluate(() => document.fonts.ready);
    await page.evaluate(() => {
      current().turns = Array.from({ length: 12 }, (_, i) => ({
        id: 'fold-' + i, question: '请解释第 ' + (i + 1) + ' 个步骤。',
        answer: '先写下已知条件，再检查这个步骤的结论。', answeredAt: '2026-11-24T08:00:00Z',
        collapsed: i > 1 && i < 8 && i !== 5
      }));
      renderTurns();
    });
    const compact = await page.locator('.fold-line').count() > 0;
    if (compact) {
      assert.deepEqual(await page.locator('.fold-number').allTextContents(), ['[3 ~ 5]', '[7, 8]']);
      await page.locator('#fold-5-divider').dblclick();
      assert.deepEqual(await page.locator('.fold-number').allTextContents(), ['[3 ~ 8]']);
      await page.locator('.fold-line').press('Enter');
      assert.equal(await page.locator('.question').count(), 12);
      await page.locator('#fold-2-divider').press('Space');
      assert.deepEqual(await page.locator('.fold-number').allTextContents(), ['[3]']);
      await page.locator('.fold-line').dblclick();
      assert.equal(await page.locator('.question').count(), 12);
    }
    const session = await page.context().newCDPSession(page);
    const measure = async () => {
      await page.evaluate(() => {
        current().turns.forEach((turn, i) => { turn.collapsed = i > 1 && i < 8 && i !== 5; });
        renderTurns();
      });
      await session.send('HeapProfiler.collectGarbage');
      const before = await session.send('Runtime.getHeapUsage');
      const sample = await page.evaluate(async () => {
        const start = performance.now();
        const divider = document.getElementById('fold-5-divider');
        divider.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        return { ms: +(performance.now() - start).toFixed(2),
          elements: document.querySelectorAll('#thread *').length,
          visibleControls: [...document.querySelectorAll('#thread button,#thread [role="button"]')].filter(el => el.getClientRects().length).length };
      });
      const after = await session.send('Runtime.getHeapUsage');
      return { ...sample, heapDelta: after.usedSize - before.usedSize };
    };
    await measure();
    const samples = [];
    for (let i = 0; i < 7; i++) samples.push(await measure());
    if (process.argv[3]) {
      await page.locator('.fold-line').first().scrollIntoViewIfNeeded();
      await page.screenshot({ path: process.argv[3] });
    }
    console.log(JSON.stringify(samples));
  } finally { await browser.close(); }
}
run().catch(error => { console.error(error); process.exitCode = 1; });

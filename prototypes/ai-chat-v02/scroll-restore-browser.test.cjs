'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch (error) {
  if (process.env.CI || process.env.TEXEED_PLAYWRIGHT_PATH) throw error;
  console.log('SKIP scroll restore: Playwright unavailable'); process.exit(0);
}

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 849, height: 932 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(process.env.TEXEED_PROTOTYPE||__dirname, 'index.html')).href + '?test=1');
    await page.evaluate(() => document.fonts.ready);
    await page.waitForTimeout(350);
    const reader = page.locator('#reader');
    await page.locator('#composer').fill('保留草稿');
    await reader.evaluate(el => { el.scrollTop = el.scrollHeight; });
    await page.waitForTimeout(250);
    const bottom = await reader.evaluate(el => el.scrollTop);
    assert(bottom > 500, 'fixture must contain scrollable reading content');
    await page.evaluate(() => {
      const nativeFrame = window.requestAnimationFrame;
      window.__pendingRestoreFrames = [];
      window.requestAnimationFrame = callback => { window.__pendingRestoreFrames.push(callback); return 1; };
      try { renderConversation(); } finally { window.requestAnimationFrame = nativeFrame; }
    });
    const box = await reader.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, -350);
    await page.waitForTimeout(180);
    const afterWheel = await reader.evaluate(el => el.scrollTop);
    assert(afterWheel < bottom - 100, 'wheel must move the reading viewport upward');
    await page.evaluate(() => { for (const callback of window.__pendingRestoreFrames.splice(0)) callback(performance.now()); });
    await page.waitForTimeout(350);
    const afterRestore = await reader.evaluate(el => el.scrollTop);
    assert(Math.abs(afterRestore - afterWheel) < 3, `late restore overrode wheel: ${afterWheel} -> ${afterRestore}`);
    if (process.env.TEXEED_SCREENSHOT_PATH) await page.screenshot({ path: process.env.TEXEED_SCREENSHOT_PATH });
    await page.reload(); await page.waitForTimeout(500);
    assert(Math.abs((await reader.evaluate(el => el.scrollTop)) - afterWheel) < 5, 'reopening must restore the user viewport');
    assert.equal(await page.locator('#composer').inputValue(), '保留草稿');
    await page.evaluate(() => { current().entries.forEach(entry => entry.selected = false); renderEntries(); });
    await page.locator('#send').click();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, -350); await page.waitForTimeout(250);
    const during = await reader.evaluate(el => el.scrollTop);
    await page.waitForTimeout(2000);
    assert(Math.abs((await reader.evaluate(el => el.scrollTop)) - during) < 5, 'answer completion must not pull a reader who scrolled up to the bottom');
    await page.locator('#composer').fill('继续提问'); await page.locator('#send').click();
    await page.waitForTimeout(2000);
    const distanceToBottom = await reader.evaluate(el => el.scrollHeight - el.clientHeight - el.scrollTop);
    assert(distanceToBottom < 5, 'reader at the bottom must continue to follow an answer');
    await require('./fold-boundary-wheel-browser.cjs')(page);
    assert.deepEqual(errors, []);
    console.log('PASS: wheel cancels delayed restore; reopen keeps viewport and draft; answer completion respects reading and follows at bottom');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

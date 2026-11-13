'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP settings login: Playwright unavailable'); process.exit(0); }

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 849, height: 932 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.locator('#composer').fill('保留未发送的草稿');
    await page.locator('#ai-menu-button').click(); await page.locator('#ai-settings').click();
    await page.locator('[data-setting="add"]').click();
    assert.equal(await page.locator('#settings-account').isEnabled(), true, '登录期间账号选择可用');
    assert.equal(await page.locator('#settings-use-key').isEnabled(), true, '登录期间连接方式可用');
    assert.equal(await page.locator('#settings-effort').isEnabled(), true, '登录期间推理强度可用');
    assert.equal(await page.locator('[data-setting="login"]').isEnabled(), true, '登录期间可重试');
    assert.equal(await page.locator('#settings-cancel').isEnabled(), true);
    assert.match(await page.locator('#settings-status').innerText(), /模拟浏览器授权/);
    if (process.env.TEXEED_SCREENSHOT_PATH) await page.screenshot({ path: process.env.TEXEED_SCREENSHOT_PATH });
    await page.locator('#settings-account').selectOption({ label: '示例账号 B' });
    await page.waitForFunction(() => document.getElementById('settings-status').textContent.includes('模拟操作已完成'));
    assert.equal(await page.locator('#settings-account').inputValue(), '示例账号 B', '后一次切换生效');
    await page.waitForTimeout(1700);
    assert.match(await page.locator('#settings-status').innerText(), /模拟操作已完成/, '被替代的旧登录不会覆盖新状态');
    await page.locator('[data-setting="login"]').click();
    await page.waitForFunction(() => document.getElementById('settings-status').textContent.includes('模拟登录失败'));
    assert.equal(await page.locator('#settings-cancel').isDisabled(), true);
    assert.equal(await page.locator('#settings-account').isEnabled(), true, '失败后无需重开即可切换');
    await page.locator('[data-setting="login"]').click(); await page.locator('#settings-cancel').click();
    await page.waitForTimeout(1700);
    assert.match(await page.locator('#settings-status').innerText(), /已取消/, '取消后不会由过期回调变回失败');
    await page.locator('[data-setting="add"]').click(); await page.locator('#settings-use-key').check();
    assert.match(await page.locator('#settings-status').innerText(), /已切换示例连接方式/);
    await page.setViewportSize({ width: 390, height: 800 });
    const narrow = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: innerWidth }));
    assert.ok(narrow.width <= narrow.viewport, '窄屏设置不应撑宽整个页面');
    if (process.env.TEXEED_NARROW_SCREENSHOT_PATH) await page.screenshot({ path: process.env.TEXEED_NARROW_SCREENSHOT_PATH });
    await page.locator('[data-tab]').first().click();
    assert.equal(await page.locator('#composer').inputValue(), '保留未发送的草稿', '切换设置和账号不清空对话草稿');
    assert.deepEqual(errors, []);
    console.log('PASS settings: usable while logging in, cancellation, failure, supersession and retained draft.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

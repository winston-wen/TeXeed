'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP relationship tabs browser: Playwright unavailable'); process.exit(0); }
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const context = await browser.newContext({ viewport: { width: 1000, height: 800 } });
    const page = await context.newPage();
    const errors = []; page.on('pageerror', e => errors.push(e.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href);
    await page.evaluate(() => {
      const fixture = branchDemo.getState();
      fixture.sessions.push({ id: 'child', title: '追问', parent: 'root', source: { blockId: fixture.sessions[0].turns[0].answer[1].id, quote: '理解' }, draft: '追问草稿', turns: [] },
        { id: 'sibling', title: '另一个追问', parent: 'root', source: { blockId: fixture.sessions[0].turns[0].answer[1].id, quote: '理解' }, draft: '', turns: [] },
        { id: 'other', title: '无关会话', parent: null, source: null, draft: '', turns: [] });
      branchDemo.setState(fixture);
    });
    await page.locator('#composer').fill('主会话草稿');
    await page.locator('#map-toggle').click();
    assert.equal(await page.locator('#relation-tab-wrap').isVisible(), true);
    assert.equal(await page.locator('#conversation-toolbar').isHidden(), true);
    assert.equal(await page.locator('[data-node-id]').count(), 3, 'only the current family appears');
    assert.equal(await page.locator('[data-node-id="other"]').count(), 0);
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/branch-relationship-tab.png') });
    await page.locator('#conversation-tab').click();
    assert.equal(await page.locator('#composer').inputValue(), '主会话草稿');
    await page.setViewportSize({ width: 390, height: 800 });
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/branch-navigation-narrow.png') });
    await page.setViewportSize({ width: 1000, height: 800 });
    await page.locator('#relation-tab').click();
    await page.locator('[data-node-id="child"]').dblclick();
    assert.equal(await page.locator('#composer').inputValue(), '追问草稿');
    assert.equal(await page.locator('#relation-tab-wrap').isVisible(), true, 'tab stays available');
    await page.locator('#back-source').click();
    await page.locator('#child-sessions').click();
    assert.equal(await page.locator('#child-menu button').count(), 2);
    await page.locator('#child-menu button').filter({hasText:'追问'}).first().click();
    assert.equal(await page.locator('#composer').inputValue(), '追问草稿');
    await page.locator('#relation-tab-close').click();
    assert.equal(await page.locator('#relation-tab-wrap').isHidden(), true);
    await page.locator('#new-conversation').click();
    const first = (await page.evaluate(() => branchDemo.getState())).active;
    await page.locator('#new-conversation').click();
    assert.equal((await page.evaluate(() => branchDemo.getState())).active, first, 'reuse blank session');
    await page.locator('#composer').fill('未发送草稿');
    await page.locator('#new-conversation').click();
    const result = await page.evaluate(() => branchDemo.getState());
    assert.equal(result.sessions.find(s => s.id === first).draft, '未发送草稿');
    assert.notEqual(result.active, first);
    await page.evaluate(() => { const data = branchDemo.getState(); data.sessions.push({ id: 'old-blank', title: '新对话', parent: null, source: null, draft: '', turns: [] }, { id: 'protected', title: '新对话', parent: null, source: null, draft: '', turns: [], futureData: { note: '保留' } }); localStorage.setItem('texeed-branch-reading-prototype-v1', JSON.stringify(data)); });
    const second = await page.context().newPage();
    await second.goto(page.url());
    const restored = await second.evaluate(() => branchDemo.getState());
    assert.equal(restored.sessions.some(s => s.id === 'old-blank'), false, 'remove an existing truly empty duplicate');
    assert.equal(restored.sessions.find(s => s.id === 'protected').futureData.note, '保留', 'preserve unknown future fields');
    assert.equal(restored.sessions.find(s => s.id === first).draft, '未发送草稿', 'reload preserves nonempty draft');
    await second.close();
    await page.screenshot({ path: path.join(__dirname, '../../artifacts/branch-tabs-browser.png') });
    assert.deepEqual(errors, []);
    console.log('PASS scoped relationship tab, upstream/downstream and blank draft preservation');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

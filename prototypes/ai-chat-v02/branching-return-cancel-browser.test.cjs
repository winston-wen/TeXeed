'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP branch return cancellation browser: Playwright unavailable'); process.exit(0); }

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href + '?test=1');
    async function fixture() {
      await page.evaluate(() => {
        const f = branchDemo.getState(), para = (id, text) => ({ id, kind: 'paragraph', text });
        const answer = [...Array.from({ length: 15 }, (_, i) => para('before-' + i, '定位前的测试文字。'.repeat(7))), para('source', '这里是用户选择的来源位置。'), ...Array.from({ length: 15 }, (_, i) => para('after-' + i, '定位后的测试文字。'.repeat(7)))];
        f.sessions = [
          { id: 'root', title: '上游', parent: null, source: null, draft: '上游草稿', turns: [{ id: 'root-turn', question: '问题', answer }] },
          { id: 'child', title: '追问', parent: 'root', source: { blockId: 'source', endBlockId: 'source', textOffset: 3, endTextOffset: 8, quote: '用户选择的' }, draft: '追问草稿', turns: [{ id: 'child-turn', question: '追问', answer: [para('reply', '答复')] }] }
        ];
        f.active = 'child'; f.selected = 'child'; f.bookmarks = []; branchDemo.setState(f);
      });
    }
    async function holdAndNavigate() {
      await page.evaluate(() => {
        const originalRequest = window.requestAnimationFrame, originalCancel = window.cancelAnimationFrame;
        const queued = new Map(); let next = 2000000000;
        const tracked = [];
        for (const [target, names] of [[document.querySelector('#reader'), ['wheel', 'pointerdown', 'touchstart']], [document, ['keydown', 'visibilitychange']], [window, ['pagehide']]]) {
          const add = target.addEventListener, remove = target.removeEventListener, active = new Map(names.map(name => [name, new Set()]));
          target.addEventListener = function(type, listener, options) { active.get(type)?.add(listener); return add.call(this, type, listener, options); };
          target.removeEventListener = function(type, listener, options) { active.get(type)?.delete(listener); return remove.call(this, type, listener, options); };
          tracked.push({ target, add, remove, active });
        }
        window.requestAnimationFrame = callback => { const id = next++; queued.set(id, callback); return id; };
        window.cancelAnimationFrame = id => { queued.delete(id); };
        window.flushTestFrames = () => {
          window.requestAnimationFrame = originalRequest; window.cancelAnimationFrame = originalCancel;
          const callbacks = [...queued.values()]; queued.clear(); callbacks.forEach(callback => callback(performance.now()));
          window.pendingInputListeners = tracked.reduce((count, entry) => count + [...entry.active.values()].reduce((sum, listeners) => sum + listeners.size, 0), 0);
          for (const entry of tracked) { entry.target.addEventListener = entry.add; entry.target.removeEventListener = entry.remove; }
        };
        document.querySelector('#back-source').click();
      });
    }
    for (const action of ['wheel', 'pointerdown', 'touchstart', 'PageDown', 'ArrowUp', 'ArrowRight', ' ']) {
      await fixture(); await holdAndNavigate();
      const after = await page.evaluate(action => {
        const reader = document.querySelector('#reader'); reader.scrollTop = 280; const before = reader.scrollTop;
        if (action === 'wheel') reader.dispatchEvent(new WheelEvent('wheel', { deltaY: 100, bubbles: true }));
        else if (action === 'pointerdown') reader.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'mouse', button: 0 }));
        else if (action === 'touchstart') reader.dispatchEvent(new Event('touchstart', { bubbles: true }));
        else reader.dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, key: action }));
        window.flushTestFrames();
        return { before, after: reader.scrollTop, active: branchDemo.getState().active, status: document.querySelector('#status').textContent, highlighted: !!document.querySelector('.source-flash'), listeners: window.pendingInputListeners };
      }, action);
      assert.equal(after.active, 'root', action + ' should keep the upstream conversation');
      assert.equal(after.after, after.before, action + ' should retain the user-selected viewport');
      assert(!/无法定位|已定位/.test(after.status), action + ' must not report a successful or failed automatic location');
      assert.equal(after.highlighted, false);
      assert.equal(after.listeners, 0, 'pending listeners must be removed after ' + action);
    }
    for (const action of ['programmatic-scroll', 'editor-key']) {
      await fixture(); await holdAndNavigate();
      const result = await page.evaluate(action => {
        const reader = document.querySelector('#reader'); reader.scrollTop = 280;
        if (action === 'editor-key') document.querySelector('#composer').dispatchEvent(new KeyboardEvent('keydown', { bubbles: true, key: 'ArrowDown' }));
        window.flushTestFrames();
        const el = document.querySelector('.source-flash');
        return { found: el?.dataset.blockId, status: document.querySelector('#status').textContent, listeners: window.pendingInputListeners };
      }, action);
      assert.equal(result.found, 'source', action + ' must not cancel an otherwise valid jump');
      assert.equal(result.status, '已定位提问出处。');
      assert.equal(result.listeners, 0, 'pending listeners must be removed after completion');
    }
    await fixture();
    await page.evaluate(() => {
      window.realWheelSeen = false;
      document.querySelector('#reader').addEventListener('wheel', () => { window.realWheelSeen = true; }, { once: true });
    });
    await holdAndNavigate();
    await page.evaluate(() => { document.querySelector('#reader').scrollTop = 280; });
    await page.mouse.move(500, 300); await page.mouse.wheel(0, 120);
    await page.waitForFunction(() => window.realWheelSeen, null, { polling: 20, timeout: 3000 });
    await page.waitForTimeout(100);
    const realWheel = await page.evaluate(() => {
      const reader = document.querySelector('#reader'), before = reader.scrollTop; window.flushTestFrames();
      return { before, after: reader.scrollTop, status: document.querySelector('#status').textContent, listeners: window.pendingInputListeners };
    });
    assert.equal(realWheel.before, realWheel.after, 'actual browser wheel must not be overridden');
    assert(!/无法定位|已定位/.test(realWheel.status)); assert.equal(realWheel.listeners, 0);
    assert.deepEqual(errors, []);
    console.log('PASS upstream jump cancellation: wheel, pointer/touch drag and scroll keys preserve user viewport without false failure');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

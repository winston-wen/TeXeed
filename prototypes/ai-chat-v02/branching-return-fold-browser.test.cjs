'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP branch return fold browser: Playwright unavailable'); process.exit(0); }
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href + '?test=1');
    async function fixture(kind, target = 'source') {
      await page.evaluate(({ kind, target }) => {
        const para = (id, text = '这是一段用于测试定位与阅读的文字。'.repeat(5)) => ({ id, kind: 'paragraph', text });
        const heading = (id, level) => ({ id, kind: 'heading', level, text: id });
        let answer;
        if (kind === 'middle') answer = [
          heading('other', 1), para('other-content'),
          heading('before', 1), ...Array.from({ length: 12 }, (_, i) => para('before-' + i)),
          heading('parent', 1), heading('sibling', 2), para('sibling-content'),
          heading('child', 2), heading('grand', 3), para('source'),
          heading('after', 1), para('after-content'),
          heading('tail', 1), ...Array.from({ length: 12 }, (_, i) => para('tail-' + i))
        ];
        else answer = kind === 'top' ? [para('source'), ...Array.from({ length: 20 }, (_, i) => para('filler-' + i))]
          : [...Array.from({ length: 20 }, (_, i) => para('filler-' + i)), para('source')];
        const root = { id: 'root', title: '上游会话', parent: null, source: null, draft: '上游草稿', turns: [{ id: 'root-turn', question: '上游问题', answer,
          collapsedSections: kind === 'middle' ? { other: true, parent: true, sibling: true, child: true, grand: true, after: true } : {} }] };
        const child = { id: 'child-session', title: '追问', parent: 'root', source: { blockId: target, textOffset: target === 'source' ? 5 : 0, quote: '来源文字' }, draft: '追问草稿', turns: [{ id: 'child-turn', question: '追问', answer: [para('reply')] }] };
        const state = branchDemo.getState(); state.sessions = [root, child];
        if (kind === 'middle') state.sessions.push({ id: 'earlier-link', title: '此前追问', parent: 'root', source: { blockId: 'source', textOffset: 0, endBlockId: 'source', endTextOffset: 3, quote: '这是一' }, draft: '', turns: [] });
        state.active = 'child-session'; state.selected = 'child-session'; state.bookmarks = []; branchDemo.setState(state);
      }, { kind, target });
    }
    const metrics = () => page.evaluate(() => {
      const reader = document.querySelector('#reader'), el = document.querySelector('.source-flash'), walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT, { acceptNode: node => node.parentElement?.closest('.associated') ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT });
      let node, left = 5, target; while ((node = walker.nextNode())) { if (left <= node.length) { target = node; break; } left -= node.length; }
      const range = document.createRange(); range.setStart(target, Math.min(left, target.length)); range.collapse(true); const rect = range.getBoundingClientRect(), bounds = reader.getBoundingClientRect();
      return { distance: rect.top + Math.min(rect.height, 24) / 2 - bounds.top - reader.clientHeight / 2, scroll: reader.scrollTop, max: reader.scrollHeight - reader.clientHeight };
    });
    await fixture('middle');
    assert.equal(await page.locator('.sent-origin').count(), 1, 'legacy branch without FoldKeys still shows its source');
    await page.locator('.sent-origin').click(); await page.locator('.source-flash').waitFor();
    let state = await page.evaluate(() => branchDemo.getState());
    const collapsed = state.sessions.find(s => s.id === 'root').turns[0].collapsedSections;
    for (const id of ['parent', 'child', 'grand']) assert.equal(collapsed[id], false, id + ' ancestor must open');
    for (const id of ['other', 'sibling', 'after']) assert.equal(collapsed[id], true, id + ' unrelated fold must remain closed');
    assert.equal(await page.locator('[data-block-id="source"]').isVisible(), true);
    assert.equal(await page.locator('[data-block-id="sibling-content"]').isVisible(), false);
    assert.equal(await page.locator('[data-block-id="after-content"]').isVisible(), false);
    assert.equal(await page.locator('#composer').inputValue(), '上游草稿');
    const middle = await metrics();
    assert(middle.scroll > 0 && middle.scroll < middle.max && Math.abs(middle.distance) < 2, JSON.stringify(middle));
    if (process.env.TEXEED_RETURN_SCREENSHOT) await page.screenshot({ path: process.env.TEXEED_RETURN_SCREENSHOT });
    await page.reload(); await pause(120);
    state = await page.evaluate(() => branchDemo.getState());
    assert.equal(state.sessions.find(s => s.id === 'root').turns[0].collapsedSections.sibling, true, 'unrelated fold survives reload');
    await fixture('middle', 'grand');
    await page.locator('#back-source').click(); await pause(100);
    state = await page.evaluate(() => branchDemo.getState());
    const headingState = state.sessions.find(s => s.id === 'root').turns[0].collapsedSections;
    assert.equal(headingState.parent, false); assert.equal(headingState.child, false);
    assert.equal(headingState.grand, true, 'referenced heading itself need not be expanded');
    assert.equal(await page.locator('[data-block-id="grand"]').isVisible(), true);
    assert.equal(await page.locator('[data-block-id="source"]').isVisible(), false);
    for (const boundary of ['top', 'bottom']) {
      await fixture(boundary); await page.locator('#back-source').click(); await page.locator('.source-flash').waitFor();
      const result = await metrics();
      if (boundary === 'top') assert(result.scroll < 1 && result.distance < 0, JSON.stringify(result));
      else assert(Math.abs(result.max - result.scroll) < 1 && result.distance > 0, JSON.stringify(result));
    }
    assert.deepEqual(errors, []);
    console.log('PASS upstream: only required ancestors open, unrelated folds preserved, source centered and edges clamped');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

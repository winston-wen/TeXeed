'use strict';
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP branching browser: Playwright unavailable'); process.exit(0); }
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 800 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'branching.html')).href);
    await page.waitForFunction(() => !!window.branchDemo);
    await page.evaluate(() => { localStorage.setItem('texeed-legacy-test-key', 'untouched'); branchDemo.reset(); });
    await pause(100);
    const state = () => page.evaluate(() => branchDemo.getState());
    const select = async (locator, length = 12) => {
      await locator.scrollIntoViewIfNeeded();
      await locator.evaluate((element, n) => {
        const walker = document.createTreeWalker(element, NodeFilter.SHOW_TEXT);
        let text = walker.nextNode();
        while (text && !text.textContent.trim()) text = walker.nextNode();
        if (!text) throw new Error('No text to select');
        const range = document.createRange();
        range.setStart(text, 0); range.setEnd(text, Math.min(n, text.length));
        const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
        element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
      }, length);
      await page.waitForSelector('#selection-tools:not([hidden])');
    };

    await page.locator('#composer').fill('主会话草稿');
    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }));
    await page.locator('#selection-branch').click();
    assert.equal((await state()).sessions.length, 1, '选择追问本身不发送');
    assert.equal(await page.locator('#composer').inputValue(), '');
    await page.locator('#composer').fill('为何需要这个条件？');
    await page.locator('#send').click(); await pause(120);
    let snapshot = await state();
    assert.equal(snapshot.sessions.length, 2);
    assert.equal(snapshot.sessions[0].draft, '主会话草稿');
    const child = snapshot.active;
    assert.equal(snapshot.sessions[1].parent, 'root');

    await page.locator('#composer').fill('请举个例子');
    await page.locator('#send').click(); await pause(100);
    snapshot = await state();
    assert.equal(snapshot.sessions.length, 2, '继续会话不新建节点');
    assert.equal(snapshot.sessions[1].turns.length, 2);
    await select(page.locator('.passage').filter({ hasText: '预设示例答复' }).first());
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('这一步如何理解？');
    await page.locator('#send').click(); await pause(100);
    snapshot = await state();
    assert.equal(snapshot.sessions.length, 3);
    assert.equal(snapshot.sessions[2].parent, child);

    await page.locator('#map-toggle').click();
    assert.equal(await page.locator('[data-node-id]').count(), 3);
    await page.locator(`[data-node-id="${child}"]`).click();
    assert.ok((await page.locator('#map-detail').innerText()).includes('Saber OT'));
    await page.locator('#map-detail [data-map-open]').click(); await pause(100);
    assert.equal((await state()).active, child);
    await page.locator('#back-source').click(); await pause(120);
    assert.equal((await state()).active, 'root');
    assert.ok(await page.locator('#back-reading').isVisible());
    await page.locator('#back-reading').click(); await pause(100);
    assert.equal(await page.locator('#composer').inputValue(), '主会话草稿');
    await page.reload(); await pause(120);
    assert.equal((await state()).sessions.length, 3);
    assert.equal(await page.locator('#composer').inputValue(), '主会话草稿');
    assert.equal(await page.evaluate(() => localStorage.getItem('texeed-legacy-test-key')), 'untouched');

    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }));
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('待发送的追问草稿');
    await page.locator('#map-toggle').click();
    await page.locator(`[data-node-id="${child}"]`).click();
    await page.locator('#map-detail [data-map-open]').click(); await pause(100);
    await page.locator('#map-toggle').click();
    await page.locator('[data-node-id="root"]').click();
    await page.locator('#map-detail [data-map-open]').click(); await pause(100);
    assert.equal(await page.locator('#composer').inputValue(), '待发送的追问草稿', '切换会话保留追问草稿');
    await page.reload(); await pause(100);
    assert.equal(await page.locator('#composer').inputValue(), '待发送的追问草稿', '重开保留追问草稿');
    await page.locator('#cancel-context').click();
    assert.equal(await page.locator('#composer').inputValue(), '主会话草稿', '取消追问恢复主草稿');

    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }));
    await page.locator('#selection-quote').click();
    await page.locator('#composer').fill('当前会话引用后继续');
    await page.locator('#send').click(); await pause(80);
    snapshot = await state();
    assert.equal(snapshot.sessions.length, 3, '引用并继续不新建节点');
    assert.ok(snapshot.sessions[0].turns.at(-1).quote);

    await page.setViewportSize({ width: 390, height: 800 }); await pause(100);
    await page.locator('#map-toggle').click();
    const widths = await page.evaluate(() => ({ body: document.documentElement.scrollWidth, viewport: innerWidth }));
    assert.equal(widths.body, widths.viewport, '窄屏导图只在自身区域横向滚动');

    await page.locator('#map-close').click();
    await page.evaluate(() => branchDemo.reset()); await pause(100);
    await page.setViewportSize({ width: 800, height: 600 });
    await page.locator('.passage').filter({ hasText: '即使我们理解了运算所在' }).scrollIntoViewIfNeeded(); await pause(100);
    await page.locator('#mark-reading').click();
    const marked = (await state()).sessions[0].markedReading;
    assert.ok(marked?.blockId);
    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }), 8);
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('解释这里'); await page.locator('#send').click(); await pause(120);
    const branch = (await state()).sessions[1];
    assert.equal(branch.source.parentReading.blockId, marked.blockId);
    assert.notEqual(branch.source.blockId, marked.blockId, '选区和断点分开保存');
    await page.locator('#back-reading').click(); await pause(120);
    const sourceHeading = page.locator('[data-heading-id]').filter({ hasText: '从整数模运算开始' });
    await sourceHeading.click();
    assert.equal(await page.locator(`[data-block-id="${branch.source.blockId}"]`).isVisible(), false);
    await page.locator('#map-toggle').click();
    await page.locator(`[data-node-id="${branch.id}"]`).click();
    await page.locator('#map-detail [data-map-open]').click(); await pause(100);
    await page.reload(); await pause(120);
    await page.locator('#back-source').click(); await pause(120);
    assert.equal(await page.locator('.source-flash').getAttribute('data-block-id'), branch.source.blockId);
    assert.equal(await page.locator(`[data-block-id="${branch.source.blockId}"]`).isVisible(), true, '查看出处时展开折叠的来源');
    await page.locator('#back-reading').click(); await pause(120);
    const actualOffset = await page.evaluate(id => {
      const reader = document.getElementById('reader'), element = document.querySelector(`[data-block-id="${id}"]`);
      return reader.getBoundingClientRect().top - element.getBoundingClientRect().top;
    }, marked.blockId);
    assert.ok(Math.abs(actualOffset - marked.offset) < 3, '按文本块及相对视口位置恢复阅读断点');

    await page.evaluate(() => {
      branchDemo.reset();
      const fixture = branchDemo.getState();
      fixture.sessions[0].turns[0].answer[8].text = '段落中的概念需要结合前后文理解。'.repeat(120);
      branchDemo.setState(fixture);
    });
    await pause(120); await page.setViewportSize({ width: 800, height: 600 });
    await page.evaluate(() => {
      const reader = document.getElementById('reader');
      const passage = Array.from(document.querySelectorAll('.passage')).find(e => e.textContent.startsWith('段落中的概念'));
      reader.scrollTop += passage.getBoundingClientRect().top - reader.getBoundingClientRect().top + 220;
    });
    await pause(100); await page.locator('#mark-reading').click();
    const wrapped = (await state()).sessions[0].markedReading;
    assert.ok(wrapped?.textOffset > 0, '长段落应记录文本位置');
    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }));
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('请解释选中部分'); await page.locator('#send').click(); await pause(100);
    await page.setViewportSize({ width: 450, height: 600 });
    await page.locator('#back-reading').click(); await pause(150);
    const linePosition = await page.evaluate(pos => {
      const el = document.querySelector(`[data-block-id="${pos.blockId}"]`), reader = document.getElementById('reader');
      const walker = document.createTreeWalker(el, NodeFilter.SHOW_TEXT); let node, left = pos.textOffset;
      while ((node = walker.nextNode()) && left > node.length) left -= node.length;
      const range = document.createRange(); range.setStart(node, left); range.collapse(true);
      return range.getBoundingClientRect().top - reader.getBoundingClientRect().top;
    }, wrapped);
    assert.ok(Math.abs(linePosition - wrapped.lineY) < 3, `宽度变化后保留同一文字行（${linePosition} / ${wrapped.lineY}）`);

    await page.evaluate(() => {
      branchDemo.reset();
      const fixture = branchDemo.getState();
      fixture.sessions[0].turns[0].answer[8].text = '背景介绍需要逐行阅读。'.repeat(100) + '这里是要查看的具体选区' + '后文需要继续阅读。'.repeat(40);
      branchDemo.setState(fixture);
    });
    await page.setViewportSize({ width: 800, height: 600 }); await pause(120);
    await page.locator('.passage').filter({ hasText: '这里是要查看的具体选区' }).evaluate(element => {
      const text = element.firstChild, start = text.textContent.indexOf('这里是要查看的具体选区');
      const range = document.createRange(); range.setStart(text, start); range.setEnd(text, start + 11);
      const reader = document.getElementById('reader');
      reader.scrollTop += range.getBoundingClientRect().top - reader.getBoundingClientRect().top - 60;
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    });
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('这一句具体是什么意思？'); await page.locator('#send').click(); await pause(120);
    await page.locator('#back-source').click(); await pause(120);
    const sourceVisible = await page.locator('.passage').filter({ hasText: '这里是要查看的具体选区' }).evaluate(element => {
      const text = element.firstChild, start = text.textContent.indexOf('这里是要查看的具体选区');
      const range = document.createRange(); range.setStart(text, start); range.setEnd(text, start + 11);
      const r = range.getBoundingClientRect(), reader = document.getElementById('reader').getBoundingClientRect();
      return r.top >= reader.top && r.bottom <= reader.bottom;
    });
    assert.ok(sourceVisible, '长段落的提问出处应定位到具体选区，而不是只跳到段首');

    await page.evaluate(() => branchDemo.reset()); await pause(100);
    await select(page.locator('.passage').filter({ hasText: '先看一个较简单的模型' }));
    await page.locator('#selection-branch').click();
    await page.locator('#composer').fill('保存失败时保留这个问题');
    await page.evaluate(() => {
      window.originalStorageSetItem = Storage.prototype.setItem;
      Storage.prototype.setItem = function () { throw new DOMException('storage disabled', 'QuotaExceededError'); };
    });
    await page.locator('#send').click();
    assert.equal(await page.locator('#composer').inputValue(), '保存失败时保留这个问题', '保存失败不清空待发问题');
    assert.equal((await state()).sessions.length, 1, '保存失败不创建未落盘的会话');
    assert.ok((await page.locator('#status').innerText()).includes('无法保存'), '清楚显示保存失败');
    await page.evaluate(() => { Storage.prototype.setItem = window.originalStorageSetItem; });
    await page.locator('#send').click(); await pause(100);
    assert.equal((await state()).sessions.length, 2, '恢复存储后可以重试创建会话');
    await page.locator('#composer').fill('保存失败时保留当前续问');
    await page.evaluate(() => { Storage.prototype.setItem = function () { throw new DOMException('storage disabled', 'QuotaExceededError'); }; });
    await page.locator('#send').click();
    assert.equal(await page.locator('#composer').inputValue(), '保存失败时保留当前续问');
    assert.equal((await state()).sessions[1].turns.length, 1, '保存失败不添加未落盘的轮次');
    assert.ok((await page.locator('#status').innerText()).includes('无法保存'));
    await page.evaluate(() => { Storage.prototype.setItem = window.originalStorageSetItem; });
    await page.locator('#send').click(); await pause(100);
    assert.equal((await state()).sessions[1].turns.length, 2, '恢复存储后可以重试续聊');
    await page.evaluate(() => branchDemo.reset()); await pause(100);
    const dragTarget = page.locator('.passage').filter({ hasText: '理解 Saber 与 OT 时' });
    await dragTarget.scrollIntoViewIfNeeded();
    const dragBox = await dragTarget.boundingBox();
    await page.mouse.move(dragBox.x + 8, dragBox.y + 10);
    await page.mouse.down();
    await page.mouse.move(dragBox.x + 180, dragBox.y + 10, { steps: 12 });
    await page.mouse.up();
    assert.ok(await page.evaluate(() => getSelection().toString().trim().length > 0), '实际鼠标拖选文字');
    assert.equal(await page.locator('#selection-tools').isVisible(), true, '鼠标拖选后出现提问选项');
    await page.locator('#selection-quote').click();
    assert.ok((await page.locator('#composer-context').innerText()).includes('引用到当前会话'));
    await page.evaluate(() => branchDemo.reset()); await pause(100);
    const formula = page.locator('.formula').first(); await formula.scrollIntoViewIfNeeded();
    await formula.evaluate(element => {
      const range = document.createRange(); range.selectNodeContents(element);
      const selection = getSelection(); selection.removeAllRanges(); selection.addRange(range);
      element.dispatchEvent(new PointerEvent('pointerup', { bubbles: true }));
    });
    await page.locator('#selection-branch').click();
    assert.ok((await page.locator('#composer-context').innerText()).trim());
    assert.deepEqual(errors, []);
    console.log('PASS branching, continuation, nesting, map, separate reading/source positions, reload, quotes, formula and narrow layout');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

async function run() {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.TEXEED_BROWSER_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 830, height: 740 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.evaluate(async () => {
      await document.fonts.ready;
      current().turns = Array.from({ length: 23 }, (_, i) => ({
        id: 'fold-' + i,
        question: '第 ' + (i + 1) + ' 轮问了什么？',
        answer: '这一轮的第一段。\n\n这一轮的第二段。',
        answeredAt: '2026-11-24T08:00:00Z',
        collapsed: (i >= 4 && i <= 7 && i !== 6) || i === 12
      }));
      current().turns[6].annotations = [{id:'first-ref',block:'fold-3-p0',quote:'第一段',endTextOffset:7}];
      current().draft = '不随折叠变化的未发送草稿';
      $('composer').value = current().draft;
      renderTurns();
    });
    const line = n => page.locator('#fold-' + n + '-divider');
    const hint = n => line(n).locator('.fold-run-hint');
    const numbers = () => page.locator('.fold-number').allTextContents();
    const states = () => page.evaluate(() => current().turns.map(t => [!!t.collapsed, !!t.foldGroup]));
    const y = selector => page.locator(selector).evaluate(node => node.getBoundingClientRect().top - $('reader').getBoundingClientRect().top);
    const screenshot = process.env.TEXEED_FOLD_SCREENSHOTS;
    if (screenshot) fs.mkdirSync(screenshot, { recursive: true });
    assert.deepEqual(await numbers(), ['[5]', '[6]', '[8]', '[13]'], 'Existing records default to individual folded rows');
    await line(6).scrollIntoViewIfNeeded();
    await line(6).dblclick();
    assert.deepEqual(await numbers(), ['[5]', '[6]', '[7]', '[8]', '[13]']);
    assert.deepEqual(await page.locator('[data-fold-run="compact"]').evaluateAll(hints => hints.map(h => h.closest('.turn').id)), ['fold-4', 'fold-7']);
    assert.equal(await hint(12).count(), 0, 'A single collapsed turn needs no run control');
    assert.equal(await hint(5).count(), 0, 'Intermediate lines need no run control');
    const checkLead = button => {
      const track = button.parentElement, style = getComputedStyle(track,'::before');
      const length = button.getBoundingClientRect().left - track.getBoundingClientRect().left;
      const gap = track.getBoundingClientRect().left - track.previousElementSibling.getBoundingClientRect().right;
      return style.borderTopStyle === 'solid' && parseFloat(style.borderTopWidth) >= 1 &&
        Math.abs(length - 4 * parseFloat(getComputedStyle(track).fontSize)) <= 1 && gap >= 4 && gap <= 20;
    };
    for (const width of [440, 1550, 830]) {
      await page.setViewportSize({width,height:740});
      assert(await hint(4).evaluate(checkLead), 'Four-character divider precedes run action at width ' + width);
    }
    assert(Math.abs((await y('#fold-5-divider') - await y('#fold-4-divider')) -
      await line(4).evaluate(el => el.getBoundingClientRect().height)) < 1, 'Internal folded rows use one reading-line height');
    await line(4).scrollIntoViewIfNeeded();
    if (screenshot) await page.screenshot({ path: path.join(screenshot, 'individual.png') });

    await page.evaluate(() => { window.unchangedTurn = $('fold-3'); });
    await hint(7).click();
    assert.deepEqual(await numbers(), ['[5]', '[6]', '[7]', '[8]', '[13]'], 'Single click on either hint must not change the group');
    const tailY = await y('#fold-7-divider');
    await hint(7).dblclick();
    assert.deepEqual(await numbers(), ['[5 ~ 8]', '[13]']);
    assert.equal(await hint(4).textContent(), '展开');
    assert(await hint(4).evaluate(checkLead));
    assert(Math.abs(await y('#fold-4-divider') - tailY) < 2, 'Compacting from the last row keeps the action at its screen position');
    assert.equal(await page.evaluate(() => $('fold-3') === window.unchangedTurn), true, 'Compacting only replaces folded rows');
    if (screenshot) await page.screenshot({ path: path.join(screenshot, 'compact.png') });
    const compactY = await y('#fold-4-divider');
    await hint(4).click();
    assert.deepEqual(await numbers(), ['[5 ~ 8]', '[13]']);
    await hint(4).press('Enter');
    assert.deepEqual(await numbers(), ['[5]', '[6]', '[7]', '[8]', '[13]']);
    assert(Math.abs(await y('#fold-4-divider') - compactY) < 2, 'Uncompacting keeps the first divider at its screen position');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.foldRun), 'compact', 'Keyboard focus follows the new run control');
    assert.equal(await page.evaluate(() => $('fold-3') === window.unchangedTurn), true, 'Uncompacting only replaces folded rows');

    const peekY = await y('#fold-5-divider');
    await line(5).locator('.fold-number').dblclick();
    assert.deepEqual(await numbers(), ['[5]', '[7]', '[8]', '[13]'], 'Peeking opens only the chosen turn');
    assert.equal(await page.locator('#fold-5 .question').count(), 1);
    assert(Math.abs(await y('#fold-5-question') - peekY) < 2, 'The revealed question appears where its divider was');
    await line(5).press('Space');
    await hint(4).dblclick();
    assert.deepEqual(await numbers(), ['[5 ~ 8]', '[13]']);
    await line(4).locator('.fold-number').dblclick();
    assert.deepEqual(await numbers(), ['[5]', '[6]', '[7]', '[8]', '[13]'], 'The compact divider restores the individual folded rows');
    await hint(4).dblclick();
    await page.reload();
    await page.locator('[data-fold-run="expand"]').waitFor();
    assert.deepEqual(await numbers(), ['[5 ~ 8]', '[13]']);
    assert.equal(await page.locator('#composer').inputValue(), '不随折叠变化的未发送草稿');
    await page.locator('#fold-3 [data-open-turn="fold-6"]').click();
    assert.deepEqual((await states()).slice(4, 8), [[true, false], [true, false], [false, false], [true, false]],
      'Following a reference into a compacted group opens the target and restores its folded neighbors');
    assert.equal(await page.locator('#fold-6 .question').count(), 1);
    if (screenshot) await page.screenshot({ path: path.join(screenshot, 'peek.png') });
    await page.evaluate(() => {
      const turns = current().turns;
      for (const i of [4, 5, 6, 7, 8]) { turns[i].collapsed = true; delete turns[i].foldGroup; }
      turns[5].foldGroup = turns[6].foldGroup = 'existing';
      renderTurns();
    });
    assert.equal(await hint(7).count(), 1);
    await hint(5).dblclick();
    assert.deepEqual(await page.locator('[data-fold-run="compact"]').evaluateAll(hints => hints.map(h => h.closest('.turn').id)), ['fold-4', 'fold-8'],
      'Restoring a range next to individual rows moves the hints to the new outer ends');
    await page.evaluate(() => {
      for (const [i,t] of current().turns.entries()) {
        t.collapsed=[0,2,3,6,7,8].includes(i); delete t.foldGroup;
        if (i===2||i===3) t.foldGroup='upper';
        if (i===6||i===7) t.foldGroup='lower';
      }
      renderTurns();
    });
    for (const [i,expected] of [[1,'[2 ~ 4]'],[4,'[2 ~ 5]'],[5,'[2 ~ 8]']]) {
      await line(i).scrollIntoViewIfNeeded(); await line(i).press('Space');
      assert((await numbers()).includes(expected));
    }
    assert.deepEqual(await numbers(),['[1]','[2 ~ 8]','[9]']);
    await page.reload(); await hint(1).waitFor();
    assert.deepEqual(await numbers(),['[1]','[2 ~ 8]','[9]']);
    await hint(1).dblclick();
    assert.deepEqual(await numbers(),Array.from({length:9},(_,i)=>'['+(i+1)+']'));
    assert.deepEqual(errors, []);
    console.log('PASS: three folding states, nearby start/end hints, absorption and merge, double-click and keyboard, peek, viewport, persistence and navigation.');
  } finally {
    await browser.close();
  }
}

run().catch(error => { console.error(error); process.exitCode = 1; });

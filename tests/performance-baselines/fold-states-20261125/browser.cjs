'use strict';
const fs = require('node:fs'), path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

async function bench() {
  const [baseline, candidate, out] = process.argv.slice(2);
  if (!baseline || !candidate) throw new Error('Provide a 0.6.0 index.html and a candidate index.html.');
  const browser = await chromium.launch({ headless: true, executablePath: process.env.TEXEED_BROWSER_PATH });
  try {
    const viewport = { width: 800, height: 920 };
    async function open(name, index) {
      const page = await browser.newPage({ viewport });
      await page.goto(pathToFileURL(path.resolve(index)).href + '?test=1');
      await page.evaluate(async () => { await document.fonts.ready; });
      const session = await page.context().newCDPSession(page);
      return { name, page, session };
    }
    async function sample(target, operation) {
      const { name, page, session } = target;
      await page.evaluate(() => {
        current().turns = Array.from({ length: 12 }, (_, i) => ({
          id: 'fold-' + i, question: '请解释第 ' + (i + 1) + ' 个步骤。',
          answer: '先写下已知条件，再检查这个步骤的结论。', answeredAt: '2026-11-24T08:00:00Z',
          collapsed: i > 1 && i < 8 && i !== 5
        }));
        renderTurns();
        $('reader').scrollTop = 0;
      });
      if (operation === 'restore') {
        await page.evaluate(() => {
          const doubleClick = el => el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
          doubleClick($('fold-5-divider'));
          doubleClick($('fold-2-divider').querySelector('[data-fold-run="compact"]'));
        });
      }
      await session.send('HeapProfiler.collectGarbage');
      const before = await session.send('Runtime.getHeapUsage');
      const measured = await page.evaluate(async ({ name, operation }) => {
        const doubleClick = el => el.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }));
        const start = performance.now();
        if (operation === 'restore') doubleClick($('fold-2-divider').querySelector('[data-fold-run="expand"]'));
        else {
          doubleClick($('fold-5-divider'));
          if (operation === 'pack' && name === 'candidate')
            doubleClick($('fold-2-divider').querySelector('[data-fold-run="compact"]'));
        }
        const handlerMs = performance.now() - start;
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        const reader = $('reader').getBoundingClientRect();
        const controls = [...document.querySelectorAll('#thread button,#thread [role="button"]')].filter(el => {
          const box = el.getBoundingClientRect();
          return box.height > 0 && box.bottom > reader.top && box.top < reader.bottom;
        });
        return {
          handlerMs: +handlerMs.toFixed(2), twoFramesMs: +(performance.now() - start).toFixed(2),
          elements: document.querySelectorAll('#thread *').length, visibleControls: controls.length,
          labels: [...document.querySelectorAll('.fold-number')].map(el => el.textContent)
        };
      }, { name, operation });
      const after = await session.send('Runtime.getHeapUsage');
      return { ...measured, transientHeapBytes: after.usedSize - before.usedSize };
    }
    const sessions = await Promise.all([open('baseline', baseline), open('candidate', candidate)]);
    const data = { baseline: '42ba0da', chrome: browser.version(), viewport, fixture: '12 turns, collapse sixth turn in gap between two folded ranges', afterSingleFold: {}, compactResult: {}, restoreIndividuals: [] };
    for (const [key, operation] of [['afterSingleFold', 'fold'], ['compactResult', 'pack']]) {
      for (const session of sessions) {
        const expected = session.name === 'baseline' || operation === 'pack' ? ['[3 ~ 8]'] : ['[3]', '[4]', '[5]', '[6]', '[7]', '[8]'];
        const warm = await sample(session, operation);
        if (JSON.stringify(warm.labels) !== JSON.stringify(expected)) throw new Error('Unexpected fold rows ' + warm.labels);
        data[key][session.name] = [];
      }
      for (let i = 0; i < 7; i++)
        for (const target of i % 2 === 0 ? sessions : [...sessions].reverse())
          data[key][target.name].push(await sample(target, operation));
    }
    await sample(sessions[1], 'restore');
    for (let i = 0; i < 7; i++) data.restoreIndividuals.push(await sample(sessions[1], 'restore'));
    if (out) fs.writeFileSync(out, JSON.stringify(data, null, 2) + '\n');
    const median = (samples, key) => [...samples].sort((a, b) => a[key] - b[key])[Math.floor(samples.length / 2)][key];
    const report = samples => Object.fromEntries(['handlerMs', 'twoFramesMs', 'elements', 'visibleControls', 'transientHeapBytes'].map(key => [key, median(samples, key)]));
    for (const key of ['afterSingleFold','compactResult'])
      for (const session of sessions) console.log(key, session.name, report(data[key][session.name]));
    console.log('restoreIndividuals', report(data.restoreIndividuals));
  } finally { await browser.close(); }
}

bench().catch(error => { console.error(error); process.exitCode = 1; });

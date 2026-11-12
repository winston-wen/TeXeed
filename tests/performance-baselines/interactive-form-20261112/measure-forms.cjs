'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..');
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 820 } });
    await page.goto(pathToFileURL(path.join(root, 'prototypes/ai-chat-v02/branching.html')).href);
    await page.evaluate(() => { branchDemo.setState(branchDemo.makeFixture(120, 0)); document.querySelector('#demo-forms').click(); });
    const cdp = await page.context().newCDPSession(page);
    async function sample(work) {
      await cdp.send('HeapProfiler.startSampling', { samplingInterval: 1024, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
      const timing = await page.evaluate(async work => {
        const start = performance.now();
        if (work) { const input = document.querySelector('.exercise input[type=text]'); input.value = input.value === '83' ? '84' : '83'; input.dispatchEvent(new Event('input', { bubbles: true })); }
        const sync = performance.now() - start;
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))); await new Promise(resolve => setTimeout(resolve, 220));
        return { sync, elapsed: performance.now() - start };
      }, work);
      const { profile } = await cdp.send('HeapProfiler.stopSampling');
      return { ...timing, bytes: profile.samples.reduce((sum, item) => sum + item.size, 0) };
    }
    for (let i = 0; i < 2; i++) { await sample(false); await sample(true); }
    const pairs = [];
    for (let i = 0; i < 7; i++) { const empty = await sample(false), work = await sample(true); pairs.push({ empty, work, correctedMs: work.elapsed - empty.elapsed, correctedBytes: work.bytes - empty.bytes }); }
    const median = numbers => [...numbers].sort((a, b) => a - b)[Math.floor(numbers.length / 2)];
    const counts = await page.evaluate(() => ({ nodes: document.querySelectorAll('*').length, visibleControls: [...document.querySelectorAll('button,input,textarea,select')].filter(el => { const r = el.getBoundingClientRect(); return r.width && r.height && r.bottom > 0 && r.top < innerHeight && r.right > 0 && r.left < innerWidth; }).length }));
    const resources = Object.fromEntries(['branching.html', 'branching.css', 'branching.js'].map(file => [file, crypto.createHash('sha256').update(fs.readFileSync(path.join(root, 'prototypes/ai-chat-v02', file))).digest('hex')]));
    const result = { browser: browser.version(), fixture: '120 turns plus one form, input event, two warmups, seven pairs, two frames and 220 ms', resources, pairs, medianSync: median(pairs.map(p => p.work.sync)), medianCorrected: median(pairs.map(p => p.correctedMs)), medianBytes: median(pairs.map(p => p.correctedBytes)), counts };
    fs.writeFileSync(process.argv[2] || path.join(__dirname, 'form-input.json'), JSON.stringify(result, null, 2));
    console.log(JSON.stringify({ ...result, pairs: undefined }));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

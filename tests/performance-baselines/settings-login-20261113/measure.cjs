'use strict';
const fs = require('node:fs'), path = require('node:path'), cp = require('node:child_process'), os = require('node:os'), crypto = require('node:crypto');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..'), work = path.join(root, 'artifacts/settings-login-perf');
fs.mkdirSync(work, { recursive: true });
const before = path.join(work, 'before'), archive = path.join(work, 'before.tar'); fs.mkdirSync(before, { recursive: true });
const commit = cp.execFileSync('git', ['rev-parse', 'HEAD'], { cwd: root, encoding: 'utf8' }).trim();
fs.writeFileSync(archive, cp.execFileSync('git', ['archive', '--format=tar', commit, 'prototypes/ai-chat-v02'], { cwd: root, maxBuffer: 100 * 1024 * 1024 }));
cp.execFileSync('tar', ['-xf', archive, '-C', before]);
const versions = { before: path.join(before, 'prototypes/ai-chat-v02'), after: path.join(root, 'prototypes/ai-chat-v02') };
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const sha = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  const measurements = [];
  try {
    for (const operation of ['open', 'effort', 'login']) for (const mode of ['before', 'after']) {
      const page = await browser.newPage({ viewport: { width: 849, height: 932 } });
      const cdp = await page.context().newCDPSession(page);
      async function sample(workItem) {
        await page.goto(pathToFileURL(path.join(versions[mode], 'index.html')).href + '?test=1');
        await page.evaluate(async op => { await document.fonts.ready; if (op !== 'open') document.getElementById('ai-settings').click(); await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); }, operation);
        await cdp.send('HeapProfiler.startSampling', { samplingInterval: 1024, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
        const timing = await page.evaluate(async ({ workItem, operation }) => {
          const start = performance.now();
          if (workItem) {
            if (operation === 'open') document.getElementById('ai-settings').click();
            if (operation === 'effort') { const effort = document.getElementById('settings-effort'); effort.value = '中'; effort.dispatchEvent(new Event('change', { bubbles: true })); }
            if (operation === 'login') document.querySelector('[data-setting="login"]').click();
          }
          const sync = performance.now() - start;
          await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
          await new Promise(r => setTimeout(r, operation === 'login' ? 1800 : 220));
          return { sync, elapsed: performance.now() - start };
        }, { workItem, operation });
        const { profile } = await cdp.send('HeapProfiler.stopSampling');
        return { ...timing, bytes: profile.samples.reduce((total, sample) => total + sample.size, 0) };
      }
      for (let i = 0; i < 2; i++) { await sample(false); await sample(true); }
      const pairs = [];
      for (let i = 0; i < 7; i++) { const empty = await sample(false), work = await sample(true); pairs.push({ empty, work, correctedMs: work.elapsed - empty.elapsed, correctedBytes: work.bytes - empty.bytes }); }
      const counts = await page.evaluate(() => ({ nodes: document.querySelectorAll('*').length, visibleControls: [...document.querySelectorAll('button,input,textarea,select')].filter(el => { const r=el.getBoundingClientRect(), s=getComputedStyle(el); return r.width>0 && r.height>0 && r.bottom>0 && r.top<innerHeight && r.right>0 && r.left<innerWidth && s.visibility!=='hidden' && s.display!=='none'; }).length }));
      const result = { operation, mode, pairs, medianSync: median(pairs.map(p => p.work.sync)), medianCorrected: median(pairs.map(p => p.correctedMs)), medianBytes: median(pairs.map(p => p.correctedBytes)), counts };
      measurements.push(result); console.log(JSON.stringify({ ...result, pairs: undefined })); await page.close();
    }
    fs.writeFileSync(process.argv[2] || path.join(__dirname, 'results.json'), JSON.stringify({ commit, browser: browser.version(), node: process.version, system: os.release(), viewport: '849x932', method: 'two warmups, seven paired samples, two frames plus 220 ms; login waits 1800 ms to include the simulated completion', resources: Object.fromEntries(Object.entries(versions).map(([version, dir]) => [version, sha(path.join(dir, 'index.html'))])), fixture: sha(__filename), measurements }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

'use strict';
const fs = require('fs'), path = require('path'), cp = require('child_process'), crypto = require('crypto'), os = require('os'), { pathToFileURL } = require('url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = process.cwd(), work = path.resolve('artifacts/annotation-popup-perf'); fs.mkdirSync(work, { recursive: true });
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
function archived(commit) {
  const dir = path.join(work, commit); fs.mkdirSync(dir, { recursive: true });
  const tar = path.join(work, commit + '.tar');
  fs.writeFileSync(tar, cp.execFileSync('git', ['archive', '--format=tar', commit, 'prototypes/ai-chat-v02'], { cwd: root, maxBuffer: 100 * 1024 * 1024 }));
  cp.execFileSync('tar', ['-xf', tar, '-C', dir]);
  return path.join(dir, 'prototypes/ai-chat-v02/index.html');
}
const files = { historical: archived('8990572'), before: archived('dddee8f'), after: path.resolve('prototypes/ai-chat-v02/index.html') };
const median = a => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)];
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  const rows = [];
  try {
    for (const mode of ['historical', 'before', 'after']) for (const op of ['open', 'input', 'finish']) {
      const page = await browser.newPage({ viewport: { width: 1000, height: 820 } });
      await page.goto(pathToFileURL(files[mode]).href + '?test=1');
      await page.waitForSelector('.answer .passage');
      await page.evaluate(() => {
        const block = document.querySelector('.answer .passage').id;
        current().entries = [{ id: 'perf', number: 1, title: 'AI 回答批注', question: '说明文字', selected: true, state: '待提交', sources: [], pdf: [], block, quote: '参考选区' }];
        current().next = 2; current().draft = '保留主草稿'; $('composer').value = current().draft;
        renderEntries();
      });
      const cdp = await page.context().newCDPSession(page);
      async function prepare() {
        await page.evaluate(op => {
          commitAnnotation();
          current().entries[0].question = '说明文字';
          if (op !== 'open') openEditor('perf');
        }, op);
        await page.evaluate(() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))));
      }
      async function sample(kind) {
        await prepare();
        await cdp.send('HeapProfiler.startSampling', { samplingInterval: 1024, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
        const measure = await page.evaluate(async ({ op, kind }) => {
          const t = performance.now();
          if (kind === 'work') {
            if (op === 'open') openEditor('perf');
            if (op === 'input') { $('annotation-input').value += '甲'; $('annotation-input').dispatchEvent(new Event('input', { bubbles: true })); }
            if (op === 'finish') $('send-annotation').click();
          }
          const sync = performance.now() - t;
          await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
          await new Promise(r => setTimeout(r, 220));
          return { sync, elapsed: performance.now() - t };
        }, { op, kind });
        const profile = (await cdp.send('HeapProfiler.stopSampling')).profile;
        return { ...measure, bytes: profile.samples.reduce((sum, item) => sum + item.size, 0) };
      }
      for (let i = 0; i < 2; i++) { await sample('empty'); await sample('work'); }
      const pairs = [];
      for (let i = 0; i < 7; i++) {
        const empty = await sample('empty'), workSample = await sample('work');
        pairs.push({ empty, work: workSample, correctedMs: workSample.elapsed - empty.elapsed, correctedBytes: workSample.bytes - empty.bytes });
      }
      await page.evaluate(() => openEditor('perf'));
      const counts = await page.evaluate(() => ({ nodes: document.querySelectorAll('*').length, controls: [...document.querySelectorAll('button,input,textarea,select,summary')].filter(e => {
        const r = e.getBoundingClientRect(), s = getComputedStyle(e); return s.display !== 'none' && s.visibility !== 'hidden' && r.width > 0 && r.height > 0 && r.top < innerHeight && r.bottom > 0;
      }).length }));
      rows.push({ mode, op, pairs, medianSync: median(pairs.map(x => x.work.sync)), medianCorrectedMs: median(pairs.map(x => x.correctedMs)), medianBytes: median(pairs.map(x => x.correctedBytes)), counts });
      console.log(JSON.stringify({ ...rows.at(-1), pairs: undefined }));
      await page.close();
    }
    fs.writeFileSync(path.join(work, 'results.json'), JSON.stringify({ environment: { browser: browser.version(), platform: os.platform(), release: os.release(), cpu: os.cpus()[0].model, viewport: '1000x820' }, hashes: Object.fromEntries(Object.entries(files).map(([name, file]) => [name, hash(file)])), method: 'One existing example conversation and one annotation; 2 warmups, 7 paired work/empty samples; two frames plus 220ms; CDP allocation sampling 1024 bytes.', rows }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

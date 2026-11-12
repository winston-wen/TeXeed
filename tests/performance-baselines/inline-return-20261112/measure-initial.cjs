'use strict';
const fs = require('fs'), path = require('path'), os = require('os'), crypto = require('crypto'), cp = require('child_process'), { pathToFileURL } = require('url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..'), work = path.join(root, 'artifacts/inline-return-perf'); fs.mkdirSync(work, { recursive: true });
const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
const median = a => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)];
function archive(commit) {
  const dir = path.join(work, commit); fs.mkdirSync(dir, { recursive: true });
  const tar = path.join(work, commit + '.tar'); fs.writeFileSync(tar, cp.execFileSync('git', ['archive', '--format=tar', commit, 'prototypes/ai-chat-v02'], { cwd: root, maxBuffer: 100 * 1024 * 1024 }));
  cp.execFileSync('tar', ['-xf', tar, '-C', dir]); return path.join(dir, 'prototypes/ai-chat-v02');
}
const dirs = { historic: archive('cf7cb23'), before: archive('dddee8f'), after: path.join(root, 'prototypes/ai-chat-v02') };
(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  const measurements = [];
  try {
    for (const mode of ['historic', 'before', 'after']) for (const operation of ['return-new', 'return-legacy', 'toggle']) {
      const page = await browser.newPage({ viewport: { width: 1000, height: 820 } });
      await page.goto(pathToFileURL(path.join(dirs[mode], 'branching.html')).href + '?test=1');
      await page.evaluate(() => document.fonts.ready);
      await page.evaluate(operation => {
        const f = branchDemo.makeFixture(120, 60), root = f.sessions.find(s => s.id === 'root');
        root.turns[0].answer.unshift({ id: 'test-heading', kind: 'heading', level: 1, text: '章节' });
        root.turns[0].collapsedSections = { 'test-heading': true };
        for (const child of f.sessions.filter(s => s.parent)) {
          const block = root.turns.flatMap(t => t.answer).find(b => b.id === child.source.blockId);
          child.source.textOffset = 50; child.source.quote = block.text.slice(50, 55);
          if (operation !== 'return-legacy') { child.source.endBlockId = block.id; child.source.endTextOffset = 55; }
          child.turns = [{ id: child.id + '-turn', question: '示例追问', answer: [{ id: child.id + '-answer', kind: 'paragraph', text: '示例回答内容。' }], demo: true }];
        }
        f.active = operation === 'toggle' ? 'root' : 'fixture-branch-0'; f.selected = f.active;
        window.fixture = f;
      }, operation);
      const cdp = await page.context().newCDPSession(page);
      async function prepare() {
        await page.evaluate(() => branchDemo.setState(window.fixture));
        await page.evaluate(async () => { await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); await new Promise(r => setTimeout(r, 220)); });
      }
      async function sample(kind) {
        await prepare(); await cdp.send('HeapProfiler.startSampling', { samplingInterval: 1024, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
        const timing = await page.evaluate(async ({ kind, operation }) => {
          const start = performance.now();
          if (kind === 'work') document.querySelector(operation === 'toggle' ? '[data-heading-id="test-heading"]' : '#back-source').click();
          const sync = performance.now() - start;
          await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r))); await new Promise(r => setTimeout(r, 220));
          return { sync, elapsed: performance.now() - start };
        }, { kind, operation });
        const profile = (await cdp.send('HeapProfiler.stopSampling')).profile;
        return { ...timing, bytes: profile.samples.reduce((sum, item) => sum + item.size, 0) };
      }
      for (let i = 0; i < 2; i++) { await sample('empty'); await sample('work'); }
      const pairs = []; for (let i = 0; i < 7; i++) { const empty = await sample('empty'), task = await sample('work'); pairs.push({ empty, work: task, correctedMs: task.elapsed - empty.elapsed, correctedBytes: task.bytes - empty.bytes }); }
      const counts = await page.evaluate(() => {
        const visible = el => { const b = el.getBoundingClientRect(); let left = Math.max(0, b.left), right = Math.min(innerWidth, b.right), top = Math.max(0, b.top), bottom = Math.min(innerHeight, b.bottom); for (let p = el; p; p = p.parentElement) { const s = getComputedStyle(p); if (s.display === 'none' || s.visibility === 'hidden') return false; if (p !== el && /(hidden|clip|scroll|auto)/.test(s.overflow + s.overflowX + s.overflowY)) { const r = p.getBoundingClientRect(); left = Math.max(left, r.left); right = Math.min(right, r.right); top = Math.max(top, r.top); bottom = Math.min(bottom, r.bottom); } } return right > left && bottom > top; };
        return { nodes: document.querySelectorAll('*').length, visibleControls: [...document.querySelectorAll('button,input,textarea,select,summary')].filter(visible).length };
      });
      const row = { mode, operation, pairs, medianSync: median(pairs.map(p => p.work.sync)), medianCorrected: median(pairs.map(p => p.correctedMs)), medianBytes: median(pairs.map(p => p.correctedBytes)), counts };
      measurements.push(row); console.log(JSON.stringify({ ...row, pairs: undefined })); await page.close();
    }
    fs.writeFileSync(path.join(work, 'results.json'), JSON.stringify({ environment: { browser: browser.version(), node: process.version, platform: os.platform(), release: os.release(), cpu: os.cpus()[0].model, viewport: '1000x820' }, resources: Object.fromEntries(Object.entries(dirs).map(([key, dir]) => [key, Object.fromEntries(['branching.html', 'branching.css', 'branching.js'].map(file => [file, hash(path.join(dir, file))]))])), fixtureHash: hash(__filename), method: '120 turns, 60 branches, two warmups and seven paired samples, two frames plus 220ms, CDP allocation sampling 1024 bytes.', measurements }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

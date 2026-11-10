'use strict';
const fs = require('node:fs'), path = require('node:path'), { pathToFileURL, fileURLToPath } = require('node:url');
const cp = require('node:child_process'), crypto = require('node:crypto'), os = require('node:os');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..'), prototype = path.join(root, 'prototypes/ai-chat-v02');
const baseline = cp.execFileSync('git', ['rev-parse', process.env.TEXEED_PERF_BASELINE || 'a0b50ba9d2944706c6c6a4cd2905d1ef34c7e8b0'], { cwd: root, encoding: 'utf8' }).trim();
const historic = cp.execFileSync('git', ['rev-parse', '7af94c2'], { cwd: root, encoding: 'utf8' }).trim();
const digest = data => crypto.createHash('sha256').update(data).digest('hex');
const rounds = Math.max(1, Number(process.env.TEXEED_PERF_ROUNDS || 3));
const resources = { historic: {}, before: {}, after: {} };
fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
const temp = fs.mkdtempSync(path.join(root, 'artifacts', 'branching-perf-'));
const dirs = { after: prototype };
for (const [mode, rev] of [['historic', historic], ['before', baseline]]) {
  const out = path.join(temp, mode); fs.mkdirSync(out);
  const archive = cp.execFileSync('git', ['archive', '--format=tar', rev, 'prototypes/ai-chat-v02'], { cwd: root, maxBuffer: 50 * 1024 * 1024 });
  const archivePath = path.join(out, 'snapshot.tar'); fs.writeFileSync(archivePath, archive);
  cp.execFileSync('tar', ['-xf', archivePath, '-C', out]); dirs[mode] = path.join(out, 'prototypes/ai-chat-v02');
}
const median = values => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const frames = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  const result = { schema: 'branching-reading-20261110-v1', baseline, historic, fixtureHash: digest(fs.readFileSync(__filename)), resources,
    environment: { platform: os.platform(), release: os.release(), architecture: os.arch(), cpu: os.cpus()[0]?.model, node: process.version, browser: browser.version(), viewport: { width: 1000, height: 820 } },
    method: { turns: 120, rounds, warmups: 2, samples: 7, frames: 2, delayedWaitMs: 220, heapSamplingInterval: 1024,
      note: 'Paired empty operation includes the same two frames and delayed wait. V8 sampled allocation is an estimate, including harness and scheduling; it is not exact total allocation. Comparison is only for equivalent synthetic reading interactions, not identical feature coverage.' }, measurements: [] };
  try {
    for (let round = 0; round < rounds; round++) for (const mode of (round % 2 ? ['after', 'before', 'historic'] : ['historic', 'before', 'after'])) {
      const page = await browser.newPage({ viewport: { width: 1000, height: 820 } }), errors = [];
      page.on('pageerror', e => errors.push(e.message));
      page.on('request', req => { if (!req.url().startsWith('file:')) return; try { const file = fileURLToPath(new URL(req.url())); if (fs.statSync(file).isFile()) resources[mode][path.relative(dirs[mode], file).replaceAll('\\', '/')] = digest(fs.readFileSync(file)); } catch {} });
      await page.goto(pathToFileURL(path.join(dirs[mode], mode === 'after' ? 'branching.html' : 'index.html')).href + '?test=1');
      await page.evaluate(() => document.fonts.ready);
      await page.evaluate(mode => {
        const turns = Array.from({ length: 120 }, (_, i) => ({ id: 'synthetic-' + i, question: '合成提问 ' + i, answer: '合成段落 ' + i + ' 用于书签标记和版面性能测量。', sections: {} }));
        if (mode === 'after') {
          const fixture = window.branchDemo.makeFixture(0, 0);
          fixture.sessions[0].turns = turns.map(t => ({ id: t.id, question: t.question, answer: [{ id: t.id + '-block', kind: 'paragraph', text: t.answer }], demo: true }));
          fixture.sessions[0].draft = '保留性能草稿';
          window.branchDemo.setState(fixture);
        }
        else { const c = current(); c.draft = '保留性能草稿'; c.turns = turns; c.bookmarks = []; if ('reviewPinnedTurns' in state) { state.reviewPinnedTurns = []; state.reviewTemporaryTurns = []; state.reviewAutoSeen = {}; } renderConversation(); }
      }, mode);
      for (const operation of ['input', 'scroll']) {
        const client = await page.context().newCDPSession(page);
        async function sample(kind) {
          return await page.evaluate(async ({ kind, operation }) => {
            const begin = performance.now();
            if (kind === 'work') {
              if (operation === 'input') { const box = document.querySelector('#composer'); box.value = box.value === '性能测量甲' ? '性能测量乙' : '性能测量甲'; box.dispatchEvent(new Event('input', { bubbles: true })); }
              else { const reader = document.querySelector('#reader'); const y = reader.scrollTop < 100 ? 600 : 0; reader.scrollTop = y; }
            }
            const sync = performance.now() - begin;
            await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
            await new Promise(r => setTimeout(r, 220));
            return { sync, elapsed: performance.now() - begin };
          }, { kind, operation });
        }
        for (let i = 0; i < 2; i++) { await sample('work'); await sample('empty'); }
        const pair = [];
        for (let i = 0; i < 7; i++) { const empty = await sample('empty'), work = await sample('work'); pair.push({ empty, work, correctedElapsed: work.elapsed - empty.elapsed }); }
        async function sampledBytes(kind) {
          await client.send('HeapProfiler.startSampling', { samplingInterval: 1024, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true });
          for (let i = 0; i < 7; i++) await sample(kind);
          return (await client.send('HeapProfiler.stopSampling')).profile.samples.reduce((sum, s) => sum + s.size, 0);
        }
        const idleBytes = await sampledBytes('empty'), workBytes = await sampledBytes('work');
        const allocatedBytes = (workBytes - idleBytes) / 7;
        const counts = await page.evaluate(() => {
          const controls = [...document.querySelectorAll('button,input,textarea,select,summary')];
          return { nodes: document.querySelectorAll('*').length, visibleControls: controls.filter(el => { const b = el.getBoundingClientRect(); const s = getComputedStyle(el); return b.width > 0 && b.height > 0 && b.bottom > 0 && b.right > 0 && b.top < innerHeight && b.left < innerWidth && s.visibility !== 'hidden'; }).length };
        });
        result.measurements.push({ round, mode, operation, pair, medianSync: median(pair.map(p => p.work.sync)), medianElapsed: median(pair.map(p => p.work.elapsed)), medianCorrected: median(pair.map(p => p.correctedElapsed)), idleBytes, workBytes, allocatedBytes, counts });
        console.log(JSON.stringify({ round, mode, operation, medianSync: result.measurements.at(-1).medianSync, medianCorrected: result.measurements.at(-1).medianCorrected, allocatedBytes, counts }));
        if (errors.length) throw Error(mode + ': ' + errors.join('; '));
      }
      await page.close();
    }
    const dest = process.argv[2] || path.join(__dirname, 'samples.json'); fs.writeFileSync(dest, JSON.stringify(result, null, 2) + '\n');
    console.log(JSON.stringify(result.measurements.map(({ round, mode, operation, medianSync, medianCorrected, allocatedBytes, counts }) => ({ round, mode, operation, medianSync, medianCorrected, allocatedBytes, counts }))));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

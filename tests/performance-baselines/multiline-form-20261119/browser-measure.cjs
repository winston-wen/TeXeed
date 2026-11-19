'use strict';
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

const root = process.cwd();
const output = path.resolve(process.argv[2] || 'artifacts/prototype-multiline-20261119/benchmark');
const prototype = 'prototypes/ai-chat-v02';
const revisions = { history: 'fb4ff2a', before: '4dd276b', after: null };
const files = [
  'branching.html', 'branching.css', 'branching.js', 'reading-fonts.css', 'quote-summary.js',
  'scrollbar-activity.js', 'fonts/IBMPlexSans-Regular.otf', 'fonts/IBMPlexSans-SemiBold.woff2',
  'vendor/katex/katex.min.js', 'vendor/katex/katex.min.css'
];
const git = (args, encoding) => execFileSync('git', args, { cwd: root, encoding, maxBuffer: 15_000_000 });
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const hashTree = (dir, assets) => Object.fromEntries(assets.flatMap(asset => {
  const file = path.join(dir, asset);
  return fs.existsSync(file) ? [[asset, hash(fs.readFileSync(file))]] : [];
}));
const median = values => values.toSorted((a, b) => a - b)[Math.floor(values.length / 2)];
const windows = { wide: { width: 1000, height: 1000 }, narrow: { width: 390, height: 1000 } };
const rounds = 3, iterations = 9, warmup = 2;

function stage(revision, name) {
  if (!revision) return path.join(root, prototype);
  const dir = path.join(output, name), commit = git(['rev-parse', revision], 'utf8').trim();
  const assets = new Set(git(['ls-tree', '-r', '--name-only', commit, '--', prototype], 'utf8').trim().split('\n'));
  for (const file of files) {
    const relative = prototype + '/' + file;
    if (!assets.has(relative)) continue;
    const destination = path.join(dir, file);
    fs.mkdirSync(path.dirname(destination), { recursive: true });
    fs.writeFileSync(destination, git(['show', commit + ':' + relative]));
  }
  for (const directory of ['vendor/katex/fonts', 'fonts/local']) {
    const source = path.join(root, prototype, directory);
    if (fs.existsSync(source)) fs.cpSync(source, path.join(dir, directory), { recursive: true });
  }
  return dir;
}

async function seed(page) {
  await page.evaluate(() => {
    const turns = Array.from({ length: 48 }, (_, i) => ({
      id: 'perf-turn-' + i,
      question: '独立的合成性能夹具 ' + (i + 1),
      answer: [
        { id: 'start-' + i, kind: 'paragraph', text: '用于定位的段落。'.repeat(8) },
        ...(i === 12 ? [{ id: 'exercise', kind: 'exercise', text: '' }] : []),
        { id: 'end-' + i, kind: 'paragraph', text: '用于滚动的普通段落。'.repeat(12) }
      ],
      demo: true
    }));
    branchDemo.seedPerf(turns);
    window.multilineFixture = document.querySelector('.exercise textarea');
  });
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
}

async function measure(page, text, keepPosition) {
  return page.evaluate(async ({ text, keepPosition }) => {
    const note = window.multilineFixture, reader = document.getElementById('reader');
    if (keepPosition) {
      reader.scrollTop += note.getBoundingClientRect().top - reader.getBoundingClientRect().top - 90;
      note.focus({ preventScroll: true });
    }
    const before = note.getBoundingClientRect();
    const start = performance.now();
    if (text !== null) {
      note.value = text;
      note.dispatchEvent(new Event('input', { bubbles: true }));
    }
    note.getBoundingClientRect();
    const feedbackMs = performance.now() - start;
    await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const after = note.getBoundingClientRect();
    const completeMs = performance.now() - start;
    const clip = reader.getBoundingClientRect();
    const visibleControls = [...reader.querySelectorAll('input,button,textarea')].filter(control => {
      const box = control.getBoundingClientRect();
      return box.width > 0 && box.height > 0 && box.bottom > clip.top && box.top < clip.bottom &&
        box.right > clip.left && box.left < clip.right;
    }).length;
    return { feedbackMs, completeMs, height: after.height, positionDelta: after.top - before.top, innerRange: note.scrollHeight - note.clientHeight, visibleControls };
  }, { text, keepPosition });
}

function heapBytes(node) {
  return node.selfSize + node.children.reduce((total, child) => total + heapBytes(child), 0);
}

(async () => {
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
  const result = {
    browser: browser.version(), node: process.version, platform: process.platform, windows, rounds, iterations, warmup,
    revisions: Object.fromEntries(Object.entries(revisions).map(([name, rev]) => [name, rev && git(['rev-parse', rev], 'utf8').trim()])),
    resources: {}, samples: {}
  };
  const dirs = Object.fromEntries(Object.entries(revisions).map(([name, rev]) => [name, stage(rev, name)]));
  const wrapped = '阅读区长文字中也会自动换行，含 English 以覆盖混排。'.repeat(12);
  const tall = Array.from({ length: 12 }, (_, i) => '第 ' + (i + 1) + ' 行混排 English。').join('\n');
  for (const [name, dir] of Object.entries(dirs)) result.resources[name] = hashTree(dir, files);
  try {
    for (let round = 0; round < rounds; round++) {
      const order = round % 2 === 0 ? Object.keys(dirs) : Object.keys(dirs).reverse();
      for (const name of order) {
        const context = await browser.newContext({ viewport: windows.wide });
        const page = await context.newPage();
        const client = await context.newCDPSession(page);
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.goto(pathToFileURL(path.join(dirs[name], 'branching.html')).href + '?test=1');
        await seed(page);
        const sample = [];
        for (const viewport of ['wide', 'narrow']) {
          await page.setViewportSize(windows[viewport]);
          const heapStart = { samplingInterval: 256, includeObjectsCollectedByMajorGC: true, includeObjectsCollectedByMinorGC: true };
          await client.send('HeapProfiler.startSampling', heapStart);
          for (let i = 0; i < iterations; i++) {
            const idle = await measure(page, null, true);
            const growth = await measure(page, viewport === 'wide' ? tall : wrapped, true);
            const shrinkage = await measure(page, '短答案 English', true);
            if (i >= warmup) sample.push({ viewport, idle, growth, shrinkage });
          }
          const bytes = heapBytes((await client.send('HeapProfiler.stopSampling')).profile.head);
          sample.push({ viewport, sampledJsBytes: bytes });
        }
        assert.deepEqual(errors, [], name + ' page errors');
        (result.samples[name] ||= []).push(sample);
        await context.close();
      }
    }
    result.summary = Object.fromEntries(Object.entries(result.samples).map(([name, measured]) => [name,
      Object.fromEntries(Object.keys(windows).map(viewport => {
        const all = measured.flat().filter(s => s.viewport === viewport);
        const actions = all.filter(s => s.idle);
        return [viewport, {
          sampledJsBytes: median(all.filter(s => s.sampledJsBytes !== undefined).map(s => s.sampledJsBytes)),
          ...Object.fromEntries(['growth', 'shrinkage'].map(action => [action, {
            feedbackMs: median(actions.map(s => s[action].feedbackMs)),
            correctedMs: median(actions.map(s => s[action].completeMs - s.idle.completeMs)),
            visibleControls: median(actions.map(s => s[action].visibleControls)),
            height: median(actions.map(s => s[action].height)),
            maxAbsPositionDelta: Math.max(...actions.map(s => Math.abs(s[action].positionDelta))),
            innerRange: median(actions.map(s => s[action].innerRange))
          }]))
        }];
      }))
    ]));
    fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(result, null, 2) + '\n');
    console.log(JSON.stringify(result.summary, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

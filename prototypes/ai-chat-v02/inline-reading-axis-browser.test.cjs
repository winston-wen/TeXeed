'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP inline reading axis: Playwright unavailable'); process.exit(0); }
const { PNG } = require(require.resolve('pngjs', { paths: [process.env.TEXEED_PLAYWRIGHT_PATH || __dirname] }));

const scale = 3;
const output = path.resolve(process.env.TEXEED_READING_AXIS_OUTPUT || 'artifacts/prototype-reading-axis');

function center(png, rect) {
  let top = Infinity, bottom = -Infinity;
  for (let y = Math.max(0, Math.floor(rect.top * scale)); y < Math.min(png.height, Math.ceil(rect.bottom * scale)); y++) {
    for (let x = Math.max(0, Math.floor(rect.left * scale)); x < Math.min(png.width, Math.ceil(rect.right * scale)); x++) {
      const i = (png.width * y + x) * 4;
      if (png.data[i] < 165 || png.data[i + 1] < 165 || png.data[i + 2] < 165) continue;
      top = Math.min(top, y);
      bottom = Math.max(bottom, y);
    }
  }
  assert(Number.isFinite(top), 'Pixel probe did not contain painted text or a symbol');
  return (top + bottom + 1) / (2 * scale);
}

async function inspect(page, filename, probes) {
  const rectangles = await page.evaluate(probes => probes.map(({ selector, glyphs, sample = '恢复答案' }) => {
    const block = document.querySelector(selector);
    const text = [...block.childNodes].find(node => node.nodeType === Node.TEXT_NODE && node.nodeValue.includes(sample));
    const range = document.createRange(), start = text.nodeValue.indexOf(sample);
    range.setStart(text, start);
    range.setEnd(text, start + sample.length);
    return {
      selector, text: range.getBoundingClientRect().toJSON(),
      glyphs: glyphs.map(glyph => block.querySelector(glyph).getBoundingClientRect().toJSON())
    };
  }), probes);
  const png = PNG.sync.read(await page.screenshot({ path: path.join(output, filename) }));
  for (const probe of rectangles) {
    const textY = center(png, probe.text);
    const differences = probe.glyphs.map(glyph => center(png, glyph) - textY);
    console.log(filename, probe.selector, 'glyph-center deltas in CSS pixels:', differences);
    assert(differences.every(delta => Math.abs(delta) <= 1.5), `${probe.selector} failed actual ink alignment: ${differences}`);
  }
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {})
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1020, height: 900 }, deviceScaleFactor: scale });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const local = name => pathToFileURL(path.join(__dirname, name)).href + '?test=1';
    await page.goto(local('index.html'));
    await page.evaluate(() => document.fonts.ready);
    const bundled = page.locator('#demo-1-p4 .math[data-tex="x=2"]');
    await bundled.scrollIntoViewIfNeeded();
    assert.equal(await bundled.evaluate(el => getComputedStyle(el).fontSize), '18px');
    await inspect(page, 'reading-axis-bundled-svg.png', [
      { selector: '#demo-1-p4 p', sample: '在', glyphs: ['.math[data-tex="x=2"] svg use[*|href$="-61"]'] }
    ]);
    await page.evaluate(async () => {
      await document.fonts.ready;
      const heading = '恢复答案 $x=2$ 恢复答案', paragraph = '恢复答案 $\\frac{1}{2}$ 恢复答案';
      current().turns = [
        { id: 'axis', question: '公式与文字中轴', answer: '# ' + heading + '\n\n' + paragraph + '\n\n$$x=2$$', sections: {} },
        { id: 'follow-up', question: '同会话追问', answer: '答复', sections: {}, request: { annotations: [
          { block: 'axis-p0', endTextOffset: '恢复答案  恢复答案'.length },
          { block: 'axis-p1', endTextOffset: '恢复答案  恢复答案'.length }
        ] } }
      ];
      renderTurns();
      await document.fonts.ready;
      window.alignReadingMedia(document.querySelector('#thread'));
      document.querySelector('#reader').scrollTop = 0;
    });
    const fontSizes = await page.evaluate(() => ({
      inline: getComputedStyle(document.querySelector('#axis-p0 .math .katex')).fontSize,
      display: getComputedStyle(document.querySelector('#axis-p2 .math .katex')).fontSize
    }));
    assert.deepEqual(fontSizes, { inline: '18px', display: '20.7px' });
    await inspect(page, 'reading-axis-dark.png', [
      { selector: '#axis-p0 .heading-label', glyphs: ['.katex-html .mrel', '.reading-bubble'] },
      { selector: '#axis-p1 p', glyphs: ['.katex-html .frac-line', '.reading-bubble'] }
    ]);
    await page.evaluate(() => {
      const theme = { '--bg': '#fffdf9', '--side': '#f1ede7', '--panel': '#ebe5dc', '--question': '#eee7df',
        '--ink': '#211c1b', '--muted': '#605957', '--quiet': '#776d68', '--line': '#ddd4cb', '--guide': '#b8a99d' };
      for (const [key, value] of Object.entries(theme)) document.documentElement.style.setProperty(key, value);
      document.documentElement.style.colorScheme = 'light';
    });
    await page.screenshot({ path: path.join(output, 'reading-axis-light.png') });

    await page.goto(local('branching.html'));
    await page.evaluate(async () => {
      await document.fonts.ready;
      const state = branchDemo.getState(), text = '恢复答案，同一行继续读原文。';
      state.sessions[0].turns = [{ id: 'proof', question: '气泡与文字中轴', answer: [
        { id: 'axis-heading', kind: 'heading', text },
        { id: 'axis-body', kind: 'paragraph', text },
        { id: 'axis-display', kind: 'math', text: 'x=2', number: '(1)' }
      ] }];
      for (const [index, id] of ['axis-heading', 'axis-body'].entries()) {
        state.sessions.push({
          id: 'branch-axis-' + index, title: '继续追问', parent: 'root', draft: '', turns: [],
          source: { blockId: id, textOffset: 0, quote: '恢复答案' }
        });
      }
      state.active = 'root'; state.selected = 'root';
      branchDemo.setState(state);
      await document.fonts.ready;
      window.alignReadingMedia(document.querySelector('#transcript'));
    });
    assert.equal(await page.locator('[data-block-id="axis-display"] .katex').evaluate(el => getComputedStyle(el).fontSize), '20.7px');
    await inspect(page, 'branch-axis-dark.png', [
      { selector: '[data-block-id="axis-heading"]', glyphs: ['.reading-bubble'] },
      { selector: '[data-block-id="axis-body"]', glyphs: ['.reading-bubble'] }
    ]);
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

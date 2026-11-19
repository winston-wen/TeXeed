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

function center(png, rect, light = false) {
  let top = Infinity, bottom = -Infinity;
  for (let y = Math.max(0, Math.floor(rect.top * scale)); y < Math.min(png.height, Math.ceil(rect.bottom * scale)); y++) {
    for (let x = Math.max(0, Math.floor(rect.left * scale)); x < Math.min(png.width, Math.ceil(rect.right * scale)); x++) {
      const i = (png.width * y + x) * 4;
      const isInk = light
        ? Math.max(...png.data.subarray(i, i + 3)) < 110
        : Math.min(...png.data.subarray(i, i + 3)) >= 165;
      if (!isInk) continue;
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

// A zero-height inline block has its bottom edge on the prose baseline. Compare
// it to the renderer's own glyph strut or SVG depth in the same reading line.
async function inspectMath(page, file, selector, light = false) {
  const samples = await page.locator(selector).evaluateAll(nodes => nodes.map(node => {
    const probe = document.createElement('i');
    probe.style.cssText = 'display:inline-block;vertical-align:baseline;width:0;height:0';
    node.before(probe);
    const baseline = probe.getBoundingClientRect().bottom;
    probe.remove();
    const strut = node.querySelector('.katex-html > .base > .strut') || node.querySelector('svg');
    const baselineInMath = strut.getBoundingClientRect().bottom + (parseFloat(getComputedStyle(strut).verticalAlign) || 0);
    const axis = node.querySelector('.mrel, .frac-line, svg use[*|href$="-61"]');
    return { tex: node.dataset.tex || node.getAttribute('aria-label'), baseline, baselineInMath,
      size: parseFloat(getComputedStyle(strut).fontSize), axis: axis?.getBoundingClientRect().toJSON() };
  }));
  assert(samples.length > 0, 'The formula sample must be rendered');
  const png = PNG.sync.read(await page.screenshot({ path: path.join(output, file) }));
  for (const { tex, baseline, baselineInMath, size, axis } of samples) {
    const shift = baselineInMath - baseline;
    console.log(file, tex, 'TeX baseline minus text baseline:', shift);
    assert.ok(Math.abs(shift) < 0.25, `${tex}: formula baseline off by ${shift} CSS px`);
    if (axis) {
      const shift = center(png, axis, light) - (baseline - 0.25 * size);
      console.log('Painted math axis error:', shift);
      assert.ok(Math.abs(shift) <= 0.8, `${tex}: math-axis ink off by ${shift} CSS px`);
    }
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
    await inspectMath(page, 'reading-axis-bundled-svg.png', '#demo-1-p4 .math[data-tex="x=2"]');
    await page.evaluate(async () => {
      await document.fonts.ready;
      const heading = '恢复答案 $x=2$ 恢复答案', paragraph = '恢复答案 $\\frac{1}{2}$ 恢复答案';
      const mixed = String.raw`双方都知道公共矩阵 $A$ 和公共向量 $r$。接收方自己采样 $b'_{1-x}=r-b'_x$。无论选哪支，它都只发送 $B=b'_0$。`;
      const numbers = String.raw`## 为什么 Help 里看到的是 $512$ 和 $64$？

正文的 $ 064 $、$０５１２$ 和 \(0\)。

| 取值 |
|---|
| $002$ |

$$512$$`;
      current().turns = [
        { id: 'axis', question: '行内公式与文字基线', answer: '# ' + heading + '\n\n' + paragraph + '\n\n$$x=2$$\n\n' + mixed + '\n\n' + numbers,
          annotations: [{ id: 'ref', title: '来源', question: '', quote: String.raw`正文 $b^{\prime}_{1-x}$ 与 $r$ 的关系。`, sources: [], pdf: [] }], sections: {} },
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
    const digits = await page.locator('#axis .answer [data-inline-number]').evaluateAll(nodes => nodes.map(n => ({
      text: n.textContent, inline: getComputedStyle(n).display, font: getComputedStyle(n).fontFamily,
      adjacentFont: getComputedStyle(n.parentElement).fontFamily, renderer: !!n.querySelector('.katex, svg'),
      copy: serialize(n)
    })));
    assert.deepEqual(digits.map(n => n.text), ['512', '64', '064', '０５１２', '0', '002']);
    assert.ok(digits.every(n => n.inline === 'inline' && n.font === n.adjacentFont && !n.renderer && n.copy === n.text));
    assert.equal(await page.locator('#axis .equation .math[data-tex="512"] .katex').count(), 1, 'Display numerals retain their TeX layout');
    const fontSizes = await page.evaluate(() => ({
      inline: getComputedStyle(document.querySelector('#axis-p0 .math .katex')).fontSize,
      display: getComputedStyle(document.querySelector('#axis-p2 .math .katex')).fontSize
    }));
    assert.deepEqual(fontSizes, { inline: '18px', display: '20.7px' });
    await inspect(page, 'reading-axis-dark.png', [
      { selector: '#axis-p0 .heading-label', glyphs: ['.reading-bubble'] },
      { selector: '#axis-p1 p', glyphs: ['.reading-bubble'] }
    ]);
    const formulas = '#axis-p0 .math, #axis-p1 .math, #axis-p3 .math, #axis .sent-summary .quote-math-window';
    assert.equal(await page.locator('#axis .sent-summary .quote-math-window').count(), 2);
    await inspectMath(page, 'reading-baseline-dark.png', formulas);
    await page.evaluate(() => {
      const theme = { '--bg': '#fffdf9', '--side': '#f1ede7', '--panel': '#ebe5dc', '--question': '#eee7df',
        '--ink': '#211c1b', '--muted': '#605957', '--quiet': '#776d68', '--line': '#ddd4cb', '--guide': '#b8a99d' };
      for (const [key, value] of Object.entries(theme)) document.documentElement.style.setProperty(key, value);
      document.documentElement.style.colorScheme = 'light';
    });
    await inspectMath(page, 'reading-axis-light.png', formulas, true);

    await page.evaluate(() => {
      const saved = current().turns[0];
      const template = document.createElement('template');
      template.innerHTML = saved.answerHtml;
      const n = template.content.querySelector('[data-inline-number="512"]');
      n.outerHTML = '<span class="math" data-display="false" data-tex="512" role="math"><svg height="12"></svg></span>';
      saved.answerHtml = template.innerHTML;
      const target = current().turns[1].request.annotations;
      target.push({block: 'axis-p4', endMediaIndex: 1});
      renderTurns();
    });
    assert.equal(await page.locator('#axis-p4 [data-inline-number="512"]').textContent(), '512');
    assert.equal(await page.locator('#axis-p4 [data-inline-number="64"] + .same-turn-link').count(), 1, 'A saved media-index reference still targets the second numeric span');
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

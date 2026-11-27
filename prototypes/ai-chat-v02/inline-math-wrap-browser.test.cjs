'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const output = path.resolve(process.env.TEXEED_INLINE_WRAP_OUTPUT || 'artifacts/inline-math-wrap-browser');
const tex = 'R_p=\\mathbb{Z}_p[X]/(X^N+1)';
const source = '其中每个系数都按同一规则处理，结果属于 $' + tex + '$。后续按同一规则计算。';
const complex = 'a=-\\frac{b+c}{d-e}+\\sqrt{e+f}+g_{i+j}+(m+n)=k';
const answer = [source, '分式、根式和括号内的项 $' + complex + '$ 保持完整。',
  '短式 $x=2$。完整分式 $\\frac{a_1+a_2+a_3+a_4+a_5}{b_1+b_2+b_3}$。',
  '推导 $' + Array.from({length: 15}, (_, i) => 'w_{' + i + '}').join('+') + '=w$。',
  '$$' + tex + '$$'].join('\n\n');

async function fixture(page, url) {
  await page.goto(url);
  await page.evaluate(({ answer, source }) => {
    current().draft = '继续核对';
    current().turns = [{ id: 'wrap', question: source, answer, sections: {} }];
    renderTurns();
    persist();
  }, { answer, source });
  await page.evaluate(() => document.fonts.ready);
}

async function positions(formula) {
  return formula.evaluate(el => {
    const p = el.closest('p'), rect = node => node.getBoundingClientRect().toJSON();
    const before = document.createRange();
    before.setStart(el.previousSibling, el.previousSibling.length - 2);
    before.setEndBefore(el);
    return { p: rect(p), prefix: rect(before), chunks: [...el.querySelectorAll('.math-chunk')].map(rect) };
  });
}

async function checkChineseMath(page) {
  const inline = 'w_0=b_0+1\\quad\\text{所有位相互独立}';
  const display = '\\begin{aligned}w_0&=b_0+1\\\\w_1&=b_1+1\\quad\\text{所有位相互独立}\\end{aligned}\\label{eq:bits}';
  await page.setViewportSize({width: 850, height: 690});
  await page.evaluate(({inline,display}) => {
    current().turns = [{id:'song',question:'各位之间有什么关系？',sections:{},
      answer:'写作 $' + inline + '$，如公式 \\eqref{eq:bits} 所示。\n\n$$' + display + '$$\n\n所有位相互独立。'}];
    renderTurns(); persist();
  }, {inline,display});
  await page.evaluate(() => document.fonts.ready);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
  const {root} = await cdp.send('DOM.getDocument');
  async function face(selector) {
    const {nodeId} = await cdp.send('DOM.querySelector', {nodeId:root.nodeId,selector});
    assert(nodeId, selector);
    const {fonts} = await cdp.send('CSS.getPlatformFontsForNode', {nodeId});
    return fonts.filter(f => f.glyphCount);
  }
  const font = async selector => {
    const fonts = await face(selector);
    assert.equal(fonts.reduce((sum,f) => sum + f.glyphCount, 0), '所有位相互独立'.length, 'All Han glyphs should be drawn');
    return fonts[0].familyName;
  };
  const inlineFamily = await font('#song-p0 .cjk_fallback');
  const displayFamily = await font('#song-p1 .cjk_fallback');
  assert.equal(displayFamily, inlineFamily);
  const installed = await page.evaluate(async () => {
    for (const name of ['Source Han Serif SC','思源宋体','Noto Serif CJK SC','Noto Serif SC','SimSun','宋体']) {
      try { await new FontFace('test font availability','local("' + name + '")').load(); return name; } catch {}
    }
    return null;
  });
  if (installed) assert.match(inlineFamily, /(?:Serif|Song|SimSun|宋)/iu, installed + ' was available, but Chromium used ' + inlineFamily);
  // Exercise the end of the font stack even on a development machine with
  // Source Han installed. Chromium reports the face actually used for the glyphs.
  await page.evaluate(async () => {
    const host=document.createElement('div');
    host.id='fallback-probes';host.className='katex';
    host.style.cssText='position:fixed;top:0;left:0;z-index:1000;pointer-events:none';
    for (const [id,stack] of [['missing-song','"TeXeed Unavailable CJK Font",serif'],['system-serif','serif']]) {
      const probe=document.createElement('span');probe.id=id;probe.style.fontFamily=stack;
      probe.textContent='所有位相互独立';host.append(probe);
    }
    document.body.append(host);
    await document.fonts.ready;
    await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
  });
  const genericFamily=await font('#system-serif');
  assert.equal(await font('#missing-song'), genericFamily);
  if (!installed) assert.equal(inlineFamily, genericFamily, 'Use the host serif fallback when no preferred CJK font is installed');
  await page.locator('#fallback-probes').evaluate(el=>el.remove());
  const latin = await face('#song-p0 .mathnormal');
  assert(latin.length && latin.every(f => f.familyName.startsWith('KaTeX')));
  const bodyFont = await page.locator('#song-p0 p').evaluate(p => getComputedStyle(p).fontFamily);
  const bodyFaces = await face('#song-p2 p');
  assert.match(bodyFont, /Plex Sans/);
  assert.doesNotMatch(bodyFont, /Source Han Serif|SimSun/i);
  assert(bodyFaces.length > 0);
  if (bodyFaces.some(f => /Hiragino|YaHei/i.test(f.familyName))) assert(bodyFaces.every(f => f.familyName !== inlineFamily));
  await page.locator('#song-p0 .equation-ref').click();
  assert.equal(await font('#equation-preview .cjk_fallback'), inlineFamily);
  await page.locator('.reading-shell').screenshot({path:path.join(output, 'chinese-math.png')});
  fs.writeFileSync(path.join(output,'fonts.json'),JSON.stringify({inlineFamily,displayFamily,latin,bodyFont,bodyFaces,installed,genericFamily},null,2));
  console.log('Formula Han glyphs:', inlineFamily, 'installed preference:', installed, 'system serif fallback:', genericFamily);
  const quote = await page.locator('#song-p0 .cjk_fallback').evaluate(glyph => targetContext(glyph).quote);
  assert.equal(quote, '$' + inline + '$');
  await page.keyboard.press('Escape');
  // Older saved markup contains fixed SVG outlines. The original data-tex remains
  // authoritative when regenerating its CJK glyphs; unsupported TeX retains its SVG.
  await page.evaluate(() => {
    const turn=current().turns[0], old=document.createElement('template');
    old.innerHTML=turn.answerHtml;
    for (const m of old.content.querySelectorAll('.math')) m.innerHTML='<svg width="12em" height="1em"><path d="M0 0h1"/></svg>';
    turn.answerHtml=old.innerHTML;
    renderTurns();persist();
  });
  await page.reload();
  assert.equal(await page.locator('#song-p1 .math > svg').count(), 0);
  assert.equal(await page.locator('#song-p1 .cjk_fallback').textContent(), '所有位相互独立');
  assert.equal(await page.locator('#song-p0 .math').getAttribute('data-tex'), inline);
  await cdp.detach();
}

async function benchmark(page, url) {
  await fixture(page, url);
  // Reading only: equal Markdown, renderer, fonts and viewport for both versions.
  await page.evaluate(source => {
    current().turns = Array.from({length: 16}, (_, i) => ({id: 'measure-' + i, question: '环与向量',
      answer: [source, '短式 $x=2$。', source].join('\n\n'), sections: {}}));
    renderTurns();
  }, source);
  const session = await page.context().newCDPSession(page);
  await session.send('HeapProfiler.enable');
  const samples = [];
  for (let i = 0; i < 7; i++) {
    await session.send('HeapProfiler.collectGarbage');
    await session.send('HeapProfiler.startSampling', {samplingInterval: 8192, includeObjectsCollectedByMajorGC: true,
      includeObjectsCollectedByMinorGC: true});
    const sample = await page.evaluate(async () => {
      const start = performance.now();
      renderTurns();
      document.querySelector('#thread').getBoundingClientRect();
      const renderMs = performance.now() - start;
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      const reader = document.querySelector('#reader').getBoundingClientRect();
      return { renderMs, settledMs: performance.now() - start,
        visibleControls: [...document.querySelectorAll('#thread :is(button,input,select,textarea)')].filter(el => {
          const r = el.getBoundingClientRect();
          return r.width > 0 && r.height > 0 && r.top < reader.bottom && r.bottom > reader.top;
        }).length };
    });
    const {profile} = await session.send('HeapProfiler.stopSampling');
    sample.sampledBytes = profile.samples.reduce((total, s) => total + s.size, 0);
    if (i > 0) samples.push(sample);
  }
  await session.detach();
  return samples;
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? {executablePath: process.env.TEXEED_BROWSER_PATH} : {}) });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 740 }, deviceScaleFactor: 1 });
    const errors = [];
    page.on('pageerror', error => errors.push(String(error)));
    const url = pathToFileURL(path.join(__dirname, 'index.html')).href;
    await fixture(page, url);
    const formula = page.locator('#wrap-p0 .math');
    const chunks = formula.locator('.math-chunk');
    assert.deepEqual((await chunks.allTextContents()).map(s => s.replace(/\u200b/g,'')), ['Rp=', 'Zp[X]/', '(XN+1)']);
    const wide = await positions(formula);
    assert(wide.chunks.every(rect => Math.abs(rect.top - wide.chunks[0].top) < 1));
    // Compare every painted leaf of the original complete KaTeX output against the
    // grouped result, including scripts and operator spacing at both break points.
    const errorsPx = await formula.evaluate(el => {
      const original = document.createElement('span');
      original.className = 'math';
      original.dataset.display = 'false';
      katex.render(el.dataset.tex, original);
      el.replaceWith(original);
      const glyphs = node => [...node.querySelectorAll('.katex-html span')].filter(s => !s.children.length && s.textContent.trim()).map(s => {
        const r = s.getBoundingClientRect(); return [s.textContent, r.x, r.y, r.width, r.height];
      });
      const reference = glyphs(original);
      original.replaceWith(el);
      const observed = glyphs(el);
      return observed.map(([s,...rect], i) => s !== reference[i][0] ? Infinity : Math.max(...rect.map((n,j) => Math.abs(n-reference[i][j+1]))));
    });
    assert(errorsPx.length >= 12 && Math.max(...errorsPx) < 0.3, 'Wide formula shifted by ' + Math.max(...errorsPx));
    await page.locator('#wrap').screenshot({ path: path.join(output, 'wide.png') });
    assert.equal(await page.locator('#wrap-p2 .math .math-chunk').count(), 0);
    assert.equal(await page.locator('#wrap-p4 .equation-number').textContent(), '(1)');
    assert.equal(await page.locator('#wrap-p4 .math-chunk').count(), 0);
    const protectedParts = (await page.locator('#wrap-p1 .math-chunk').allTextContents()).map(s => s.replace(/[\u200b\s]/g, ''));
    assert.equal(protectedParts.length, 6);
    assert(protectedParts.some(s => s.includes('m+n')));
    for (const selector of ['.mfrac', '.sqrt', '.msupsub']) {
      assert.equal(await page.locator('#wrap-p1 ' + selector).evaluateAll(nodes => nodes.every(node => !!node.closest('.math-chunk'))), true);
    }
    let equalWidth = 0, slashWidth = 0;
    for (let width = 680; width >= 400 && !(equalWidth && slashWidth); width -= 8) {
      await page.setViewportSize({ width, height: 740 });
      const pos = await positions(formula), [first, second, third] = pos.chunks;
      const prefixY = (pos.prefix.top + pos.prefix.bottom) / 2;
      if (prefixY < first.top || prefixY > first.bottom) continue;
      if (!slashWidth && Math.abs(first.top-second.top) < 1 && third.top > first.bottom && Math.abs(third.left-pos.p.left) < 1)
        slashWidth = width;
      if (!equalWidth && second.top > first.bottom && Math.abs(second.top-third.top) < 1 && Math.abs(second.left-pos.p.left) < 1)
        equalWidth = width;
    }
    assert(equalWidth && slashWidth, 'Both available-width break choices must be reachable by resizing');
    await page.setViewportSize({ width: equalWidth, height: 740 });
    await page.locator('#wrap').screenshot({ path: path.join(output, 'break-equals.png') });
    await page.setViewportSize({ width: slashWidth, height: 740 });
    await page.locator('#wrap').screenshot({ path: path.join(output, 'break-slash.png') });
    await page.setViewportSize({ width: 330, height: 740 });
    assert(await page.locator('#wrap-p3 .math-chunk').evaluateAll(nodes => new Set(nodes.map(n => n.offsetTop)).size) >= 3);
    assert(await page.locator('#reader').evaluate(r => r.scrollWidth <= r.clientWidth + 1));
    // A right-click on the continuation still addresses the complete source formula.
    await page.setViewportSize({ width: slashWidth, height: 740 });
    await chunks.last().click({button: 'right'});
    const selectedMath = await page.evaluate(() => ({quote: context.quote, index: context.endMediaIndex}));
    assert.deepEqual(selectedMath, {quote: '$' + tex + '$', index: 0});
    await page.keyboard.press('Escape');
    const copied = await chunks.last().evaluate(chunk => {
      const range = document.createRange(); range.selectNodeContents(chunk);
      getSelection().removeAllRanges(); getSelection().addRange(range);
      const data = new DataTransfer();
      chunk.dispatchEvent(new ClipboardEvent('copy', { bubbles: true, cancelable: true, clipboardData: data }));
      getSelection().removeAllRanges();
      return data.getData('text/plain').trim();
    });
    assert.equal(copied, '$' + tex + '$');
    await page.reload();
    assert.equal(await chunks.count(), 3);
    assert.equal(await page.locator('#composer').inputValue(), '继续核对');
    assert.equal(await page.evaluate(() => current().turns[0].answer), answer);
    await page.evaluate(() => {
      const turn = BUNDLED_TURNS.find(t => t.id === 'demo-3');
      current().turns = [{...turn, sections:{}, collapsed:false}]; renderTurns();
    });
    assert.equal(await page.locator('.answer .math[data-tex="1+2+3+4=10"] > .katex .math-chunk').count(), 5, 'Breakable bundled SVG');
    await checkChineseMath(page);
    if (process.env.TEXEED_INLINE_WRAP_BASELINE) {
      await page.setViewportSize({width:800,height:740});
      const baseline = pathToFileURL(path.resolve(process.env.TEXEED_INLINE_WRAP_BASELINE)).href;
      const samples = { chromium: browser.version(), old: await benchmark(page, baseline), current: await benchmark(page, url) };
      fs.writeFileSync(path.join(output,'performance.json'), JSON.stringify(samples,null,2));
    }
    assert.deepEqual(errors, []);
    console.log('PASS: actual available line widths, unchanged wide glyph positions, intact atoms, three-line flow, clipboard, right-click, display math and reopened content');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

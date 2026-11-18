'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const font = path.join(__dirname, 'fonts/local/Hiragino_Sans_GB.ttc');
const hasLocalHiragino = fs.existsSync(font);
let chromium;
try { ({ chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright')); }
catch { console.log('SKIP reading fonts: Playwright unavailable'); process.exit(0); }
(async () => {
  const root = __dirname;
  const server = http.createServer((request, response) => {
    const requested = path.resolve(root, '.' + decodeURIComponent(new URL(request.url, 'http://localhost').pathname));
    if (!requested.startsWith(root + path.sep) || !fs.existsSync(requested) || !fs.statSync(requested).isFile()) { response.writeHead(404).end(); return; }
    const mime = { '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript', '.ttc': 'font/collection', '.otf': 'font/otf' };
    response.setHeader('Content-Type', mime[path.extname(requested)] || 'application/octet-stream');
    fs.createReadStream(requested).pipe(response);
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  let browser;
  try {
    browser = await chromium.launch({ headless: true, ...(process.env.TEXEED_BROWSER_PATH ? { executablePath: process.env.TEXEED_BROWSER_PATH } : {}) });
    for (const mode of ['file', 'http']) for (const pageName of ['index.html', 'branching.html']) {
      const page = await browser.newPage({ viewport: { width: 1180, height: 850 } });
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      const url = mode === 'file' ? pathToFileURL(path.join(__dirname, pageName)).href : `http://127.0.0.1:${server.address().port}/${pageName}`;
      await page.goto(url + '?test=1');
      const result = await page.evaluate(async () => {
        const load = async (family, text) => { try { return await document.fonts.load(`16px "${family}"`, text); } catch { return []; } };
        const fonts = await Promise.all([
          load('TeXeed Plex Sans', 'Il1O0'), load('TeXeed Hiragino W3', '中文排版')
        ]);
        const container = document.querySelector('.answer') || document.querySelector('.transcript');
        const probe = document.createElement('p');
        probe.id = 'font-probe';
        probe.innerHTML = '<span id="probe-cn">中文阅读样本</span> <span id="probe-latin">Il1 O0</span> <span id="probe-math"></span>';
        container.prepend(probe);
        window.katex.render('x+1', document.getElementById('probe-math'));
        const latin = getComputedStyle(document.getElementById('probe-latin'));
        const math = getComputedStyle(probe.querySelector('.katex'));
        const mathGlyph = getComputedStyle(probe.querySelector('.katex .mathnormal'));
        return { loaded: fonts.map(item => item.length), family: latin.fontFamily,
          feature: latin.fontFeatureSettings, mathFeature: math.fontFeatureSettings,
          mathFamily: mathGlyph.fontFamily, editor: getComputedStyle(document.querySelector('textarea')).fontFamily };
      });
      assert.equal(result.loaded[0], 1, pageName + ' IBM font could not be loaded');
      if (hasLocalHiragino) assert.equal(result.loaded[1], 1, pageName + ' local Hiragino font could not be loaded');
      if (result.loaded[1] === 1) {
        await page.waitForFunction(() => !!document.querySelector('.reading-font-notice'));
        assert.equal(await page.locator('.reading-font-notice').evaluate(el => el.hidden && el.getBoundingClientRect().height === 0), true, 'available fonts must reserve no notice height');
      } else {
        await page.waitForFunction(() => !!document.querySelector('.reading-font-notice:not([hidden])'));
        assert.match(await page.locator('.reading-font-notice').textContent(), /冬青黑体 W3/);
      }
      assert.match(result.family, /TeXeed Plex Sans/);
      assert.match(result.family, /TeXeed Hiragino W3/);
      assert.match(result.feature, /ss04/);
      assert.equal(result.mathFeature, 'normal', 'math must not inherit the prose feature');
      assert.match(result.mathFamily, /KaTeX/, 'math must continue to use TeX fonts');
      assert.doesNotMatch(result.editor, /TeXeed Plex Sans|TeXeed Hiragino W3/, 'editor should retain its own typography');
      const cdp = await page.context().newCDPSession(page);
      await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
      const doc = await cdp.send('DOM.getDocument');
      for (const [selector, pattern] of [['#probe-cn', /Hiragino/i], ['#probe-latin', /IBM Plex Sans/i]]) {
        if (selector === '#probe-cn' && !hasLocalHiragino) continue;
        const node = await cdp.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector });
        const { fonts } = await cdp.send('CSS.getPlatformFontsForNode', { nodeId: node.nodeId });
        console.log(pageName, selector, JSON.stringify(fonts));
        assert.ok(fonts.some(font => pattern.test(font.familyName) && font.glyphCount > 0), selector + ' used a fallback font');
        if (selector === '#probe-cn') assert.ok(fonts.some(font => font.postScriptName === 'HiraginoSansGB-W3' && font.glyphCount > 0), 'Chinese did not use the W3 face');
      }
      await page.locator('#probe-latin').evaluate(el => { el.textContent = '0000'; el.style.fontSize = '80px'; el.style.fontFeatureSettings = 'normal'; });
      const plainZero = await page.locator('#probe-latin').screenshot();
      await page.locator('#probe-latin').evaluate(el => { el.style.fontFeatureSettings = '"ss04" 1'; });
      const dottedZero = await page.locator('#probe-latin').screenshot();
      assert.notDeepEqual(dottedZero, plainZero, 'ss04 did not change the rendered zero glyph');
      await page.locator('#probe-latin').evaluate(el => { el.style.display = 'inline-block'; el.style.width = '100px'; el.style.textAlign = 'center'; });
      const glyphs = {};
      for (const character of ['I', 'l', '1', 'O', '0']) {
        await page.locator('#probe-latin').evaluate((el, value) => { el.textContent = value; }, character);
        glyphs[character] = await page.locator('#probe-latin').screenshot();
      }
      for (const [a, b] of [['I', 'l'], ['l', '1'], ['I', '1'], ['O', '0']])
        assert.notDeepEqual(glyphs[a], glyphs[b], `${a} and ${b} rendered indistinguishably`);
      await page.locator('#probe-latin').evaluate(el => { el.textContent = 'Il1 O0'; el.removeAttribute('style'); });
      if (mode === 'file' && process.env.TEXEED_READING_SCREENSHOTS) {
        await page.screenshot({ path: path.join(process.env.TEXEED_READING_SCREENSHOTS, pageName.replace('.html', '-dark.png')) });
        await page.evaluate(() => { const root = document.documentElement; for (const [k,v] of Object.entries({ '--bg':'#fffdf9','--side':'#f1ede7','--sidebar':'#f1ede7','--panel':'#ebe5dc','--question':'#eee7df','--ink':'#211c1b','--muted':'#605957','--quiet':'#776d68','--line':'#ddd4cb','--guide':'#b8a99d','--answer-emphasis':'#006d78' })) root.style.setProperty(k,v); root.style.colorScheme='light'; });
        await page.screenshot({ path: path.join(process.env.TEXEED_READING_SCREENSHOTS, pageName.replace('.html', '-light.png')) });
      }
      assert.deepEqual(errors, []);
      await page.close();
    }
    for (const pageName of ['index.html', 'branching.html']) {
      for (const [missing, expected] of [['TeXeed Hiragino W3', /冬青黑体 W3/], ['TeXeed Plex Sans', /IBM Plex Sans/], ['both', /未找到阅读字体/]]) {
        const page = await browser.newPage({ viewport: { width: 1180, height: 850 } });
        await page.addInitScript(missing => {
          const original = document.fonts.load.bind(document.fonts);
          document.fonts.load = (specification, text) => missing === 'both' || specification.includes(missing)
            ? new Promise((_, reject) => setTimeout(() => reject(new Error('simulated missing font')), 200)) : original(specification, text);
        }, missing);
        await page.goto(pathToFileURL(path.join(__dirname, pageName)).href + '?test=1');
        await page.evaluate(() => { document.querySelector('#status').textContent = '保留的错误'; document.querySelector('textarea').value = '未发送草稿'; });
        await page.waitForFunction(() => !!document.querySelector('.reading-font-notice:not([hidden])'));
        assert.match(await page.locator('.reading-font-notice').textContent(), expected);
        assert.equal(await page.locator('#status').textContent(), '保留的错误', 'font notice overwrote an unrelated status');
        assert.equal(await page.locator('textarea').first().inputValue(), '未发送草稿', 'font notice overwrote the draft');
        await page.close();
      }
    }
    if (!hasLocalHiragino) console.log('SKIP local Hiragino glyph check: user-supplied TTC not present');
    console.log('PASS: IBM glyphs, ss04 zero, math and editor typography in file and HTTP pages.');
  } finally { if (browser) await browser.close(); await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });

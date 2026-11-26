'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.TEXEED_BROWSER_PATH });
  try {
    const page = await browser.newPage({ viewport: { width: 920, height: 760 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(__dirname, 'index.html')).href + '?test=1');
    await page.evaluate(()=>newConversation());
    await page.locator('#composer').fill('用两条公式说明结论。');
    await page.locator('#send').click();
    assert.equal(await page.evaluate(()=>current().turns.length),1,await page.locator('#status').textContent());
    const instructions=await page.evaluate(()=>current().turns.at(-1).request.displayInstructions);
    assert.ok(instructions.includes('如公式 \\eqref{eq:receiver-w} 所示'));
    assert.ok(instructions.includes('公式 \\eqref{eq:first}、\\eqref{eq:second} 说明了……'));
    await page.locator('#stop').click();
    await page.evaluate(()=>newConversation());
    await page.evaluate(() => {
      const filler = Array.from({ length: 20 }, (_, n) => '讨论的第 ' + (n + 1) + ' 段。').join('\n\n');
      const source = [
        '# 参数', '$$z=x+y\\label{eq:sum}$$',
        '## 双方的计算',
        "$$\\begin{aligned}w_x&=(\\boldsymbol b'_x)^\\top(\\boldsymbol r\\bmod p)+h_1,\\\\w'&=\\boldsymbol b^\\top(\\boldsymbol u\\bmod p)+h_1.\\end{aligned}\\tag{17}\\label{eq:receiver-w}$$",
        '## 另一节', '保持折叠。', '# 接收方', filler,
        "接收方利用私有的 $\\boldsymbol u$ 和收到的 $\\boldsymbol b$ 算出 $w'$，如公式 \\eqref{eq:receiver-w} 所示。",
        '单行定义见公式 \\eqref{eq:sum}。普通编号为 \\ref{eq:sum}。',
        '公式 \\eqref{eq:sum}、\\eqref{eq:receiver-w} 说明了接收方的计算依据。',
        '未定义的 \\eqref{missing} 和代码 ' + String.fromCharCode(96) + '\\eqref{eq:sum}' + String.fromCharCode(96),
        filler
      ].join('\n\n');
      current().turns = [{id:'case', question:'怎样算出接收方的量？', answer:source, sections:{'case-p0':true,'case-p2':true,'case-p4':true}}];
      current().draft = '保留草稿';
      renderConversation();
    });
    await page.evaluate(() => document.fonts.ready);
    const links = page.locator('#case .equation-ref');
    assert.deepEqual(await links.allTextContents(), ['（17）', '（1）', '1', '（1）', '（17）']);
    async function place(link) {
      await link.evaluate(el => el.scrollIntoView({ block: 'center' }));
      await page.waitForTimeout(130);
    }
    async function position(link) {
      return link.evaluate(el => [el.getBoundingClientRect().top, document.getElementById('reader').scrollTop]);
    }
    function numberStyle(node) {
      const s=getComputedStyle(node);return [s.fontFamily,s.fontSize,s.fontWeight,s.fontStyle,s.color];
    }
    async function waitForTarget(id) {
      await page.waitForFunction(id => {
        const box=document.getElementById(id).getBoundingClientRect(), reader=document.getElementById('reader').getBoundingClientRect();
        return box.height && box.top>=reader.top && box.bottom<=reader.bottom;
      }, id);
    }
    for (const [index, id] of [[0,'case-eq-2'], [1,'case-eq-1']]) {
      const link = links.nth(index);
      await place(link);
      const before = await position(link);
      const folds = await page.evaluate(() => ({...current().turns[0].sections}));
      await link.click();
      const popup = page.locator('#equation-preview');
      await popup.waitFor();
      assert.equal(await popup.locator('.katex').count(), 1);
      assert.equal(await popup.locator('.render-fallback').count(), 0);
      assert.equal(await popup.locator('.equation-number').textContent(), index ? '(1)' : '(17)');
      const displayed=await page.locator('#'+id+' .equation-number').evaluate(numberStyle);
      assert.match(displayed[0],/Times New Roman/);
      assert.deepEqual(await popup.locator('.equation-number').evaluate(numberStyle),displayed);
      assert.match(await popup.locator('.math').getAttribute('data-tex'), index ? /z=x\+y/ : /w_x.*w'/s);
      if (!index) {
        const out = process.env.TEXEED_EQREF_SCREENSHOT;
        if (out) { fs.mkdirSync(path.dirname(out), {recursive:true}); await page.screenshot({path:out}); }
      }
      await page.waitForTimeout(200);
      assert.deepEqual(await position(link), before);
      assert.deepEqual(await page.evaluate(() => current().turns[0].sections), folds);
      await page.keyboard.press('Escape');
      assert.equal(await popup.count(), 0);
      await link.dblclick();
      await waitForTarget(id);
      assert.equal(await popup.count(), 0);
      assert.equal(await page.evaluate(() => current().turns[0].sections['case-p4']), true);
    }
    await place(links.nth(0));
    await links.nth(0).focus();
    await page.keyboard.press('Enter');
    await page.locator('#equation-preview').waitFor();
    await page.keyboard.press('Control+Enter');
    await waitForTarget('case-eq-2');
    await place(links.nth(2));
    await links.nth(2).click();
    await waitForTarget('case-eq-1');
    assert.equal(await page.locator('#equation-preview').count(), 0);
    await place(links.nth(3));
    const adjacentPosition=await position(links.nth(3));
    for(const [index,number,formula] of [[3,'(1)',/z=x\+y/],[4,'(17)',/w_x.*w'/s]]){
      await links.nth(index).click();
      const popup=page.locator('#equation-preview');
      assert.equal(await popup.count(),1);
      assert.equal(await popup.locator('.equation-number').textContent(),number);
      assert.match(await popup.locator('.math').getAttribute('data-tex'),formula);
      assert.deepEqual(await position(links.nth(3)),adjacentPosition);
    }
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#composer').inputValue(), '保留草稿');
    await page.setViewportSize({width:440,height:740});
    await page.evaluate(()=>{
      const wide=Array.from({length:36},(_,n)=>'a_{'+n+'}').join('+');
      current().turns=[{id:'wide',question:'长式',answer:'$$w='+wide+'$$\n\n按公式 \\eqref{eq-1} 计算。'}];
      renderConversation();
    });
    await page.locator('#wide .equation-ref').click();
    const wideBox=await page.locator('#equation-preview').evaluate(p=>{
      const b=p.getBoundingClientRect(),body=p.querySelector('.equation-preview-body'),r=document.getElementById('reader').getBoundingClientRect();
      body.scrollLeft=body.scrollWidth;
      return {inside:b.left>=r.left&&b.right<=r.right&&b.top>=r.top&&b.bottom<=r.bottom,local:body.scrollLeft};
    });
    assert.equal(wideBox.inside,true);
    assert.ok(wideBox.local>0,'Wide formula must scroll inside its preview');
    await page.keyboard.press('Escape');
    await page.evaluate(()=>{
      const sample=BUNDLED_TURNS.find(turn=>turn.answerHtml.includes('class="equation"'));
      const saved={...sample,id:'bundled',question:'已有公式'};
      saved.answerHtml+='<section class="passage" id="bundled-ref"><div class="passage-body"><p>如公式 \\eqref{eq-1} 所示。</p></div></section>';
      current().turns=[saved];renderConversation();
    });
    const bundledLink=page.locator('#bundled-ref .equation-ref');
    await bundledLink.click();
    const ids=await page.locator('#equation-preview svg').evaluate(svg=>[...svg.querySelectorAll('use')].every(use=>{
      const href=use.getAttribute('href')||use.getAttribute('xlink:href');
      return href?.startsWith('#')&&svg.contains(document.getElementById(href.slice(1)));
    }));
    assert.ok(ids,'Bundled SVG glyph definitions should resolve inside the popup');
    await page.evaluate(() => {
      const c = current();
      c.turns = [
        {id:'target',question:'推导',collapsed:true,foldGroup:'pair',sections:{'target-p0':true,'target-p3':true},
          answer:'# 定义\n\n$$a=b\\label{eq:a}$$\n\n$$\\begin{aligned}u&=x\\\\v&=y\\end{aligned}\\tag{17}\\label{eq:pair}$$\n\n# 附录\n\n补充。'},
        {id:'neighbor',question:'旁例',answer:'$$w=0\\label{eq:a}$$',collapsed:true,foldGroup:'pair'},
        {id:'source',question:'两式说明了什么？',answer:'见公式 \\eqref{1:eq:a}、\\eqref{1:eq:pair}。不存在的 \\eqref{4:eq:a} 和未指定轮次的 \\eqref{eq:a}。'},
      ];
      c.draft='跨轮阅读时的草稿'; renderConversation(); persist();
    });
    const cross = page.locator('#source .equation-ref');
    assert.deepEqual(await cross.allTextContents(), ['（1:1）','（1:17）']);
    for (const [i,tex] of [[0,/a=b/],[1,/begin\{aligned\}.*v&=y/]]) {
      const link=cross.nth(i);
      await place(link);
      const before=await position(link);
      await link.click();
      const previewNumber=await page.locator('#equation-preview .equation-number').evaluate(numberStyle);
      assert.match(await page.locator('#equation-preview .math').getAttribute('data-tex'),tex);
      assert.equal(await page.locator('#equation-preview .katex').count(),1);
      assert.equal(await page.locator('#target .answer').count(),0);
      assert.deepEqual(await position(link),before);
      await page.keyboard.press('Escape');
      await link.dblclick();
      await waitForTarget('target-eq-'+(i+1));
      assert.deepEqual(await page.locator('#target-eq-'+(i+1)+' .equation-number').evaluate(numberStyle),previewNumber);
      assert.equal(await page.evaluate(()=>current().turns[0].sections['target-p3']),true);
      assert.equal(await page.locator('#neighbor .answer').count(),0);
      await page.evaluate(()=>{
        for(const t of current().turns.slice(0,2)){t.collapsed=true;t.foldGroup='pair';}
        renderTurns();
      });
    }
    await page.evaluate(()=>{
      const [t,,s]=current().turns;
      t.readingRevisions=[{id:'new-order',author:'用户',rows:[
        {id:'one',level:0,parts:[{ref:'block:target-p2'}]},
        {id:'two',level:0,parts:[{ref:'block:target-p1'}]},
      ]}];
      s.answer+='\n\n$$q=r\\label{eq:q}$$\n\n公式 \\eqref{3:eq:q}。';
      delete s.answerHtml;
      renderTurns();persist();
    });
    await page.reload();
    assert.deepEqual(await cross.allTextContents(),['（1:2）','（1:17）','（1）']);
    assert.equal(await page.locator('#composer').inputValue(),'跨轮阅读时的草稿');
    assert.deepEqual(errors, []);
    console.log('PASS: single and multiline formula previews preserve viewport, folds and draft; double click, direct ref and keyboard reach the target.');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode=1; });

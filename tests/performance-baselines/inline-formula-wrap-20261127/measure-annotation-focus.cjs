// Pass the baseline index.html; the current prototype is resolved from this checkout.
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const [baseline, output] = process.argv.slice(2);
if (!baseline) throw new Error('Pass the baseline prototype index.html followed by an optional output JSON path.');

async function sample(page) {
  return page.evaluate(async () => {
    const settled = () => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    const start = performance.now();
    openEditor('focus');
    const visible = [...document.querySelectorAll('#annotation-editor :is(button,input,textarea)')]
      .filter(el => el.offsetWidth && el.offsetHeight).length;
    const open = performance.now() - start;
    await settled();
    const closeStart = performance.now();
    confirmAnnotation('focus');
    document.getElementById('reader').getBoundingClientRect();
    const close = performance.now() - closeStart;
    await settled();
    return {open, close, settled: performance.now()-start, visible};
  });
}

async function measure(browser, entry) {
  const page = await browser.newPage({viewport: {width: 1000, height: 820}});
  try {
    await page.goto(pathToFileURL(path.resolve(entry)).href);
    await page.evaluate(() => {
      const c = current();
      c.draft = '保留主草稿';
      c.turns = [{id:'measure',question:'怎样核对？',sections:{},answer:Array.from({length:45},
        (_,i) => '第'+(i+1)+'段，选择这几个字，然后继续阅读。').join('\n\n')}];
      c.entries = [{id:'focus',number:1,title:'AI 回答批注',question:'第一条批注',selected:true,
        state:'待提交',sources:[],pdf:[],block:'measure-p26',quote:'选择这几个字'}];
      renderConversation();
      document.querySelector('#measure-p26').scrollIntoView({block:'center'});
    });
    await page.evaluate(() => document.fonts.ready);
    const session = await page.context().newCDPSession(page);
    await session.send('HeapProfiler.enable');
    const samples = [];
    for (let i = 0; i < 14; i++) {
      await session.send('HeapProfiler.collectGarbage');
      await session.send('HeapProfiler.startSampling', {samplingInterval:1024,
        includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
      const result = await sample(page);
      const {profile} = await session.send('HeapProfiler.stopSampling');
      result.sampledBytes = profile.samples.reduce((total,s) => total+s.size,0);
      if (i >= 2) samples.push(result);
    }
    return samples;
  } finally { await page.close(); }
}

(async () => {
  const browser = await chromium.launch({headless:true, ...(process.env.TEXEED_BROWSER_PATH ?
    {executablePath:process.env.TEXEED_BROWSER_PATH} : {})});
  try {
    const current = path.join(__dirname, '../../../prototypes/ai-chat-v02/index.html');
    const result = {chromium:browser.version(),viewport:[1000,820],rounds:12,discardedWarmups:2,
      old:await measure(browser, baseline),current:await measure(browser, current)};
    const json = JSON.stringify(result, null, 2);
    if (output) fs.writeFileSync(output, json + '\n'); else console.log(json);
  } finally { await browser.close(); }
})().catch(err => { console.error(err); process.exitCode = 1; });

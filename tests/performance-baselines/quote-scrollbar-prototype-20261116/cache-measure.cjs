'use strict';
const fs = require('node:fs'), path = require('node:path'), { pathToFileURL } = require('node:url');
const crypto = require('node:crypto'), os = require('node:os');
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..'), prototype = path.join(root, 'prototypes/ai-chat-v02');
const hash = data => crypto.createHash('sha256').update(data).digest('hex');
const temp = fs.mkdtempSync(path.join(root, 'artifacts', 'quote-cache-perf-'));
const dirs = {};
for (const mode of ['off', 'on']) {
  const dir = path.join(temp, mode); fs.cpSync(prototype, dir, { recursive: true }); dirs[mode] = dir;
  if (mode === 'off') {
    const file = path.join(dir, 'quote-summary.js'); let source = fs.readFileSync(file, 'utf8');
    const read = 'if (key !== null && cache.has(key)) return cache.get(key);';
    const write = 'if (key !== null && result.length <= 32768) {';
    if (!source.includes(read) || !source.includes(write)) throw Error('Cache switch mismatch');
    source = source.replace(read, 'if (false) return cache.get(key);').replace(write, 'if (false) {');
    fs.writeFileSync(file, source);
  }
}
const median = values => [...values].sort((a,b)=>a-b)[Math.floor(values.length/2)];
(async()=>{
  const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
  const result={schema:'quote-summary-cache-20261116-v1', fixtureHash:hash(fs.readFileSync(__filename)), resources:Object.fromEntries(Object.entries(dirs).map(([mode,dir])=>[mode,hash(fs.readFileSync(path.join(dir,'quote-summary.js')))])), environment:{node:process.version,browser:browser.version(),cpu:os.cpus()[0]?.model,viewport:{width:1000,height:820}},method:{turns:120,formulaQuotes:10,order:['off','on','on','off'],warmups:2,samples:7,frames:2,delayedWaitMs:700,heapSamplingInterval:1024},measurements:[]};
  try {
    for (const mode of result.method.order) {
      const page=await browser.newPage({viewport:result.environment.viewport}),errors=[];page.on('pageerror',e=>errors.push(e.message));
      await page.goto(pathToFileURL(path.join(dirs[mode],'index.html')).href+'?test=1');await page.evaluate(()=>document.fonts.ready);
      await page.evaluate(()=>{
        const quote=String.raw`形式化地，对于一个输出 $0$ 或 $1$ 的区分算法 $D$，定义其优势 $$\operatorname{Adv}^{\mathrm{dMLWR}}_D=\left|\Pr[D(\mathsf{real})=1]-\Pr[D(\mathsf{rand})=1]\right|$$`;
        const c=current();c.draft='保留性能草稿';c.turns=Array.from({length:120},(_,i)=>({id:'synthetic-'+i,question:'合成提问 '+i,answer:'合成段落 '+i+' 用于版面性能测量。',sections:{},...(i>=110?{prompt:'合成提问 '+i,annotations:[{id:'quote-'+i,title:'来源',question:'',quote,summary:quote.slice(0,48),sources:[],pdf:[]}]}:{})}));renderConversation();
      });
      const client=await page.context().newCDPSession(page);
      async function sample(work){return page.evaluate(async work=>{const start=performance.now();if(work)renderTurns();const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,700));return{sync,elapsed:performance.now()-start};},work);}
      for(let i=0;i<2;i++){await sample(true);await sample(false);}
      const pair=[];for(let i=0;i<7;i++){const empty=await sample(false),work=await sample(true);pair.push({empty,work,corrected:work.elapsed-empty.elapsed});}
      async function bytes(work){await client.send('HeapProfiler.startSampling',{samplingInterval:1024,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});for(let i=0;i<7;i++)await sample(work);return(await client.send('HeapProfiler.stopSampling')).profile.samples.reduce((n,s)=>n+s.size,0);}
      const idle=await bytes(false),work=await bytes(true),counts=await page.evaluate(()=>({nodes:document.querySelectorAll('*').length,visibleControls:[...document.querySelectorAll('button,input,textarea,select,summary')].filter(e=>{const r=e.getBoundingClientRect(),s=getComputedStyle(e);return r.width>0&&r.height>0&&r.bottom>0&&r.right>0&&r.top<innerHeight&&r.left<innerWidth&&s.visibility!=='hidden';}).length}));
      const item={mode,pair,medianSync:median(pair.map(p=>p.work.sync)),medianCorrected:median(pair.map(p=>p.corrected)),allocatedBytes:(work-idle)/7,idleBytes:idle,workBytes:work,counts};result.measurements.push(item);console.log(JSON.stringify({mode,medianSync:item.medianSync,medianCorrected:item.medianCorrected,allocatedBytes:item.allocatedBytes,counts}));
      if(errors.length)throw Error(errors.join('; '));await page.close();
    }
    fs.writeFileSync(process.argv[2]||path.join(__dirname,'cache-samples.json'),JSON.stringify(result,null,2)+'\n');
  } finally { await browser.close(); }
})().catch(e=>{console.error(e);process.exitCode=1;});

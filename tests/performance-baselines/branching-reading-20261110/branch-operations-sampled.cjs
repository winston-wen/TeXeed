'use strict';
const fs = require('node:fs'), path = require('node:path'), os = require('node:os'), crypto = require('node:crypto'), {pathToFileURL} = require('node:url');
const {chromium} = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const root = path.resolve(__dirname, '../../..'), dir = path.join(root, 'prototypes/ai-chat-v02');
const hash = p => crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const median = a => [...a].sort((x, y) => x - y)[Math.floor(a.length / 2)];
const rounds = Math.max(1, Number(process.env.TEXEED_PERF_ROUNDS || 1));
(async () => {
 const browser = await chromium.launch({headless:true, ...(process.env.TEXEED_BROWSER_PATH ? {executablePath:process.env.TEXEED_BROWSER_PATH}: {})});
 const result = {schema:'branch-ops-v1', fixtureHash:hash(__filename), resources:Object.fromEntries(['branching.html','branching.css','branching.js'].map(p => [p,hash(path.join(dir,p))])), environment:{platform:os.platform(),release:os.release(),cpu:os.cpus()[0]?.model,node:process.version,browser:browser.version(),viewport:{width:1000,height:820}}, method:{rounds,warmups:2,samples:7,turns:120,branches:60,frames:2,waitMs:220,allocation:'CDP V8 sampling at 1024 bytes. Each action sampled separately with paired empty action. Sample allocations are estimates and include browser dispatch and queue costs.'}, measurements:[]};
 try {
  for(let round=0;round<rounds;round++) for(const operation of (round%2 ? ['return','switch','map'] : ['map','switch','return'])) {
   const page=await browser.newPage({viewport:{width:1000,height:820}}), errors=[];page.on('pageerror',e=>errors.push(e.message));
   await page.goto(pathToFileURL(path.join(dir,'branching.html')).href+'?test=1');await page.evaluate(()=>document.fonts.ready);
   await page.evaluate(()=>{const f=branchDemo.makeFixture(120,60);for(const s of f.sessions.filter(x=>x.parent)){s.source.parentReading={blockId:'fixture-block-50',offset:-10};s.turns=[{id:s.id+'-turn',question:'示例追问',answer:[{id:s.id+'-answer',kind:'paragraph',text:'示例回答内容。'}],demo:true}];}branchDemo.setState(f);});
   const client=await page.context().newCDPSession(page);
   async function prepare() {
    await page.evaluate(operation=>{
     const map=document.querySelector('#relation-map');
     if(!map.hidden)document.querySelector('#map-close').click();
     if(branchDemo.getState().active!=='root')document.querySelector('#back-reading').click();
     if(operation==='switch'||operation==='return'){
      document.querySelector('#map-toggle').click();document.querySelector('[data-node-id="fixture-branch-0"]').click();
      if(operation==='return')document.querySelector('[data-map-open="fixture-branch-0"]').click();
     }
    },operation);
    await page.evaluate(async()=>{await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,220));});
   }
   async function sample(kind) {
    await prepare(); await client.send('HeapProfiler.startSampling',{samplingInterval:1024,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
    const time=await page.evaluate(async({kind,operation})=>{
     const start=performance.now();
     if(kind==='work')document.querySelector(operation==='map'?'#map-toggle':operation==='switch'?'[data-map-open="fixture-branch-0"]':'#back-reading').click();
     const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,220));return {sync,elapsed:performance.now()-start};
    },{kind,operation});
    const profile=(await client.send('HeapProfiler.stopSampling')).profile;
    return {...time,bytes:profile.samples.reduce((a,x)=>a+x.size,0)};
   }
   for(let i=0;i<2;i++){await sample('empty');await sample('work');}
   const pairs=[];for(let i=0;i<7;i++){const empty=await sample('empty'),work=await sample('work');pairs.push({empty,work,correctedElapsed:work.elapsed-empty.elapsed,correctedBytes:work.bytes-empty.bytes});}
   const counts=await page.evaluate(()=>{const controls=[...document.querySelectorAll('button,input,textarea,select,summary')];return {nodes:document.querySelectorAll('*').length,visibleControls:controls.filter(el=>{const b=el.getBoundingClientRect(),s=getComputedStyle(el);return b.width>0&&b.height>0&&b.bottom>0&&b.right>0&&b.top<innerHeight&&b.left<innerWidth&&s.visibility!=='hidden';}).length};});
   const item={round,operation,pairs,medianSync:median(pairs.map(x=>x.work.sync)),medianCorrected:median(pairs.map(x=>x.correctedElapsed)),medianBytes:median(pairs.map(x=>x.correctedBytes)),counts};result.measurements.push(item);console.log(JSON.stringify({...item,pairs:undefined}));
   if(errors.length)throw Error(errors.join('; '));await page.close();
  }
  fs.writeFileSync(process.argv[2]||path.join(__dirname,'branch-operations.json'),JSON.stringify(result,null,2)+'\n');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

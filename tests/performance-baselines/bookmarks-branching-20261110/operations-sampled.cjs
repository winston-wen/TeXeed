'use strict';
const fs=require('node:fs'), path=require('node:path'), os=require('node:os'), crypto=require('node:crypto'), cp=require('node:child_process'), {pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const root=path.resolve(__dirname,'../../..'), proto=path.join(root,'prototypes/ai-chat-v02');
const baseline=cp.execFileSync('git',['rev-parse','cf7cb23'],{cwd:root,encoding:'utf8'}).trim();
const hash=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
const median=a=>[...a].sort((x,y)=>x-y)[Math.floor(a.length/2)];
const rounds=Math.max(1,Number(process.env.TEXEED_PERF_ROUNDS||2));
fs.mkdirSync(path.join(root,'artifacts'),{recursive:true});
const temp=fs.mkdtempSync(path.join(root,'artifacts','bookmark-perf-')), tar=path.join(temp,'old.tar');
fs.writeFileSync(tar,cp.execFileSync('git',['archive','--format=tar',baseline,'prototypes/ai-chat-v02'],{cwd:root,maxBuffer:50*1024*1024}));
cp.execFileSync('tar',['-xf',tar,'-C',temp]);const dirs={before:path.join(temp,'prototypes/ai-chat-v02'),after:proto};
(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 const result={schema:'bookmarks-branching-operations-v1',baseline,fixtureHash:hash(__filename),resources:Object.fromEntries(Object.entries(dirs).map(([mode,dir])=>[mode,Object.fromEntries(['branching.html','branching.css','branching.js'].map(p=>[p,hash(path.join(dir,p))]))])),environment:{platform:os.platform(),release:os.release(),cpu:os.cpus()[0]?.model,node:process.version,browser:browser.version(),viewport:{width:1000,height:820}},method:{rounds,warmups:2,samples:7,turns:120,branches:60,bookmarks:60,frames:2,waitMs:220,allocation:'CDP V8 sampling at 1024 bytes. Paired empty action estimates are subtracted. Includes dispatch, queue and timer allocations; statistical estimates may be negative.'},measurements:[]};
 try{
  for(let round=0;round<rounds;round++)for(const mode of (round%2?['after','before']:['before','after']).filter(x=>!process.env.TEXEED_PERF_MODES||process.env.TEXEED_PERF_MODES.split(',').includes(x)))for(const operation of (mode==='before'?['source']:['source','bookmark-open','bookmark-jump']).filter(x=>!process.env.TEXEED_PERF_OPS||process.env.TEXEED_PERF_OPS.split(',').includes(x))){
   const page=await browser.newPage({viewport:{width:1000,height:820}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
   await page.goto(pathToFileURL(path.join(dirs[mode],'branching.html')).href+'?test=1');await page.evaluate(()=>document.fonts.ready);
   await page.evaluate(mode=>{
    const f=branchDemo.makeFixture(120,60);
    for(const s of f.sessions.filter(x=>x.parent)){s.source.textOffset=50;s.source.parentReading={blockId:'fixture-block-50',textOffset:40,lineY:10,offset:-10};s.turns=[{id:s.id+'-turn',question:'示例追问',answer:[{id:s.id+'-answer',kind:'paragraph',text:'示例回答内容。'}],demo:true}];}
    if(mode==='after')f.bookmarks=Array.from({length:60},(_,i)=>({id:'perf-bookmark-'+i,sessionId:'root',blockId:'fixture-block-'+i,textOffset:10,quote:'固定测试内容',label:'测试书签 '+i}));
    window.perfFixture=f;branchDemo.setState(f);
    if(mode==='after'&&branchDemo.getState().bookmarks?.length!==60)throw Error('bookmark fixture not retained');
   },mode);
   const client=await page.context().newCDPSession(page);
   async function prepare(){
    await page.evaluate(operation=>{
     branchDemo.setState(window.perfFixture);
     if(document.querySelector('#bookmarks-panel') && !document.querySelector('#bookmarks-panel').hidden)document.querySelector('#bookmarks-close').click();
     if(operation==='source'){
      document.querySelector('#map-toggle').click();document.querySelector('[data-node-id="fixture-branch-0"]').click();document.querySelector('[data-map-open="fixture-branch-0"]').click();
     }
     if(operation==='bookmark-jump')document.querySelector('#bookmarks-toggle').click();
    },operation);
    await page.evaluate(async()=>{await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,220));});
   }
   async function sample(kind){
    await prepare();await client.send('HeapProfiler.startSampling',{samplingInterval:1024,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
    const time=await page.evaluate(async({kind,operation})=>{
     const start=performance.now();
     if(kind==='work')document.querySelector(operation==='source'?'#back-source':operation==='bookmark-open'?'#bookmarks-toggle':'[data-bookmark-open]').click();
     const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,220));return{sync,elapsed:performance.now()-start};
    },{kind,operation});
    const profile=(await client.send('HeapProfiler.stopSampling')).profile;return{...time,bytes:profile.samples.reduce((a,x)=>a+x.size,0)};
   }
   for(let i=0;i<2;i++){await sample('empty');await sample('work');}
   const pairs=[];for(let i=0;i<7;i++){const empty=await sample('empty'),work=await sample('work');pairs.push({empty,work,correctedElapsed:work.elapsed-empty.elapsed,correctedBytes:work.bytes-empty.bytes});}
   const counts=await page.evaluate(()=>{
    const visible=el=>{let rect=el.getBoundingClientRect();let l=Math.max(0,rect.left),r=Math.min(innerWidth,rect.right),t=Math.max(0,rect.top),b=Math.min(innerHeight,rect.bottom);for(let p=el;p;p=p.parentElement){const s=getComputedStyle(p);if(s.display==='none'||s.visibility==='hidden')return false;if(p!==el&&/(hidden|clip|scroll|auto)/.test(s.overflow+s.overflowX+s.overflowY)){const q=p.getBoundingClientRect();l=Math.max(l,q.left);r=Math.min(r,q.right);t=Math.max(t,q.top);b=Math.min(b,q.bottom);}}return r>l&&b>t;};
    return{nodes:document.querySelectorAll('*').length,visibleControls:[...document.querySelectorAll('button,input,textarea,select,summary')].filter(visible).length};
   });
   const item={round,mode,operation,pairs,medianSync:median(pairs.map(x=>x.work.sync)),medianCorrected:median(pairs.map(x=>x.correctedElapsed)),medianBytes:median(pairs.map(x=>x.correctedBytes)),counts};result.measurements.push(item);console.log(JSON.stringify({...item,pairs:undefined}));
   if(errors.length)throw Error(errors.join('; '));await page.close();
  }
  fs.writeFileSync(process.argv[2]||path.join(__dirname,'operations.json'),JSON.stringify(result,null,2)+'\n');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

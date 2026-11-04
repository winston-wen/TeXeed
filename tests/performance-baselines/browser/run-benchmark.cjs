'use strict';
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),cp=require('node:child_process'),crypto=require('node:crypto'),os=require('node:os');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const root=path.resolve(__dirname,'../../..'),prototype=path.join(root,'prototypes/ai-chat-v02'),fixturePath=path.join(root,'tests/performance-fixtures/browser-bookmarks-v1.cjs'),fixture=require(fixturePath);
const flag=name=>{const i=process.argv.indexOf(name);return i<0?null:process.argv[i+1];};
const baseline=flag('--baseline');if(!baseline)throw new Error('Pass --baseline <commit>; the complete prototype resources are read from that commit.');
const output=path.resolve(flag('--output')||path.join(root,'artifacts/browser-bookmarks-performance.json'));
const baselineCommit=cp.execFileSync('git',['rev-parse','--verify',baseline+'^{commit}'],{cwd:root,encoding:'utf8'}).trim();
const types={'.js':'text/javascript','.html':'text/html','.css':'text/css','.woff':'font/woff','.woff2':'font/woff2','.ttf':'font/ttf','.svg':'image/svg+xml'};
const cache=new Map();
const server=http.createServer((request,response)=>{
 const url=new URL(request.url,'http://localhost'),match=/^\/(before|after)\/(.+)$/.exec(decodeURIComponent(url.pathname));
 if(!match||match[2].split('/').some(part=>part==='..'||part==='.'||!part)){response.statusCode=403;return response.end();}
 const [,mode,name]=match;response.setHeader('Content-Type',types[path.extname(name)]||'application/octet-stream');
 try{let data;if(mode==='before'){if(!cache.has(name))cache.set(name,cp.execFileSync('git',['show',baselineCommit+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:20*1024*1024}));data=cache.get(name);}else{const file=path.resolve(prototype,name);if(!file.startsWith(prototype+path.sep))throw new Error('invalid path');data=fs.readFileSync(file);}response.end(data);}catch{response.statusCode=404;response.end();}
});
const median=values=>[...values].sort((a,b)=>a-b)[Math.floor(values.length/2)];
const allocated=node=>(node.selfSize||0)+(node.children||[]).reduce((sum,child)=>sum+allocated(child),0);
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 const result={schema:'texeed-browser-performance-candidate-v1',baselineCommit,environment:{platform:os.platform(),release:os.release(),architecture:os.arch(),cpu:os.cpus()[0]?.model||null,node:process.version,browser:browser.version(),viewport:{width:1000,height:820}},method:{fixture:'tests/performance-fixtures/browser-bookmarks-v1.cjs',fixtureSha256:crypto.createHash('sha256').update(fs.readFileSync(fixturePath)).digest('hex'),iterations:9,warmups:2,frames:2,extraWaitMs:200,heapSamplingInterval:1024,limitations:['The extra 200 ms is a fixed wait in the reported completion duration, not measured work.','Frame timings depend on frame phase; sampled retained heap is not total allocation.','The after side reads the working tree; identify the exact committed files separately.']},measurements:{}};
 try{
  for(const mode of ['before','after']){
   const page=await browser.newPage({viewport:{width:1000,height:820}}),client=await page.context().newCDPSession(page),errors=[];page.on('pageerror',e=>errors.push(e.message));
   await page.goto(`http://127.0.0.1:${server.address().port}/${mode}/index.html?test=1`);await page.evaluate(()=>document.fonts.ready);await page.evaluate(fixture);await page.waitForTimeout(250);
   await client.send('HeapProfiler.startSampling',{samplingInterval:1024});const samples=[];
   for(let i=0;i<9;i++){const sample=await page.evaluate(async index=>{const reader=$('reader'),start=performance.now();reader.scrollTop+=index%2?120:-120;reader.getBoundingClientRect();const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));const frame=performance.now()-start;await new Promise(r=>setTimeout(r,200));reader.getBoundingClientRect();return {sync,frame,complete:performance.now()-start};},i);if(i>1)samples.push(sample);}
   const sampledRetainedJsBytes=allocated((await client.send('HeapProfiler.stopSampling')).profile.head);
   const counts=await page.evaluate(()=>{const controls=[...document.querySelectorAll('button,input,textarea,select,summary')];return {elements:document.querySelectorAll('*').length,controls:controls.length,visible:controls.filter(c=>{const b=c.getBoundingClientRect();return b.width&&b.height&&b.right>0&&b.left<innerWidth&&b.bottom>0&&b.top<innerHeight;}).length,draft:current().draft};});
   const measured={samples,medianSync:median(samples.map(s=>s.sync)),medianFrame:median(samples.map(s=>s.frame)),medianComplete:median(samples.map(s=>s.complete)),sampledRetainedJsBytes,counts,errors};
   if(mode==='after'){const opening=[];for(let i=0;i<9;i++){const sample=await page.evaluate(async()=>{const start=performance.now();document.dispatchEvent(new KeyboardEvent('keydown',{key:'g',ctrlKey:true,bubbles:true}));document.querySelector('#bookmark-panel').getBoundingClientRect();const sync=performance.now()-start,count=document.querySelectorAll('.bookmark-target').length;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));await new Promise(r=>setTimeout(r,200));const complete=performance.now()-start;document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true}));return {sync,complete,count};});if(i>1)opening.push(sample);}measured.bookmarkOpening={samples:opening,medianSync:median(opening.map(s=>s.sync)),medianComplete:median(opening.map(s=>s.complete))};}
   result.measurements[mode]=measured;await page.close();
  }
  fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});

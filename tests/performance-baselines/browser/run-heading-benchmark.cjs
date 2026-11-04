'use strict';
const fs=require('node:fs'),path=require('node:path'),http=require('node:http'),cp=require('node:child_process'),crypto=require('node:crypto'),os=require('node:os');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const root=path.resolve(__dirname,'../../..'),prototype=path.join(root,'prototypes/ai-chat-v02'),fixturePath=path.join(root,'tests/performance-fixtures/browser-heading-axis-v1.cjs'),fixture=require(fixturePath);
const flag=name=>{const i=process.argv.indexOf(name);return i<0?null:process.argv[i+1];};
const baseline=flag('--baseline');if(!baseline)throw new Error('Pass --baseline <commit>.');
const output=path.resolve(flag('--output')||path.join(root,'artifacts/heading-axis-performance.json'));
const baselineCommit=cp.execFileSync('git',['rev-parse','--verify',baseline+'^{commit}'],{cwd:root,encoding:'utf8'}).trim();
const hash=data=>crypto.createHash('sha256').update(data).digest('hex'),types={'.js':'text/javascript','.html':'text/html','.css':'text/css','.woff':'font/woff','.woff2':'font/woff2','.ttf':'font/ttf','.svg':'image/svg+xml'};
const cache=new Map(),resources={before:{},after:{}};
const server=http.createServer((request,response)=>{
 const url=new URL(request.url,'http://localhost'),match=/^\/(before|after)\/(.+)$/.exec(decodeURIComponent(url.pathname));
 if(!match||match[2].split('/').some(part=>part==='..'||part==='.'||!part)){response.statusCode=403;return response.end();}
 const [,mode,name]=match;response.setHeader('Content-Type',types[path.extname(name)]||'application/octet-stream');
 try{let data;if(mode==='before'){if(!cache.has(name))cache.set(name,cp.execFileSync('git',['show',baselineCommit+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:20*1024*1024}));data=cache.get(name);}else{const file=path.resolve(prototype,name);if(!file.startsWith(prototype+path.sep))throw new Error('invalid path');data=fs.readFileSync(file);}resources[mode][name]=hash(data);response.end(data);}catch{response.statusCode=404;response.end();}
});
const median=values=>[...values].sort((a,b)=>a-b)[Math.floor(values.length/2)];
const allocated=node=>(node.selfSize||0)+(node.children||[]).reduce((sum,child)=>sum+allocated(child),0);
(async()=>{
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 const result={schema:'texeed-browser-heading-axis-performance-v1',baselineCommit,environment:{platform:os.platform(),release:os.release(),architecture:os.arch(),cpu:os.cpus()[0]?.model||null,node:process.version,browser:browser.version(),viewport:{width:1000,height:820}},method:{fixture:'tests/performance-fixtures/browser-heading-axis-v1.cjs',fixtureSha256:hash(fs.readFileSync(fixturePath)),rounds:3,iterations:9,warmups:2,frames:2,extraWaitMs:200,heapSamplingInterval:1024,limitations:['Frame times depend on frame phase; the extra 200 ms is a fixed wait, not measured work.','V8 sampled retained heap is an estimate, not total allocations.','The after side reads the working tree; resource hashes identify the measured bytes.','All rounds share one browser process.']},resources,measurements:{before:[],after:[]}};
 try{
  for(let round=0;round<3;round++)for(const mode of round%2?['after','before']:['before','after']){
   const page=await browser.newPage({viewport:{width:1000,height:820}}),client=await page.context().newCDPSession(page),errors=[];page.on('pageerror',e=>errors.push(e.message));
   await page.goto(`http://127.0.0.1:${server.address().port}/${mode}/index.html?test=1`);await page.evaluate(()=>document.fonts.ready);await page.evaluate(fixture);
   const firstRender=await page.evaluate(async()=>{alignReadingInk.metrics?.clear();alignReadingInk.inkCache?.clear();alignReadingInk.axisCache?.clear();const start=performance.now();renderTurns();$('reader').scrollTop=300;$('reader').getBoundingClientRect();const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));return {sync,frame:performance.now()-start};});await page.waitForTimeout(250);
   await client.send('HeapProfiler.startSampling',{samplingInterval:1024});const samples=[];
   for(let i=0;i<9;i++){const sample=await page.evaluate(async()=>{const start=performance.now();renderTurns();$('reader').getBoundingClientRect();const sync=performance.now()-start;await new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));const frame=performance.now()-start;await new Promise(r=>setTimeout(r,200));$('reader').getBoundingClientRect();return {sync,frame,complete:performance.now()-start};});if(i>1)samples.push(sample);}
   const sampledRetainedJsBytes=allocated((await client.send('HeapProfiler.stopSampling')).profile.head);
   const counts=await page.evaluate(()=>{const controls=[...document.querySelectorAll('button,input,textarea,select,summary')];return {elements:document.querySelectorAll('*').length,controls:controls.length,visible:controls.filter(c=>{const b=c.getBoundingClientRect();return b.width&&b.height&&b.right>0&&b.left<innerWidth&&b.bottom>0&&b.top<innerHeight;}).length,headings:document.querySelectorAll('.heading-marker').length,draft:current().draft};});
   if(errors.length||counts.headings!==300||counts.draft!=='保留测试草稿')throw new Error(`Invalid ${mode} fixture: ${JSON.stringify({errors,counts})}`);
   result.measurements[mode].push({firstRender,samples,medianSync:median(samples.map(s=>s.sync)),medianFrame:median(samples.map(s=>s.frame)),medianComplete:median(samples.map(s=>s.complete)),sampledRetainedJsBytes,counts,errors});await page.close();
  }
  fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(result,null,2)+'\n');console.log(JSON.stringify(result));
 }finally{await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);server.close();process.exitCode=1;});

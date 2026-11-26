'use strict';
const { chromium } = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');
const { pathToFileURL } = require('node:url');
const { readFileSync, writeFileSync } = require('node:fs');
const { resolve, join } = require('node:path');
const { createHash } = require('node:crypto');

function seed() {
  current().turns=Array.from({length:12}, (_,i)=>({
    id:'bench-'+i, question:'推导 '+i, draft:'',
    answer:['# 推导', '$$\\begin{aligned}a&=b+c,\\\\d&=a^2\\end{aligned}$$',
      ...Array.from({length:8},(_,n)=>'验证公式时保留当前阅读位置，第 '+n+' 段。'),
      '如公式（1）所示。相应结果见公式 \\eqref{eq-1}。'].join('\n\n')}));
  renderConversation();
}
const median = a => [...a].sort((x,y)=>x-y)[a.length>>1];
const bytes = node => node.selfSize + node.children.reduce((sum,c)=>sum+bytes(c),0);
const hash = file => createHash('sha256').update(readFileSync(file)).digest('hex');
(async()=>{
  const args=process.argv.slice(2);
  const allPreview=args.includes('--all-preview');
  const [out,...directories]=args.filter(arg=>arg!=='--all-preview');
  if(!out||directories.length<2)throw Error('Pass result.json, historical prototype directories, and the candidate prototype directory last.');
  const browser=await chromium.launch({headless:true,executablePath:process.env.TEXEED_BROWSER_PATH});
  const results={chrome:browser.version(),platform:process.platform,node:process.version,
    viewport:{width:920,height:760},fixture:hash(__filename),samples:[]};
  try {
    for(let round=0;round<3;round++){
      const order=directories.map((_,i)=>(round+i)%directories.length);
      for(const index of order){
        const dir=resolve(directories[index]),currentBuild=allPreview||index===directories.length-1;
        const page=await browser.newPage({viewport:results.viewport});
        await page.goto(pathToFileURL(join(dir,'index.html')).href+'?test=1');
        await page.evaluate(()=>document.fonts.ready); await page.evaluate(seed);
        // Finish the seeded conversation's queued restore before positioning the measured link.
        await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
        await page.evaluate(()=>document.fonts.ready);
        const client=await page.context().newCDPSession(page);
        for(const action of (currentBuild?['preview','jump']:['jump'])){
         const measures=[];
         for(let i=0;i<7;i++){
          const link=page.locator('#bench-6 .equation-ref').last();
          await link.evaluate(el=>el.scrollIntoView({block:'center'}));
          await page.waitForTimeout(150);
          const doubleClick=currentBuild&&action==='jump';
          const idle=await page.evaluate(async doubleClick=>{
            const settled=async()=>{
              await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
              await new Promise(resolve=>setTimeout(resolve,200));
              document.getElementById('reader').getBoundingClientRect();
            };
            const empty=performance.now(); await settled(); const idle=performance.now()-empty;
            let start;
            document.addEventListener('click',()=>{start=performance.now();},{capture:true,once:true});
            window.addEventListener(doubleClick?'dblclick':'click',()=>{
              document.getElementById('reader').getBoundingClientRect();
              const sync=performance.now()-start;
              window.sample=settled().then(()=>({sync,completeMs:performance.now()-start}));
            },{once:true});
            return idle;
          },doubleClick);
          await client.send('HeapProfiler.startSampling',{samplingInterval:1024});
          const before=await link.evaluate(el=>el.getBoundingClientRect().top);
          if(doubleClick)await link.dblclick();else await link.click();
          const sample=await page.evaluate(()=>window.sample);
          const profile=await client.send('HeapProfiler.stopSampling');
          const surface=await page.evaluate(()=>{
            const r=document.getElementById('reader').getBoundingClientRect();
            const e=document.getElementById('bench-6-eq-1').getBoundingClientRect();
            return {preview:!!document.getElementById('equation-preview'),targetVisible:e.top>=r.top&&e.bottom<=r.bottom,
              visibleControls:[...document.querySelectorAll('button,input,textarea,select,summary')].filter(c=>{
                const b=c.getBoundingClientRect();return b.width&&b.height&&b.right>0&&b.bottom>0&&b.top<innerHeight&&b.left<innerWidth;
              }).length};
          });
          const delta=(await link.evaluate(el=>el.getBoundingClientRect().top))-before;
          if(surface.preview!==(action==='preview') || (action==='preview'?Math.abs(delta)>1:!surface.targetVisible))throw Error(JSON.stringify({index,action,surface,delta}));
          if(i>=2)measures.push({...sample,idle,correctedMs:sample.completeMs-idle,delta,retainedBytes:bytes(profile.profile.head),...surface});
          await page.keyboard.press('Escape');
         }
         results.samples.push({round,index,action,indexHash:hash(join(dir,'index.html')),measures});
        }
        await page.close();
      }
    }
    writeFileSync(resolve(out),JSON.stringify(results,null,2)+'\n');
    for(let index=0;index<directories.length;index++)for(const action of ['preview','jump']){
      const samples=results.samples.filter(x=>x.index===index&&x.action===action).flatMap(x=>x.measures);
      if(samples.length)console.log(index,action,...['sync','correctedMs','retainedBytes','visibleControls','delta'].map(key=>median(samples.map(m=>m[key]))));
    }
  }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});

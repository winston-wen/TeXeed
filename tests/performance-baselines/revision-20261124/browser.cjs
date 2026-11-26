'use strict';
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),{execFileSync}=require('node:child_process'),{pathToFileURL}=require('node:url');
const assert=require('node:assert/strict'),{chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const here=path.resolve(__dirname,'../../../prototypes/ai-chat-v02'),base=process.argv[2]||'0ea29064',destination=process.argv[3];
const withStrong=process.argv.includes('--revision-strong');
const withInlineFormulas=withStrong||process.argv.includes('--inline-formulas');
function baseline(){
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'texeed-revision-perf-')),files=execFileSync('git',['ls-tree','--full-tree','--name-only',base+':prototypes/ai-chat-v02'],{cwd:here,encoding:'utf8'}).trim().split(/\r?\n/);
 for(const name of files){
  if(['vendor','fonts','assets'].includes(name)){fs.symlinkSync(path.join(here,name),path.join(root,name),'junction');continue;}
  if(name==='index.html'||(name.endsWith('.js')&&!name.endsWith('test.js'))||name.endsWith('.css'))fs.writeFileSync(path.join(root,name),execFileSync('git',['show',base+':prototypes/ai-chat-v02/'+name],{cwd:here,maxBuffer:8388608}));
 }
 return root;
}
async function run(browser,folder,isNew){
 const page=await browser.newPage({viewport:{width:1280,height:900}}),c=await page.context().newCDPSession(page);
 try{
  await page.goto(pathToFileURL(path.join(folder,'index.html')).href+'?test=1');await page.evaluate(()=>document.fonts.ready);
  await page.evaluate(({withInlineFormulas,withStrong})=>{
   const chat=current();chat.draft='保留草稿';chat.entries=[];chat.turns=Array.from({length:36},(_,i)=>({id:'perf-'+i,question:'第 '+(i+1)+' 轮问题',answer:'# 结论\n\n'+Array.from({length:6},(_,j)=>'第 '+(j+1)+' 段。'+'计算之后核对结果。'.repeat(7)+(withInlineFormulas?' 行内 $x+1$ 和 \\(y^2\\)。':'' )).join('\n\n'),segments:[]}));
   if(withStrong){
     const turn=chat.turns[17];
     turn.answer=turn.answer.replaceAll('行内 $x+1$ 和 \\(y^2\\)。','要点：** \\eqref{eq:sum} 用 $x+1$ 和 \\(y^2\\) 恢复。** 再核对。')+'\n\n$$w=x+1\\label{eq:sum}$$';
   }
   renderConversation();const reader=$('reader');
   window.perform=async op=>{
    const twoFrames=()=>new Promise(r=>requestAnimationFrame(()=>requestAnimationFrame(r)));
    if(op==='render'){reader.scrollTop=0;await twoFrames();}
    const start=performance.now(),node=document.getElementById('perf-35-p1');
    if(op==='render')renderTurns();
    if(op==='open')document.querySelector('[data-revise="perf-17"]').click();
    if(op==='save'||op==='formula'){
      const field=document.querySelector('.revision-row[data-id="perf-17-'+(withInlineFormulas?'p1':'p0')+'"] .words');
      field.focus();
      if(op==='formula'){
        const range=document.createRange();range.selectNodeContents(field);const selection=getSelection();selection.removeAllRanges();selection.addRange(range);
        document.execCommand('insertText',false,field.textContent.replace(/x\+[12]/,match=>match==='x+1'?'x+2':'x+1'));
      }else document.execCommand('insertText',false,'改');
      const finishButton=field.closest('.revision-editor').querySelector('.revision-toolbar .revision-primary');
      if(finishButton)finishButton.click();
      else field.dispatchEvent(new KeyboardEvent('keydown',{key:'s',ctrlKey:true,bubbles:true,cancelable:true}));
      if(document.querySelector('.revision-editor'))throw Error('保存未完成整理');
      if(node!==document.getElementById('perf-35-p1'))throw Error('保存重建其他轮次');
    }
    reader.getBoundingClientRect();await twoFrames();
    return {ms:Math.round((performance.now()-start)*10)/10,nodes:document.querySelectorAll('#thread *').length,visible:Array.from(reader.querySelectorAll('.passage,.revision-row,.revision-open,.revision-toolbar button,.words')).filter(el=>{const r=el.getBoundingClientRect(),b=reader.getBoundingClientRect();return r.height>0&&r.bottom>b.top&&r.top<b.bottom;}).length};
   };
  },{withInlineFormulas,withStrong});
  await page.waitForTimeout(300);const records={};
  const ops=isNew||withInlineFormulas?['render','open','save']:['render'];
  for(let i=-1;i<7;i++)for(const op of ops){const result=await page.evaluate(op=>perform(op),op);if(i>=0)(records[op]??=[]).push(result);}
  for(const op of ops){
   if(op==='save')await page.evaluate(()=>perform('open'));
   await page.waitForTimeout(300); // Settle writes scheduled by the preceding action.
   await c.send('HeapProfiler.collectGarbage');await c.send('HeapProfiler.startSampling',{samplingInterval:1024,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
   await page.evaluate(op=>perform(op),op);await page.waitForTimeout(300);
   const {profile}=await c.send('HeapProfiler.stopSampling');records[op+'SampledAllocationBytes']=profile.samples.reduce((n,s)=>n+s.size,0);
  }
  if(isNew&&withInlineFormulas&&!withStrong){
   records.formula=[];
   for(let i=-1;i<7;i++){
    await page.evaluate(()=>perform('open'));const sample=await page.evaluate(()=>perform('formula'));
    if(i>=0)records.formula.push(sample);
   }
   await page.evaluate(()=>perform('open'));
   await page.waitForTimeout(300);
   await c.send('HeapProfiler.collectGarbage');await c.send('HeapProfiler.startSampling',{samplingInterval:1024,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
   await page.evaluate(()=>perform('formula'));await page.waitForTimeout(300);
   const {profile}=await c.send('HeapProfiler.stopSampling');records.formulaSampledAllocationBytes=profile.samples.reduce((n,s)=>n+s.size,0);
  }
  if(withStrong){
   records.settledAllocation={open:[],save:[]};
   for(let i=0;i<5;i++)for(const op of ['open','save']){
    await page.waitForTimeout(350);
    await c.send('HeapProfiler.collectGarbage');
    await c.send('HeapProfiler.startSampling',{samplingInterval:512,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
    await page.evaluate(op=>perform(op),op);await page.waitForTimeout(350);
    const {profile}=await c.send('HeapProfiler.stopSampling');
    records.settledAllocation[op].push(profile.samples.reduce((sum,sample)=>sum+sample.size,0));
   }
   records.editorControls=await page.evaluate(async()=>{
     document.getElementById('perf-17-p1').scrollIntoView({block:'center'});await perform('open');
     const root=document.querySelector('.revision-editor'),bounds=document.getElementById('reader').getBoundingClientRect();
     return {visibleButtonsAndSelectors:[...root.querySelectorAll('button,select')].filter(el=>{
       const r=el.getBoundingClientRect();return r.height&&r.bottom>bounds.top&&r.top<bounds.bottom;
     }).length,toolbarHeight:root.querySelector('.revision-toolbar')?.getBoundingClientRect().height??0};
   });
  }
  return records;
 }finally{await page.close();}
}
(async()=>{
 const old=baseline(),browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 try{
  const result={baseline:base,chrome:browser.version(),viewport:[1280,900],fixture:withStrong?'36 turns, 12 inline expressions per turn, six edited spans across eqref and inline math in turn 18':withInlineFormulas?'36 turns, seven passages each, 12 inline expressions per turn':'36 turns, seven passages each',before:await run(browser,old,false),after:await run(browser,here,true)};
  if(destination)fs.writeFileSync(destination,JSON.stringify(result,null,2)+'\n');
  for(const [version,data] of Object.entries({before:result.before,after:result.after}))for(const [op,values] of Object.entries(data))if(Array.isArray(values)){const sorted=values.map(v=>v.ms).sort((a,b)=>a-b);console.log(version,op,'median ms',sorted[3],'nodes',values[0].nodes,'visible',values[0].visible,'sampled bytes',data[op+'SampledAllocationBytes']);}
  if(!withInlineFormulas)assert(result.after.save.every(v=>v.nodes===result.after.render[0].nodes));
 }finally{await browser.close();if(path.dirname(old)!==os.tmpdir()||!path.basename(old).startsWith('texeed-revision-perf-'))throw Error('Unexpected temporary directory');for(const name of ['vendor','fonts','assets']){if(fs.existsSync(path.join(old,name)))fs.unlinkSync(path.join(old,name));};fs.rmSync(old,{recursive:true,force:true});}
})().catch(e=>{console.error(e);process.exitCode=1;});

'use strict';
const fs=require('node:fs'),path=require('node:path'),{pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');

// Run the same confirmation against both builds. CDP CPU time excludes the
// simulated response delay; settled wall time includes it and two paint frames.
async function measure(browser,index){
  const page=await browser.newPage({viewport:{width:1250,height:900}});
  const cdp=await page.context().newCDPSession(page),samples=[];
  async function cpu(){
    const {metrics}=await cdp.send('Performance.getMetrics');
    return metrics.find(metric=>metric.name==='TaskDuration').value*1000;
  }
  try{
    await page.goto(pathToFileURL(path.resolve(index)).href+'?test=1');
    await cdp.send('Performance.enable');
    for(let iteration=0;iteration<9;iteration++){
      await page.evaluate(()=>{
        const conversation=current();
        conversation.turns=[{id:'measured',question:'怎样求周长？',segments:[],answer:'',
          decision:{id:'side',question:'想先看哪种说明？',options:['推导','例子'],draft:'例子',results:['周长等于边长的四倍。','边长为 3，周长为 12。']}}];
        conversation.entries=[];conversation.draft='独立草稿';
        state.reviewPinnedTurns=[];state.reviewTemporaryTurns=[];renderConversation();persist();
      });
      await page.waitForFunction(()=>!restoring);await page.evaluate(()=>document.fonts.ready);
      await cdp.send('HeapProfiler.collectGarbage');
      await cdp.send('HeapProfiler.startSampling',{samplingInterval:4096,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
      const startCpu=await cpu();
      const sample=await page.evaluate(async()=>{
        const root=document.querySelector('#measured'),start=performance.now();
        const countControls=()=>[...document.querySelectorAll('#measured :is(button,input,textarea,select)')].filter(el=>el.getClientRects().length).length;
        root.querySelector('[data-decision-action=confirm]').click();
        const clickMs=performance.now()-start,controlsAfterClick=countControls(),pendingLamps=document.querySelectorAll('#measured .thinking-dot').length;
        const completed=()=>current().turns[0].answeredAt;
        if(!completed())await new Promise(resolve=>{
          const observer=new MutationObserver(()=>{if(completed()){observer.disconnect();resolve();}});
          observer.observe(document.querySelector('#thread'),{childList:true,subtree:true});
        });
        await new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)));
        const settledMs=performance.now()-start,latest=document.querySelector('#measured');
        if(!latest.innerText.includes('边长为 3，周长为 12。')||latest.querySelector('.thinking-dot'))throw Error('续答未完成');
        return {clickMs,settledMs,pendingLamps,controlsAfterClick,finalControls:countControls()};
      });
      const cpuMs=await cpu()-startCpu;
      const {profile}=await cdp.send('HeapProfiler.stopSampling');
      if(iteration>=2)samples.push({...sample,cpuMs,sampledBytes:profile.samples.reduce((total,s)=>total+s.size,0)});
    }
  }finally{await page.close();}
  return samples;
}
(async()=>{
  const [previous,output]=process.argv.slice(2),browser=await chromium.launch({headless:true,executablePath:process.env.TEXEED_BROWSER_PATH});
  try{
    const report={baseline:'bc4caa1',chromium:browser.version(),viewport:[1250,900],scenario:'Confirm the same selected answer on one saved question. Two warmups, seven measured runs. The current prototype uses the initial simulated request delay (1700 ms); CPU and JS click timings exclude the idle wait.',
      previous:await measure(browser,previous),current:await measure(browser,path.join(__dirname,'index.html'))};
    fs.mkdirSync(path.dirname(output),{recursive:true});
    fs.writeFileSync(output,JSON.stringify(report,null,2)+'\n');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

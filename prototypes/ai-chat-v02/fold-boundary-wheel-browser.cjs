'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs');

module.exports=async function checkFoldBoundary(page){
  const reader=page.locator('#reader');
  await page.evaluate(()=>{
    newConversation();
    const paragraphs=count=>Array.from({length:count},(_,n)=>'推导的第 '+(n+1)+' 段，核对计算用到的条件。').join('\n\n');
    current().turns=Array.from({length:10},(_,i)=>({id:'wheel-'+(i+1),question:'第 '+(i+1)+' 轮的问题',answer:paragraphs(i===0?120:10),
      collapsed:i>=1&&i<=4,foldGroup:i>=1&&i<=4?'boundary':undefined}));
    current().turns[5].answer='参见公式 \\eqref{10:eq:result}。\n\n'+paragraphs(25);
    current().turns[9].answer+='\n\n$$A=B+C\\label{eq:result}$$';
    current().draft='尚未发送的草稿';renderConversation();persist();
  });
  await page.waitForFunction(()=>!restoring);
  const link=page.locator('#wheel-6 .equation-ref');
  const center=()=>link.evaluate(el=>el.scrollIntoView({block:'center'}));
  await center();
  const box=await reader.boundingBox();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);
  const cdp=await page.context().newCDPSession(page);
  const results=[];
  const runWheel=async delta=>{
    const before=await reader.evaluate(el=>el.scrollTop);
    for(const d of delta)await page.mouse.wheel(0,d);
    const wanted=before+delta.reduce((n,d)=>n+d,0);
    await page.waitForFunction(wanted=>Math.abs($('reader').scrollTop-wanted)<2,wanted,{timeout:3000});
  };
  for(let i=0;i<(process.env.TEXEED_BOUNDARY_PERF?9:1);i++){
    await center();await page.waitForTimeout(200);
    if(process.env.TEXEED_BOUNDARY_PERF){
      await cdp.send('HeapProfiler.collectGarbage');
      await cdp.send('HeapProfiler.startSampling',{samplingInterval:4096,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
    }
    await page.evaluate(()=>{
      window.wheelTrace=[];window.anchorCaptureCount=0;window.anchorCaptureMs=0;
      window.capturePosition=readingPosition;
      readingPosition=function(){const start=performance.now();try{return capturePosition();}finally{anchorCaptureMs+=performance.now()-start;anchorCaptureCount++;}};
      const start=performance.now(),frames=wheelTrace;
      requestAnimationFrame(function sample(){if(window.wheelTrace!==frames)return;
        frames.push({ms:performance.now()-start,top:$('reader').scrollTop,first:document.elementFromPoint($('reader').getBoundingClientRect().left+75,$('reader').getBoundingClientRect().top+5)?.closest('.turn,.fold-line')?.id});
        requestAnimationFrame(sample);
      });
    });
    await runWheel([-240,-240,-240,-240]);
    const lastUp=await reader.evaluate(el=>el.scrollTop);await page.waitForTimeout(250);
    assert.equal(await reader.evaluate(el=>el.scrollTop),lastUp,'停止向上滚动后应留在首轮末段');
    await runWheel([240,240,-120]);await page.waitForTimeout(250);
    const sample=await page.evaluate(()=>{
      readingPosition=capturePosition;
      const frames=wheelTrace;window.wheelTrace=null;
      const writes=frames.map(f=>f.top).filter((v,i,a)=>!i||v!==a[i-1]);
      const frameGaps=frames.slice(1).map((f,i)=>f.ms-frames[i].ms).sort((a,b)=>a-b);
      const box=$('reader').getBoundingClientRect(),controls=[...$('reader').querySelectorAll('button,input,select,textarea')].filter(el=>{const r=el.getBoundingClientRect();return r.height&&r.bottom>box.top&&r.top<box.bottom;}).length;
      return {writes,firstTurns:[...new Set(frames.map(f=>f.first).filter(Boolean))],anchorCaptureCount,anchorCaptureMs,controls,frameP95:frameGaps[Math.floor(.95*frameGaps.length)]};
    });
    assert(sample.firstTurns.includes('wheel-1'),'应已滚过第 2~5 轮，进入首轮');
    const signs=sample.writes.slice(1).map((top,i)=>Math.sign(top-sample.writes[i])).filter((x,i,a)=>!i||x!==a[i-1]);
    assert.deepEqual(signs,[-1,1,-1],'录像中的连续越界、停下和反向操作应保留方向');
    if(process.env.TEXEED_BOUNDARY_PERF){
      const {profile}=await cdp.send('HeapProfiler.stopSampling');
      sample.sampledBytes=profile.samples.reduce((sum,s)=>sum+s.size,0);
      if(i>=2)results.push(sample);
    }
  }
  if(process.env.TEXEED_BOUNDARY_PERF)fs.writeFileSync(process.env.TEXEED_BOUNDARY_PERF,JSON.stringify({chromium:page.context().browser().version(),viewport:page.viewportSize(),scenario:'Long first turn, turns 2 to 5 folded together. Four -240 wheel impulses, pause, two +240 and one -120; includes both 250 ms idle periods. Two warmups and seven samples.',samples:results},null,2)+'\n');
  await center();
  await page.evaluate(()=>{
    window.oldNavigation=[];
    document.addEventListener('dblclick',()=>{
      const raf=window.requestAnimationFrame;
      window.requestAnimationFrame=callback=>{oldNavigation.push(callback);return 0;};
      window.addEventListener('dblclick',()=>{window.requestAnimationFrame=raf;},{once:true});
    },{capture:true,once:true});
  });
  await link.dblclick();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);
  await runWheel([-240,-240,-240]);await page.waitForTimeout(180);
  const rested=await reader.evaluate(el=>el.scrollTop);
  await page.evaluate(()=>{if(!oldNavigation.length)throw Error('未捕获待处理导航');oldNavigation.splice(0).forEach(cb=>cb(performance.now()));});
  await page.waitForTimeout(160);
  assert.equal(await reader.evaluate(el=>el.scrollTop),rested,'延迟执行的旧导航不能覆盖实际滚轮位置');
  if(process.env.TEXEED_BOUNDARY_SCREENSHOT)await reader.screenshot({path:process.env.TEXEED_BOUNDARY_SCREENSHOT});
  await center();await link.dblclick();
  await page.waitForFunction(()=>{
    const eq=document.querySelector('#wheel-10 .equation'),r=eq.getBoundingClientRect(),v=$('reader').getBoundingClientRect();
    return r.top>=v.top&&r.bottom<=v.bottom;
  });
  await page.locator('#composer').fill('请改写第 6 轮，让例子更清楚。');
  await page.locator('#send').click();
  const question=page.locator('.decision-question');
  await question.locator('textarea').fill('推导的第 10 段，核对计算用到的条件。');
  await question.getByRole('button',{name:'确认并继续'}).click();
  await page.mouse.move(box.x+box.width/2,box.y+box.height/2);
  await runWheel([-240,-240,-240,-240]);
  const where=await reader.evaluate(el=>el.scrollTop);
  await question.waitFor();assert.match(await question.innerText(),/什么文字/);
  assert.equal(await reader.evaluate(el=>el.scrollTop),where,'后台生成下一题时不能从用户当前位置滚回原轮次');
  console.log('PASS: long first turn, folded 2–5, repeated native wheel, rest/reversal, cancelled stale navigation, fresh explicit jump');
};

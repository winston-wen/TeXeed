'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {chromium} = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

(async () => {
  const browser = await chromium.launch({headless:true, executablePath:process.env.TEXEED_BROWSER_PATH});
  try {
    const page = await browser.newPage({viewport:{width:960,height:820},deviceScaleFactor:1});
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.join(process.env.TEXEED_PROTOTYPE || __dirname,'index.html')).href + '?test=1');
    await page.evaluate(async () => {
      await document.fonts.ready;
      newConversation();
      current().turns = Array.from({length:48}, (_, i) => ({
        id:'reading-'+i, question:'第 '+(i+1)+' 轮讨论', answeredAt:'2026-11-26T09:00:00Z',
        collapsed:[17,18,20,21,22,23,42,43].includes(i), foldGroup:i===22||i===23?'stored':i===42||i===43?'other':undefined,
        answer:'本轮讨论第 '+(i+1)+' 项条件。' + '\n\n' + ('内容与公式 $q^2$。'.repeat(8))
      }));
      current().turns[19].answer = '# 推导\n\n'+Array.from({length:20},(_,n)=>'第 '+n+' 段推导，说明变量并给出近似条件。').join('\n\n')+
        '\n\n$$\\begin{aligned}f(x)&=x^2+1,\\\\g(x)&=2x.\\end{aligned}$$\n\n在这个位置继续阅读。';
      current().turns[22].answer = '唯一搜索命中样本';
      current().turns[42].answer = '最后一组查找目标';
      current().draft = '保留这里的草稿';
      $('composer').value = current().draft;
      renderTurns();
    });
    const reader = page.locator('#reader');
    const formula = page.locator('#reading-19 .equation');
    const anchor = page.locator('#reading-19 .passage').last();
    await page.evaluate(() => new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
    await page.evaluate(() => document.fonts.ready);
    await formula.evaluate(el=>el.scrollIntoView({block:'center'}));
    await reader.focus();
    const settle = () => page.evaluate(() => new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(done))));
    await settle();

    await page.evaluate(() => { window.inspectReadingFrame = () => {
      const reader = document.getElementById('reader'), viewport = reader.getBoundingClientRect();
      const rows = [...reader.querySelectorAll('article.turn')];
      let previous = -Infinity;
      for(const row of rows) {
        const b = row.getBoundingClientRect();
        if(b.top < previous - 1) throw Error('Overlapping turn '+row.id);
        previous=b.bottom;
        if(b.bottom < viewport.top || b.top > viewport.bottom) continue;
        for(const item of row.querySelectorAll('.question,.passage,.fold-line')) {
          const r=item.getBoundingClientRect();
          if(!r.height || r.width < 1) continue;
          if(r.top < b.top-1 || r.bottom > b.bottom+1) throw Error('Painted content outside its turn '+item.id);
          const y=r.top+Math.min(12,r.height/2), x=r.left+Math.min(110,r.width/2);
          if(y>viewport.top+1 && y<viewport.bottom-1 && document.elementFromPoint(x,y)?.closest('.turn') !== row)
            throw Error('Hit test at '+item.id+' found '+document.elementFromPoint(x,y)?.outerHTML.slice(0,90));
        }
      }
      const anchor=rows.find(row=>row.id==='reading-19').querySelector('.passage:last-child');
      return {y:anchor.getBoundingClientRect().top,top:viewport.top,bottom:viewport.bottom,offset:reader.scrollTop};
    }; });
    const frame = () => page.evaluate(()=>window.inspectReadingFrame());
    const initial = await frame();
    if(process.env.TEXEED_READER_CHROME_PERF){
      const cdp=await page.context().newCDPSession(page),samples=[];
      const bytes=node=>node.selfSize+node.children.reduce((sum,child)=>sum+bytes(child),0);
      for(let i=0;i<9;i++) for(const key of ['Control+f','Escape']){
        const idle=await page.evaluate(async()=>{
          const settled=()=>new Promise(done=>requestAnimationFrame(()=>requestAnimationFrame(()=>setTimeout(done,120))));
          const idleStart=performance.now();
          await settled();
          const idle=performance.now()-idleStart;
          window.chromeMeasure=new Promise(resolve=>{
            function keydown(event){
              if(!['f','Escape'].includes(event.key))return;
              window.removeEventListener('keydown',keydown,true);
              const start=performance.now();
              window.addEventListener('keydown',async()=>{
                document.getElementById('reader').getBoundingClientRect();
                const syncMs=performance.now()-start;
                await settled();
                resolve({ms:performance.now()-start,syncMs});
              },{once:true});
            }
            window.addEventListener('keydown',keydown,true);
          });
          return idle;
        });
        await cdp.send('HeapProfiler.startSampling',{samplingInterval:256,includeObjectsCollectedByMajorGC:true,includeObjectsCollectedByMinorGC:true});
        await page.keyboard.press(key);
        const result=await page.evaluate(()=>window.chromeMeasure);
        const sample=await cdp.send('HeapProfiler.stopSampling');
        const controls=await page.evaluate(()=>[...document.querySelectorAll('button,input,select,textarea')].filter(el=>{
          const r=el.getBoundingClientRect();
          return r.height&&r.width&&r.top<innerHeight&&r.bottom>0&&r.left<innerWidth&&r.right>0;
        }).length);
        if(i>=2)samples.push({key,...result,idle,correctedMs:result.ms-idle,controls,
          sampledBytes:bytes(sample.profile.head),displacement:(await frame()).y-initial.y});
      }
      fs.writeFileSync(process.env.TEXEED_READER_CHROME_PERF,JSON.stringify({chromium:browser.version(),samples},null,2)+'\n');
      return;
    }
    const mathBox = await formula.boundingBox(), anchorBox = await anchor.boundingBox();
    const clip = {x:Math.floor(Math.min(mathBox.x,anchorBox.x)), y:Math.floor(mathBox.y-3),
      width:Math.ceil(Math.max(mathBox.width,anchorBox.width))+1,height:Math.ceil(anchorBox.y+anchorBox.height-mathBox.y+6)};
    const dir = process.env.TEXEED_READER_CHROME_SCREENSHOTS;
    if(dir) fs.mkdirSync(dir,{recursive:true});
    const picture = async name => page.screenshot({clip,path:dir?path.join(dir,name+'.png'):undefined});
    const comparePaint = async name => page.evaluate(async png=>{
      const image=await createImageBitmap(await (await fetch('data:image/png;base64,'+png)).blob());
      const canvas=document.createElement('canvas'); canvas.width=image.width; canvas.height=image.height;
      const ctx=canvas.getContext('2d'); ctx.drawImage(image,0,0);
      const {data}=ctx.getImageData(0,0,canvas.width,canvas.height);
      const width=image.width, ink=new Uint8Array(width*image.height);
      for(let i=0;i<ink.length;i++)
        ink[i]=Number(Math.abs(data[4*i]-data[0])+Math.abs(data[4*i+1]-data[1])+Math.abs(data[4*i+2]-data[2])>90);
      const original=window.originalReaderPixels??=ink;
      let different=0,total=0;
      for(let i=0;i<ink.length;i++){
        // Fractional CSS line heights can round the same ink to either adjacent device pixel.
        if(ink[i]&&!original[i]&&!original[i-width]&&!original[i+width])different++;
        if(original[i]&&!ink[i]&&!ink[i-width]&&!ink[i+width])different++;
        total+=original[i];
      }
      return {different,total};
    },(await picture(name)).toString('base64'));
    assert((await comparePaint('before')).total>300,'Expected visible formula and reading text in the measured area');
    async function stable(label){
      await settle();
      const observed=await frame();
      assert(Math.abs(observed.y-initial.y)<=1,label+' moved the reading line '+JSON.stringify({initial,observed}));
      const paint=await comparePaint(label);
      assert(paint.different/paint.total<.01,label+' altered the painted formula or text '+JSON.stringify(paint));
      return observed;
    }
    await page.keyboard.press('Control+f');
    assert(await page.locator('#search-input').evaluate(el=>document.activeElement===el));
    const opened=await stable('search-open');
    assert(opened.top>initial.top+10,'Search must occupy space above the reader');
    if(dir) await page.screenshot({path:path.join(dir,'search-window.png')});
    await page.keyboard.press('Escape');
    assert(await page.locator('#search').isHidden());
    assert(await reader.evaluate(el=>document.activeElement===el),'Closing search lost keyboard focus in the reader');
    await stable('search-closed');
    for(let i=0;i<4;i++){
      await page.keyboard.press('Control+f'); await settle();
      await page.keyboard.press('Escape'); await settle();
    }
    await stable('search-repeated');
    await page.keyboard.press('Control+f'); await settle();
    const stationary=await frame();
    const area=await reader.boundingBox();
    await page.mouse.move(area.x+area.width/2,area.y+area.height/2);
    await page.evaluate(()=>{
      window.wheelFrames=[];window.sampleWheel=true;
      function sample(){
        if(!window.sampleWheel)return;
        window.wheelFrames.push(window.inspectReadingFrame());
        requestAnimationFrame(sample);
      }
      requestAnimationFrame(sample);
    });
    await page.mouse.wheel(0,360);
    await page.waitForFunction(start=>document.getElementById('reader').scrollTop>start+350,stationary.offset);
    await page.mouse.wheel(0,-360);
    await page.waitForFunction(start=>Math.abs(document.getElementById('reader').scrollTop-start)<1,stationary.offset);
    await settle();
    const wheelFrames=await page.evaluate(()=>{window.sampleWheel=false;return window.wheelFrames;});
    assert(wheelFrames.some(sample=>sample.offset>=stationary.offset+350),'Wheel never moved across the folded rows');
    for(const sample of wheelFrames)
      assert(Math.abs(sample.y-stationary.y+sample.offset-stationary.offset)<1,'Wheel movement and drawn text diverged');
    await page.keyboard.press('Escape');
    await stable('wheel-closed');

    await anchor.click({button:'right'});
    await page.locator('#annotate').click();
    assert(await page.locator('#thread').evaluate(el=>el.classList.contains('answer-marking')));
    const marking=await stable('marking');
    assert(marking.bottom<initial.bottom-5,'The annotation status must occupy space below the reader');
    await page.keyboard.press('Escape');
    await stable('message');
    await page.locator('#status').waitFor({state:'hidden',timeout:6000});
    await stable('message-closed');

    async function checkSearchResult(index,phase){
      await page.waitForFunction(i=>!current().turns[i].collapsed,index);
      await settle();
      const result=await frame(),found=await page.locator('#reading-'+index+' .question').boundingBox();
      const visible=found&&found.y>=result.top-1&&found.y+found.height<=result.bottom+1;
      if(!visible&&dir)await page.screenshot({path:path.join(dir,'search-navigation-'+phase+'-failed.png')});
      assert(visible,'Search did not reveal its compacted target '+JSON.stringify({phase,index,viewport:result,target:found}));
      assert(await page.evaluate(i=>current().turns[i+1].collapsed,index));
    }
    await page.keyboard.press('Control+f');await settle();
    await page.locator('#search-input').fill('唯一搜索命中样本');await checkSearchResult(22,'settled');
    await page.keyboard.press('Escape');await settle();
    // Chrome can deliver input after opening the bar but before the resize callback.
    await page.evaluate(()=>{
      document.activeElement.dispatchEvent(new KeyboardEvent('keydown',{key:'f',ctrlKey:true,bubbles:true,cancelable:true}));
      const input=document.getElementById('search-input');
      input.value='最后一组查找目标';input.dispatchEvent(new Event('input',{bubbles:true}));
    });
    await checkSearchResult(42,'same-frame');
    assert.equal(await page.locator('#composer').inputValue(),'保留这里的草稿');
    assert.deepEqual(errors,[]);
    console.log('PASS: Ctrl+F, escaping, opening and dismissing annotation status preserve painted math, text and adjacent folded turn boundaries. Search still reaches a compacted result.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

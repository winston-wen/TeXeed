'use strict';
const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
let chromium;
try{({chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright'));}
catch{console.log('SKIP emphasis color browser: Playwright unavailable');process.exit(0);}
(async()=>{
 const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});
 try{
  const page=await browser.newPage({viewport:{width:1000,height:820}}),errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  await page.goto(pathToFileURL(path.join(__dirname,'index.html')).href+'?test=1');
  await page.evaluate(()=>{
   const c=current();c.draft='原有草稿';c.entries=[];c.bookmarks=[];
   c.turns=[{id:'legacy-color',question:'请解释**这个问题**。',answer:'旧答案中的**重点内容**。',answerHeadingVersion:1,
    answerHtml:'<section class="passage" id="legacy-color-p0" data-block="legacy-color-p0"><div class="passage-body"><p>旧答案中的<strong>重点内容</strong>。</p></div><div class="margin-notes"></div></section>',segments:[]},
    {id:'fresh-color',question:'请继续。',prompt:'请继续。',annotations:[{quote:'来源中的 **重点内容** 和 [普通链接](https://example.test/)'}],answer:'普通文字 **出处也正在这里：**项目论文第 7 页。\n\n**公式 $x+1$： **下一步。\n\n[普通链接](https://example.test/)',segments:[]}];renderTurns();document.getElementById('reader').scrollTop=0;
  });
  await page.locator('#fresh-color .sent-summary').click();
  assert.equal(await page.locator('#fresh-color .snapshot-scroll strong').evaluate(node=>getComputedStyle(node).color),'rgb(85, 199, 238)');
  assert.equal(await page.locator('#fresh-color .snapshot-scroll a').getAttribute('href'),'https://example.test/');
  const previewLink=await page.locator('#fresh-color .snapshot-scroll a').evaluate(node=>({color:getComputedStyle(node).color,decoration:getComputedStyle(node).textDecorationLine}));
  assert.deepEqual(previewLink,{color:'rgb(216, 184, 118)',decoration:'underline'});
  const styles=await page.locator('.question strong,.answer strong').evaluateAll(nodes=>nodes.map(node=>({color:getComputedStyle(node).color,background:getComputedStyle(node).backgroundImage,weight:getComputedStyle(node).fontWeight,text:node.textContent})));
  assert.ok(styles.length>=4);for(const style of styles){assert.equal(style.color,'rgb(85, 199, 238)');assert.equal(style.background,'none');assert.equal(style.weight,'400');}
  assert.equal(await page.locator('#legacy-color-p0').count(),1);
  assert.equal(await page.evaluate(()=>current().draft),'原有草稿');
  assert.equal(await page.locator('.answer a').last().getAttribute('href'),'https://example.test/');
  assert.deepEqual(errors,[]);
  if(process.env.TEXEED_EMPHASIS_SCREENSHOT)await page.screenshot({path:process.env.TEXEED_EMPHASIS_SCREENSHOT});
  const branching=await browser.newPage({viewport:{width:1000,height:820}});
  await branching.goto(pathToFileURL(path.join(__dirname,'branching.html')).href+'?test=1');
  const branchColor=await branching.locator('.passage').first().evaluate(node=>{
   const strong=document.createElement('strong');strong.textContent='重点';node.append(strong);return getComputedStyle(strong).color;
  });
  assert.equal(branchColor,'rgb(85, 199, 238)');
  console.log('PASS: question, cached and new answer emphasis uses theme color, preserves weight, links and draft.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});

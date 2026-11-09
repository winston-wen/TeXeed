'use strict';
const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
let chromium;try{({chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright'));}catch{console.log('SKIP prototype retention: Playwright unavailable.');process.exit(0);}
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});try{
 const page=await browser.newPage({viewport:{width:1200,height:850}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 const legacyEntry={id:'legacy-pending',question:'旧批注数据',state:'提交中',selected:true,sources:[{file:'main.tex',text:'旧原文'}]};
 const legacyTurn={id:'legacy-turn',question:'原问题',prompt:'原问题',answer:'旧回答',sentAt:'2026-11-01T12:00:00+08:00',answeredAt:'2026-11-01T12:00:01+08:00',annotations:[{id:'archived',question:'历史内容'}],request:{prompt:'原问题',annotations:[{id:'archived',question:'历史内容'}]}};
 const legacy={sampleHistoryVersion:1,visualMediaVersion:1,active:'one',conversations:[{id:'one',title:'旧会话的长标题需要完整保留，不应该被原型截断修改',turns:[legacyTurn],entries:[legacyEntry],draft:'本次提问',permission:'完全访问'}],events:[]};
 await page.addInitScript(state=>{if(!sessionStorage.getItem('legacy-retention-initialized')){localStorage.setItem('texeed-ai-baseline-v02-test',JSON.stringify(state));sessionStorage.setItem('legacy-retention-initialized','1');}},legacy);
 await page.goto(pathToFileURL(path.join(__dirname,'index.html')).href+'?test=1');
 assert.equal(await page.locator('#composer').inputValue(),'本次提问');
 for(const id of ['mark-button','entries-button','entries-dialog','annotation-editor','annotate','marking'])assert.equal(await page.locator('#'+id).count(),0,id+' survived');
 const before=await page.evaluate(()=>JSON.parse(localStorage.getItem('texeed-ai-baseline-v02-test')));
 assert.equal(before.conversations[0].title,legacy.conversations[0].title);assert.deepEqual(before.conversations[0].entries,[legacyEntry]);assert.deepEqual(before.conversations[0].turns[0].annotations,legacyTurn.annotations);
 await page.locator('#send').click();await page.waitForFunction(()=>!document.querySelector('#stop').hidden);await page.waitForFunction(()=>document.querySelector('#stop').hidden,{timeout:10000});
 let current=await page.evaluate(()=>JSON.parse(localStorage.getItem('texeed-ai-baseline-v02-test')));
 assert.deepEqual(current.conversations[0].entries,[legacyEntry]);assert.equal(current.conversations[0].turns.at(-1).request.prompt,'本次提问');assert.equal('annotations' in current.conversations[0].turns.at(-1).request,false);
 await page.locator('#composer').fill('失败后重新发送的问题');await page.evaluate(()=>document.querySelector('#playback-mode').value='program-failure');await page.locator('#send').click();await page.waitForFunction(()=>document.querySelector('#stop').hidden,{timeout:10000});
 await page.locator('#composer').fill('等待期间的新草稿');await page.locator('[data-retry-turn]').last().click();
 current=await page.evaluate(()=>JSON.parse(localStorage.getItem('texeed-ai-baseline-v02-test')));assert.equal(current.conversations[0].turns.at(-1).request.prompt,'失败后重新发送的问题');assert.equal('annotations' in current.conversations[0].turns.at(-1).request,false);assert.equal(await page.locator('#composer').inputValue(),'等待期间的新草稿');assert.deepEqual(current.conversations[0].entries,[legacyEntry]);
 await page.reload();assert.equal(await page.locator('#composer').inputValue(),'等待期间的新草稿');current=await page.evaluate(()=>JSON.parse(localStorage.getItem('texeed-ai-baseline-v02-test')));assert.deepEqual(current.conversations[0].entries,[legacyEntry]);assert.deepEqual(current.conversations[0].turns[0].annotations,legacyTurn.annotations);
 if(process.env.TEXEED_SCREENSHOT_DIR){await page.screenshot({path:path.join(process.env.TEXEED_SCREENSHOT_DIR,'prototype-no-marks-wide.png')});await page.setViewportSize({width:420,height:820});await page.screenshot({path:path.join(process.env.TEXEED_SCREENSHOT_DIR,'prototype-no-marks-narrow.png')});}
 assert.deepEqual(errors,[]);console.log('PASS old entries and archived snapshots preserved; no circle UI or submission, retry retains next draft.');
 }finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});

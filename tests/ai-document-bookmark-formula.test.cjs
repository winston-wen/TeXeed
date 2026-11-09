'use strict';
const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});try{
 const page=await browser.newPage({viewport:{width:1000,height:820}});
 await page.addInitScript(()=>{window.messages=[];window.chrome={webview:{postMessage:m=>window.messages.push(m),addEventListener:(_,fn)=>window.hostSend=d=>fn({data:d})}}});
 await page.goto(pathToFileURL(path.join(__dirname,'../src/TeXeed.App/bin/Release/net10.0-windows/win-x64/Assets/AiDocument/document.html')).href);
 await page.waitForFunction(()=>window.messages.some(m=>m.type==='ready'));
 const source='\\documentclass{article}\\begin{document}\\section{面积 $x^2$}正文\\end{document}';
 await page.evaluate(source=>window.hostSend({type:'state',binding:'1',source,initialSource:source,revision:'r',authorSpans:[{start:0,length:source.length,role:'ai',author:'model'}],comments:[],question:'',userName:'用户',modelName:'model'}),source);
 await page.locator('.document-bookmark-ball').click();const labels=await page.locator('.document-bookmark-list button').allTextContents();
 assert.equal(labels.length,2);assert.match(labels[0],/\[1\].*面积 \$x\^2\$/);assert.equal((labels[0].match(/x\^2/g)||[]).length,1,'A heading formula was duplicated in the bookmark label');assert.match(labels[1],/\$x\^2\$/);await page.close();console.log('PASS heading formula bookmark contains one readable formula');
}finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});

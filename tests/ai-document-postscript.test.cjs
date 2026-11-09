'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {pathToFileURL} = require('node:url');
const {chromium} = require(process.env.TEXEED_PLAYWRIGHT_PATH || 'playwright');

(async () => {
 const browser = await chromium.launch({headless: true, executablePath: process.env.TEXEED_BROWSER_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});
 try {
  const page = await browser.newPage({viewport: {width: 1000, height: 820}});
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.addInitScript(() => { window.messages = []; window.chrome = {webview: {postMessage: message => window.messages.push(message), addEventListener: (_, handler) => window.hostSend = data => handler({data})}}; });
  await page.goto(pathToFileURL(path.join(__dirname, '../src/TeXeed.App/bin/Release/net10.0-windows/win-x64/Assets/AiDocument/document.html')).href);
  await page.waitForFunction(() => window.messages.some(message => message.type === 'ready'));
  const source = process.env.TEXEED_POSTSCRIPT_TEX_OUT ? fs.readFileSync(process.env.TEXEED_POSTSCRIPT_TEX_OUT, 'utf8') :
   '\\documentclass{article}\n\\begin{document}\n\n% texeed-postscript-begin\n附言（示例模型 Ω）：检查 50\\% 与 a\\_b。 \\#x \\$y\\$ \\verb|\\|path \\verb|^| \\verb|~| 继续核实。\n% texeed-postscript-end\n\n补充论证。\n\\end{document}';
  await page.evaluate(source => window.hostSend({type:'state', binding:'note-test', source, initialSource:source, revision:'1', authorSpans:[{start:0, length:source.length, role:'ai', author:'示例模型 Ω'}], comments:[], question:'测试', userName:'用户', modelName:'示例模型 Ω'}), source);
  const first = page.locator('#mentor-document .mentor-block').first();
  const text = await first.innerText();
  assert.match(text, /附言（示例模型 Ω）：检查 50% 与 a_b。 #x \$y\$ \\path \^ ~/);
  assert.doesNotMatch(text, /texeed-postscript|\\verb|\\%|\\_/);
  assert.match(await page.locator('#mentor-document').innerText(), /补充论证/);
  assert.deepEqual(errors, []);
  await page.screenshot({path:path.join(__dirname, '../artifacts/postscript-tests/rendered-note.png')});
  console.log('AI document postscript rendered as readable TeX text.');
 } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });

'use strict';
const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const url=pathToFileURL(path.join(__dirname,'../src/TeXeed.App/bin/Release/net10.0-windows/win-x64/Assets/AiDocument/document.html')).href;
const full=String.raw`\documentclass{article}
\usepackage{amsmath}
\newcommand{\RR}{\mathbb{R}}
\newcommand{\vect}[1]{\mathbf{#1}}
% \newcommand{\bad}{\wrong}
\begin{document}
\section{含 \emph{强调} 和 $x^2$ 的标题}接续正文：\textbf{重要条件}，价格为 \$5、\$8，比例 50\%，$\RR$ 与 $\vect{x}$。
\begin{itemize}
\item 第一条
\item 第二条：\emph{关键结论}
\begin{enumerate}
\item 嵌套第一条
\item 嵌套第二条
\end{enumerate}
\end{itemize}
\begin{equation}
x^2=4
\end{equation}
\begin{align}
a&=b+c\\
x&=y+z
\end{align}
\begin{alignat}{2}
x&=y & z&=w
\end{alignat}
\begin{lstlisting}[language=Python]
print(1)
\end{lstlisting}
插图 \includegraphics[width=0.5\linewidth]{images/plot.png}，文献 \cite{ref2026}。

危险图片 \includegraphics{../secret.png}，\includegraphics{images/.. /secret.png}，\includegraphics{images/trailing./secret.png}。

未知 \mymacro{原样保留}。
\end{document}`;
async function open(browser,source){const page=await browser.newPage({viewport:{width:1100,height:850}}),errors=[];await page.route('https://texeed-project.invalid/**',route=>route.abort());page.on('pageerror',e=>errors.push(e.message));await page.addInitScript(()=>{window.messages=[];window.chrome={webview:{postMessage:m=>window.messages.push(m),addEventListener:(_,fn)=>window.hostSend=d=>fn({data:d})}}});await page.goto(url);await page.waitForFunction(()=>window.messages.some(m=>m.type==='ready'));await page.evaluate(source=>window.hostSend({type:'state',binding:'1',source,initialSource:source,revision:'r1',authorSpans:[{start:0,length:source.length,role:'ai',author:'model'}],comments:[],userName:'用户',modelName:'model'}),source);return {page,errors};}
async function replaceInText(page,selector,oldText,newText){await page.locator(selector).evaluate((el,oldText)=>{const w=document.createTreeWalker(el,NodeFilter.SHOW_TEXT);let n;while((n=w.nextNode())){if(n.parentElement.closest('[data-tex]'))continue;const at=n.data.indexOf(oldText);if(at>=0){const r=document.createRange();r.setStart(n,at);r.setEnd(n,at+oldText.length);const s=getSelection();s.removeAllRanges();s.addRange(r);return;}}throw Error('text not found: '+oldText)},oldText);await page.keyboard.insertText(newText);await page.waitForFunction(()=>window.messages.some(m=>m.type==='edit'));return page.evaluate(()=>window.messages.filter(m=>m.type==='edit').at(-1));}
function apply(source,e){assert.equal(source.slice(e.start,e.start+e.removed.length),e.removed);return source.slice(0,e.start)+e.added+source.slice(e.start+e.removed.length)}
(async()=>{const browser=await chromium.launch({headless:true,executablePath:process.env.TEXEED_BROWSER_PATH||'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'});try{
 {
  const {page,errors}=await open(browser,full);const doc=page.locator('#mentor-document');await page.evaluate(()=>document.fonts.ready);const text=await doc.innerText();assert(!text.includes('documentclass')&&!text.includes('usepackage')&&!text.includes('begin{document}'));assert.match(text,/接续正文/);assert.match(text,/价格为 \$5、\$8/);assert.match(text,/比例 50%/);assert.equal(await page.locator('h2.mentor-block').count(),1);assert.equal(await page.locator('h2.mentor-block em').count(),1);assert.equal(await page.locator('h2.mentor-block .katex').count(),1);assert.equal(await page.locator('.mentor-block strong').count(),1);assert.equal(await page.locator('.mentor-block:has-text("第一条")').count(),2);assert.equal(await page.locator('.mentor-block[data-wrapper="display"] .katex').count(),3);assert.equal(await page.locator('.mentor-math-error').count(),0);assert.ok(await page.locator('.mentor-math[data-tex*="alignat"] .katex').count(),'alignat must not display its column-count argument as text');assert.equal(await page.locator('.mentor-image img').getAttribute('src'),'https://texeed-project.invalid/1/images/plot.png');await page.locator('.mentor-image-error').first().waitFor({state:'visible'});assert.match(await page.locator('.mentor-image-error').first().innerText(),/图片无法加载/);assert.match(text,/图片路径无效/);assert.equal(await page.locator('.mentor-image:has-text("图片路径无效")').count(),3);assert.match(text,/引用：ref2026/);assert.equal(await page.locator('p.mentor-block strong').evaluate(e=>getComputedStyle(e).fontWeight),'400');assert.equal(await page.locator('pre.mentor-block').innerText(),'print(1)');assert.match(text,/\\mymacro\{原样保留\}/);assert.equal(await page.locator('#mentor-source-view').inputValue(),full);await page.evaluate(source=>window.hostSend({type:'state',binding:'2',source,initialSource:source,revision:'r2',authorSpans:[{start:0,length:source.length,role:'ai',author:'model'}],comments:[],userName:'用户',modelName:'model'}),full);assert.equal(await page.locator('.mentor-image img').getAttribute('src'),'https://texeed-project.invalid/2/images/plot.png');assert.deepEqual(errors,[]);await page.screenshot({path:path.join(__dirname,'../artifacts/ai-document-rendering-040.png')});await page.close();
 }
 for(const [selector,oldText,newText] of [['p.mentor-block strong','重要条件','必要条件'],['p.mentor-block:has-text("接续正文")','接续正文','新正文'],['p.mentor-block:has-text("第二条：") em','关键结论','核心结论'],['h2.mentor-block em','强调','说明'],['p.mentor-block:has-text("嵌套第二条")','嵌套第二条','最后一条']]){
  const {page}=await open(browser,full);const edit=await replaceInText(page,selector,oldText,newText);assert.equal(apply(full,edit),full.replace(oldText,newText),`source round-trip for ${selector}`);assert.ok(await page.locator('.mentor-user').count()>0,'typed text must retain user font attribution');await page.close();
 }
 {
  const {page}=await open(browser,full);await page.locator('.mentor-image-error').first().waitFor({state:'visible'});page.once('dialog',d=>d.accept('\\includegraphics{images/new.png}'));await page.locator('.mentor-image').first().dblclick();await page.waitForFunction(()=>window.messages.some(m=>m.type==='edit'));const edit=await page.evaluate(()=>window.messages.find(m=>m.type==='edit'));assert.equal(apply(full,edit),full.replace('\\includegraphics[width=0.5\\linewidth]{images/plot.png}','\\includegraphics{images/new.png}'),'double click edits the whole source command');await page.close();
 }
 {
  const {page}=await open(browser,full);await page.locator('p.mentor-block strong').evaluate(el=>{const n=document.createTreeWalker(el,NodeFilter.SHOW_TEXT).nextNode(),r=document.createRange(),s=getSelection();r.setStart(n,0);r.setEnd(n,n.length);s.removeAllRanges();s.addRange(r);document.dispatchEvent(new Event('selectionchange'))});await page.locator('#mentor-comment-add').click();await page.locator('#mentor-comment-input').fill('请解释');await page.locator('#mentor-comment-save').click();await page.waitForFunction(()=>window.messages.some(m=>m.type==='comment'));const c=await page.evaluate(()=>window.messages.find(m=>m.type==='comment').comment);assert.equal(c.quote,'重要条件');assert.equal(c.start,full.indexOf('重要条件'));await page.close();
 }
 {
  const {page}=await open(browser,'');await page.evaluate(()=>window.hostSend({type:'preview',source:'\\documentclass{article}\n\\usepackage{amsmath}'}));assert.equal(await page.locator('.mentor-block').count(),0,'streamed preamble must not appear as document');await page.evaluate(()=>window.hostSend({type:'preview',source:'\\documentclass{article}\n\\begin{document}\n正文'}));assert.match(await page.locator('#mentor-document').innerText(),/正文/);await page.close();
 }
 {
  const fragment='\\section{无正文环境的旧片段}\n正文';const {page}=await open(browser,fragment);assert.match(await page.locator('#mentor-document').innerText(),/正文/);assert.equal(await page.locator('h2.mentor-block').count(),1);await page.close();
 }
 {
  const source='\\documentclass{article}\n% \\newcommand{\\bad}{1}\n\\begin{document}\n$\\bad$\n\\end{document}';const {page}=await open(browser,source);assert.equal(await page.locator('.mentor-math-error').count(),1,'a commented macro must remain undefined');await page.close();
 }
 {
  const source='\\begin{verbatim}\n\\begin{document}\nkeep this code\n\\end{document}\n\\end{verbatim}';const {page}=await open(browser,source);assert.match(await page.locator('pre.mentor-block').innerText(),/keep this code/);assert.equal(await page.locator('#mentor-source-view').inputValue(),source);await page.close();
 }
 {
  const source='\\documentclass{article}\\begin{document}单行正文\\end{document}';const {page}=await open(browser,source);assert.equal((await page.locator('#mentor-document').innerText()).trim(),'单行正文');assert.equal(await page.locator('#mentor-source-view').inputValue(),source);await page.close();
 }
 {
  const source='\\documentclass{article}\\newcommand{\\demo}{\\begin{document}}% \\begin{document}\n\\begin{document}真实正文 \\verb|\\end{document}| 继续正文\\end{document}';const {page}=await open(browser,source);const text=await page.locator('#mentor-document').innerText();assert.match(text,/真实正文/);assert.match(text,/继续正文/);assert.equal(await page.locator('#mentor-source-view').inputValue(),source);await page.close();
 }
 console.log('PASS document shell, balanced headings, nested styles/lists, escaped characters, equations, code and source-preserving edits/comment');
 }finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});

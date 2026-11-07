'use strict';
const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
const {chromium}=require(process.env.TEXEED_PLAYWRIGHT_PATH||'playwright');
const html=pathToFileURL(path.join(__dirname,'mentor.html')).href+'?test=1';
const source=String.raw`\documentclass{article}
\usepackage{amsmath}
\addbibresource{references.bib}
\begin{document}
\section{研究的 \emph{边界}：$x^2$}
这里讨论 \textbf{必要条件}；价格是 \$5，占比 100\%。

\subsection{推导}
根据定义有 $a+b=c$。
\begin{itemize}
\item 第一项说明前提。
\item 第二项包含 \emph{限制条件}。
\end{itemize}
\begin{equation}
x^2+y^2=z^2
\end{equation}

普通正文可以继续修改。

\begin{verbatim}
print("hello")
\end{verbatim}

\includegraphics{figures/model.png}
\bibliography{references}
\end{document}`;
(async()=>{const browser=await chromium.launch({headless:true,...(process.env.TEXEED_BROWSER_PATH?{executablePath:process.env.TEXEED_BROWSER_PATH}:{})});try{
 const context=await browser.newContext({viewport:{width:1200,height:820}}),page=await context.newPage(),errors=[];page.on('pageerror',e=>errors.push(e.message));await page.goto(html);await page.evaluate(source=>{MentorTest.setDocument(source);const w=MentorTest.workspace(),data=w.sessions.find(s=>s.id===w.active).data;data.draft='留待发送的草稿';localStorage.setItem('texeed-mentor-prototype-v2',JSON.stringify(w));},source);await page.reload();await page.evaluate(()=>document.fonts.ready);
 const doc=page.locator('#mentor-document'),rendered=await doc.innerText();assert.match(rendered,/研究的/);assert.match(rendered,/边界/);assert.match(rendered,/必要条件/);assert.match(rendered,/\$5/);assert.match(rendered,/100%/);assert.match(rendered,/第一项说明前提/);assert.match(rendered,/第二项包含/);assert.doesNotMatch(rendered,/\\(?:documentclass|usepackage|addbibresource|section|subsection|emph|textbf|item|begin|end)/);assert((await doc.locator('.katex').count())>=3,'Title, inline and display maths should render');assert.equal((await page.evaluate(()=>MentorTest.state())).source,source,'Displaying must not rewrite source');assert.equal(await page.locator('#mentor-prompt').inputValue(),'留待发送的草稿','Display update must preserve draft');assert.deepEqual(errors,[]);
 await page.screenshot({path:path.join(__dirname,'../../artifacts/mentor-display-wide.png')});await page.setViewportSize({width:420,height:850});await page.screenshot({path:path.join(__dirname,'../../artifacts/mentor-display-narrow.png')});
 await page.locator('#mentor-source-toggle').click();assert.equal(await page.locator('#mentor-source-view').inputValue(),source,'Source view must retain all TeX syntax');await page.locator('#mentor-source-toggle').click();assert.equal((await page.evaluate(()=>MentorTest.state())).source,source,'Switching views must preserve source');const paragraph=doc.locator('p').filter({hasText:'普通正文可以继续修改。'}).first();await paragraph.evaluate(el=>{const walker=document.createTreeWalker(el,NodeFilter.SHOW_TEXT);let n;while((n=walker.nextNode())){const i=n.data.indexOf('普通正文');if(i>=0){const r=document.createRange();r.setStart(n,i);r.setEnd(n,i+4);const sel=getSelection();sel.removeAllRanges();sel.addRange(r);return}}throw Error('paragraph not found')});await page.locator('#mentor-comment-add').click();await page.locator('#mentor-comment-input').fill('正文选区');await page.locator('#mentor-comment-save').click();let state=await page.evaluate(()=>MentorTest.state());assert.equal(state.source,source);assert.equal(state.source.slice(state.comments[0].start,state.comments[0].end),'普通正文','Comments must anchor to original TeX after document preamble');await paragraph.click();await page.keyboard.press('End');await page.keyboard.type('abc');state=await page.evaluate(()=>MentorTest.state());assert.equal(state.source,source.replace('普通正文可以继续修改。','普通正文可以继续修改。abc'),'Plain paragraph edit must preserve the surrounding TeX and external references');const unknown=String.raw`未知 \custommacro{<img src=x onerror=alert(1)>}`;await page.evaluate(s=>MentorTest.setDocument(s),unknown);assert.match(await doc.innerText(),/custommacro/,'Unknown syntax must remain visible');assert.equal(await doc.locator('img').count(),0,'Model output must be escaped');assert.equal((await page.evaluate(()=>MentorTest.state())).source,unknown);const oneLine='\\documentclass{article}\\begin{document}单行正文\\end{document}';await page.evaluate(s=>MentorTest.setDocument(s),oneLine);assert.equal((await doc.innerText()).trim(),'单行正文');assert.equal((await page.evaluate(()=>MentorTest.state())).source,oneLine);const nested='\\documentclass{article}\\newcommand{\\demo}{\\begin{document}}% \\begin{document}\n\\begin{document}真实正文 \\verb|\\end{document}| 继续正文\\end{document}';await page.evaluate(s=>MentorTest.setDocument(s),nested);assert.match(await doc.innerText(),/继续正文/);assert.equal((await page.evaluate(()=>MentorTest.state())).source,nested);await context.close();console.log('PASS prototype TeX display, maths and source/draft preservation');
 }finally{await browser.close()}})().catch(e=>{console.error(e);process.exitCode=1});

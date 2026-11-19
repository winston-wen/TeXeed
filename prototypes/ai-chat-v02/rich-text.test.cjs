const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const context = vm.createContext({
  markdownit: require('./vendor/markdown-it/markdown-it.min.js'),
  katex: require('./vendor/katex/katex.min.js'),
});
vm.runInContext(fs.readFileSync(__dirname + '/inline-number.js', 'utf8'), context);
const source = fs.readFileSync(__dirname + '/rich-text.js', 'utf8');
vm.runInContext(source, context);
const render = context.renderRichText;
assert.match(render('**粗体**\n\n1. 第一项\n2. 第二项'), /<strong>粗体<\/strong>[\s\S]*<ol>[\s\S]*<li>第二项/);
for (const text of ['**出处也正在这里：**项目', '**（引用）**后文', '前文**「重要」**后文', '前文*注意：*后文', '**出典。**これは', '**설명。**다음', '**结论。**𠀀']) {
  assert.doesNotMatch(render(text), /\*\*/);
  assert.match(render(text), /<(?:strong|em)>/);
  assert.match(context.renderSavedAnswer(text, 'cjk'), /<(?:strong|em)>/);
}
assert.match(render('`**代码：**后文`'), /<code>\*\*代码：\*\*后文<\/code>/);
assert.match(render(String.raw`\*\*字面：\*\*后文`), /\*\*字面：\*\*后文/);
assert.match(render('**[链接：](https://example.test)**后文'), /<strong><a [^>]+>链接：<\/a><\/strong>后文/);
assert.match(render('**公式 $x_i$：**后文'), /<strong>公式 [\s\S]*class="katex"[\s\S]*：<\/strong>后文/);
assert.match(context.renderSavedAnswer('**公式 $x_i$：**后文', 'math-cjk'), /<strong>公式 [\s\S]*class="katex"[\s\S]*：<\/strong>后文/);
assert.match(render('**出处也正在这里： **项目'), /<strong>出处也正在这里： <\/strong>项目/);
assert.match(context.renderSavedAnswer('**出处也正在这里： **项目', 'typo'), /<strong>出处也正在这里： <\/strong>项目/);
assert.match(render('**公式 $x$： **后文'), /<strong>公式 [\s\S]*class="katex"[\s\S]*： <\/strong>后文/);
assert.match(context.renderSavedAnswer('**公式 $x$： **后文', 'typo-math'), /<strong>公式 [\s\S]*class="katex"[\s\S]*： <\/strong>后文/);
assert.match(render('**含 `代码`： **后文'), /<strong>含 <code>代码<\/code>： <\/strong>后文/);
assert.match(render('**含 [链接](https://example.test/)： **后文'), /<strong>含 <a href="https:\/\/example.test\/">链接<\/a>： <\/strong>后文/);
assert.match(render('[普通链接](https://example.test/**出处：%20**项目)'), /<a href="https:\/\/example.test\/\*\*%E5%87%BA%E5%A4%84%EF%BC%9A%20\*\*%E9%A1%B9%E7%9B%AE">普通链接<\/a>/);
assert.match(render('**已识别**并且**出处： **后文'), /<strong>已识别<\/strong>并且<strong>出处： <\/strong>后文/);
assert.match(render('| 列名 |\n|---|\n| **出处：**后文 |'), /<td><strong>出处：<\/strong>后文<\/td>/);
assert.match(render('# **出处：**后文'), /<h1><strong>出处：<\/strong>后文<\/h1>/);
assert.match(render('```md\n**出处： **后文\n```'), /<code class="language-md">\*\*出处： \*\*后文/);
assert.match(render('**正文 **后文'), /\*\*正文 \*\*后文/);
assert.match(render('`**出处： **项目`'), /<code>\*\*出处： \*\*项目<\/code>/);
assert.match(render(String.raw`\*\*出处： \*\*项目`), /\*\*出处： \*\*项目/);
assert.match(render('**出处：\n **项目'), /\*\*出处：[\s\S]*\*\*项目/);
for (const formula of ['$x^2$', String.raw`\(\frac{1}{2}\)`, '$$x^2$$', String.raw`\[\sum_{i=1}^n i\]`]) {
  assert.match(render(formula), /class="katex"/);
  assert.doesNotMatch(render(formula), /render-fallback/);
}
assert.match(render(String.raw`错误 $\notARealCommand{x}$`), /<code class="render-fallback">/);
assert.match(render(String.raw`$$\notARealCommand{x}$$`), /<pre class="render-fallback"><code>/);
assert.match(render(String.raw`\[x^2`), /<pre class="render-fallback"><code>/);
assert.match(render('$x'), /<code class="render-fallback">\$x<\/code>/);
assert.doesNotMatch(render('`$x$`\n\n```tex\n$$x$$\n```'), /class="katex"/);
assert.match(render('`$x$`'), /<code>\$x\$<\/code>/);
assert.match(render(String.raw`\$5`), /\$5/);
assert.doesNotMatch(render(String.raw`\$5`), /render-fallback/);
assert.doesNotMatch(render('<script>alert(1)</script>\n\n[x](javascript:alert(1))\n\n![image](javascript:alert(1))'), /<script|href="javascript:|<img/);
for(const link of ['assets/flight-comparison.svg','https://example.test/image.png','http://example.test/image.jpg']){
 const html=render('![示例图片]('+link+')');assert.match(html,/<img /);assert.ok(html.includes('src="'+link+'"'));assert.match(html,/class="zoomable-image"/);assert.match(html,/tabindex="0"/);assert.match(html,/referrerpolicy="no-referrer"/);
}
assert.doesNotMatch(render('`![代码](assets/example.svg)`'),/<img /);
assert.match(render('[![图片链接](assets/example.svg)](https://example.test/full.svg)'),/<a href="https:\/\/example.test\/full.svg"><img [^>]*class="zoomable-image"/);
assert.doesNotMatch(render('![image](data:text/html;base64,PHNjcmlwdD4=)'),/<img /);
assert.match(render('| A | B |\n|---|---|\n| One | Two |'),/<div class="table-wrap"[^>]*><table>[\s\S]*<\/table><\/div>/);
assert.match(context.renderSavedAnswer('![保留图片](assets/flight-comparison.svg)','saved-image'),/<img [^>]*src="assets\/flight-comparison.svg"/);
assert.doesNotMatch(render('[label](https://example.test)', true, true), /<a /);
const broken = vm.createContext({markdownit: () => {throw Error('missing');}});
vm.runInContext(source, broken);
assert.match(broken.renderRichText('**source**'), /<pre class="render-fallback"><code>\*\*source\*\*/);
assert.match(broken.renderRichText('$x$', true), /<code class="render-fallback">\$x\$/);
const numericContext = vm.createContext({
  markdownit: context.markdownit,
  katex: {renderToString(){throw new Error('Numeric prose invoked KaTeX');}}
});
vm.runInContext(fs.readFileSync(__dirname + '/inline-number.js', 'utf8'), numericContext);
vm.runInContext(source, numericContext);
assert.equal(numericContext.renderRichText(String.raw`数 $ 064 $、\(０５１２\)、$𝟘𝟛$。`, true), '数 064、０５１２、𝟘𝟛。');
const numericAnswer=numericContext.renderSavedAnswer('## $512$\n\n|数|\n|---|\n|$064$|','num');
assert.match(numericAnswer, /<h2><span data-inline-number="512">512<\/span>/);
assert.match(numericAnswer, /<td><span data-inline-number="064">064<\/span>/);
assert.doesNotMatch(numericAnswer, /render-fallback|class="katex"/);
assert.match(render('`$064$`', true), /<code>\$064\$<\/code>/);
for (const expression of ['$-2$','$+2$','$2.5$','$2x$',String.raw`$\mathrm{3}$`, '$$42$$']) assert.match(render(expression), /class="katex"/);
console.log('Rich text: Markdown, TeX, code isolation, safe images, scrollable tables and rendering fallback passed.');

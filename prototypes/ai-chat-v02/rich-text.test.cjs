const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const context = vm.createContext({
  markdownit: require('./vendor/markdown-it/markdown-it.min.js'),
  katex: require('./vendor/katex/katex.min.js'),
});
const source = fs.readFileSync(__dirname + '/rich-text.js', 'utf8');
vm.runInContext(source, context);
const render = context.renderRichText;
assert.match(render('**粗体**\n\n1. 第一项\n2. 第二项'), /<strong>粗体<\/strong>[\s\S]*<ol>[\s\S]*<li>第二项/);
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
assert.doesNotMatch(render('<script>alert(1)</script>\n\n[x](javascript:alert(1))\n\n![image](https://example.test/image.png)'), /<script|href="javascript:|<img/);
assert.doesNotMatch(render('[label](https://example.test)', true, true), /<a /);
const broken = vm.createContext({markdownit: () => {throw Error('missing');}});
vm.runInContext(source, broken);
assert.match(broken.renderRichText('**source**'), /<pre class="render-fallback"><code>\*\*source\*\*/);
assert.match(broken.renderRichText('$x$', true), /<code class="render-fallback">\$x\$/);
console.log('Rich text: Markdown, TeX, code isolation, failure fallback and escaped content passed.');

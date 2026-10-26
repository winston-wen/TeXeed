'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const context=vm.createContext({
 markdownit:require('./vendor/markdown-it/markdown-it.min.js'),
 katex:require('./vendor/katex/katex.min.js'),BUNDLED_TURNS:[],DATA:{playback:{answer:'Bundled playback'}}
});
vm.runInContext(fs.readFileSync(__dirname+'/rich-text.js','utf8'),context);
const app=fs.readFileSync(__dirname+'/app.js','utf8');
vm.runInContext(app.slice(app.indexOf('function svgHtml('),app.indexOf('function activity(')),context);
const turn={id:'saved-turn',answer:'## Saved heading\n\nA saved formula $x+1$.\n\n\\[x^2=4\\]\n\n1. First\n2. Second\n\n<script>private()</script>'};
const html=context.svgHtml(turn);
assert.equal(turn.answerHtml,html);
assert.deepEqual([...html.matchAll(/data-block="([^"]+)"/g)].map(m=>m[1]),Array.from({length:5},(_,i)=>'saved-turn-p'+i));
assert.match(html,/<h3>Saved heading<\/h3>/);
assert.match(html,/data-tex="x\+1"/);
assert.match(html,/<span class="equation-number">\(1\)<\/span>/);
assert.match(html,/<ol>/);
assert.match(html,/&lt;script&gt;private\(\)&lt;\/script&gt;/);
assert.doesNotMatch(html,/<script>/);
assert.equal(context.svgHtml(turn),html);
assert.equal(context.svgHtml({id:'pending',answer:''}),'');
const replay={id:'sim-1234',answer:'## Earlier playback\n\nSaved paragraph.',sections:{'sim-1234-playback-p0':true}};
const conversation={turns:[replay],entries:[{block:'sim-1234-playback-p1',question:'Saved annotation'}],reading:{anchor:'sim-1234-playback-p1'},draft:'Saved draft'};
const replayHtml=context.svgHtml(replay);
for(const anchor of [conversation.entries[0].block,conversation.reading.anchor,...Object.keys(replay.sections)])assert.ok(replayHtml.includes('id="'+anchor+'"'));
assert.equal(conversation.draft,'Saved draft');assert.equal(replay.sections['sim-1234-playback-p0'],true);
assert.match(context.renderSavedAnswer('Use @@M0@@ literally.','literal'),/Use @@M0@@ literally\./);
const code=context.renderSavedAnswer('Inline `$x$`.\n\n```tex\n$x$\n\n$y$\n```\n\nAfter code.','code');
assert.match(code,/<code>\$x\$<\/code>/);assert.doesNotMatch(code,/class="katex"/);
assert.equal((code.match(/<pre>/g)||[]).length,1);assert.match(code,/\$x\$\n\n\$y\$\n<\/code>/);assert.match(code,/After code\./);
for(const source of ['```tex\n$x$\n````','```tex\n$x$','`first line\n$x$`']){
 const protectedCode=context.renderSavedAnswer(source,'code-variant');assert.doesNotMatch(protectedCode,/class="katex"/);assert.match(protectedCode,/\$x\$/);
}
let linked=false;
const reference={textContent:'main.tex:5',closest:()=>({dataset:{turn:'saved-turn'}}),replaceChildren:()=>{linked=true;}};
context.document={querySelectorAll:selector=>selector==='.answer code'?[reference]:[],createElement:()=>({dataset:{}})};
context.current=()=>({turns:[turn]});
vm.runInContext(app.slice(app.indexOf('function enhanceAnswers('),app.indexOf('function toggleTurn(')),context);
context.enhanceAnswers();assert.equal(linked,false);
context.BUNDLED_TURNS.push({...turn});context.enhanceAnswers();assert.equal(linked,true);
console.log('Saved answers: paragraph/replay anchors, saved state, source links, code, literal markers and cached rendering passed.');

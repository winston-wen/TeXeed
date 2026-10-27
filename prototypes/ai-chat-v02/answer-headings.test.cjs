'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
const context=vm.createContext({markdownit:require('./vendor/markdown-it/markdown-it.min.js'),katex:require('./vendor/katex/katex.min.js'),BUNDLED_TURNS:[],DATA:{playback:{answer:'playback'}}});
vm.runInContext(fs.readFileSync(__dirname+'/rich-text.js','utf8'),context);
vm.runInContext(app.slice(app.indexOf('function svgHtml('),app.indexOf('function activity(')),context);
for(let level=1;level<=6;level++){
 assert.match(context.renderSavedAnswer('#'.repeat(level)+' Heading','answer'),new RegExp('<h'+level+'>Heading</h'+level+'>'));
 assert.match(context.renderRichText('#'.repeat(level)+' Question'),new RegExp('<h'+level+'>Question</h'+level+'>'));
}
const passage=(index,body)=>'<section class="passage" id="cached-p'+index+'" data-block="cached-p'+index+'"><div class="passage-body">'+body+'</div><div class="margin-notes"></div></section>';
for(const [source,first,level] of [['> ## Quoted title\n\n### Main title','<blockquote>## Quoted title</blockquote>',3],['- ## Listed title\n\n##### Main title','<ul><li>## Listed title</li></ul>',5]]){
 const cached=passage(0,first)+passage(1,'<h4>Main title</h4>');
 const restored=context.restoreAnswerHeadings(cached,source);
 assert.ok(restored.includes(first));assert.ok(restored.includes('<h'+level+'>Main title</h'+level+'>'));
 assert.deepEqual([...restored.matchAll(/data-block="([^"]+)"/g)].map(m=>m[1]),['cached-p0','cached-p1']);
}
function legacyRuntime(source){return context.renderSavedAnswer(source,'cached').replace(/(<div class="passage-body">)<h([1-6])>([\s\S]*?)<\/h\2>/g,(_,prefix,level,body)=>prefix+'<h'+Math.min(4,Number(level)+1)+'>'+body+'</h'+Math.min(4,Number(level)+1)+'>');}
for(const source of ['```js\na\n\nb\n```\n\n##### Title','`$$x$$`\n\n##### Title','## First\n### Second']){
 const cached=legacyRuntime(source),restored=context.restoreAnswerHeadings(cached,source);
 assert.equal(restored,context.renderSavedAnswer(source,'cached'));
}
const oldTurn={id:'cached',answer:'###### Deep title',answerHtml:passage(0,'<h4>Deep title</h4>'),sections:{'cached-p0':true}};
const saved={turns:[oldTurn],draft:'Keep this draft',reading:{anchor:'cached-p0'},entries:[{block:'cached-p0',question:'Keep this annotation'}]};
const before=JSON.stringify({sections:oldTurn.sections,draft:saved.draft,reading:saved.reading,entries:saved.entries});
assert.match(context.svgHtml(oldTurn),/<h6>Deep title<\/h6>/);assert.equal(oldTurn.answerHeadingVersion,1);
const restore=context.restoreAnswerHeadings;context.restoreAnswerHeadings=()=>{throw Error('Unexpected repeated heading migration');};context.svgHtml(oldTurn);context.restoreAnswerHeadings=restore;
assert.equal(JSON.stringify({sections:oldTurn.sections,draft:saved.draft,reading:saved.reading,entries:saved.entries}),before);

let renderedAnswer;
const turn={id:'third',sections:{'p4':true,'p8':true},answer:'synthetic'};
const conversation={turns:[{id:'first'},{id:'second'},turn]};
function answer(){
 const levels=[1,0,2,0,3,0,4,0,5,0,6,0,2,0,1,0];
 const passages=levels.map((level,index)=>({id:'p'+index,dataset:{},heading:level?{tagName:'H'+level,innerHTML:'Title '+level,replaceChildren(button){this.button=button;}}:null,querySelector(selector){return this.querySelectorAll(selector)[0]||null;},querySelectorAll(selector){return [this.heading,...this.additionalHeadings||[]].filter(heading=>heading&&selector.split(',').includes(heading.tagName.toLowerCase()));},contains(target){return target?.passage===this;}}));
 return {dataset:{turn:'third'},passages,querySelectorAll:()=>passages};
}
context.current=()=>conversation;context.prepareReadingLayout=()=>{};
context.document={querySelectorAll:selector=>selector==='.answer'?[renderedAnswer]:[],createElement:()=>({dataset:{},attributes:{},setAttribute(name,value){this.attributes[name]=value;}})};
vm.runInContext(app.slice(app.indexOf('function enhanceAnswers('),app.indexOf('function toggleTurn(')),context);
renderedAnswer=answer();const adjacentHeading={tagName:'H6',innerHTML:'Adjacent title'};renderedAnswer.passages[2].additionalHeadings=[adjacentHeading];context.enhanceAnswers();assert.match(adjacentHeading.innerHTML,/\[3:h6\]/);
for(const p of renderedAnswer.passages.filter(p=>p.heading))assert.ok(p.heading.button.innerHTML.includes('[3:'+p.heading.tagName.toLowerCase()+']'));
assert.equal(renderedAnswer.passages[4].hidden,false);for(let i=5;i<12;i++)assert.equal(renderedAnswer.passages[i].hidden,true);assert.equal(renderedAnswer.passages[12].hidden,false);
turn.sections.p0=true;renderedAnswer=answer();context.enhanceAnswers();for(let i=1;i<14;i++)assert.equal(renderedAnswer.passages[i].hidden,true);assert.equal(renderedAnswer.passages[14].hidden,false);
delete turn.sections.p0;conversation.turns.unshift({id:'new-first'});renderedAnswer=answer();context.enhanceAnswers();assert.match(renderedAnswer.passages[4].heading.button.innerHTML,/\[4:h3\]/);assert.equal(turn.sections.p4,true);

vm.runInContext(app.slice(app.indexOf('function serialize('),app.indexOf('function targetContext(')),context);
const text=value=>({nodeType:3,nodeValue:value}),element=(name,classes,children)=>({nodeType:1,childNodes:children,matches:selectors=>selectors.split(',').some(selector=>selector.startsWith('.')?classes.includes(selector.slice(1)):name===selector)});
const heading=element('h6',[],[element('button',[],[element('span',['heading-marker'],[text('[4:h6]')]),element('span',['heading-label'],[text('Deep title')])])]);
assert.equal(context.serialize(heading),'Deep title\n');assert.equal(context.serialize({nodeType:11,childNodes:[heading]}),'Deep title\n');

let formulaHandler;const formulaTarget={passage:renderedAnswer.passages[11],scrollIntoView(){},classList:{add(){}}};
context.$=id=>id==='thread'?{addEventListener(type,handler){formulaHandler=handler;}}:formulaTarget;context.renderTurns=()=>{};context.requestAnimationFrame=callback=>callback();context.saveReading=()=>{};
const formulaStart=app.indexOf("$('thread').addEventListener('click',event=>{const b=event.target.closest('[data-equation-ref]')");
vm.runInContext(app.slice(formulaStart,app.indexOf("window.addEventListener('resize',commitAnnotation)",formulaStart)),context);
turn.sections={p0:true,p2:true,p4:true,p6:true,p8:true,p10:true,p12:true};
const link={dataset:{equationRef:'third-eq-1'},closest:selector=>selector==='.turn'?{id:turn.id}:renderedAnswer};formulaHandler({target:{closest:()=>link}});
for(const key of ['p0','p2','p4','p6','p8','p10'])assert.equal(turn.sections[key],false);assert.equal(turn.sections.p12,true);
console.log('Answer headings: real h1–h6, legacy cache anchors, dynamic ordinals, nested folds, copy text and formula ancestor restoration passed.');

'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
function element(){return {style:{},children:[],setAttribute(){},append(...children){this.children.push(...children);},remove(){},getBoundingClientRect(){return {height:(this.textContent||'').length>8?60:20};}};}
const document={createElement:element};
const activityContext=vm.createContext({document,time:()=> '09:00:00',esc:value=>String(value).replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;'),getComputedStyle:()=>({lineHeight:'20px',paddingTop:'8px',paddingBottom:'8px'})});
vm.runInContext(app.slice(app.indexOf('function activity('),app.indexOf('const ICON_CLOSE=')),activityContext);
for(const details of [undefined,'','已经完成']){
 const html=activityContext.activity({text:'已经完成',details,time:'2000-01-01T09:00:00+08:00'});
 assert.match(html,/class="activity no-details"/);assert.match(html,/<div class="activity-summary">/);assert.doesNotMatch(html,/<details|<summary|<template|tabindex/);
}
for(const details of ['共处理三项','已经完成\n详情仍可展开','已经完成\r\n第二行']){
 const html=activityContext.activity({text:'已经完成',details,time:'2000-01-01T09:00:00+08:00'});
 assert.match(html,/<details class="activity">/);assert.match(html,/<template class="activity-source">/);assert.doesNotMatch(html,/no-details/);
}
const details={children:[],append(node){this.children.push(node);},querySelector(selector){return selector==='.activity-source'?{content:{textContent:'first\nsecond'}}:this.children.find(node=>node.className==='activity-code');}};
activityContext.mountActivityCode(details);assert.equal(details.children[0].style.height,'56px');
activityContext.mountActivityCode(details);assert.equal(details.children.length,1,'Details mount only on first expansion');
const [numbers,code]=details.children[0].children;code.scrollTop=37;code.onscroll();assert.equal(numbers.children[0].style.transform,'translateY(-37px)');
const gutter=element(),labels=element(),box={append(){},querySelector:selector=>selector==='.line-numbers'?gutter:labels},shell={prepend(grip){this.grip=grip;}};
const field={id:'composer',value:'',style:{},clientWidth:240,clientHeight:200,scrollTop:0,closest:selector=>selector==='.composer-shell'?shell:box};
const editorContext=vm.createContext({document,getComputedStyle:()=>({font:'15px monospace',lineHeight:'20px',paddingLeft:'8px',paddingRight:'8px',tabSize:'4'}),$:id=>id==='composer'?field:{clientHeight:600},current:()=>({id:'saved'}),composerHeights:new Map()});
vm.runInContext(app.slice(app.indexOf('function updateEditor('),app.indexOf("$('thread').addEventListener('toggle'")),editorContext);
function lineHeights(){return [...labels.innerHTML.matchAll(/height:(\d+)px/g)].map(match=>Number(match[1]));}
for(const value of ['', 'one', 'one\ntwo']){field.value=value;editorContext.updateEditor(field);assert.deepEqual(lineHeights(),[20,20,20]);assert.equal(field.value,value);}
field.value='a long wrapped logical line';editorContext.updateEditor(field);assert.deepEqual(lineHeights(),[60,20,20]);assert.equal(field.value,'a long wrapped logical line');
field.value='one\ntwo\nthree\nfour';editorContext.updateEditor(field);assert.deepEqual(lineHeights(),[20,20,20,20]);
field.id='annotation-input';field.value='one';editorContext.updateEditor(field);assert.deepEqual(lineHeights(),[20]);field.id='composer';
editorContext.restoreComposerHeight();assert.equal(field.style.height,'81px');editorContext.resizeComposer(1);assert.equal(field.style.height,'41px');
editorContext.installComposerResize();shell.grip.onkeydown({key:'Home',preventDefault(){}});assert.equal(field.style.height,'81px');
console.log('Editor layout: redundant activity disclosures, measured line height, lazy mounting, scroll sync, three composer gutters, soft wraps and retained drafts passed.');

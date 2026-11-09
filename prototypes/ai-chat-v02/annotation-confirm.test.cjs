'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
const c={draft:'统一问题仍在编辑',turns:[],entries:[{id:'one',question:'旧内容',selected:false},{id:'two',question:'其他圈点',selected:false}]};
const elements={
 'annotation-input':{value:'新批注'},'annotation-editor':{hidden:false},
 'entries-dialog':{open:false,close(){this.open=false;}}
};
let sent=0;
const ctx=vm.createContext({current:()=>c,editing:'one',$:id=>elements[id],record:()=>{},renderEntries:()=>{},updateEditor:()=>{},send:()=>sent++,ICON_SEND:'↑',document:{createElement:()=>({dataset:{},classList:{},children:[],setAttribute(){},append(...children){this.children.push(...children);},firstElementChild:{style:{}}})}});
vm.runInContext(app.slice(app.indexOf('function commitAnnotation(){'),app.indexOf('function positionEditor(){')),ctx);
vm.runInContext(app.slice(app.indexOf('function mountEditors(root){'),app.indexOf('function updateEditor(field){')),ctx);
function input(id,dataset={}){
 const field={id,dataset,listeners:{},closest:()=>null,before(box){this.box=box;},addEventListener(name,fn){this.listeners[name]=fn;}};
 ctx.field=field;vm.runInContext('mountEditors({querySelectorAll:()=>[field]})',ctx);return field;
}
function key(field,extra={}){const event={key:'Enter',ctrlKey:true,shiftKey:false,altKey:false,repeat:false,isComposing:false,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;},...extra};field.listeners.keydown(event);return event;}
const pin=input('annotation-input');
for(const extra of [{ctrlKey:false},{shiftKey:true},{altKey:true},{repeat:true},{isComposing:true}]){key(pin,extra);assert.equal(ctx.editing,'one');assert.equal(sent,0);}
const event=key(pin);assert.ok(event.prevented&&event.stopped);assert.equal(ctx.editing,null);assert.equal(elements['annotation-editor'].hidden,true);
assert.equal(c.entries[0].question,'新批注');assert.equal(c.entries[0].selected,true);assert.equal(c.entries[1].selected,false);assert.equal(c.entries.length,2);assert.equal(c.turns.length,0);assert.equal(c.draft,'统一问题仍在编辑');assert.equal(sent,0);
ctx.editing='one';elements['annotation-input'].value='继续改同一条';elements['annotation-editor'].hidden=false;
vm.runInContext(app.match(/\$\('send-annotation'\)\.onclick=([^;]+);/)[1],ctx)();
assert.equal(c.entries[0].question,'继续改同一条');assert.equal(sent,0);
const list=input('',{question:'two'});key(list);assert.equal(c.entries[1].selected,true);assert.equal(sent,0);
c.entries[1].selected=false;list.box.children[2]?.onclick?.();
assert.equal(c.entries[1].selected,true);assert.equal(sent,0);
const main=app.slice(app.indexOf("$('composer').onkeydown="),app.indexOf("$('send').onclick="));
elements.composer={};vm.runInContext(main,ctx);elements.composer.onkeydown({key:'Enter',ctrlKey:true,preventDefault(){},stopPropagation(){}});assert.equal(sent,1);
assert.equal(c.turns.length,0);assert.equal(c.draft,'统一问题仍在编辑');
console.log('Annotation confirmation: pin/list Ctrl+Enter and arrows stage editable content without sending; only composer sends.');

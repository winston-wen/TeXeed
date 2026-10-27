'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8'),nodes=new Map(),listeners={document:{},window:{}};
function node(id){if(!nodes.has(id))nodes.set(id,{id,style:{},listeners:{},textContent:'',disabled:false,focus(){this.focused=(this.focused||0)+1;},addEventListener(type,listener){this.listeners[type]=listener;}});return nodes.get(id);}
const dialog=node('image-viewer'),preview=node('image-preview'),viewport=node('image-viewport');
dialog.showModal=()=>{dialog.open=true;};dialog.close=()=>{dialog.open=false;dialog.listeners.close();};
Object.assign(preview,{naturalWidth:1600,naturalHeight:900,complete:true,removeAttribute(name){delete this[name];}});
Object.assign(viewport,{clientWidth:800,clientHeight:450});
const context=vm.createContext({$:node,document:{addEventListener(type,listener){(listeners.document[type]||=[]).push(listener);}},window:{addEventListener(type,listener){(listeners.window[type]||=[]).push(listener);}}});
vm.runInContext(app.slice(app.indexOf('function installImageViewer(){'),app.indexOf('function turnEnd(')),context);context.installImageViewer();
function image(){return {alt:'合成宽图',src:'assets/flight-comparison.svg',isConnected:true,focused:0,focus(){this.focused++;},closest:selector=>selector==='.zoomable-image'?source:null,matches:selector=>selector==='.zoomable-image'};}
let source=image();
function event(target,key){return {target,key,repeat:false,preventDefault(){this.prevented=true;}};}
function clickButton(id){dialog.listeners.click(event({closest:()=>({id})}));}
const click=event(source);listeners.document.click[0](click);
assert.equal(click.prevented,true);assert.equal(dialog.open,true);assert.equal(preview.src,source.src);assert.equal(preview.style.width,'800px');assert.equal(preview.style.height,'450px');assert.equal(node('image-scale').textContent,'50%');
clickButton('image-actual');assert.equal(preview.style.width,'1600px');assert.equal(node('image-scale').textContent,'100%');
clickButton('image-zoom-in');assert.equal(preview.style.width,'2000px');
clickButton('image-zoom-out');assert.equal(preview.style.width,'1600px');
viewport.clientWidth=400;viewport.clientHeight=225;listeners.window.resize[0]();assert.equal(preview.style.width,'1600px');
clickButton('image-fit');assert.equal(preview.style.width,'400px');assert.equal(node('image-scale').textContent,'25%');
viewport.clientWidth=800;viewport.clientHeight=450;listeners.window.resize[0]();assert.equal(preview.style.width,'800px');
const focuses=source.focused;dialog.close();assert.equal(source.focused,focuses+1);assert.equal(preview.src,undefined);
source.parentElement={tagName:'A',href:'https://example.test/full.svg'};
const linkedImageClick=event(source);listeners.document.click[0](linkedImageClick);assert.equal(linkedImageClick.prevented,true,'Previewing a linked image must cancel anchor navigation');dialog.close();
const enter=event(source,'Enter');listeners.document.keydown[0](enter);assert.equal(enter.prevented,true);assert.equal(dialog.open,true);
const escape=event(source,'Escape');listeners.document.keydown[0](escape);assert.equal(escape.prevented,undefined,'Native Escape dismissal must remain available');dialog.close();
source=image();preview.complete=false;preview.naturalWidth=preview.naturalHeight=0;listeners.document.click[0](event(source));assert.equal(node('image-scale').textContent,'加载中');
clickButton('image-zoom-in');preview.naturalWidth=1600;preview.naturalHeight=900;preview.onload();assert.equal(node('image-scale').textContent,'125%');
clickButton('image-fit');assert.equal(node('image-scale').textContent,'50%');
preview.naturalWidth=32000;preview.naturalHeight=18000;clickButton('image-fit');assert.equal(preview.style.width,'800px');assert.equal(preview.style.height,'450px');
clickButton('image-close');assert.equal(dialog.open,false);assert.equal(source.focused,2);
assert.equal(listeners.document.click.length,1);assert.equal(listeners.document.keydown.length,1);assert.equal(listeners.window.resize.length,1);
console.log('Image viewer: delegated activation, async load, fit/native/zoom sizes, resize behavior, native Escape and focus restoration passed.');

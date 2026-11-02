'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
function events(){const handlers=new Map();return {handlers,addEventListener(type,fn){if(!handlers.has(type))handlers.set(type,new Set());handlers.get(type).add(fn);},removeEventListener(type,fn){handlers.get(type)?.delete(fn);},emit(type,event={}){for(const fn of [...(handlers.get(type)||[])])fn(event);}};}
const win=events(),doc=events();let indicator,frames=new Map(),nextFrame=1;
doc.body={append(node){indicator=node;}};
doc.createElement=()=>({style:{},setAttribute(){},remove(){this.removed=true;}});
const reader={...events(),scrollHeight:1200,clientHeight:400,scrollTop:400};
const text={parentElement:reader,closest:()=>null,scrollWidth:0,clientWidth:0,scrollHeight:0,clientHeight:0};
const context=vm.createContext({window:win,document:doc,innerWidth:1000,innerHeight:800,setTimeout:fn=>fn(),getComputedStyle:node=>node.style||{},requestAnimationFrame:fn=>{const id=nextFrame++;frames.set(id,fn);return id;},cancelAnimationFrame:id=>frames.delete(id)});
vm.runInContext(fs.readFileSync(__dirname+'/reader-autoscroll.js','utf8'),context);
const stop=context.installReaderAutoScroll(reader);
function middle(target=text){const event={button:1,target,clientX:180,clientY:250,preventDefault(){this.prevented=true;}};reader.emit('mousedown',event);return event;}
function step(time){const [id,fn]=frames.entries().next().value;frames.delete(id);fn(time);}
let event=middle();assert(event.prevented);assert.equal(indicator.style.left,'180px');assert.equal(indicator.style.top,'250px');assert.equal(frames.size,0,'No idle animation frame');
win.emit('mouseup',{button:1});win.emit('mousemove',{clientX:180,clientY:258});assert.equal(frames.size,0,'Pointer remains in dead zone');
win.emit('mousemove',{clientX:180,clientY:300});step(100);step(150);assert(reader.scrollTop>400,'Pointer below anchor scrolls down');
const lower=reader.scrollTop;win.emit('mousemove',{clientX:180,clientY:200});step(200);assert(reader.scrollTop<lower,'Pointer above anchor scrolls up');
event={button:0,preventDefault(){this.prevented=true;},stopPropagation(){this.stopped=true;}};win.emit('mousedown',event);assert(event.prevented&&event.stopped);assert(indicator.removed);assert.equal(frames.size,0);
middle();win.emit('mousemove',{clientX:180,clientY:280});win.emit('mouseup',{button:1});assert(indicator.removed,'Releasing a middle-button drag ends scrolling');assert.equal(frames.size,0);
middle();win.emit('keydown',{key:'Escape',preventDefault(){},stopPropagation(){}});assert(indicator.removed);
middle();stop();assert(indicator.removed,'Changing the view ends scrolling');
reader.scrollTop=799;middle();win.emit('mouseup',{button:1});win.emit('mousemove',{clientX:180,clientY:300});step(100);step(150);assert.equal(reader.scrollTop,800);assert.equal(frames.size,0,'Animation stops at boundary');
win.emit('mousemove',{clientX:180,clientY:200});step(200);step(250);assert(reader.scrollTop<800,'Moving inward resumes from boundary');stop();
const input={...text,closest:()=>({})};assert(!middle(input).prevented,'Editable and interactive elements keep their middle button');
const inner={...text,parentElement:reader,style:{overflowX:'auto'},scrollWidth:300,clientWidth:100};assert(!middle(inner).prevented,'Nested scrolling area keeps its own interaction');
reader.scrollHeight=reader.clientHeight;assert(!middle().prevented,'No anchor when reading viewport cannot scroll');
console.log('PASS: reading autoscroll direction, dead zone, drag/click/key exit, nested controls and idle cleanup.');

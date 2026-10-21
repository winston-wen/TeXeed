'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
const nodes=new Map(), node=id=>{if(!nodes.has(id))nodes.set(id,{});return nodes.get(id);};
const c=vm.createContext({$:node,state:{active:'one',conversations:[{id:'one',draft:'未发送的输入',entries:[{question:'可继续修改'}]},{id:'two',draft:'另一份输入'}]},
  openTabs:['one','two'],activeView:'one',stopped:[],persisted:0,
  saveReading(){},historyDialog:{commit(){},refresh(){}},renderTabs(){},
  stopConversation(id){c.stopped.push(id);},persist(){c.persisted++;},
  openConversation(id){c.activeView=c.state.active=id;},showView(id){c.activeView=id;}});
vm.runInContext(app.slice(app.indexOf('function closeTab(id){'),app.indexOf('function showSettings()'))+
  app.slice(app.indexOf("$('workspace-tabs').onmousedown="),app.indexOf("$('workspace-tabs').onkeydown=")),c);
function event(button,id){return {button,prevented:false,preventDefault(){this.prevented=true;},target:{closest(){return id?{dataset:{tab:id}}:null;}}};}
const tabs=node('workspace-tabs');
let e=event(1,'two');tabs.onmousedown(e);assert(e.prevented);
tabs.onauxclick(e);assert.deepEqual(Array.from(c.openTabs),['one']);assert.equal(c.activeView,'one');
assert.equal(c.state.conversations[1].draft,'另一份输入');
for(const button of [0,2]){e=event(button,'one');tabs.onauxclick(e);assert(!e.prevented);assert.equal(c.openTabs.length,1);}
e=event(1,null);tabs.onauxclick(e);assert(!e.prevented);assert.equal(c.openTabs.length,1);
e=event(1,'one');tabs.onauxclick(e);assert(e.prevented);assert.equal(c.openTabs.length,0);assert.equal(c.activeView,null);
assert.equal(c.state.conversations[0].draft,'未发送的输入');assert.equal(c.state.conversations[0].entries[0].question,'可继续修改');
assert(node('closed').hidden===false);assert.equal(c.persisted,2);assert.deepEqual(Array.from(c.stopped),['two','one']);
console.log('Middle-click tabs: native close path, inactive/active tabs, non-middle/blank protection and retained conversation/input passed.');

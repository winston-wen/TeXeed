'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
const escape=value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const context=vm.createContext({
 running:null,esc:escape,
 questionHtml:turn=>'<p>'+escape(turn.question)+'</p>',
 svgHtml:turn=>turn.answer?'<p>'+escape(turn.answer)+'</p>':'',
 activity:segment=>'<details class="activity"><summary>'+escape(segment.text)+'</summary></details>'
});
vm.runInContext(app.slice(app.indexOf('function time('),app.indexOf('function scrollEnd(')),context);
vm.runInContext(app.slice(app.indexOf('function turnEnd('),app.indexOf('function renderTurns(')),context);

const sentAt='2000-01-01T09:00:00+08:00',endedAt='2000-01-01T09:00:12+08:00';
const base={id:'synthetic-turn',question:'比较两次纸飞机试飞的距离。',sentAt,answer:'两次距离分别为四米和五米。'};
const cases=[
 {name:'completed',turn:{...base,incomplete:false,answeredAt:endedAt},action:'答复',incomplete:false,at:endedAt},
 {name:'program stop',turn:{...base,answer:'',incomplete:true,endReason:'program',endedAt},action:'程序中止',incomplete:true,at:endedAt},
 {name:'user stop with partial answer',turn:{...base,incomplete:true,endReason:'user',endedAt},action:'用户中止',incomplete:true,at:endedAt},
 {name:'legacy answered turn',turn:{...base,answeredAt:endedAt},action:'答复',incomplete:false,at:endedAt},
 {name:'legacy answer without recorded completion time',turn:{...base},action:'答复',incomplete:false,at:null},
 {name:'legacy program failure',turn:{...base,answer:'',segments:[{kind:'activity',text:'本轮未完成：连接不可用。',time:endedAt}]},action:'程序中止',incomplete:true,at:endedAt},
 {name:'legacy user stop',turn:{...base,segments:[{kind:'activity',text:'已停止。已完成的修改保留；可继续提问。',time:endedAt}]},action:'用户中止',incomplete:true,at:endedAt},
 {name:'legacy user stop with ASCII punctuation',turn:{...base,answer:'',segments:[{kind:'activity',text:'已停止. 已保留本轮内容。',time:endedAt}]},action:'用户中止',incomplete:true,at:endedAt}
];
const plain=html=>html.replace(/<[^>]+>/g,'').replace(/&nbsp;/g,' ');
function block(html,className){
 const match=html.match(new RegExp('<div\\b[^>]*class="'+className+'"[^>]*>([\\s\\S]*?)<\\/div>'));
 assert.ok(match,'Missing '+className);return match[1];
}
function actionFollowsTime(html,stamp,action,label){
 const text=plain(html),formatted=stamp?context.time(stamp):'未记录';
 assert.ok(text.includes(formatted),label+': missing timestamp');
 assert.ok(text.indexOf(action)>text.indexOf(formatted),label+': action must follow its timestamp');
}

for(const fixture of cases){
 const turn=JSON.parse(JSON.stringify(fixture.turn)),snapshot=JSON.stringify(turn);
 const end=context.turnEnd(turn);
 assert.equal(end.action,fixture.action,fixture.name);
 assert.equal(end.incomplete,fixture.incomplete,fixture.name);
 assert.equal(end.at,fixture.at,fixture.name);
 for(const collapsed of [false,true]){
  turn.collapsed=collapsed;
  const html=context.turnHtml(turn,9),footer=block(html,'turn-time answer-time');
  assert.match(html,/class="turn-number"[^>]*>\[10\]<\//,fixture.name);
  assert.ok(html.includes(escape(turn.question)),fixture.name+': question lost');
  actionFollowsTime(footer,fixture.at,fixture.action,fixture.name);
  assert.doesNotMatch(plain(footer),/答复时间[：:]/,fixture.name+': old prefix remains');
  if(collapsed){
   assert.equal((html.match(/class="turn-time sent-time"/g)||[]).length,0,fixture.name+': duplicate collapsed sending block');
   const lines=footer.split('\n');
   assert.equal(lines.length,2,fixture.name+': collapsed timestamps must share a two-line block');
   actionFollowsTime(lines[0],sentAt,'发送',fixture.name);
   actionFollowsTime(lines[1],fixture.at,fixture.action,fixture.name);
   assert.match(html,/id="synthetic-turn-segments"[^>]*\bhidden\b/,fixture.name);
   assert.match(html,/aria-expanded="false"/,fixture.name);
  }else{
   actionFollowsTime(block(html,'turn-time sent-time'),sentAt,'发送',fixture.name);
   assert.doesNotMatch(html,/id="synthetic-turn-segments"[^>]*\bhidden\b/,fixture.name);
   assert.match(html,/aria-expanded="true"/,fixture.name);
   assert.ok(!turn.answer||html.includes(escape(turn.answer)),fixture.name+': partial or complete answer lost');
  }
 }
 delete turn.collapsed;
 assert.equal(JSON.stringify(turn),snapshot,fixture.name+': rendering mutated stored turn');
}
for(const [index,expected] of [[undefined,1],[0,1],[9,10],[99,100]]){
 const html=index===undefined?context.turnHtml({...base,answeredAt:endedAt}):context.turnHtml({...base,answeredAt:endedAt},index);
 assert.match(html,new RegExp('class="turn-number"[^>]*>\\['+expected+'\\]<\\/'));
 assert.equal((html.match(/class="turn-number"/g)||[]).length,1,'Exactly one turn number is rendered');
}
context.running={turn:{id:base.id}};
const pending=context.turnHtml({...base,answer:'',incomplete:true},0);
assert.match(pending,/class="thinking-label"/);
assert.doesNotMatch(plain(block(pending,'turn-time answer-time')),/答复|用户中止|程序中止/);
assert.doesNotMatch(pending,/aria-controls="synthetic-turn-segments"/,'Running turns must not offer a collapse action');

function requestFixture(mode){
 const conversation={id:'synthetic-conversation',title:'纸飞机试飞',entries:[],turns:[],draft:'比较两次试飞。'};
 const nodes={composer:{value:conversation.draft},'playback-mode':{value:mode},'entries-dialog':{open:false},reader:{scrollHeight:200,scrollTop:150,clientHeight:50}};
 const timers=[],cancelled=[],records=[];
 const noop=()=>{};
 const runtime=vm.createContext({
  running:null,requests:new Map(),state:{active:conversation.id},activeView:'conversation',
  current:()=>conversation,$:id=>nodes[id],clone:value=>JSON.parse(JSON.stringify(value)),
  DATA:{prompts:{answer:'合成说明',title:'合成标题'},playback:{answer:'第二次飞得更远。',answerHtml:'<p>第二次飞得更远。</p>'}},
  closePopups:noop,refreshEditors:noop,renderTabs:noop,status:noop,setBusy:noop,renderTurns:noop,scrollEnd:noop,
  record:action=>records.push({action,conversation:JSON.parse(JSON.stringify(conversation))}),
  setTimeout:callback=>{timers.push(callback);return timers.length;},clearTimeout:id=>cancelled.push(id)
 });
 vm.runInContext(app.slice(app.indexOf('function send('),app.indexOf('function newConversation(){')),runtime);
 runtime.send();
 assert.equal(conversation.turns.length,1,'Sending must create exactly one turn');
 assert.equal(timers.length,1,'A normal or failed playback has one completion callback');
 return {runtime,conversation,turn:conversation.turns[0],timers,cancelled,records};
}

for(const mode of ['normal','program-failure']){
 const request=requestFixture(mode);
 request.timers[0]();
 const expected=mode==='program-failure'?'程序中止':'答复';
 assert.equal(context.turnEnd(request.turn).action,expected,'Completion action for '+mode);
 assert.equal(request.turn.incomplete,mode==='program-failure');
 assert.equal(request.turn.endReason,mode==='program-failure'?'program':undefined,'New failure must persist its explicit cause');
 assert.ok(request.turn[mode==='program-failure'?'endedAt':'answeredAt'],'Completion must record its timestamp');
 assert.equal(request.runtime.requests.size,0,'Completion must clear the pending request');
 assert.equal(request.records.at(-1).action,'finish');
 assert.equal(request.records.at(-1).conversation.turns[0].endReason,request.turn.endReason,'Persisted failure cause must match the rendered turn');
}
const stopped=requestFixture('normal');
stopped.runtime.stopConversation();
assert.equal(stopped.turn.endReason,'user','User stop must persist its explicit cause');
assert.equal(stopped.turn.incomplete,true);
assert.equal(context.turnEnd(stopped.turn).action,'用户中止');
assert.ok(stopped.turn.endedAt,'User stop must record its timestamp');
assert.deepEqual(stopped.cancelled,[1],'User stop must cancel the scheduled completion');
assert.equal(stopped.runtime.requests.size,0);
assert.equal(stopped.records.at(-1).action,'stop');
assert.equal(stopped.records.at(-1).conversation.turns[0].endReason,'user','Stored turn must retain the user-stop cause');
const stoppedSnapshot=JSON.stringify(stopped.turn);
stopped.timers[0]();
assert.equal(JSON.stringify(stopped.turn),stoppedSnapshot,'A late completion must not replace a user stop');

console.log('Turn metadata: ordinal numbering, chronological labels, complete/program/user endings, legacy records, collapsed two-line timestamps, running state and persisted termination causes passed.');

const offline=requestFixture('failure');
offline.timers[0]();assert.equal(offline.runtime.requests.size,1);assert.equal(offline.timers.length,2);
assert.ok(offline.turn.segments.some(s=>s.text.includes('网络连接中断')));
offline.runtime.stopConversation();assert.equal(offline.runtime.requests.size,0);assert.equal(offline.turn.endReason,'user');
const retry=requestFixture('program-failure');retry.timers[0]();
retry.conversation.draft='下一条草稿';retry.runtime.$('composer').value='下一条草稿';
retry.conversation.entries.push({id:'unsent',selected:true,question:'未发批注'});
retry.runtime.send(retry.turn);
assert.equal(retry.conversation.turns.length,2);assert.equal(retry.conversation.turns[1].prompt,'比较两次试飞。');
assert.equal(retry.conversation.draft,'下一条草稿');assert.equal(retry.runtime.$('composer').value,'下一条草稿');assert.equal(retry.conversation.entries.length,1);
context.running=null;
const older={...base,id:'older-failed',incomplete:true,endReason:'program',endedAt};
assert.doesNotMatch(context.turnHtml(older,0,[older,base]),/data-retry-turn/);
assert.match(context.turnHtml(older,0,[older]),/data-retry-turn="older-failed"/);
console.log('Retry: latest interrupted turn only, continuous network playback, cancel, original question and next draft preservation passed.');

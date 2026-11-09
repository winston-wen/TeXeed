'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const app=fs.readFileSync(__dirname+'/app.js','utf8');
const context=vm.createContext({esc:value=>String(value??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]))});
vm.runInContext(app.slice(app.indexOf('function time('),app.indexOf('function scrollEnd(')),context);
vm.runInContext(app.slice(app.indexOf('function activity('),app.indexOf('function mountActivityCode(')),context);
const stamp='2026-11-03T08:56:05Z';
for(const record of [{text:'已查看项目目录',time:stamp},{text:'已读取源码',details:'第一行\n第二行',time:stamp}]){
 const html=context.activity(record),summary=html.match(/<summary>(.*?)<\/summary>/)[1];
 assert.doesNotMatch(summary,/<time/);assert.match(html,/<div class="activity-time"><time/);assert.match(html,/16:56:05/);
 assert.equal(html.includes('activity-source'),!!record.details);
}
assert.match(context.activity({text:'未记录时间'}),/class="activity no-details"/);
assert.match(context.activity({text:'有详情',details:'详情'}),/<details class="activity">/);
const template=fs.readFileSync(__dirname+'/template.html','utf8');
assert.match(template,/\.answer-start-divider\s*\{[^}]*border-top:1px solid var\(--line\)/);
assert.match(template,/\.answer \.equation\s*\{\s*margin:0;\s*padding-top:0;\s*padding-bottom:0/);
assert.doesNotMatch(template,/\.answer>\.passage:has\(>\.passage-body>:is\(h1/);
assert.match(template,/\.answer strong,\.question strong/);
assert.match(template,/font-weight:inherit;\s*line-height:1\.9;\s*padding-bottom:\.25em/);
assert.match(template,/\.passage ol:has\(>li:nth-child\(10\)\)/);
assert.match(template,/\.passage ol\[start\]/);
assert.match(template,/background-size:\.5em \.125em/);
assert.match(template,/\.heading-marker\s*\{[^}]*font-family:inherit/);
assert.doesNotMatch(template,/\.heading-marker,\.heading-text\s*\{/);
console.log('Reading layout: process time disclosure, single-line records, answer divider and uniform paragraph blocks passed.');

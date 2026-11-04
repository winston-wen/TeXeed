'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),hash=x=>crypto.createHash('sha256').update(x).digest('hex'),raw=fs.readFileSync(path.join(__dirname,'browser-performance.json'));
assert.equal(hash(raw),'68ff8f5bc8eede592ff51bd8c5f7d732e94c68172bc400d0d34c08fb2e598292');
const result=JSON.parse(raw),fixture=fs.readFileSync(path.join(__dirname,'snapshot/bookmark-markers-perf.cjs'));
assert.equal(hash(fixture),result.fixtureSha256);assert.equal(result.measurements.length,12);
for(const row of result.measurements){assert.equal(row.samples.length,7);assert.equal(row.counts.visible,11);assert.equal(row.counts.markers,row.mode==='after'?row.labels:0);}
for(const [name,expected] of Object.entries(result.resources.after)){
 const data=['index.html','bookmarks.js'].includes(name)?fs.readFileSync(path.join(__dirname,'snapshot',name)):cp.execFileSync('git',['show',result.baseline+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:20*1024*1024}),crlf=Buffer.from(data.toString('utf8').replace(/\r?\n/g,'\r\n'));
 assert.ok(hash(data)===expected||hash(crlf)===expected,`Measured resource differs: ${name}`);
}
console.log('Archived browser bookmark marker comparison, resources and fixture verified.');

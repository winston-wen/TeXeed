'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),hash=x=>crypto.createHash('sha256').update(x).digest('hex');
const read=(name,expected)=>{const raw=fs.readFileSync(path.join(__dirname,name));assert.equal(hash(raw),expected,name);return JSON.parse(raw);};
const baseline=read('browser-performance.json','b9bf93b069d5a7c826e8ef0ba529da59f8bb75e38e004ed369afde4b1d7c1836');
const gc=read('gc-diagnostic.json','7670c6f0352ee4eac01b290ea5ba7c4ca531d582ec5861156c0c0b3bc375d0da');
assert.equal(hash(fs.readFileSync(path.join(__dirname,'run-gc-diagnostic.cjs'))),'98407e261c064f6f80b9119eab200b818379b19530386c6f51352f649c336bbd');
for(const result of [baseline,gc]){
 assert.equal(result.baselineCommit,'444c3a8e435416f8e6a0fe7a0be3fb9d45e291af');
 assert.equal(hash(fs.readFileSync(path.join(root,result.method.fixture))),result.method.fixtureSha256);
 for(const mode of ['before','after']){assert.equal(result.measurements[mode].length,3);for(const sample of result.measurements[mode]){assert.equal(sample.samples.length,7);assert.equal(sample.counts.headings,300);assert.equal(sample.counts.visible,21);assert.deepEqual(sample.errors,[]);}}
 for(const [name,expected] of Object.entries(result.resources.after)){
  const data=['index.html','bookmarks.js'].includes(name)?fs.readFileSync(path.join(__dirname,'snapshot',name)):cp.execFileSync('git',['show',result.baselineCommit+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:20*1024*1024}),crlf=Buffer.from(data.toString('utf8').replace(/\r?\n/g,'\r\n'));
  assert.ok(hash(data)===expected||hash(crlf)===expected,`Measured resource differs: ${name}`);
 }
}
assert.equal(gc.method.collectGarbageBeforeSamplingStop,true);
console.log('Archived heading numbering performance, GC diagnostics and resources verified.');

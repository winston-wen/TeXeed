'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../../..'),hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const raw=fs.readFileSync(path.join(__dirname,'browser-performance.json'));
assert.equal(hash(raw),'cf340cf11096ce7290a31172108f71d68d76394069fb1c17a9670b6f629bd7b7');
const result=JSON.parse(raw);
assert.equal(hash(fs.readFileSync(path.join(root,result.method.fixture))),result.method.fixtureSha256);
assert.equal(result.baselineCommit,'8f3e434274bc81cea66f968e3c27b91cb54e7724');
for(const mode of ['before','after']){
 assert.equal(result.measurements[mode].length,3);
 for(const round of result.measurements[mode]){assert.equal(round.samples.length,7);assert.deepEqual(round.errors,[]);assert.equal(round.counts.headings,300);assert.equal(round.counts.visible,21);}
}
for(const [name,expected] of Object.entries(result.resources.after)){
 const data=fs.readFileSync(path.join(root,'prototypes/ai-chat-v02',name));
 // Git stores text with LF; the benchmark ran with CRLF working-tree files.
 const crlf=Buffer.from(data.toString('utf8').replace(/\r?\n/g,'\r\n'));
 assert.ok(hash(data)===expected||hash(crlf)===expected,`Measured resource differs: ${name}`);
}
console.log('Archived browser heading benchmark fixture, results and measured resources verified.');

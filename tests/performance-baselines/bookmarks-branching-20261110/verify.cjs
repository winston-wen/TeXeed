'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02');
const sha=x=>crypto.createHash('sha256').update(x).digest('hex');
function check(value,msg){if(!value)throw Error(msg)}
for(const [sample,script] of [['samples.json','measure.cjs'],['operations.json','operations-sampled.cjs'],['source-zero.json','operations.cjs']]){
 const o=JSON.parse(fs.readFileSync(path.join(__dirname,sample)));check(sha(fs.readFileSync(path.join(__dirname,script)))===o.fixtureHash,sample+' script mismatch');
 for(const [mode,files] of Object.entries(o.resources))for(const [name,digest] of Object.entries(files)){
  const bytes=mode==='after'?fs.readFileSync(path.join(proto,name)):cp.execFileSync('git',['show',(mode==='historic'?o.historic:o.baseline)+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:100*1024*1024});
  check(sha(bytes)===digest,sample+' '+mode+'/'+name+' hash mismatch');
 }
 console.log('verified '+sample);
}

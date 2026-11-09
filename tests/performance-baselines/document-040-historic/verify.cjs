'use strict';
const fs=require('fs'),path=require('path'),cp=require('child_process'),crypto=require('crypto');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02'),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
for(const [file,fixture] of [['results.json','compare.cjs'],['allocation-results.json','allocation.cjs']]){const result=JSON.parse(fs.readFileSync(path.join(__dirname,file),'utf8'));
if(result.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,fixture))))throw Error('fixture mismatch: '+fixture);
for(const [mode,entries] of Object.entries(result.resources))for(const [name,expected] of Object.entries(entries)){const bytes=mode==='before'?cp.execFileSync('git',['show',result.baseline+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:48*1024*1024}):fs.readFileSync(path.join(proto,name));if(sha(bytes)!==expected)throw Error('resource differs: '+mode+'/'+name);}
if(result.measurements.length!==12||(fixture==='compare.cjs'&&result.measurements.some(x=>x.samples.length!==7)))throw Error('incomplete measurement samples');}
console.log('Mentor comment UI comparison fixture, historical resources and measured output verified.');

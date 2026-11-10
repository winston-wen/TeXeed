'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02');
const hash=x=>crypto.createHash('sha256').update(x).digest('hex');
function check(cond,message){if(!cond)throw Error(message)}
function file(name){return fs.readFileSync(path.join(__dirname,name));}
for(const [data,script] of [['samples-final.json','measure-sampled.cjs'],['samples-last.json','measure-recheck-sampled.cjs'],['samples-ultimate.json','measure.cjs'],['branch-operations.json','branch-operations-sampled.cjs'],['branch-operations-final.json','branch-operations-recheck-sampled.cjs'],['send-final.json','branch-operations.cjs']]){
 const obj=JSON.parse(file(data));check(hash(file(script))===obj.fixtureHash,data+' fixture hash mismatch');
 check(obj.measurements?.length>0,data+' has no measurements');
 if(obj.historic&&obj.baseline)for(const [mode,rev] of [['historic',obj.historic],['before',obj.baseline]])for(const [name,expected] of Object.entries(obj.resources[mode])){
  const actual=cp.execFileSync('git',['show',rev+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:100*1024*1024});check(hash(actual)===expected,data+' '+mode+'/'+name+' hash mismatch');
 }
 if(data==='samples-ultimate.json')for(const [name,expected] of Object.entries(obj.resources.after))check(hash(fs.readFileSync(path.join(proto,name)))===expected,data+' after/'+name+' hash mismatch');
 if(data==='send-final.json')for(const [name,expected] of Object.entries(obj.resources))check(hash(fs.readFileSync(path.join(proto,name)))===expected,data+' '+name+' hash mismatch');
 console.log('verified '+data+(data==='samples-final.json'||data==='samples-last.json'||data==='branch-operations.json'||data==='branch-operations-final.json'?' (intermediate after resource not checked)':''));
}

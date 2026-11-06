'use strict';
const fs=require('fs'),path=require('path'),cp=require('child_process'),crypto=require('crypto');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02'),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const before=JSON.parse(fs.readFileSync(path.join(__dirname,'before-results.json'),'utf8'));
const after=JSON.parse(fs.readFileSync(path.join(__dirname,'after-results.json'),'utf8'));
const interactions=JSON.parse(fs.readFileSync(path.join(__dirname,'interaction-results.json'),'utf8'));
const oldInteractions=JSON.parse(fs.readFileSync(path.join(__dirname,'pre-lazy-interaction-results.json'),'utf8'));
const lazy=JSON.parse(fs.readFileSync(path.join(__dirname,'lazy-comparison-results.json'),'utf8'));
for(const [data,fixture] of [[before,'before.cjs'],[after,'after.cjs'],[interactions,'interactions.cjs'],[oldInteractions,'interactions.cjs'],[lazy,'lazy-compare.cjs']])if(data.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,fixture))))throw Error('fixture mismatch: '+fixture);
for(const [side,entries] of Object.entries(before.resources))for(const [name,expected] of Object.entries(entries)){const bytes=cp.execFileSync('git',['show',before.refs[side]+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:48*1024*1024});if(sha(bytes)!==expected)throw Error('historical resource differs: '+side+'/'+name);}
for(const data of [after,interactions])for(const [name,expected] of Object.entries(data.resources)){const bytes=fs.readFileSync(path.join(proto,name));if(sha(bytes)!==expected)throw Error('after resource differs: '+name);}
for(const [name,expected] of Object.entries(oldInteractions.resources)){const bytes=fs.readFileSync(name==='mentor-core.js'?path.join(__dirname,'pre-lazy-mentor-core.js'):path.join(proto,name));if(sha(bytes)!==expected)throw Error('pre-lazy resource differs: '+name);}
for(const [side,entries] of Object.entries(lazy.resources))for(const [name,expected] of Object.entries(entries)){const bytes=fs.readFileSync(side==='before'&&name==='mentor-core.js'?path.join(__dirname,'pre-lazy-mentor-core.js'):path.join(proto,name));if(sha(bytes)!==expected)throw Error('lazy comparison resource differs: '+side+'/'+name);}
console.log('Mentor comparison fixtures and measured resources verified.');

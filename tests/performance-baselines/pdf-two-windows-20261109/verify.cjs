'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const sha=x=>crypto.createHash('sha256').update(x).digest('hex');
const data=JSON.parse(fs.readFileSync(path.join(__dirname,'results.json'),'utf8'));
if(data.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,'measure.cjs'))))throw Error('测量脚本指纹不符');
for(const [name,hash] of Object.entries(data.resources))if(sha(fs.readFileSync(path.join(__dirname,'resources',name)))!==hash)throw Error('资源指纹不符：'+name);
if(data.measurements.length!==6||data.measurements.some(x=>x.samples.length!==7))throw Error('样本不完整');
const before=JSON.parse(fs.readFileSync(path.join(__dirname,'before-results.json'),'utf8'));
if(before.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,'measure.cjs'))))throw Error('优化前夹具指纹不符');
for(const [name,hash] of Object.entries(before.resources))if(sha(fs.readFileSync(path.join(__dirname,'before',name)))!==hash)throw Error('优化前资源指纹不符：'+name);
for(const name of ['sync-before-results.json','sync-results.json']){const sample=JSON.parse(fs.readFileSync(path.join(__dirname,name),'utf8'));if(sample.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,'sync.cjs')))||sample.measurements.length!==6||sample.measurements.some(x=>x.samples.length!==7))throw Error('跨窗样本不完整或夹具不符：'+name)}
console.log('PASS 两窗口编辑场景样本和资源指纹');

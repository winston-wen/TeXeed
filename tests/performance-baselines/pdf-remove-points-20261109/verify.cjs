'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const here=__dirname,sha=x=>crypto.createHash('sha256').update(x).digest('hex'),r=JSON.parse(fs.readFileSync(path.join(here,'results.json'),'utf8'));
if(r.fixture!==sha(fs.readFileSync(path.join(here,'measure.cjs'))))throw Error('fixture differs');
for(const [mode,entries] of Object.entries(r.resources))for(const [name,expected] of Object.entries(entries))if(sha(fs.readFileSync(path.join(here,mode,name)))!==expected)throw Error(mode+'/'+name+' differs');
if(r.measurements.length!==6||r.measurements.some(x=>x.samples.length!==7))throw Error('incomplete samples');
console.log('PASS removal comparison resources and samples');

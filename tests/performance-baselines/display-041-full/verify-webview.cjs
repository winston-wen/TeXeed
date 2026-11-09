'use strict';
const fs=require('fs'),path=require('path'),crypto=require('crypto');
const root=path.resolve(__dirname,'../../..'),here=__dirname,sha=p=>crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex');
for(const mode of ['before','after']){
 const metadata=JSON.parse(fs.readFileSync(path.join(here,'webview-'+mode+'-meta.json'),'utf8'));
 const resultPath=path.join(here,'webview-'+mode+'.json'),result=JSON.parse(fs.readFileSync(resultPath,'utf8'));
 if(sha(resultPath)!==metadata.resultSha256)throw Error(mode+' result hash mismatch');
 const sourceBase=mode==='before'?path.join(here,'native-before'):root;
 for(const [file,expected] of Object.entries(metadata.files)){const actual=sha(path.join(sourceBase,file));if(actual!==expected)throw Error(mode+' source differs: '+file);}
 if(result.measurements.length!==6||result.measurements.some(m=>m.samples.length!==7||!m.allocatedByAction))throw Error(mode+' sample count or separate allocations missing');
}
console.log('Full TeX native before/after fixture, resources and measured samples verified.');

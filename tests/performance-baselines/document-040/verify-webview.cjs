'use strict';
const fs=require('fs'),crypto=require('crypto'),path=require('path');
const root=path.resolve(__dirname,'../../..'),result=JSON.parse(fs.readFileSync(path.join(__dirname,'webview-results.json'),'utf8'));
for(const [file,expected] of Object.entries(result.sha256)){const actual=crypto.createHash('sha256').update(fs.readFileSync(path.join(root,file))).digest('hex');if(actual!==expected)throw Error('source differs: '+file);}
if(result.measurements.length!==6||result.measurements.some(m=>m.samples.length!==7))throw Error('incomplete WebView samples');
console.log('WebView fixture sources and samples verified.');

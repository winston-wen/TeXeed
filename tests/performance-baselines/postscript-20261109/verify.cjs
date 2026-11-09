'use strict';
const fs=require('fs'),path=require('path'),crypto=require('crypto'),assert=require('assert/strict');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
for(const ref of ['0be9eb7','cce0214','74f01c7'])for(const type of ['results','allocation']){
 const sample=JSON.parse(fs.readFileSync(path.join(__dirname,`${type}-${ref}.json`)));
 assert.equal(hash(fs.readFileSync(path.join(__dirname,`sample-${type==='results'?'compare':'allocation'}.cjs`))),sample.fixtureSha256);
 for(const mode of ['before','after'])for(const [name,expected] of Object.entries(sample.resources[mode])){
  const file=path.join(__dirname,mode==='before'?ref:'current',name);assert.equal(hash(fs.readFileSync(file)),expected,`${ref} ${mode} ${name}`);
 }
}
const profile=JSON.parse(fs.readFileSync(path.join(__dirname,'selection-profile.json')));assert.equal(hash(fs.readFileSync(path.join(__dirname,'selection-profile.cjs'))),profile.fixtureSha256);assert.equal(profile.measurements.length,24);
for(const [mode,files] of Object.entries(profile.resources))for(const [name,expected] of Object.entries(files)){
 let bytes=fs.readFileSync(path.join(__dirname,mode==='historical'?'74f01c7':'current',name));
 if(name==='mentor-core.js'&&(mode==='without-positioning'||mode==='optimized')){let text=bytes.toString('utf8');const changes=mode==='without-positioning'?[['if(!selectingByPointer&&!selectionFrame)selectionFrame=requestAnimationFrame(positionSelectionButton)','']]:[['function nearbyTextRects(block){const result=[];let count=0;','function nearbyTextRects(block){const result=[],reuseRange=document.createRange();let count=0;'],['const range=document.createRange();range.selectNodeContents(node);for(const r of range.getClientRects())','reuseRange.selectNodeContents(node);for(const r of reuseRange.getClientRects())'],['let placed=false;for(const y of ys)','const obstacles=nearby.concat(rects);let placed=false;for(const y of ys)'],['nearby.concat(rects).some','obstacles.some']];for(const [needle,replacement] of changes){assert.equal(text.split(needle).length,2);text=text.replace(needle,replacement)}bytes=Buffer.from(text);}
 assert.equal(hash(bytes),expected,`${mode} ${name}`);
}
console.log('PASS postscript samples, scripts and resource fingerprints');

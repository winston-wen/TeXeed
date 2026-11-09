'use strict';
const fs=require('fs'),path=require('path'),crypto=require('crypto'),assert=require('assert/strict');
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
for(const ref of ['0be9eb7','fe878b8'])for(const type of ['results','allocation']){
 const sample=JSON.parse(fs.readFileSync(path.join(__dirname,`${type}-${ref}.json`)));
 assert.equal(hash(fs.readFileSync(path.join(__dirname,`sample-${type==='results'?'compare':'allocation'}.cjs`))),sample.fixtureSha256);
 for(const mode of ['before','after'])for(const [name,expected] of Object.entries(sample.resources[mode])){
  const file=path.join(__dirname,mode==='before'?ref:'current',name);assert.equal(hash(fs.readFileSync(file)),expected,`${ref} ${mode} ${name}`);
 }
}
console.log('PASS carry-removal samples, scripts and resource fingerprints');

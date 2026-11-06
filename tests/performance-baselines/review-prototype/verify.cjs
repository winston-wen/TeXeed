'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02'),sha=b=>crypto.createHash('sha256').update(b).digest('hex');
const optimized=`const draft=document.querySelector('[data-review-view="draft"]');if(draft&&draft.getAttribute('aria-pressed')!=='true')draft.click();else renderTurns();`,previous=`document.querySelector('[data-review-view="draft"]')?.click();renderTurns();`;
for(const [file,fixture,doubled] of [['browser-performance.json','run.cjs',false],['interactions-results.json','interactions.cjs',false],['search-results.json','search.cjs',false],['search-double-results.json','search.cjs',true],['selection-results.json','selection.cjs',false]]){
 const data=JSON.parse(fs.readFileSync(path.join(__dirname,file),'utf8'));if(data.fixtureSha256!==sha(fs.readFileSync(path.join(__dirname,fixture))))throw Error(`${file}: fixture mismatch`);
 const sides=data.resources.after?['before','after']:['after'];for(const side of sides)for(const [name,expected] of Object.entries(data.resources[side]||data.resources)){let bytes=side==='before'?cp.execFileSync('git',['show',data.baseline+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:40*1024*1024}):fs.readFileSync(path.join(proto,name));if(doubled&&side==='after'&&name==='review-search.js'){const source=bytes.toString('utf8');if(!source.includes(optimized))throw Error('search optimized source missing');bytes=Buffer.from(source.replace(optimized,previous));}if(sha(bytes)!==expected)throw Error(`${file}: ${side}/${name} changed`);}
}
console.log('Review performance fixtures, historical inputs and measured resources verified.');

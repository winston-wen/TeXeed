'use strict';
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),cp=require('node:child_process');
const root=path.resolve(__dirname,'../../..'),proto=path.join(root,'prototypes/ai-chat-v02'),sha=x=>crypto.createHash('sha256').update(x).digest('hex');
function check(ok,message){if(!ok)throw Error(message)}
function read(name){return fs.readFileSync(path.join(__dirname,name));}
for(const [data,script,intermediate] of [['samples.json','measure.cjs',true],['samples-final.json','measure.cjs',true],['branch-final.json','measure.cjs',false],['operations-concurrent.json','operations.cjs',true],['operations-final.json','operations.cjs',false],['index-samples.json','index-measure.cjs',false],['index-render-deep.json','index-render-deep.cjs',false]]){
 const o=JSON.parse(read(data));check(sha(read(script))===o.fixtureHash,data+' script mismatch');check(o.measurements.length>0,data+' no measurements');
 for(const [mode,files] of Object.entries(o.resources))for(const [name,expected] of Object.entries(files)){
  if(mode==='after'&&intermediate)continue;
  const rev=o.revisions?.[mode]||(mode==='historic'?o.historic:o.baseline);
  const bytes=mode==='after'?fs.readFileSync(path.join(proto,name)):cp.execFileSync('git',['show',rev+':prototypes/ai-chat-v02/'+name],{cwd:root,maxBuffer:100*1024*1024});
  check(sha(bytes)===expected,data+' '+mode+'/'+name+' mismatch');
 }
 console.log('verified '+data+(intermediate?' (intermediate after resource not frozen)':''));
}
check(sha(read('native-fixture.cs.txt'))==='012efeabe5cb7af359facf481efa26e4d34bba4c9f69276cc04f6dccc28f99c6','native fixture hash mismatch');
check(sha(read('native-image-update-fixture.cs.txt'))==='9438c74fc4d2dd66d7dd80ee51a8cf62be98f7ac4c89ed785bead7065dfae23c','native image fixture hash mismatch');
check(sha(read('native-image-final-source.cs.txt'))===sha(fs.readFileSync(path.join(root,'src/TeXeed.App/Controls/AiAnnotationImages.cs'))),'native image final source mismatch');
for(const name of ['native-historic.json','native-before.json','native-before-repeat.json','native-after.json','native-images.json',...['old','new'].flatMap(mode=>[1,2,3].map(n=>`native-image-${mode}-${n}.json`))])check(JSON.parse(read(name)).samples.length>0,name+' no samples');
console.log('verified native fixtures, source and samples');

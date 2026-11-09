'use strict';
const fs=require('node:fs'),path=require('node:path');
const here=__dirname,source=fs.readFileSync(path.join(here,'after','app.js'),'utf8');
const original=" function updateChatLines(){if(!chat.clientWidth)return;chatMeasure.style.width=Math.max(0,chat.clientWidth-12)+'px';const lines=chat.value.split('\\n');while(lines.length<3)lines.push('');chatLines.replaceChildren(...lines.map((line,i)=>{chatMeasure.textContent=line||' ';const height=Math.max(20,chatMeasure.getBoundingClientRect().height);const span=document.createElement('span');span.textContent=i+1;span.style.height=height+'px';return span}));chatLines.style.transform='translateY(-'+chat.scrollTop+'px)';}";
const result=source.replace(/^ function updateChatLines\(\).*$/m,original);if(result===source)throw Error('cannot reconstruct update function');
const target=path.join(here,'reconstructed');fs.mkdirSync(target,{recursive:true});fs.writeFileSync(path.join(target,'app.js'),result);for(const name of ['index.html','style.css'])fs.copyFileSync(path.join(here,'after',name),path.join(target,name));

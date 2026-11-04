'use strict';
module.exports = () => {
 const source=Array.from({length:6},(_,i)=>'#'.repeat(i+1)+(i%2?' 1. 数字标题':' 中文标题')+'\n\n正文说明。').join('\n\n');
 current().turns=Array.from({length:50},(_,i)=>({id:'head-'+i,question:'合成问题 '+i,answer:source,answerHeadingVersion:1,sentAt:'2026-10-28T15:00:00+08:00',answeredAt:'2026-10-28T15:00:01+08:00',segments:[]}));
 current().draft='保留测试草稿';$('composer').value=current().draft;
};

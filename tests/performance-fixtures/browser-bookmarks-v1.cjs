'use strict';
module.exports = () => {
 const source='# 合成 **标题**\n\n合成正文 **第一段**。\n\n$$a(X)=2+3X+X^2$$\n\n合成正文 ***第二段***。\n\n'+Array.from({length:12},(_,i)=>(i+1)+'. 合成列表条目').join('\n')+'\n\n合成正文第三段。';
 current().turns=Array.from({length:50},(_,i)=>({id:'bench-'+i,question:'合成 **问题** '+i,answer:source,answerHeadingVersion:1,sentAt:'2026-10-28T15:00:00+08:00',answeredAt:'2026-10-28T15:00:01+08:00',segments:Array.from({length:3},(_,j)=>({kind:'activity',text:'已处理条目 '+j,details:j===0?'已处理条目 '+j:Array.from({length:20},(_,k)=>'合成详情 '+k).join('\n'),time:'2026-10-28T15:00:00+08:00'}))}));
 current().draft='保留测试草稿';$('composer').value=current().draft;renderTurns();$('reader').scrollTop=300;
};

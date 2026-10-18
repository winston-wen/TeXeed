/* Offline Markdown/TeX rendering for questions and saved previews. */
(function(root){
 'use strict';
 const escape=text=>String(text).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const code=(source,display)=>display?'<pre class="render-fallback"><code>'+escape(source)+'</code></pre>':'<code class="render-fallback">'+escape(source)+'</code>';
 function math(tex,display,raw,closed=true){
  try{
   if(!closed)throw new Error('Unclosed math delimiter');
   return root.katex.renderToString(tex,{displayMode:display,throwOnError:true,trust:false,strict:'ignore',maxExpand:1000,maxSize:20,macros:{}});
  }catch{return code(raw,display);}
 }
 function closing(source,delimiter,start){
  for(let i=source.indexOf(delimiter,start);i>=0;i=source.indexOf(delimiter,i+delimiter.length)){
   let slashes=0;for(let j=i-1;j>=0&&source[j]==='\\';j--)slashes++;
   if(slashes%2===0)return i;
  }return -1;
 }
 let md;
 try{
  md=root.markdownit({html:false,linkify:false,breaks:false});
  // Never request remote image resources from saved or user-provided Markdown.
  md.renderer.rules.image=(tokens,i)=>code('!['+tokens[i].content+']('+tokens[i].attrGet('src')+')',false);
  const openLink=md.renderer.rules.link_open,closeLink=md.renderer.rules.link_close;
  md.renderer.rules.link_open=(tokens,i,options,env,self)=>env.noLinks?'<span>':openLink?openLink(tokens,i,options,env,self):self.renderToken(tokens,i,options);
  md.renderer.rules.link_close=(tokens,i,options,env,self)=>env.noLinks?'</span>':closeLink?closeLink(tokens,i,options,env,self):self.renderToken(tokens,i,options);
  md.inline.ruler.before('escape','tex_inline',(state,silent)=>{
   const start=state.pos,tail=state.src.slice(start);
   const left=tail.startsWith('\\(')?'\\(':tail.startsWith('\\[')?'\\[':tail.startsWith('$$')?'$$':tail.startsWith('$')?'$':null;
   if(!left)return false;
   const endDelimiter=left==='\\('? '\\)':left==='\\['?'\\]':left;
   let end=closing(state.src,endDelimiter,start+left.length),closed=end>=0;
   if(left==='$'&&closed&&state.src.slice(start,end).includes('\n'))closed=false;
   if(!closed){end=state.src.indexOf('\n',start);if(end<0)end=state.src.length;}
   const next=end+(closed?endDelimiter.length:0);
   if(!silent){const token=state.push('tex_inline','',0);token.content=state.src.slice(start+left.length,end);token.meta={display:left==='$$'||left==='\\[',raw:state.src.slice(start,next),closed};}
   state.pos=next;return true;
  });
  md.renderer.rules.tex_inline=(tokens,i)=>{const t=tokens[i];return math(t.content,t.meta.display,t.meta.raw,t.meta.closed);};
  md.block.ruler.before('fence','tex_block',(state,start,end,silent)=>{
   const offset=state.bMarks[start]+state.tShift[start],first=state.src.slice(offset,state.eMarks[start]);
   if(state.sCount[start]-state.blkIndent>=4)return false;
   const left=first.startsWith('$$')?'$$':first.startsWith('\\[')?'\\[':null;if(!left)return false;
   const right=left==='$$'?'$$':'\\]';
   let last=start,content=first.slice(left.length),at=closing(content,right,0);
   while(at<0&&last+1<end){last++;content+='\n'+state.src.slice(state.bMarks[last]+state.tShift[last],state.eMarks[last]);at=closing(content,right,0);}
   if(at>=0&&content.slice(at+right.length).trim())return false;
   if(silent)return true;
   const token=state.push('tex_block','',0);token.block=true;token.map=[start,last+1];token.content=at<0?content:content.slice(0,at);token.meta={closed:at>=0,raw:left+content};state.line=last+1;return true;
  },{alt:['paragraph','reference','blockquote','list']});
  md.renderer.rules.tex_block=(tokens,i)=>{const t=tokens[i];return '<div class="rich-equation">'+math(t.content,true,t.meta.raw,t.meta.closed)+'</div>\n';};
 }catch{md=null;}
 root.renderRichText=function(source,inline=false,noLinks=false){
  source=String(source??'');
  try{if(!md)throw new Error('Markdown renderer unavailable');return inline?md.renderInline(source,{noLinks}):md.render(source,{noLinks});}
  catch{return code(source,!inline);}
 };
})(globalThis);

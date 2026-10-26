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
  // Show Markdown image references as source text.
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
 // Keep saved passage IDs stable when a bundled sample has been replaced.
 root.renderSavedAnswer=function(source,prefix){
  const tokens=[];let marker='@@M';while(source.includes(marker))marker+='@';
  const placeholder=index=>marker+index+'@@',singleToken=new RegExp('^'+marker+'(\\d+)@@$'),allTokens=new RegExp(marker+'(\\d+)@@','g');
  const stashCode=(raw,block)=>{tokens.push({block,html:root.renderRichText(raw,!block)});return (block?'\n\n':'')+placeholder(tokens.length-1)+(block?'\n\n':'');};
  const starts=[0,...[...source.matchAll(/\n/g)].map(match=>match.index+1)];
  for(const token of (md?md.parse(source,{}):[]).filter(token=>token.type==='fence'||token.type==='code_block').reverse()){
   const start=starts[token.map[0]],end=starts[token.map[1]]??source.length;
   source=source.slice(0,start)+stashCode(source.slice(start,end),true)+source.slice(end);
  }
  source=source.replace(/(`+)([\s\S]*?)\1(?!`)/g,raw=>stashCode(raw,false));
  source=source.replace(/\\\[([\s\S]*?)\\\]|\\\(([\s\S]*?)\\\)|\$\$([\s\S]*?)\$\$|\$([^$\n]+)\$/g,(raw,...groups)=>{
   const tex=groups.slice(0,4).find(value=>value!==undefined).trim(),display=groups[0]!==undefined||groups[2]!==undefined;
   tokens.push({display,html:'<span class="math" data-tex="'+escape(tex)+'" data-display="'+display+'" role="math" aria-label="'+escape(tex)+'">'+root.renderRichText(raw,true)+'</span>'});
   return (display?'\n\n':'')+placeholder(tokens.length-1)+(display?'\n\n':'');
  });
  let equation=0;
  return source.trim().split(/\n\s*\n/).map((chunk,index)=>{
   const token=singleToken.exec(chunk.trim());let content;
   if(token&&tokens[token[1]].display)content='<div class="equation">'+tokens[token[1]].html+'<span class="equation-number">('+(++equation)+')</span></div>';
   else if(token&&tokens[token[1]].block)content=tokens[token[1]].html;
   else content=root.renderRichText(chunk.replace(/^#+/,marks=>'#'.repeat(Math.min(4,marks.length+1)))).replace(allTokens,(_,index)=>tokens[index].html);
   const id=escape(prefix)+'-p'+index;
   return '<section class="passage" id="'+id+'" data-block="'+id+'"><div class="passage-body">'+content+'</div><div class="margin-notes"></div></section>';
  }).join('\n');
 };
})(globalThis);

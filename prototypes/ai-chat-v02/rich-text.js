/* Markdown/TeX rendering for questions and saved previews. */
(function(root){
 'use strict';
 const escape=text=>String(text).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const code=(source,display)=>display?'<pre class="render-fallback"><code>'+escape(source)+'</code></pre>':'<code class="render-fallback">'+escape(source)+'</code>';
 const compile=(tex,display=false)=>root.katex.renderToString(tex,{displayMode:display,throwOnError:true,trust:false,strict:'ignore',maxExpand:1000,maxSize:20,macros:{}});
 function math(tex,display,raw,closed=true){
  try{
   if(!closed)throw new Error('Unclosed math delimiter');
   return compile(display?tex.replace(/\\label\{[^{}]+\}/g,''):tex,display);
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
  // Apply CJK-friendly flanking rules during tokenization, keeping code and TeX opaque.
  const BaseInlineState=md.inline.State;
  const cjk=/[\p{Script=Han}\p{Script=Hiragana}\p{Script=Katakana}\p{Script=Hangul}\u3000-\u303f\ufe30-\ufe4f\uff01-\uffef]/u;
  const cjkNeighbor=char=>!!char&&cjk.test(char)&&!/\p{Emoji_Presentation}/u.test(char);
  md.inline.State=class extends BaseInlineState{
   scanDelims(start,canSplitWord){
    const result=super.scanDelims(start,canSplitWord);
    if(!canSplitWord)return result;
    const previous=start>1&&/[\uDC00-\uDFFF]/.test(this.src[start-1])?start-2:start-1;
    const before=previous<0?'':String.fromCodePoint(this.src.codePointAt(previous));
    const after=String.fromCodePoint(this.src.codePointAt(start+result.length)||0x20);
    if(cjkNeighbor(before)||cjkNeighbor(after)){
     if(after&&!md.utils.isWhiteSpace(after.codePointAt(0)))result.can_open=true;
     if(before&&!md.utils.isWhiteSpace(before.codePointAt(0)))result.can_close=true;
    }
    // Recover a common AI typo only for paired strong markers and CJK punctuation.
    if(this.src.charCodeAt(start)===42&&result.length===2){
     if(this.env.revision&&after===' '&&/\S/u.test(this.src[start+3]||'')&&this.src[start+3]!=='*')result.can_open=true;
     this._strongOpeners??=new WeakMap();
     const open=this._strongOpeners.get(this.delimiters);
     if(open!==undefined&&!result.can_close&&before===' '){
      const content=this.src.slice(open+2,start);
      if(!content.includes('**')&&!/[\r\n]/.test(content)&&/[：；，。！？、）】》」』] $/u.test(content))result.can_close=true;
     }
     if(open!==undefined&&result.can_close)this._strongOpeners.delete(this.delimiters);
     else if(result.can_open)this._strongOpeners.set(this.delimiters,start);
    }
    return result;
   }
  };
  const renderImage=md.renderer.rules.image;
  md.renderer.rules.image=(tokens,i,options,env,self)=>{
   const token=tokens[i];token.attrJoin('class','zoomable-image');token.attrSet('tabindex','0');token.attrSet('role','button');token.attrSet('aria-haspopup','dialog');token.attrSet('loading','lazy');token.attrSet('decoding','async');token.attrSet('referrerpolicy','no-referrer');
   return renderImage(tokens,i,options,env,self);
  };
  md.renderer.rules.table_open=()=>'<div class="table-wrap" tabindex="0" role="region" aria-label="可横向滚动的表格"><table>\n';
  md.renderer.rules.table_close=()=>'</table></div>\n';
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
 // Use the reading parser's delimiter pairs while keeping caret positions in the original source.
 root.revisionEmphasisMarks=function(source){
  if(!md)return [];
  const state=new md.inline.State(source,md,{revision:true},[]),groups=[];
  const scan=state.scanDelims;
  state.scanDelims=function(start,canSplitWord){
   const run=scan.call(this,start,canSplitWord);
   if(this.src[start]==='*'||this.src[start]==='_')groups.push({start,length:run.length,list:this.delimiters,first:this.delimiters.length});
   return run;
  };
  md.inline.tokenize(state);
  // Each delimiter owns a token before Markdown pairs the runs and joins plain text.
  // Keep those token objects so nested links, escapes and code use the parser's own decisions.
  const positioned=[];
  for(const {start,length,list,first} of groups)for(let i=0;i<length;i++)
   positioned.push({at:start+i,token:state.tokens[list[first+i].token]});
  for(const rule of md.inline.ruler2.getRules(''))rule(state);
  const marks=[];
  for(const {at,token} of positioned)if(token.tag==='strong'||token.tag==='em'){
   // A paired strong run keeps the inner opening star and the inner closing star.
   const open=token.nesting===1,size=token.markup.length;
   marks.push({at:open?at-size+1:at,size,open,tag:token.tag});
  }
  if(source.includes('** ')&&/[\r\n]/.test(source)){
   const stack=[],acrossBreaks=new Set();
   for(const mark of marks){
    if(mark.open){stack.push(mark);continue;}
    const opening=stack.pop();
    if(opening.tag==='strong'&&source[opening.at+2]===' '&&/[\r\n]/.test(source.slice(opening.at,mark.at))){
     acrossBreaks.add(opening);acrossBreaks.add(mark);
    }
   }
   return marks.filter(mark=>!acrossBreaks.has(mark));
  }
  return marks;
 };
 root.revisionFormulaSpans=function(source){
  if(!source.includes('$')&&!source.includes('\\(')&&!source.includes('\\['))return [];
  const spans=[];
  const openings=/(?<!\\)(?:\\\\)*(\\\(|\\\[|\$\$|\$)/g;
  for(let match;(match=openings.exec(source));){
   const left=match[1],start=openings.lastIndex-left.length;
   const right=left==='\\('? '\\)':left==='\\['?'\\]':left;
   const body=start+left.length,close=closing(source,right,body),display=left==='$$'||left==='\\[';
   const lineEnd=source.indexOf('\n',start),limit=lineEnd<0?source.length:lineEnd;
   const closed=close>=0&&(display||close<=limit),end=closed?close+right.length:limit;
   const tex=source.slice(body,closed?close:end);
   openings.lastIndex=end;
   if(left==='$'&&!closed&&(/^\s/.test(tex)||/^\d+(?:[.,]\d+)*(?:\s.*)?$/.test(tex)))continue;
   spans.push({raw:source.slice(start,end),tex,display,closed,start,end,left,right});
  }
  return spans;
 };
 root.compileRevisionFormula=compile;
 root.renderRichText=function(source,inline=false,noLinks=false){
  source=String(source??'');
  try{if(!md)throw new Error('Markdown renderer unavailable');return inline?md.renderInline(source,{noLinks}):md.render(source,{noLinks});}
  catch{return code(source,!inline);}
 };
 function answerParts(source,protectCode=true){
  const tokens=[];let marker='@@M';while(source.includes(marker))marker+='@';
  const placeholder=index=>marker+index+'@@',singleToken=new RegExp('^'+marker+'(\\d+)@@$'),allTokens=new RegExp(marker+'(\\d+)@@','g');
  if(protectCode){
   const stashCode=(raw,block)=>{tokens.push({raw,block});return (block?'\n\n':'')+placeholder(tokens.length-1)+(block?'\n\n':'');};
   const starts=[0,...[...source.matchAll(/\n/g)].map(match=>match.index+1)];
   for(const token of (md?md.parse(source,{}):[]).filter(token=>token.type==='fence'||token.type==='code_block').reverse()){
    const start=starts[token.map[0]],end=starts[token.map[1]]??source.length;
    source=source.slice(0,start)+stashCode(source.slice(start,end),true)+source.slice(end);
   }
   source=source.replace(/(`+)([\s\S]*?)\1(?!`)/g,raw=>stashCode(raw,false));
  }
  source=source.replace(/\\\[([\s\S]*?)\\\]|\\\(([\s\S]*?)\\\)|\$\$([\s\S]*?)\$\$|\$([^$\n]+)\$/g,(raw,...groups)=>{
   const tex=groups.slice(0,4).find(value=>value!==undefined).trim(),display=groups[0]!==undefined||groups[2]!==undefined;
   tokens.push({raw,tex,display});
   return (display?'\n\n':'')+placeholder(tokens.length-1)+(display?'\n\n':'');
  });
  return {blocks:source.trim().split(/\n\s*\n/),tokens,singleToken,allTokens};
 }
 root.answerSourceBlocks=function(source,protectCode=true){
  const {blocks,tokens,allTokens}=answerParts(source,protectCode);
  return blocks.map(block=>block.replace(allTokens,(_,index)=>tokens[index].raw));
 };
 // Markdown-it identifies tables even when the header shares a passage with preceding prose.
 root.answerPipeTables=function(source){
  if(!md||!source.includes('|')||!source.includes('\n'))return [];
  const {blocks,tokens,allTokens}=answerParts(source);
  return blocks.flatMap(block=>{
    const lines=block.split(/\r?\n/);
    return md.parse(block,{}).filter(t=>t.type==='table_open').map(t=>
      lines.slice(...t.map).join('\n').replace(allTokens,(_,i)=>tokens[i].raw));
  });
 };
 // markdown-it sizes a pipe table from its header; the native reader uses its widest row.
 // Widen the rendering input only. TeX and code are already single placeholders here.
 function widenTableHeaders(block){
   if(!md||!block.includes('|')||!block.includes('\n'))return block;
   const lines=block.split(/\r?\n/);
   function width(line){
     const cells=line.trim().split(/(?<!\\)\|/);
     if(cells[0]==='')cells.shift();
     if(cells.at(-1)==='')cells.pop();
     return cells.length;
   }
   for(const token of md.parse(block,{})){
     if(token.type!=='table_open')continue;
     const [header,end]=token.map,columns=width(lines[header]);
     let widest=columns;
     for(let i=header+2;i<end;i++)widest=Math.max(widest,width(lines[i]));
     if(widest===columns)continue;
     for(const i of [header,header+1]){
       const line=lines[i].trimEnd(),closing=/(?<!\\)\|$/.test(line)?'':' |';
       lines[i]=line+closing+(i===header?'   |':' --- |').repeat(widest-columns);
     }
   }
   return lines.join('\n');
 }
 // Keep the cached renderer's passage boundaries while correcting heading levels.
 root.restoreAnswerHeadings=function(markup,source){
  const runtimeMarkup=/<\/(?:h[1-6]|p|pre|ul|ol|blockquote|div)>\r?\n<\/div>/.test(markup),{blocks}=answerParts(source,runtimeMarkup);
  return markup.replace(/(<section\b[^>]*\bdata-block="[^"]+-p(\d+)"[^>]*><div class="passage-body">\s*)<h([1-6])(\b[^>]*)>([\s\S]*?)<\/h\3>/g,(original,prefix,index,oldLevel,attributes,body)=>{
   const heading=/^ {0,3}(#{1,6})(?:[ \t]+|$)/.exec(blocks[index]||'');if(!heading)return original;
   const level=heading[1].length;return prefix+'<h'+level+attributes+'>'+body+'</h'+level+'>';
  });
 };
 function answerTokenHtml(token){
  if(token.tex===undefined)return root.renderRichText(token.raw,!token.block);
  const body=token.display?token.tex.replace(/\\(?:tag\*?|label)\{[^{}]+\}/g,'').trim():token.tex;
  return '<span class="math" data-tex="'+escape(token.tex)+'" data-display="'+token.display+'" role="math" aria-label="'+escape(body)+'">'+math(body,token.display,token.raw,token.closed!==false)+'</span>';
 }
 root.renderRevisionFormula=answerTokenHtml;
 // Rebuild only the obsolete 0.5.20 placeholders from the original Markdown.
 // Patching the saved passage in place preserves anchors in older sessions.
 root.restoreCachedFormulaMarkup=function(markup,source){
  const inline=new Map(answerParts(source).tokens.filter(token=>token.tex!==undefined&&!token.display).map(token=>[token.tex,token]));
  return markup.replace(/<span data-inline-number="([^"]*)">[^<]*<\/span>/g,(saved,tex)=>{
   const token=inline.get(tex);
   return token?answerTokenHtml(token):saved;
  });
 };
 // Keep saved passage IDs stable when a bundled sample has been replaced.
 root.renderSavedAnswer=function(source,prefix){
  const {blocks,tokens,singleToken,allTokens}=answerParts(source);
  for(const token of tokens)token.html=answerTokenHtml(token);
  let equation=0;
  return blocks.map((chunk,index)=>{
   const token=singleToken.exec(chunk.trim());let content;
   if(token&&tokens[token[1]].display)content='<div class="equation">'+tokens[token[1]].html+'<span class="equation-number">('+(++equation)+')</span></div>';
   else if(token&&tokens[token[1]].block)content=tokens[token[1]].html;
   else content=root.renderRichText(widenTableHeaders(chunk)).replace(allTokens,(_,index)=>tokens[index].html);
   const id=escape(prefix)+'-p'+index;
   return '<section class="passage" id="'+id+'" data-block="'+id+'"><div class="passage-body">'+content+'</div><div class="margin-notes"></div></section>';
  }).join('\n');
 };
})(globalThis);

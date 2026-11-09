(()=>{
'use strict';
const doc=document.getElementById('mentor-document');
if(!doc)return;
function updateHeadings(){const counters=[0,0,0];for(const heading of doc.querySelectorAll('h2.mentor-block,h3.mentor-block,h4.mentor-block')){const depth=Number(heading.tagName.slice(1))-2;counters[depth]++;for(let i=depth+1;i<counters.length;i++)counters[i]=0;const number=counters.slice(0,depth+1).join('.');let marker=heading.querySelector(':scope > .document-heading-number');if(!marker){marker=document.createElement('span');marker.className='document-heading-number';marker.dataset.tex='';marker.contentEditable='false';heading.prepend(marker)}marker.textContent='['+number+']\u00a0';}}
document.addEventListener('texeed-document-rendered',updateHeadings);
requestAnimationFrame(updateHeadings);
})();

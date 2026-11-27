'use strict';

// A question remains attached to the turn that asked it. Confirmation consumes
// only that question's draft and keeps the conversation composer independent.
window.turnDecision=(()=>{
  function html(turn){
    const question=turn.decision;
    if(!question&&!turn.decisions?.length)return '';
    const history=(turn.decisions||[]).map(item=>
      '<div class="decision-record"><p>'+esc(item.question)+'</p><p>'+esc(item.cancelled?'已结束本轮':'你的回答　'+item.answer)+'</p></div>').join('');
    if(!question)return history;
    const value=String(question.draft||''),id=esc(question.id);
    const fields=question.options?question.options.map(option=>
      '<label><input type="radio" name="'+id+'" data-decision-input value="'+esc(option)+'"'+(option===value?' checked':'')+'> '+esc(option)+'</label>').join(''):
      '<textarea rows="2" data-decision-input aria-labelledby="question-'+id+'">'+esc(value)+'</textarea>';
    return history+'<section class="decision-question" aria-labelledby="question-'+id+'" data-decision="'+id+'"><p id="question-'+id+'">'+esc(question.question)+'</p>'+
      fields+'<div class="decision-actions"><button type="button" data-decision-action="confirm"'+(value.trim()?'':' disabled')+
      '>确认并继续</button><button type="button" data-decision-action="cancel">结束本轮</button></div></section>';
  }
  function showWait(turn){
    const article=document.getElementById(turn.id);
    const segments=article?.querySelector('.segments'),footer=article?.querySelector('.answer-time');
    if(!segments||!footer){renderTurns();return;}
    segments.querySelectorAll(':scope > .decision-record,:scope > .decision-question').forEach(node=>node.remove());
    const answer=segments.querySelector(':scope > .answer-start-divider');
    if(answer)answer.insertAdjacentHTML('beforebegin',html(turn));
    else segments.insertAdjacentHTML('beforeend',html(turn));
    const status=document.createElement('span');status.setAttribute('role','status');
    status.textContent='等待你确认答案';footer.replaceChildren(status);
  }
  function finish(turn,cancelled=false){
    const question=turn.decision;
    if(!question)return;
    const {decisions,answeredAt,endedAt,incomplete,endReason}=turn;
    turn.decisions=[...(decisions||[]),{question:question.question,answer:cancelled?'':question.draft,cancelled}];
    delete turn.decision;
    const at=new Date().toISOString();
    if(cancelled){turn.incomplete=true;turn.endReason='user';turn.endedAt=at;delete turn.answeredAt;}
    else {turn.incomplete=false;turn.answeredAt=at;delete turn.endedAt;delete turn.endReason;}
    return ()=>Object.assign(turn,{decision:question,decisions,answeredAt,endedAt,incomplete,endReason});
  }
  function wait(turn,next){
    const old=turn.decision,undo=finish(turn),ending=turn.answeredAt;
    turn.decision=next;delete turn.answeredAt;
    if(persist())return true;
    if(undo)undo();else {turn.decision=old;turn.answeredAt=ending;}
    return false;
  }
  function decisionFor(element){
    const form=element.closest('.decision-question');
    const turn=form&&current().turns.find(t=>t.id===form.closest('.turn')?.id);
    return turn?.decision?.id===form?.dataset.decision&&turn?.decision?turn:null;
  }
  document.addEventListener('input',event=>{
    if(!event.target.matches('[data-decision-input]'))return;
    const turn=decisionFor(event.target);
    if(!turn)return;
    turn.decision.draft=event.target.value;persist();
    event.target.closest('.decision-question').querySelector('[data-decision-action=confirm]').disabled=!event.target.value.trim();
  });
  document.addEventListener('click',event=>{
    const button=event.target.closest('[data-decision-action]');
    if(!button)return;
    const turn=decisionFor(button);
    if(!turn)return;
    const question=turn.decision,cancelled=button.dataset.decisionAction==='cancel';
    if(!cancelled){
      if(!question.draft?.trim()||(question.options&&!question.options.includes(question.draft)))return;
      if(question.task){
        const target=current().turns.find(t=>t.id===question.task.turnId);
        if(target)turnRevision.askAi(target,question.draft,null,turn);
        return;
      }
    }
    const oldAnswer=turn.answer,undo=finish(turn,cancelled);
    if(!cancelled)turn.answer=question.results?.[question.options.indexOf(question.draft)]||'已收到你的回答。';
    if(!persist()){undo();turn.answer=oldAnswer;return;}
    renderTurns();
  });
  return {html,finish,wait,showWait};
})();

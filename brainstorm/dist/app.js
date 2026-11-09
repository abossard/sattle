'use strict';
let includeLink=true;
let replayTimer=null;
let lastReplay=null;
const $=id=>document.getElementById(id);

// The previous graph is supplied first. This illustrative projection selects
// nodes and a relationship, then evaluates retained inputs. A final net diff
// is computed once; it does not purport to implement the production engine.
function evaluateEdit(include){
  const previous={nodes:['SVC-01','SVC-02'],edges:['checkout-calls-payments']};
  const retained=[{id:'flow-1',edge:'checkout-calls-payments'},{id:'http-1',edge:'checkout-calls-payments'}];
  const next={nodes:[...previous.nodes],edges:include?[...previous.edges]:[]};
  for(const input of retained){if(include&&!next.edges.includes(input.edge))next.edges.push(input.edge)}
  const removed=previous.edges.filter(id=>!next.edges.includes(id));
  const added=next.edges.filter(id=>!previous.edges.includes(id));
  return {graph:next,diff:{added,removed}};
}
function resetReplay(){
  if(replayTimer)clearTimeout(replayTimer);
  replayTimer=null;lastReplay=null;
  $('run-replay').disabled=false;
  $('repeat-test').disabled=true;
  $('determinism-result').textContent='';
  $('replay-badge').textContent='READY';
  $('replay-result').classList.remove('error');
  $('replay-result').innerHTML='<span class="result-symbol">⌁</span><span>Apply the edit to see the result.</span>';
  document.querySelectorAll('[data-replay]').forEach(el=>{el.classList.remove('complete');el.querySelector('.replay-check').textContent='○'});
}
$('rule-toggle').addEventListener('click',()=>{
  includeLink=!includeLink;$('rule-toggle').classList.toggle('on',includeLink);$('rule-toggle').setAttribute('aria-pressed',String(includeLink));resetReplay();
});
$('run-replay').addEventListener('click',()=>{
  resetReplay();$('run-replay').disabled=true;$('replay-badge').textContent='REPLAYING';
  const applied=includeLink;let i=0;
  function advance(){
    const row=document.querySelector(`[data-replay="${i}"]`);row.classList.add('complete');row.querySelector('.replay-check').textContent='✓';i++;
    if(i<4){replayTimer=setTimeout(advance,400);return}
    replayTimer=null;lastReplay={include:applied,result:evaluateEdit(applied)};
    $('replay-badge').textContent='COMPLETE';$('run-replay').disabled=false;$('repeat-test').disabled=false;
    const text=applied?'No net graph change. The same relationship remains, so no diff is emitted.':'One final diff: remove Checkout → Payments. The entities remain in this example.';
    $('replay-result').replaceChildren();const icon=document.createElement('span');icon.className='result-symbol';icon.textContent=applied?'✓':'−';const content=document.createElement('span');content.textContent=text;$('replay-result').append(icon,content);
  }
  advance();
});
$('repeat-test').addEventListener('click',()=>{
  if(!lastReplay)return;
  const a=JSON.stringify(evaluateEdit(lastReplay.include));
  const b=JSON.stringify(evaluateEdit(lastReplay.include));
  $('determinism-result').textContent=a===b?'✓ Two illustrative runs produced the same graph and net diff.':'Different results detected in the illustrative runs.';
});

// Input IDs here identify occurrences in this self-contained illustration.
// They are not a statement about how a production engine creates IDs.
let historySequence=0;
let checkoutHistory=[];
let paymentsHistory=[];
function updateHistoryDisplay(message){
  function draw(id,items){
    const target=$(id);target.replaceChildren();
    if(!items.length){const empty=document.createElement('span');empty.className='empty-history';empty.textContent='No retained inputs';target.appendChild(empty);return}
    items.forEach(n=>{const item=document.createElement('span');item.className='chip';item.textContent=`E${n}`;target.appendChild(item)});
  }
  draw('history-checkout',checkoutHistory);draw('history-payments',paymentsHistory);
  const replay=[...new Set([...checkoutHistory,...paymentsHistory])].sort((a,b)=>a-b);
  $('history-replay').textContent=replay.length?replay.map(n=>`E${n}`).join('  →  '):'—';
  $('history-message').textContent=message;
}
document.querySelectorAll('[data-memory]').forEach(button=>button.addEventListener('click',()=>{
  const kind=button.dataset.memory;const id=++historySequence;
  if(kind==='checkout'||kind==='both')checkoutHistory=[...checkoutHistory,id].slice(-2);
  if(kind==='payments'||kind==='both')paymentsHistory=[...paymentsHistory,id].slice(-2);
  const descriptions={checkout:`E${id} changed Checkout only. Payments’ history is unchanged.`,payments:`E${id} changed Payments only. Checkout’s history is unchanged.`,both:`E${id} changed both elements. It appears in both histories but will replay once.`,none:`E${id} did not change the graph. Neither element’s history changes.`};
  updateHistoryDisplay(descriptions[kind]);
}));
$('memory-reset').addEventListener('click',()=>{historySequence=0;checkoutHistory=[];paymentsHistory=[];updateHistoryDisplay('Choose an action to add an illustrative input.')});

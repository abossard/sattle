'use strict';
(()=>{
 const guide=document.querySelector('#engine-guide');if(!guide)return;
 let saved=0;try{saved=Number(sessionStorage.getItem('health-graph-guide-step'))||0}catch{}
 const progress=document.createElement('progress');progress.className='guide-progress';progress.max=21;progress.value=saved+1;progress.setAttribute('aria-label','Guide progress');guide.append(progress);
 const select=document.createElement('select');select.setAttribute('aria-label','Choose a guide step');document.querySelectorAll('[data-guide-step]').forEach(b=>{const o=document.createElement('option');o.value=b.dataset.guideStep;o.textContent=b.getAttribute('aria-label');select.append(o)});select.value=String(saved);select.onchange=()=>document.querySelector(`[data-guide-step="${select.value}"]`).click();guide.append(select);
 document.addEventListener('atlas-guide-selected',e=>{progress.value=e.detail.index+1;progress.max=e.detail.total;select.value=String(e.detail.index)});
 guide.querySelector('#guide-link').addEventListener('click',()=>{try{sessionStorage.setItem('atlas-reading-context',JSON.stringify({parentHref:'index.html#engine-guide',parentTitle:'Numbered guide',target:guide.querySelector('#guide-link').getAttribute('href'),siblings:[]}))}catch{}});
})();

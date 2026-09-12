'use strict';
const $ = id => document.getElementById(id);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const clone = value => structuredClone(value);
const state = { token:'', config:null, draft:null, route:'overview', status:null, metrics:null, resources:null, history:[], busy:false, profiles:{}, apiKeys:[], assets:[], imageUrls:new Map(), wm:null, wmSaved:null, wmName:'', wmDirty:false, analyticsSource:'' };
const sections = [
 ['overview','Overview','Your conversion service, at a glance.'],['jobs','Jobs & queue','Inspect conversions, monitor the queue, and review failed jobs.'],
 ['Performance','Performance & profiles','Configure parallel swarms and save complete service configurations.'],
 ['Service','Service','Network binding and graceful shutdown.'],['Paths','Storage & engines','Storage locations, conversion engines, and supporting tools.'],
 ['Api','Request limits','Control what enters your conversion service.'],['Resources','Resource limits','Balance memory, disk, and processing time.'],
 ['Workers','Workers & retries','Manage the queue, worker processes, and recovery.'],['Concurrency','Concurrency','Control how many conversions run together.'],
 ['WatchFolder','Watch folders','Configure automatic intake, stability, and retention.'],['Conversion','PDF output','Choose default operations and tune your output profiles.'],
 ['watermarks','Watermarks','Design reusable marks with a live document preview.'],['security','Security & system','Inspect your service environment and access boundary.']
];
const icons = {
 overview:'<rect x="3" y="3" width="7" height="7" rx="1"/><rect x="14" y="3" width="7" height="7" rx="1"/><rect x="3" y="14" width="7" height="7" rx="1"/><rect x="14" y="14" width="7" height="7" rx="1"/>',
 jobs:'<path d="M5 3h10l4 4v14H5zM14 3v5h5M8 12h8M8 16h6"/>',Performance:'<path d="M4 19V5M4 19h16M8 16v-5M12 16V8M16 16V4"/>',
 Service:'<path d="M4 7h16M7 4v6M17 4v6M4 17h16M10 14v6M14 14v6"/>',Paths:'<path d="M3 6h7l2 2h9v11H3zM3 6V4h7l2 2"/>',Api:'<path d="M7 7h10M7 12h10M7 17h10M4 7h.01M4 12h.01M4 17h.01"/>',
 Resources:'<path d="M12 3l2.2 5.1L20 9l-4 3.8 1.1 5.7L12 16l-5.1 2.5L8 12.8 4 9l5.8-.9z"/>',Workers:'<path d="M8 4h8v4H8zM5 16h5v4H5zM14 16h5v4h-5zM12 8v4M7.5 16v-4h9v4"/>',Concurrency:'<circle cx="8" cy="8" r="3"/><circle cx="16" cy="16" r="3"/><path d="M10 10l4 4M16 8h4M18 6v4M4 16h4M6 14v4"/>',
 WatchFolder:'<path d="M3 7h7l2 2h9v10H3zM5 5h6l2 2"/>',Conversion:'<path d="M6 3h9l4 4v14H6zM15 3v5h5M9 13h6M9 17h6"/>',watermarks:'<path d="M4 18h16M6 15l4-9 4 9M8 12h4M16 6h3v3"/>',security:'<path d="M12 3l7 3v5c0 5-7 9-7 9s-7-4-7-9V6zM9 12l2 2 4-5"/>'
};
const icon = (id) => `<svg viewBox="0 0 24 24" aria-hidden="true">${icons[id]||icons.Service}</svg>`;
 $('navigation').innerHTML = sections.map(([id,label])=>`<a href="#${id}" data-route="${id}">${icon(id)}${label}</a>`).join('');

async function api(path, options={}) {
 const headers = new Headers(options.headers || {}); if(state.token) headers.set('Authorization', 'Bearer '+state.token);
 const response = await fetch(path,{...options,headers,cache:'no-store',signal:options.signal || AbortSignal.timeout(20000)});
 if(!response.ok){let message=`Request failed (${response.status})`;try{const body=await response.json();message=body.message||body.detail||JSON.stringify(body.errors||body);}catch{}
  if(response.status===401)message='Authentication required. Open Connection & access to enter the server token.';
  throw new Error(message);
 }
 if(options.blob)return response.blob(); if(response.status===204)return null;return response.json();
}
function notice(message,error=false){$('notice').hidden=!message;$('notice').textContent=message;$('notice').className='notice'+(error?' error':'');}
function bytes(value){if(value==null)return '—';if(value===0)return '0 B';const i=Math.min(4,Math.floor(Math.log(Math.abs(value))/Math.log(1024)));return (value/1024**i).toLocaleString(undefined,{maximumFractionDigits:1})+' '+['B','KiB','MiB','GiB','TiB'][i];}
function savingsPercent(value){if(value==null)return '—';return (value>0?'+':'')+value.toLocaleString(undefined,{maximumFractionDigits:1})+'%';}
function human(value){return value.replace(/([a-z0-9])([A-Z])/g,'$1 $2').replace(/Pdf/g,'PDF').replace(/Api/g,'API');}
function duration(value){if(value==null)return '—';return value<3600?Math.floor(value/60)+'m '+Math.floor(value%60)+'s':Math.floor(value/3600)+'h '+Math.floor(value%3600/60)+'m';}
function flatten(object,prefix=''){return Object.entries(object).flatMap(([key,value])=>value&&typeof value==='object'&&!Array.isArray(value)?flatten(value,prefix+key+'.'):[{path:prefix+key,value}]);}
function getPath(object,path){return path.split('.').reduce((o,k)=>o?.[k],object);}
function setPath(object,path,value){const keys=path.split('.'),last=keys.pop();keys.reduce((o,k)=>o[k],object)[last]=value;}
function changes(){if(!state.draft)return [];return flatten(state.draft).filter(x=>!x.path.startsWith('CurrentDefaultsReference.')&&JSON.stringify(x.value)!==JSON.stringify(getPath(state.config.settings,x.path)));}
function updateDirty(){const count=changes().length;$('saveBar').hidden=!count;$('changeCount').textContent=count+' unsaved setting'+(count===1?'':'s');}
window.addEventListener('beforeunload',e=>{if(changes().length||state.wmDirty){e.preventDefault();e.returnValue='';}});

async function loadConfig(){const result=await api('/v1/admin/settings');state.config=result;state.draft=clone(result.settings);updateDirty();if(result.restartRequired)notice('Saved configuration differs from startup. Restart the service to apply it.');}
let liveController=null, liveGeneration=0, liveRetry=null;
async function refresh(){connectLive();}
function connectLive(){
 const generation=++liveGeneration;clearTimeout(liveRetry);liveController?.abort();liveController=new AbortController();
 const headers={};if(state.token)headers.Authorization='Bearer '+state.token;
 $('liveBadge').textContent='Connecting…';
 (async()=>{
  try{
   const liveUrl='/v1/admin/live'+(state.analyticsSource?'?source='+encodeURIComponent(state.analyticsSource):'');
   const response=await fetch(liveUrl,{headers,signal:liveController.signal,cache:'no-store'});
   if(!response.ok)throw new Error(response.status===401?'Authentication required. Open Connection & access.':'Live connection failed ('+response.status+').');
   const reader=response.body.getReader(),decoder=new TextDecoder();let buffer='';
   while(generation===liveGeneration){
    const chunk=await reader.read();if(chunk.done)throw new Error('Live connection closed. Reconnecting…');
    if(generation!==liveGeneration)return;
    buffer+=decoder.decode(chunk.value,{stream:true});
    let end;while((end=buffer.indexOf('\n\n'))>=0){const event=buffer.slice(0,end);buffer=buffer.slice(end+2);const data=event.split('\n').find(line=>line.startsWith('data: '));if(data)applyLive(JSON.parse(data.slice(6)));}
   }
  }catch(error){
   if(generation!==liveGeneration)return;
   $('liveBadge').textContent='Reconnecting';$('connectionLabel').textContent='Live connection interrupted';
   state.connectionError=true;notice(error.message,true);
   liveRetry=setTimeout(connectLive,3000);
  }
 })();
}
function applyLive(snapshot){
 state.swarm=snapshot.swarm;state.watchFolder=snapshot.watchFolder;
 if(snapshot.profiles){
  const changed=JSON.stringify(snapshot.profiles)!==JSON.stringify(state.profiles);state.profiles=snapshot.profiles;
  if(changed&&$('profileSelect')){
   const selected=state.wmName,names=Object.keys(state.profiles);if(selected&&!names.includes(selected))names.push(selected);
   $('profileSelect').innerHTML='<option value="">New watermark profile</option>'+names.sort().map(name=>'<option '+(name===selected?'selected':'')+'>'+escapeHtml(name)+'</option>').join('');
  }
 }
 if(state.connectionError){notice('Live connection restored.');state.connectionError=false;}
 state.status=snapshot.status;state.metrics=snapshot.metrics;state.resources=snapshot.resources;state.analytics=snapshot.analytics;state.apiUsage=snapshot.apiUsage||[];state.auditEvents=snapshot.auditEvents||[];state.liveJobs=snapshot.jobs;state.liveLogs=snapshot.logs;state.liveDeadLetters=snapshot.deadLetters;
 const previous=state.lastSample;state.lastSample=snapshot.sampledAt;
 if(previous!==snapshot.sampledAt){state.history.push({time:new Date(snapshot.sampledAt),queue:snapshot.metrics.queueDepth});if(state.history.length>90)state.history.shift();}
 $('liveBadge').textContent='Live · '+new Date(snapshot.sampledAt).toLocaleTimeString();$('connectionLabel').textContent='Live feed · every 2 seconds';
 if(snapshot.settings&&state.config?.revision!==snapshot.settings.revision&&!changes().length){
  state.config=snapshot.settings;state.draft=clone(snapshot.settings.settings);
  if(!['overview','jobs','watermarks','security'].includes(state.route))renderSettings();
 }
 if(state.route==='overview')renderOverview();
 if(state.route==='jobs'){renderRecentJobs(snapshot.jobs);renderLiveLogs();renderDeadLetters(snapshot.deadLetters);if(state.selectedJobId)updateSelectedJob();}
}
window.addEventListener('online',connectLive);
document.addEventListener('visibilitychange',()=>{if(!document.hidden&&(!state.lastSample||Date.now()-new Date(state.lastSample).getTime()>8000))connectLive();});
function stat(label,value){return `<div class="stat-row"><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></div>`;}
function swarmPanel(){
 const s=state.swarm,w=state.watchFolder;if(!s)return '';
 return `<article class="panel swarm-panel"><div class="panel-head"><div><h2>Swarm activity</h2><p>Independent conversions · shared capacity on this server</p></div><a href="#Performance" class="button">Performance profiles ↗</a></div><div class="panel-body"><div class="swarm-summary">${stat('Processing · selected source',s.processing)}${stat('Available workers · server',s.available+' / '+s.capacity)}${stat('Estimated reserved memory',bytes(s.reservedBytes)+' / '+bytes(s.memoryBudgetBytes))}${stat('Peak parallel workers · session',s.peakActive)}</div><div class="swarm-lanes">${s.lanes.map(l=>`<div class="swarm-lane"><strong>${escapeHtml(l.name)}</strong><span>${l.processing} processing · ${l.waiting} awaiting admission</span><div class="meter"><span style="width:${Math.min(100,l.processing/l.capacity*100)}%"></span></div><small>${l.capacity} engine slots</small></div>`).join('')}</div><div class="table-wrap"><table><thead><tr><th>Worker</th><th>Document</th><th>Source</th><th>Lane</th><th>Elapsed</th></tr></thead><tbody>${s.workers.length?s.workers.map(worker=>`<tr><td>${escapeHtml(worker.id.slice(0,8))}</td><td>${escapeHtml(worker.fileName)}</td><td>${escapeHtml(worker.source)}</td><td>${escapeHtml(worker.lane)}</td><td>${ms(worker.elapsedMs)}</td></tr>`).join(''):'<tr><td colspan="5" class="empty">Workers are available. Add documents to the watch Input folder or submit conversion jobs.</td></tr>'}</tbody></table></div><div class="coverage-note">Watch folder: ${w?.input??'—'} in Input · ${w?.processing??'—'} claimed · ${w?.errors??'—'} in Errors. API dispatch buffer: ${state.metrics.dispatchBufferDepth??0} / ${state.metrics.queueCapacity}; ${state.metrics.durableOutstanding??0} durable API jobs outstanding. ${w&&!w.statusAvailable?'Folder counts are unavailable; check access permissions.':''}</div></div></article>`;
}

async function renderPerformanceProfiles(){
 const host=document.createElement('article');host.className='panel settings-group';host.id='serviceProfileControls';
 host.innerHTML='<div class="panel-head"><div><h2>Service performance profiles</h2><p>Apply a preset to your draft, then review and save. Restart the service to activate it.</p></div></div><div class="panel-body"><div class="performance-presets"><button class="button" data-preset="conservative">Conservative · one at a time</button><button class="button" data-preset="balanced">Balanced</button><button class="button primary" data-preset="highThroughput">High Throughput · parallel processing</button></div><p>Presets adjust worker counts, engine concurrency, and memory admission for this machine. Document quality and file limits remain your chosen values.</p><div class="profile-toolbar"><label>Saved service profiles<select id="serviceProfileSelect"><option value="">Choose a profile…</option></select></label><button class="button" id="loadServiceProfile">Load into draft</button><button class="button" id="deleteServiceProfile">Delete</button></div><div class="profile-toolbar"><label>Profile name<input id="serviceProfileName" maxlength="64" placeholder="batch-processing"></label><button class="button" id="saveServiceProfile">Save current draft as profile</button></div><p id="serviceProfileStatus" role="status"></p></div>';
 $('settingsContent').prepend(host);
 const [presets,names]=await Promise.all([api('/v1/admin/performance-presets'),api('/v1/admin/service-profiles')]);
 if(!host.isConnected)return;
 $('serviceProfileSelect').innerHTML='<option value="">Choose a profile…</option>'+names.map(n=>'<option>'+escapeHtml(n)+'</option>').join('');
 for(const button of host.querySelectorAll('[data-preset]'))button.onclick=()=>{
  const p=presets[button.dataset.preset];
  state.draft.Performance=Object.fromEntries(Object.entries(p.performance).map(([k,v])=>[k[0].toUpperCase()+k.slice(1),v]));
  state.draft.Workers.Count=p.workerCount;state.draft.WatchFolder.MaximumConcurrentConversions=p.watchConcurrency;
  state.draft.WatchFolder.MaximumHeavyConversions=p.officeConcurrency;state.draft.WatchFolder.MaximumMarkupConversions=p.officeConcurrency;state.draft.WatchFolder.ResourceBudgetBytes=state.draft.Performance.MemoryBudgetBytes;
  state.draft.Concurrency={Office:p.officeConcurrency,Image:p.imageConcurrency,Pdf:p.pdfConcurrency,Text:p.textConcurrency};
  updateDirty();renderSettings();notice('Performance preset added to your draft. Review changes and save to activate after restart.');
 };
 $('loadServiceProfile').onclick=async()=>{try{const name=$('serviceProfileSelect').value;if(!name)return;if(changes().length&&!confirm('Replace the current draft with this saved profile?'))return;const profile=await api('/v1/admin/service-profiles/'+encodeURIComponent(name));state.draft={...state.draft,...profile};updateDirty();renderSettings();notice('Profile loaded into draft. Review all changes before saving.');}catch(error){notice(error.message,true);}};
 $('saveServiceProfile').onclick=async()=>{try{const name=$('serviceProfileName').value.trim();if(!/^[A-Za-z0-9_-]{1,64}$/.test(name))throw new Error('Use 1–64 letters, numbers, hyphens or underscores for the profile name.');if(names.includes(name)&&!confirm('Replace saved profile '+name+'?'))return;await api('/v1/admin/service-profiles/'+encodeURIComponent(name),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(state.draft)});renderSettings();notice('Saved service profile '+name+'. Running settings are unchanged.');}catch(error){notice(error.message,true);}};
 $('deleteServiceProfile').onclick=async()=>{try{const name=$('serviceProfileSelect').value;if(!name||!confirm('Delete saved profile '+name+'?'))return;await api('/v1/admin/service-profiles/'+encodeURIComponent(name),{method:'DELETE'});renderSettings();notice('Deleted saved service profile '+name+'.');}catch(error){notice(error.message,true);}};
}
function metric(label,value,caption){return `<div class="metric"><div class="metric-label">${label}<span>↗</span></div><div class="metric-value">${value}</div><small>${caption}</small></div>`;}
function ms(value){return value==null?'—':value<1000?Math.round(value)+' ms':(value/1000).toLocaleString(undefined,{maximumFractionDigits:2})+' s';}
function renderResourcePanel(){
 const panel=document.createElement('article');panel.className='panel resource-panel';
 const r=state.resources;
 panel.innerHTML='<div class="panel-head"><div><h2>Service resources</h2><p>Live process usage · platform-reported values</p></div><span class="pill">'+(r?'Live':'Unavailable')+'</span></div><div class="panel-body">'+
  stat('API working set',r?bytes(r.apiWorkingSetBytes):'Unavailable')+
  stat('LibreOffice working set',r?bytes(r.libreOfficeWorkingSetBytes):'Unavailable')+
  stat('LibreOffice processes',r?.libreOfficeProcessCount??'Unavailable')+
  stat('API threads',r?.apiThreadCount??'Unavailable')+
  stat('API CPU time',r?duration(r.apiCpuMilliseconds/1000):'Unavailable')+
  '<small>These are process measurements, not a hardware temperature reading.</small></div>';
 $('overview').append(panel);
}
function usagePanel(){
 const rows=state.apiUsage||[];
 return `<article class="panel usage-panel"><div class="panel-head"><div><h2>API key activity</h2><p>Conversion usage by integration key · secrets are never shown</p></div><span class="pill">${rows.length} key${rows.length===1?'':'s'}</span></div><div class="table-wrap"><table><thead><tr><th>Key</th><th>Requests</th><th>Success</th><th>Failed</th><th>Received</th><th>Produced</th><th>Rate-limit rejects</th></tr></thead><tbody>${rows.length?rows.map(row=>`<tr><td>${escapeHtml(row.apiKeyId?.slice(0,8)||'—')}…</td><td>${row.totalRequests}</td><td>${row.successfulConversions}</td><td>${row.failedConversions}</td><td>${bytes(row.bytesReceived)}</td><td>${bytes(row.bytesProduced)}</td><td>${row.rateLimitRejections}</td></tr>`).join(''):'<tr><td colspan="7" class="empty">No API-key activity recorded yet.</td></tr>'}</tbody></table></div></article>`;
}
function auditPanel(){
 const rows=state.auditEvents||[];
 return `<article class="panel audit-panel"><div class="panel-head"><div><h2>Audit events</h2><p>Recent authenticated changes and rejected requests</p></div><span class="pill">Live</span></div><div class="table-wrap"><table><thead><tr><th>Time</th><th>Action</th><th>Identity</th><th>Status</th></tr></thead><tbody>${rows.length?rows.slice(0,12).map(row=>`<tr><td>${escapeHtml(new Date(row.timestamp).toLocaleTimeString())}</td><td>${escapeHtml(row.method+' '+row.path)}</td><td>${escapeHtml(row.identity)}</td><td><span class="pill ${row.statusCode>=400?'warn':''}">${row.statusCode}</span></td></tr>`).join(''):'<tr><td colspan="4" class="empty">No audit events yet.</td></tr>'}</tbody></table></div></article>`;
}
function renderOverview(){
 const a=state.analytics,s=state.status,m=state.metrics;
 if(!a||!s){$('overview').innerHTML='<div class="panel empty"><strong>Connecting to your conversion service</strong>Live measurements will appear here automatically.</div>';return;}
 const successRate=a.attempts?Math.round(a.succeeded/a.attempts*100):null;
 const timeline=a.timeline||[],peak=Math.max(1,...timeline.map(p=>p.succeeded+p.failed));
 const historyPeak=Math.max(1,...state.history.map(p=>p.queue));
 const points=state.history.map((p,i)=>`${i/Math.max(1,state.history.length-1)*600},${100-p.queue/historyPeak*80}`).join(' ');
 $('overview').innerHTML=`<div class="overview-filter"><label>Analytics source <select id="analyticsSource"><option value="">All sources</option><option value="WatchFolder">Watch folder</option><option value="Synchronous">Synchronous API</option><option value="Queued">Queued jobs</option></select></label><small>Live metrics update for the selected source.</small></div><div class="overview-strip">
  ${metric('Conversions',a.attempts.toLocaleString(),'Execution attempts · last 24h')}
  ${metric('Successful',a.succeeded.toLocaleString(),successRate===null?'No observations yet':successRate+'% of execution attempts')}
  ${metric('Average / document',ms(a.documentTiming.averageMs),a.documentTiming.count+' single-document successes')}
  ${metric('Fastest document',ms(a.documentTiming.minMs),'Successful execution time')}
  ${metric('Slowest document',ms(a.documentTiming.maxMs),'Successful execution time')}
  ${metric('Running now',a.running,a.failed+' failed · '+a.cancelled+' cancelled')}
 </div><div class="analytics-grid">
 <article class="panel activity-panel"><div class="panel-head"><div><h2>Conversion activity</h2><p>All entry points, one view.</p></div><span class="pill">Last 24 hours · live</span></div><div class="panel-body">
 <div class="activity-summary"><strong>${a.succeeded.toLocaleString()}</strong><span>completed executions</span><span class="pill coral">${a.failed} failed</span></div>
 <div class="bar-chart" role="img" aria-label="Conversions by recorded hour">${timeline.length?timeline.map(p=>`<div class="bar-column" title="${escapeHtml(new Date(p.utc).toLocaleString())}: ${p.succeeded} succeeded, ${p.failed} failed"><span class="bar-count">${p.succeeded+p.failed}</span><div class="bar-track"><div class="bar-value" style="height:${(p.succeeded+p.failed)/peak*100}%"><span style="height:${p.failed/Math.max(1,p.succeeded+p.failed)*100}%"></span></div></div><small>${new Date(p.utc).toLocaleTimeString([], {hour:'2-digit'})}</small></div>`).join(''):'<div class="empty"><strong>Ready for your first conversion</strong>Charts populate from recorded execution times.</div>'}</div>
 <div class="legend">● Successful <span class="coral-text">● Failed</span> · Hours with recorded activity</div>
 </div></article>
 <article class="panel health-panel"><div class="panel-head"><div><h2>Instance health</h2><small>Server-pushed every 2 seconds</small></div><span class="health-dot" style="background:${s.readiness==='Healthy'?'#75cfaa':'#ff8269'}"></span></div><div class="panel-body"><div class="health-value">${escapeHtml(s.readiness)}</div>
 ${stat('Uptime',duration(s.uptimeSeconds))}${stat('API memory',bytes(s.processMemoryBytes))}${stat('Processors',s.logicalProcessors)}${stat('Waiting documents',m.queueDepth)+stat('Processing',m.processing??0)}${stat('Dead-letter jobs',m.deadLettered)}
 <small>Updated ${new Date(state.lastSample).toLocaleTimeString()}</small></div></article>
 <article class="panel format-panel"><div class="panel-head"><div><h2>Time & size by document format</h2><p>Successful conversions only · intake versus generated PDF output.</p></div><span class="pill">${a.formats.length} formats observed</span></div><div class="table-wrap"><table><thead><tr><th>Format</th><th>Attempts</th><th>Successful</th><th>Intake</th><th>PDF output</th><th>Saved</th><th>Savings</th><th>Average</th><th>Min</th><th>Max</th></tr></thead><tbody>${a.formats.length?a.formats.map(f=>`<tr><td><span class="format-badge">${escapeHtml(f.format.toUpperCase()||'UNKNOWN')}</span></td><td>${f.attempts}</td><td>${f.timing.count}</td><td>${bytes(f.inputBytes)}</td><td>${bytes(f.outputBytes)}</td><td>${bytes(f.bytesSaved)}</td><td>${savingsPercent(f.savingsPercent)}</td><td>${ms(f.timing.averageMs)}</td><td>${ms(f.timing.minMs)}</td><td>${ms(f.timing.maxMs)}</td></tr>`).join(''):'<tr><td colspan="10" class="empty">No conversion observations yet.</td></tr>'}</tbody></table></div></article>
 <article class="panel intake-panel"><div class="panel-head"><div><h2>Intake & output</h2><p>Where conversion work comes from.</p></div></div><div class="panel-body">${a.sources.map(source=>stat(source.source,source.attempts+' attempts · '+source.succeeded+' succeeded')).join('')||'<p>No sources observed yet.</p>'}${stat('Input volume',bytes(a.inputBytes))}${stat('Successful output',bytes(a.outputBytes))}${stat('Bytes saved',bytes(a.bytesSaved))}${stat('Savings',savingsPercent(a.savingsPercent))}${stat('Average queue wait',ms(a.queueWaitTiming?.averageMs))}${stat('Retry rate',a.retryRate==null?'—':a.retryRate.toLocaleString(undefined,{maximumFractionDigits:1})+'%')}${stat('Interrupted by restart',a.interrupted)}</div></article>
 <article class="panel queue-panel"><div class="panel-head"><div><h2>Queue pressure</h2><p>Observed during this session · ${m.queueDepth} waiting</p></div><a class="button" href="#jobs">Open jobs ↗</a></div><div class="panel-body"><svg class="chart compact-chart" viewBox="0 0 600 115" preserveAspectRatio="none" aria-label="Live queue depth" role="img"><path d="M0 20H600M0 60H600M0 100H600" stroke="#eceef1"/><polygon points="0,100 ${points} 600,100" fill="#efedf8"/><polyline points="${points}" fill="none" stroke="#8983b8" stroke-width="2"/></svg></div></article>
 ${usagePanel()}${auditPanel()}${swarmPanel()}</div><div class="coverage-note">Timing coverage: executions recorded since this console instrumentation was installed, within the last 24 hours. Retries count as separate attempts. Per-document timing excludes multi-input operations; per-format timing includes same-format batches, and mixed batches appear as MIXED. ${a.limited?'The newest 100,000 observations are shown.':''} ${a.recordingError?escapeHtml(a.recordingError):''}</div>`;
 renderResourcePanel();
 const selector=$('analyticsSource');selector.value=state.analyticsSource;selector.onchange=()=>{state.analyticsSource=selector.value;state.history=[];connectLive();};
}

const descriptions={
 Profile:'Name of this service performance configuration. Use a preset below to apply coordinated settings, or save your own profile.',ExecutionMode:'Production removes optional diagnostics for the normal product path. Development keeps verbose logs, resource sampling, and detailed stage timings for fidelity investigations.',MaximumParallelWorkers:'Maximum isolated conversions across all API and watch-folder sources on this server. CPU and memory capacity determine useful parallelism.',MemoryBudgetBytes:'Shared estimated memory budget for admitting conversion workers. Actual memory also depends on each document; per-worker resource limits remain active.',MaximumPending:'Maximum requests awaiting shared worker admission in memory. Watch-folder input and durable API jobs remain on disk while awaiting intake.',AgingSeconds:'After this waiting time, the oldest request reserves admission ahead of new short jobs so large documents can progress.',
 Urls:'Listening addresses, separated by semicolons. Leave blank to use the server’s deployment binding. Changing this may change how you reach the console.',ShutdownTimeoutSeconds:'How long shutdown waits for hosted services to stop gracefully, in seconds.',
 DataRoot:'Base directory for service-owned data. Blank uses the platform default.',StateRoot:'Directory for the durable job database. Blank uses the state directory inside the data root.',JobRoot:'Directory for isolated conversion workspaces. Blank uses the jobs directory inside the data root.',WatchRoot:'Root directory for watched input, processing, output, and error folders.',
 LibreOfficePath:'Path to the LibreOffice executable. Blank uses automatic platform defaults.',ConversionWorkerPath:'Path to the isolated conversion worker. Blank enables discovery beside the API or in build output.',PdfToTextPath:'Executable used by PDF text inspection tools.',PdfToPpmPath:'Executable used by PDF page rendering tools.',LibreOfficeAdditionalArguments:'Additional engine startup arguments. These are administrator-controlled process arguments; enter only arguments supported by your deployment.',
 MaximumRequestBytes:'Maximum combined HTTP request size. A multipart request also needs room for its form overhead.',MaximumFileBytes:'Maximum size of one uploaded input. Must not exceed the request limit.',MaximumFiles:'Maximum number of files accepted in a single conversion request.',
 MaximumOutputBytes:'Reject generated PDFs larger than this limit.',MaximumImagePixels:'Maximum decoded pixels accepted for an image. This bounds the size after decompression.',MaximumImageWidth:'Maximum decoded image width in pixels.',MaximumImageHeight:'Maximum decoded image height in pixels.',MaximumImageFrames:'Maximum frames accepted from a multi-frame image.',ImageMemoryBytes:'Memory budget for the image processor.',ImageDiskBytes:'Disk budget for the image processor’s pixel cache.',ImageThreadCount:'Image processing threads. Zero leaves selection to the engine.',MinimumFreeDiskBytes:'Minimum free disk required before conversion work proceeds.',CopyBufferBytes:'Buffer used while copying streams. Larger buffers increase per-copy memory.',
 Count:'Number of asynchronous workers consuming the durable queue.',MaximumAttempts:'Total attempts before a job exhausts retries and moves to the dead-letter queue.',QueueCapacity:'Maximum in-memory queue slots. Durable job state remains separate.',TimeoutSeconds:'Maximum time allowed for an isolated worker process, in seconds.',MaximumMemoryBytes:'Memory ceiling for each isolated worker process.',JobLeaseSeconds:'Time a worker owns a job claim. Must be at least 30 seconds.',DispatcherPollMilliseconds:'Interval between checks for pending jobs. Minimum 25 milliseconds.',RetryBaseDelayMilliseconds:'Base delay used for retry backoff. Minimum 25 milliseconds.',
 Office:'Maximum Office conversions allowed at once.',Image:'Maximum image conversions allowed at once.',Pdf:'Maximum PDF operations allowed at once.',Text:'Maximum text conversions allowed at once.',
 Root:'Legacy reference field. Use Paths.WatchRoot for the watch location; this field is not applied by the current loader.',MaximumConcurrentConversions:'Maximum conversions started concurrently by the watch folder.',MaximumHeavyConversions:'Maximum heavy conversions admitted together.',MaximumMarkupConversions:'Maximum HTML and RTF conversions admitted together.',ResourceBudgetBytes:'Total estimated resource budget used by watch-folder admission.',OfficeReservationBytes:'Estimated reservation for each Office conversion.',ImageReservationBytes:'Estimated reservation for each image conversion.',MarkupReservationBytes:'Estimated reservation for each markup conversion.',LargeFileReservationBytes:'Additional reservation used for large inputs.',MaximumRetries:'Retries allowed for a failed watch-folder conversion.',ScanIntervalSeconds:'Delay between watch-folder scans.',FileStabilityDelaySeconds:'How long the input must remain stable before it is processed.',
 Enabled:'Enable scheduled retention cleanup. Individual deletion switches below must also be enabled.',SweepIntervalSeconds:'Delay between retention cleanup sweeps.',MaximumAgeDays:'Maximum retained age. Zero disables the age limit.',MaximumOriginalsBytes:'Retention size limit for originals. Zero disables this size limit.',MaximumErrorsBytes:'Retention size limit for failed inputs. Zero disables this size limit.',MaximumLogsBytes:'Retention size limit for logs. Zero disables this size limit.',MinimumFileAgeSeconds:'Protect files newer than this age from retention cleanup.',DeleteOutputFiles:'Allow retention to permanently remove eligible output PDFs.',DeleteOriginalFiles:'Allow retention to permanently remove eligible original documents.',DeleteErrorFiles:'Allow retention to permanently remove eligible failed inputs.',DeleteLogFiles:'Allow retention to permanently remove eligible logs.',
 DefaultProfile:'PDF output profile used when the caller does not choose one.',DefaultOperation:'Conversion operation used for jobs that do not specify an operation.',PdfVersion:'PDF version encoded as an integer: 14 means PDF 1.4, 17 means PDF 1.7.',PreserveJpegData:'Keep original JPEG image data when safe, avoiding recompression.',DownsampleImages:'Reduce oversized images to the maximum image DPI.',MaximumImageDpi:'Target image resolution when downsampling is enabled. Zero disables the DPI limit when downsampling is off.',JpegQuality:'JPEG quality from 1 to 100. Higher values preserve detail and usually increase file size.',CompressContentStreams:'Compress PDF drawing and text streams.',BestCompression:'Prefer stronger compression at the cost of processing time.',PreserveSourceMetadata:'Preserve available source metadata where supported.',FontPolicy:'Current font policy. Changing a label does not add a new font implementation.',EncryptionPolicy:'Policy identifier for the current unencrypted output behavior. Output encryption is not implemented.',LargeMarkupPolicy:'Visual-layout conversion is the supported markup policy.',StructuralValidation:'Validate PDF structure before accepting the output.'
};
const choices={ExecutionMode:['Production','Development'],DefaultProfile:['archive','compact','compatibility'],DefaultOperation:['ConvertFile','ConvertFiles','ConvertAndMerge','CreateTextPdf'],PdfVersion:[14,15,16,17,20],LargeMarkupPolicy:['visual-layout']};
function description(path){const key=path.split('.').at(-1);if(path.includes('Retention.MaximumOutputBytes'))return 'Retention size limit for output files. Zero disables this size limit.';return descriptions[key]||(key.endsWith('Seconds')?'Maximum duration in seconds for '+human(key.replace('Seconds','')).toLowerCase()+'.':'Configure '+human(key).toLowerCase()+' for this service.');}
function inputHint(path,value){if(path.endsWith('Bytes'))return value===0&&path.includes('Retention')?'Limit disabled':bytes(value);if(path.endsWith('Milliseconds'))return value+' ms';if(path.endsWith('Seconds'))return value+' seconds';if(value===null)return 'Automatic / deployment default';return 'Saved configuration · restart to apply';}
function renderSettings(){if(!state.draft){$('settingsContent').innerHTML='<div class="panel empty"><strong>Configuration unavailable</strong>Connect to the service to load settings.</div>';return;}
 const rows=flatten(state.draft[state.route],state.route+'.');$('settingCount').textContent=rows.length+' settings';
 const groups=new Map();for(const row of rows){const key=row.path.split('.').slice(0,-1).join('.');if(!groups.has(key))groups.set(key,[]);groups.get(key).push(row);}
 $('settingsContent').innerHTML=[...groups].map(([group,items])=>`<article class="panel settings-group"><div class="panel-head"><div><h2>${escapeHtml(group.split('.').map(human).join(' / '))}</h2><small>Changes are validated before saving.</small></div><span class="pill">${items.length} controls</span></div>${items.map(({path,value})=>{
 const key=path.split('.').at(-1),id='setting-'+path,locked=!state.config.writable||path==='WatchFolder.Root'||['FontPolicy','EncryptionPolicy','LargeMarkupPolicy'].includes(key);let input;
 if(typeof value==='boolean')input=`<input id="${id}" data-path="${path}" type="checkbox" ${value?'checked':''} ${locked?'disabled':''}>`;
 else if(choices[key])input=`<select id="${id}" data-path="${path}" ${locked?'disabled':''}>${[...new Set([...choices[key],value])].map(v=>`<option value="${escapeHtml(v)}" ${v===value?'selected':''}>${escapeHtml(key==='PdfVersion'?'PDF '+(v/10).toFixed(1):v)}</option>`).join('')}</select>`;
 else input=`<input id="${id}" data-path="${path}" type="${typeof value==='number'?'number':'text'}" ${typeof value==='number'?'step="1" min="0"':''} ${locked?'disabled':''} value="${escapeHtml(value)}" placeholder="Automatic / default">`;
 return `<div class="setting-row"><div><label for="${id}">${escapeHtml(human(key))}</label><p>${escapeHtml(description(path))}</p><code>${escapeHtml(path)}</code></div><div class="setting-input">${input}<small data-hint="${path}">${escapeHtml(locked?'Read only — current implementation':inputHint(path,value))}</small></div></div>`;
 }).join('')}</article>`).join('')||'<div class="empty">No matching settings. Try another search.</div>';
 for(const input of document.querySelectorAll('[data-path]'))input.addEventListener('input',()=>{const path=input.dataset.path,original=getPath(state.config.settings,path);const value=input.type==='checkbox'?input.checked:typeof original==='number'?(input.value===''?null:Number(input.value)):(input.value||null);setPath(state.draft,path,value);input.nextElementSibling.textContent=inputHint(path,value);updateDirty();renderSettingPreview();});
 renderSettingPreview();
 if(state.route==='Performance')renderPerformanceProfiles().catch(error=>notice(error.message,true));
}

function renderSettingPreview(){
 let container=$('settingPreview');if(!container){container=document.createElement('article');container.id='settingPreview';container.className='panel settings-group';$('settingsContent').before(container);}
 const draft=state.draft,route=state.route;if(!draft)return;
 let content='',caption='Illustrates the values you are editing; running service settings are unchanged.';
 if(route==='Performance'){const p=draft.Performance;content=stat('Execution mode',p.ExecutionMode)+stat('Profile',p.Profile)+stat('Shared worker ceiling',p.MaximumParallelWorkers)+stat('Estimated admission budget',bytes(p.MemoryBudgetBytes))+stat('Fairness threshold',p.AgingSeconds+' seconds')+(p.ExecutionMode==='Development'?'<p>Development diagnostics remain enabled: resource sampling, process-tree observations, and detailed conversion stages.</p>':'<p>Production keeps required validation, timeout, cancellation, security, and atomic publishing while omitting optional diagnostic work.</p>');}
 const bar=(label,value,max,display)=>`<div class="stat-row"><span>${escapeHtml(label)}</span><strong>${escapeHtml(display??value)}</strong></div><div class="meter"><span style="width:${Math.max(0,Math.min(100,(Number(value)||0)/Math.max(1,max)*100))}%"></span></div>`;
 if(route==='Workers')content=stat('Parallel workers',draft.Workers.Count)+stat('Per-worker memory ceiling',bytes(draft.Workers.MaximumMemoryBytes))+stat('Combined configured ceilings',bytes(draft.Workers.Count*draft.Workers.MaximumMemoryBytes))+bar('Queue capacity',draft.Workers.QueueCapacity,Math.max(256,draft.Workers.QueueCapacity),draft.Workers.QueueCapacity+' slots')+`<p>${draft.Workers.MaximumAttempts} total attempts · ${draft.Workers.TimeoutSeconds}s worker timeout. Memory ceilings are limits, not predicted consumption.</p>`;
 else if(route==='Concurrency'){const max=Math.max(1,...Object.values(draft.Concurrency));content=Object.entries(draft.Concurrency).map(([k,v])=>bar(human(k)+' slots',v,max)).join('');}
 else if(route==='Api')content=bar('Single file limit',draft.Api.MaximumFileBytes,draft.Api.MaximumRequestBytes,bytes(draft.Api.MaximumFileBytes))+stat('Whole request limit',bytes(draft.Api.MaximumRequestBytes))+stat('Maximum file count',draft.Api.MaximumFiles)+`<p>${draft.Api.MaximumFileBytes>draft.Api.MaximumRequestBytes?'The single-file limit exceeds the request limit. Reduce it before saving.':'The request limit includes all files and multipart form overhead.'}</p>`;
 else if(route==='Resources')content=stat('Image memory cache',bytes(draft.Resources.ImageMemoryBytes))+stat('Image disk cache',bytes(draft.Resources.ImageDiskBytes))+stat('Free disk reserve',bytes(draft.Resources.MinimumFreeDiskBytes))+stat('Output ceiling',bytes(draft.Resources.MaximumOutputBytes))+stat('Conversion timeout',draft.Resources.ConversionTimeoutSeconds+' seconds');
 else if(route==='WatchFolder'){const w=draft.WatchFolder;content=stat('Intake cadence',w.ScanIntervalSeconds+'s scan + '+w.FileStabilityDelaySeconds+'s stability check')+stat('Concurrent conversions',w.MaximumConcurrentConversions)+stat('Admission budget',bytes(w.ResourceBudgetBytes))+stat('Retention',w.Retention.Enabled?'Enabled':'Disabled')+`<p>Input → stability check → processing → output or errors. Cleanup ${w.Retention.Enabled?'runs every '+w.Retention.SweepIntervalSeconds+' seconds':'is disabled'}. Deletion switches are opt-in.</p>`;}
 else if(route==='Conversion'){content=Object.entries(draft.Conversion.Profiles).map(([name,p])=>stat(name+' · PDF '+(p.PdfVersion/10).toFixed(1),p.DownsampleImages?p.MaximumImageDpi+' DPI · JPEG '+p.JpegQuality:'Original resolution · JPEG '+p.JpegQuality)).join('')+stat('Default profile',draft.Conversion.DefaultProfile)+`<p>Compression and quality settings affect generated files. File size and visual fidelity depend on the source document.</p>`;}
 else if(route==='Paths')content=stat('Data root',draft.Paths.DataRoot||'Platform default')+stat('Jobs',draft.Paths.JobRoot||'<data root>/jobs')+stat('State',draft.Paths.StateRoot||'<data root>/state')+stat('Watch folder',draft.Paths.WatchRoot||'Platform watch-folder default')+'<p>Paths are on the server. A browser file picker cannot choose a server directory.</p>';
 else if(route==='Service')content=stat('Configured binding',draft.Service.Urls||'Deployment / framework default')+stat('Graceful shutdown',draft.Service.ShutdownTimeoutSeconds+' seconds')+'<p>Changing a listening address requires a restart and may change the URL used to reach this console.</p>';
 const expanded=container.querySelector('details')?.open;
 container.innerHTML=`<details class="config-preview" ${expanded?'open':''}><summary><span>Configuration preview <small> · updates as you edit</small></span></summary><div class="panel-body"><p>${caption}</p>${content}</div></details>`;
}
function renderSecurity(){const s=state.status;$('security').innerHTML=`<div class="security-grid"><article class="panel"><div class="panel-head"><h2>Access boundary</h2><span class="pill">Private server</span></div><div class="panel-body">${stat('Bearer authentication',s?s.authenticationEnabled?'Enabled':'Not configured':'Unavailable')}${stat('Console transport',location.protocol==='https:'?'HTTPS':'HTTP')}${stat('Token storage','Memory in this tab only')}<p>Set INACTIVEPDF_API_TOKEN through the service environment to enable authentication. Tokens are never returned through the settings API. Remote administration requires authentication.</p></div></article><article class="panel"><div class="panel-head"><h2>Running environment</h2></div><div class="panel-body">${stat('Platform',s?.platform||'Unavailable')}${stat('Runtime',s?.runtime||'Unavailable')}${stat('API memory',bytes(s?.processMemoryBytes))}${stat('Uptime',duration(s?.uptimeSeconds))}</div></article><article class="panel"><div class="panel-head"><h2>Readiness checks</h2></div><div class="panel-body">${s?.checks.map(c=>stat(c.name,c.status)+(c.description?`<p>${escapeHtml(c.description)}</p>`:'')).join('')||'<p>No readiness sample available.</p>'}</div></article><article class="panel"><div class="panel-head"><h2>Configuration precedence</h2></div><div class="panel-body"><p>Environment variables override file settings. These override names were present before startup settings were applied; values are not exposed.</p><div class="details">${state.config?.environmentOverrides.map(escapeHtml).join('<br>')||'No non-secret environment overrides recorded.'}</div><p>Saved changes require a service restart. Command-line binding can also override the configured URL.</p></div></article><article class="panel api-key-panel"><div class="panel-head"><div><h2>API keys & limits</h2><p>Each integration key has its own request, concurrency, queue, file, output, and daily-input limits.</p></div><button class="button" id="refreshApiKeys">Refresh</button></div><div id="apiKeyList" class="api-key-list"><div class="empty">Loading API keys…</div></div></article></div>`;loadApiKeys();}
async function loadApiKeys(){try{state.apiKeys=await api('/v1/admin/api-keys');renderApiKeys();}catch(error){if($('apiKeyList'))$('apiKeyList').innerHTML='<div class="empty"><strong>API-key limits unavailable</strong>'+escapeHtml(error.message)+'</div>';}}
function renderApiKeys(){const host=$('apiKeyList');if(!host)return;if(!state.apiKeys.length){host.innerHTML='<div class="empty"><strong>No integration keys</strong>Create a key through POST /v1/admin/api-keys, then configure its limits here.</div>';return;}const fields=[['requestsPerMinute','Requests / minute'],['concurrentConversions','Concurrent conversions'],['maximumQueuedJobs','Maximum queued jobs'],['maximumFilesPerRequest','Maximum files / request'],['maximumRequestBytes','Maximum request bytes'],['maximumFileBytes','Maximum file bytes'],['maximumOutputBytes','Maximum output bytes'],['dailyInputBytes','Daily input bytes · 0 unlimited'],['jobRetentionDays','Job retention days · 0 service default']];host.innerHTML=state.apiKeys.map(key=>{const l=key.limits||{},id='key-'+key.id;return `<article class="api-key-card" data-key-card="${escapeHtml(id)}"><div class="api-key-heading"><div><strong>${escapeHtml(key.name)}</strong><small>${escapeHtml(key.prefix)} · ${escapeHtml((key.scopes||[]).join(' · '))}</small></div><span class="pill">${key.revokedAt?'Revoked':'Active'}</span></div><div class="api-key-limits">${fields.map(([name,label])=>`<label>${label}<input type="number" min="0" step="1" data-limit="${name}" value="${escapeHtml(l[name]??0)}" ${key.revokedAt?'disabled':''}></label>`).join('')}</div><div class="api-key-actions"><small>Changes apply to new admissions. Existing conversions are not interrupted.</small><button class="button primary" data-save-key="${escapeHtml(key.id)}" ${key.revokedAt?'disabled':''}>Save limits</button></div></article>`;}).join('');if($('refreshApiKeys'))$('refreshApiKeys').onclick=loadApiKeys;for(const button of host.querySelectorAll('[data-save-key]'))button.onclick=async()=>{const card=button.closest('[data-key-card]');const limits={};for(const input of card.querySelectorAll('[data-limit]'))limits[input.dataset.limit]=Number(input.value);try{button.disabled=true;await api('/v1/admin/api-keys/'+encodeURIComponent(button.dataset.saveKey)+'/limits',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(limits)});notice('API-key limits saved. New admissions use the updated values.');await loadApiKeys();}catch(error){notice(error.message,true);}finally{if(button.isConnected)button.disabled=false;}};}
async function renderJobs(){
 $('jobs').innerHTML='<article class="panel"><div class="panel-head"><div><h2>Recent conversions</h2><p>Latest 50 durable jobs · automatically updated.</p></div><span class="pill">Live</span></div><div id="recentJobs" class="empty">Loading recent jobs…</div></article><article class="panel"><div class="panel-head"><div><h2>Conversion details</h2><p>Select a recent job or find one using universal search.</p></div></div><div id="jobResult" class="empty">No job selected.</div></article><article class="panel"><div class="panel-head"><div><h2>Dead-letter queue</h2><p>Jobs that exhausted their retry allowance.</p></div><span class="pill">Live</span></div><div id="deadLetters" class="empty">Loading…</div></article>';
 installLogPanel();await Promise.all([loadDeadLetters(),loadRecentJobs()]);if(state.selectedJobId)await updateSelectedJob();
}
async function loadRecentJobs(){try{renderRecentJobs(await api('/v1/admin/jobs?limit=50'));}catch(error){notice(error.message,true);}}
function renderRecentJobs(jobs){
 if(!$('recentJobs'))return;
 $('recentJobs').className=jobs.length?'table-wrap':'empty';
 $('recentJobs').innerHTML=jobs.length?'<table><thead><tr><th>Job</th><th>Operation</th><th>Status</th><th>Attempts</th><th>Updated</th></tr></thead><tbody>'+jobs.map(j=>`<tr><td><button class="subtle job-link" data-job="${escapeHtml(j.jobId)}" title="${escapeHtml(j.jobId)}">${escapeHtml(j.jobId.slice(0,8))}…</button></td><td>${escapeHtml(j.operation)}</td><td><span class="pill ${j.state==='Failed'?'warn':''}">${escapeHtml(j.state)}</span></td><td>${j.attempts}</td><td>${escapeHtml(new Date(j.updatedAt).toLocaleTimeString())}</td></tr>`).join('')+'</tbody></table>':'<strong>No durable jobs yet</strong>Queued conversions appear here automatically.';
 for(const button of document.querySelectorAll('[data-job]'))button.onclick=()=>{state.selectedJobId=button.dataset.job;updateSelectedJob();};
}
async function updateSelectedJob(){
 if(!state.selectedJobId||!$('jobResult'))return;
 try{
  const job=await api('/v1/jobs/'+encodeURIComponent(state.selectedJobId));
  if(!$('jobResult'))return;
  $('jobResult').innerHTML='<div class="details">'+Object.entries(job).filter(([key])=>key!=='outputPath').map(([key,value])=>stat(human(key),typeof value==='object'?JSON.stringify(value):value)).join('')+'</div>';
 }catch(error){if($('jobResult'))$('jobResult').textContent=error.message;}
}
async function loadDeadLetters(){try{renderDeadLetters(await api('/v1/dead-letters?limit=50'));}catch(error){if($('deadLetters'))$('deadLetters').textContent=error.message;}}
function renderDeadLetters(records){
 if(!$('deadLetters'))return;
 $('deadLetters').className=records.length?'table-wrap':'empty';
 $('deadLetters').innerHTML=records.length?'<table><thead><tr><th>Job</th><th>Error code</th></tr></thead><tbody>'+records.map(r=>'<tr><td>'+escapeHtml(r.jobId||r.status?.jobId||'—')+'</td><td>'+escapeHtml(r.errorCode||'Unspecified failure')+'</td></tr>').join('')+'</tbody></table>':'<strong>No dead-letter jobs</strong>Exhausted retries appear here automatically.';
}
function installLogPanel(){
 if($('liveLogs'))return;
 const panel=document.createElement('article');panel.className='panel logs-panel';panel.id='logPanel';panel.innerHTML='<div class="panel-head"><div><h2>Operational logs</h2><p>Search filenames, events, errors, and job IDs with universal search.</p></div><button class="subtle" id="clearLogFilter">Show all events</button></div><div id="logCoverage" class="coverage-note"></div><div id="liveLogs" class="table-wrap"></div>';
 $('jobs').append(panel);$('clearLogFilter').onclick=()=>{state.logQuery=null;state.executionMatches=null;renderLiveLogs();};renderLiveLogs();
}
async function renderLiveLogs(){
 if(!$('liveLogs'))return;
 let logs=state.liveLogs;
 try{if(state.logQuery){const matches=await api('/v1/admin/search?q='+encodeURIComponent(state.logQuery));logs=matches.logs;state.executionMatches=matches.executions;}}catch(error){$('logCoverage').textContent=error.message;return;}
 if(!$('liveLogs'))return;
 const executions=state.executionMatches||state.analytics?.recent||[];
 const watch=logs?.rows||[];
 const combined=[...executions.map(r=>({time:r.completedAt||r.startedAt,event:r.state,file:r.fileName,source:r.source,duration:r.durationMs,error:r.errorCode,id:r.id})),...watch.map(r=>({time:r.utc,event:r.event,file:r.fileName,source:'Watch log',duration:Number(r.durationMs)||null,error:r.errorType,id:r.utc+'-'+r.fileName}))].sort((a,b)=>new Date(b.time)-new Date(a.time)).slice(0,100);
 $('logCoverage').textContent=(state.logQuery?'Search: '+state.logQuery+' · ':'')+(logs?.coverage||'Waiting for log feed.')+(logs?.unreadable?' · '+logs.unreadable+' unreadable file(s)':'')+(logs?.malformed?' · '+logs.malformed+' incomplete or malformed line(s)':'');
 $('liveLogs').innerHTML=combined.length?'<table><thead><tr><th>Time</th><th>Source</th><th>File / event</th><th>Result</th><th>Duration</th></tr></thead><tbody>'+combined.map(r=>`<tr data-log-id="${escapeHtml(r.id)}"><td>${escapeHtml(new Date(r.time).toLocaleTimeString())}</td><td>${escapeHtml(r.source)}</td><td title="${escapeHtml(r.file)}">${escapeHtml(r.file||r.event)}${r.error?'<small class="field-error">'+escapeHtml(r.error)+'</small>':''}</td><td>${escapeHtml(r.event)}</td><td>${ms(r.duration)}</td></tr>`).join('')+'</tbody></table>':'<div class="empty"><strong>No matching operational events</strong>New conversion records and structured watch-folder events appear automatically.</div>';
}

async function navigate(){const next=decodeURIComponent(location.hash.slice(1))||'overview';state.route=sections.some(x=>x[0]===next)?next:'overview';const row=sections.find(x=>x[0]===state.route);$('pageTitle').textContent=row[1];$('breadcrumb').textContent=row[1];$('pageDescription').textContent=row[2];$('eyebrow').textContent=state.route==='overview'?'YOUR CONVERSION WORKSPACE':'WORKSPACE / '+row[1].toUpperCase();
 const view=['overview','jobs','watermarks','security'].includes(state.route)?state.route:'settings';for(const element of document.querySelectorAll('.view'))element.hidden=element.id!==view;for(const a of document.querySelectorAll('[data-route]')){a.classList.toggle('active',a.dataset.route===state.route);if(a.dataset.route===state.route)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');}
 if(view==='settings')renderSettings();if(view==='overview')renderOverview();if(view==='security')renderSecurity();if(view==='jobs')await renderJobs();if(view==='watermarks')await renderWatermarks();applyPendingFocus();
}

$('accessButton').onclick=()=>$('accessDialog').showModal();
$('accessDialog').addEventListener('close',async()=>{if($('accessDialog').returnValue!=='connect')return;state.token=$('tokenInput').value;$('tokenInput').value='';notice('');try{if(!changes().length)await loadConfig();state.profiles=await api('/v1/watermark-profiles');await refresh();await navigate();}catch(error){notice(error.message,true);}});
$('refreshButton').onclick=async()=>{await refresh();try{if(state.route==='jobs')await loadDeadLetters();else if(state.route==='security')renderSecurity();else if(!['overview','watermarks','security'].includes(state.route)&&!changes().length){await loadConfig();renderSettings();}}catch(error){notice(error.message,true);}};
$('discardButton').onclick=()=>{if(!confirm('Discard unsaved configuration changes?'))return;state.draft=clone(state.config.settings);updateDirty();renderSettings();};
$('reviewButton').onclick=()=>{if(!$('settings').querySelectorAll('input:invalid').length){$('changeList').innerHTML=changes().map(x=>`<div class="change"><strong>${escapeHtml(x.path)}</strong><div><del>${escapeHtml(JSON.stringify(getPath(state.config.settings,x.path)))}</del> → <ins>${escapeHtml(JSON.stringify(x.value))}</ins></div></div>`).join('');$('reviewDialog').showModal();}else notice('Please correct invalid numeric settings before saving.',true);};
$('cancelReview').onclick=()=>$('reviewDialog').close();
$('confirmSave').onclick=async()=>{$('confirmSave').disabled=true;try{const result=await api('/v1/admin/settings',{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify({settings:state.draft,revision:state.config.revision})});state.config=result;state.draft=clone(result.settings);$('reviewDialog').close();updateDirty();notice('Configuration saved. Restart the service to apply these changes. Environment overrides retain precedence.');}catch(error){$('reviewDialog').close();notice(error.message,true);}finally{$('confirmSave').disabled=false;}};
function download(blob,name){const url=URL.createObjectURL(blob),a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),30000);}
$('exportButton').onclick=()=>download(new Blob([JSON.stringify(state.draft,null,2)],{type:'application/json'}),'InactivePDF.settings.json');
window.addEventListener('hashchange',()=>navigate().catch(error=>notice(error.message,true)));

const defaultWatermark = () => ({kind:'Text',text:'CONFIDENTIAL',imagePath:null,fontFamily:'Arial',fontSize:36,color:'#657657',opacity:.2,rotation:-35,position:'Center',offsetX:0,offsetY:0,layer:'Over',pages:'all',tile:false,width:null,height:null,header:null,footer:null,pageNumberFormat:''});
const positions=['TopLeft','TopCenter','TopRight','CenterLeft','Center','CenterRight','BottomLeft','BottomCenter','BottomRight'];
function wmField(key,label,type='text',options=null,full=false){const value=state.wm[key];let control=options?`<select data-wm="${key}" id="wm-${key}">${options.map(o=>{const [v,l]=Array.isArray(o)?o:[o,human(o)];return `<option value="${escapeHtml(v)}" ${value===v?'selected':''}>${escapeHtml(l)}</option>`;}).join('')}</select>`:`<input id="wm-${key}" data-wm="${key}" type="${type}" value="${escapeHtml(value)}" ${type==='checkbox'&&value?'checked':''} ${type==='number'?'step="any"':''}>`;return `<label class="${full?'full':''}">${label}${control}</label>`;}
async function renderWatermarks(){
 try{[state.profiles,state.assets]=await Promise.all([api('/v1/watermark-profiles'),api('/v1/watermark-assets')]);}catch(error){notice(error.message,true);}
 if(!state.wm){state.wm=defaultWatermark();state.wmSaved=clone(state.wm);state.wmName='';}
 const pageMode=['all','none','first','last','odd','even'].includes(state.wm.pages)?state.wm.pages:'custom';
 $('watermarks').innerHTML=`<div class="profile-toolbar"><select id="profileSelect" aria-label="Saved watermark profile"><option value="">New watermark profile</option>${Object.keys(state.profiles).sort().map(n=>`<option ${n===state.wmName?'selected':''}>${escapeHtml(n)}</option>`).join('')}</select><button id="newWatermark" class="button">＋ New</button><span id="wmDirtyLabel" class="dirty-mark"></span></div><div class="wm-layout"><article class="panel"><form id="wmForm" class="wm-form"><div class="wm-fields"><label class="full">Profile name<input id="wmName" value="${escapeHtml(state.wmName)}" placeholder="e.g. internal-review" required maxlength="64"></label>${wmField('kind','Watermark type','text',['Text','Image'])}${wmField('layer','Drawing layer','text',[['Over','Over content'],['Behind','Behind content']])}<div class="full" id="wmTextFields">${wmField('text','Watermark text')}</div><div class="full" id="wmImageFields"><label>Image asset<select id="wmAsset"><option value="">Choose an image…</option>${state.assets.map(n=>`<option value="${escapeHtml(n)}" ${n===state.wm.imagePath?'selected':''}>${escapeHtml(n)}</option>`).join('')}</select></label><label style="margin-top:12px">Browse and upload<input id="assetUpload" type="file" accept="image/png,image/jpeg,image/gif,image/webp,image/bmp"></label><small>PNG, JPEG, GIF, WEBP or BMP · up to 25 MiB</small></div></div><div class="wm-section"><h3>Appearance</h3><div class="wm-fields">${wmField('fontFamily','Font family')}${wmField('fontSize','Font size · points','number')}${wmField('color','Color','color')}<label>Opacity <span id="opacityValue">${Math.round(state.wm.opacity*100)}%</span><input id="wm-opacity" data-wm="opacity" type="range" min="0" max="1" step=".01" value="${state.wm.opacity}"></label>${wmField('rotation','Rotation · degrees','number')}${wmField('tile','Repeat across the page','checkbox')}</div><p>Tiling repeats the mark in a grid. Page selection below controls which pages receive it.</p></div><div class="wm-section"><h3>Position & dimensions</h3><div class="wm-fields">${wmField('position','Anchor','text',positions)}${wmField('offsetX','Horizontal offset · pt','number')}${wmField('offsetY','Vertical offset · pt','number')}<div id="imageDimensions" class="full wm-fields">${wmField('width','Image width · pt','number')}${wmField('height','Image height · pt','number')}<label class="full">Keep aspect ratio<input id="lockAspect" type="checkbox" checked></label></div></div><p>Blank image dimensions use natural size. Set one dimension to preserve proportions. Points are 1/72 of an inch.</p></div><div class="wm-section"><h3>Pages & page furniture</h3><div class="wm-fields"><label>Apply to<select id="pageMode">${[['all','All pages'],['none','No pages'],['first','First page'],['last','Last page'],['odd','Odd pages'],['even','Even pages'],['custom','Custom range']].map(([v,l])=>`<option value="${v}" ${pageMode===v?'selected':''}>${l}</option>`).join('')}</select></label><label id="customPageLabel">Page range<input id="customPages" placeholder="1,3-5" value="${pageMode==='custom'?escapeHtml(state.wm.pages):''}"></label>${wmField('header','Header','text',null,true)}${wmField('footer','Footer','text',null,true)}${wmField('pageNumberFormat','Page number format','text',null,true)}</div><p>Use {page} and {pages}. When footer is blank, the page-number format becomes the footer. Leave both blank for no footer.</p></div><div id="wmErrors" class="field-error" role="status"></div><div class="dialog-actions"><button type="button" id="resetWm" class="button">Reset</button><button type="button" id="deleteWm" class="button">Delete</button><button id="saveWm" class="button primary">Save profile</button></div></form></article><article class="panel preview-sticky"><div class="panel-head"><div><h2>Document preview</h2><small>A4 · 595 × 842 points</small></div><select id="previewPage" aria-label="Preview page" style="width:auto"><option value="1">Page 1 of 5</option><option value="2">Page 2 of 5</option><option value="3">Page 3 of 5</option><option value="4">Page 4 of 5</option><option value="5">Page 5 of 5</option></select></div><div class="preview-stage"><div class="paper" id="paper"><div class="sample-content"><div class="doc-kicker">NORTHLINE / QUARTERLY REPORT</div><h3>A clearer picture.<br>A stronger direction.</h3><p>Operations summary · September 2026</p><div class="document-box"><strong>Executive overview</strong><br>This colored area is opaque document content. A background watermark is hidden underneath it; an overlay is drawn above it.</div><p>Reliable information gives every team the confidence to move forward. This sample contains text, a solid color panel, and open space so you can compare both watermark layers.</p><div class="document-line"></div><div class="document-line"></div><div class="document-line"></div><div class="document-line"></div><div class="document-line"></div></div><div id="watermarkLayer" class="watermark-layer"></div><div id="wmHeaderPreview" class="furniture header"></div><div id="wmFooterPreview" class="furniture footer"></div></div></div><div class="preview-help"><strong id="layerDescription"></strong><div id="pageDescriptionWm"></div><p>This browser preview is an approximation. Font metrics and page geometry can differ in the generated PDF. Use the rendered sample to inspect actual output.</p><button id="renderSample" class="button">Generate sample PDF ↗</button></div></article></div>`;
 for(const input of document.querySelectorAll('[data-wm]'))input.oninput=()=>{const key=input.dataset.wm;state.wm[key]=input.type==='checkbox'?input.checked:['fontSize','opacity','rotation','offsetX','offsetY','width','height'].includes(key)?input.value===''?null:Number(input.value):input.value;
  if((key==='width'||key==='height')&&$('lockAspect').checked){state.wm[key==='width'?'height':'width']=null;$('wm-'+(key==='width'?'height':'width')).value='';}
  markWmDirty();previewWatermark();};
 $('wmName').oninput=markWmDirty;
 $('pageMode').onchange=()=>{state.wm.pages=$('pageMode').value==='custom'?$('customPages').value:$('pageMode').value;markWmDirty();previewWatermark();};
 $('customPages').oninput=()=>{state.wm.pages=$('customPages').value;markWmDirty();previewWatermark();};
 $('previewPage').onchange=previewWatermark;
 $('wmAsset').onchange=async()=>{state.wm.imagePath=$('wmAsset').value||null;markWmDirty();await loadAsset();previewWatermark();};
 $('assetUpload').onchange=async()=>{const file=$('assetUpload').files[0];if(!file)return;if(file.size>25*1024*1024){notice('Choose an image no larger than 25 MiB.',true);return;}const form=new FormData();form.append('file',file);try{const result=await api('/v1/watermark-assets',{method:'POST',body:form});state.wm.imagePath=result.name;state.wmDirty=true;await renderWatermarks();notice('Image uploaded and selected. Save the profile to retain the selection.');}catch(error){notice(error.message,true);}};
 $('profileSelect').onchange=async()=>{if(state.wmDirty&&!confirm('Discard unsaved watermark changes?')){$('profileSelect').value=state.wmName;return;}state.wmName=$('profileSelect').value;state.wm=clone(state.profiles[state.wmName]||defaultWatermark());state.wmSaved=clone(state.wm);state.wmDirty=false;await renderWatermarks();};
 $('newWatermark').onclick=async()=>{if(state.wmDirty&&!confirm('Discard unsaved watermark changes?'))return;state.wm=defaultWatermark();state.wmSaved=clone(state.wm);state.wmName='';state.wmDirty=false;await renderWatermarks();};
 $('resetWm').onclick=async()=>{state.wm=clone(state.wmSaved);state.wmDirty=false;await renderWatermarks();};
 $('deleteWm').onclick=async()=>{if(!state.wmName||!confirm('Delete watermark profile “'+state.wmName+'”? Conversions referencing this name will no longer find it.'))return;try{await api('/v1/watermark-profiles/'+encodeURIComponent(state.wmName),{method:'DELETE'});state.wm=null;state.wmDirty=false;await renderWatermarks();notice('Profile deleted.');}catch(error){notice(error.message,true);}};
 $('wmForm').onsubmit=async e=>{e.preventDefault();const errors=validateWatermark();if(errors.length)return;try{$('saveWm').disabled=true;const name=$('wmName').value.trim();await api('/v1/watermark-profiles/'+encodeURIComponent(name),{method:'PUT',headers:{'Content-Type':'application/json'},body:JSON.stringify(state.wm)});state.wmName=name;state.wmSaved=clone(state.wm);state.wmDirty=false;await renderWatermarks();notice('Watermark profile saved. New conversions can select it immediately.');}catch(error){notice(error.message,true);}finally{if($('saveWm'))$('saveWm').disabled=false;}};
 $('renderSample').onclick=async()=>{if(validateWatermark(false).length)return;$('renderSample').disabled=true;$('renderSample').textContent='Rendering…';try{const blob=await api('/v1/create-text-pdf',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text:'InactivePDF — rendered watermark sample\n\nThis PDF was generated by the conversion service.\nInspect the watermark size, opacity, position and page furniture.\n\nThis is a single-page sample; page filters still apply.',profile:'archive',watermark:state.wm}),blob:true});download(blob,'inactivepdf-watermark-preview.pdf');notice('Rendered sample PDF downloaded. This one-page sample respects the configured page filter.');}catch(error){notice(error.message,true);}finally{$('renderSample').disabled=false;$('renderSample').textContent='Generate sample PDF ↗';}};
 await loadAsset();previewWatermark();markWmDirty(false);
}
async function loadAsset(){const name=state.wm.imagePath;if(!name||state.imageUrls.has(name))return;try{const blob=await api('/v1/watermark-assets/'+encodeURIComponent(name),{blob:true});const url=URL.createObjectURL(blob);const image=new Image();await new Promise((resolve,reject)=>{image.onload=resolve;image.onerror=()=>reject(new Error('The selected asset could not be decoded as a browser image.'));image.src=url;});state.imageUrls.set(name,{url,width:image.naturalWidth,height:image.naturalHeight});}catch(error){notice(error.message,true);}}
function markWmDirty(update=true){if(update)state.wmDirty=true;if($('wmDirtyLabel'))$('wmDirtyLabel').textContent=state.wmDirty?'Unsaved changes':'All changes saved';validateWatermark();}
function selectedPage(pages,page,total){const p=pages.trim().toLowerCase();if(p==='all')return true;if(p==='none')return false;if(p==='first')return page===1;if(p==='last')return page===total;if(p==='odd')return page%2===1;if(p==='even')return page%2===0;return p.split(',').some(part=>{const [a,b]=part.trim().split('-').map(Number);return page>=a&&page<=(b??a);});}
function validPages(pages){if(/^(all|none|first|last|odd|even)$/i.test(pages.trim()))return true;return pages.split(',').every(part=>{if(!/^\s*\d+\s*(?:-\s*\d+\s*)?$/.test(part))return false;const [a,b=a]=part.trim().split('-').map(Number);return a>=1&&b>=a&&b-a<=10000&&b<2147483647;});}
function validateWatermark(requireName=true){if(!state.wm||!$('wmErrors'))return [];const w=state.wm,errors=[];
 if(requireName){const name=$('wmName').value.trim();if(!name||name.length>64||/[<>:"/\\|?*\u0000-\u001f]/.test(name)||['.','..'].includes(name))errors.push('Profile name: enter 1–64 characters without path separators or reserved filename characters.');}
 if(w.kind==='Text'&&!w.text?.trim())errors.push('Watermark text is required.');if(w.kind==='Image'&&!w.imagePath)errors.push('Choose an image asset.');
 for(const [key,min,max] of [['fontSize',.01,500],['opacity',0,1],['rotation',-360,360],['offsetX',-10000,10000],['offsetY',-10000,10000],['width',.01,2000],['height',.01,2000]])if(!(['width','height'].includes(key)&&w[key]===null)&&(!Number.isFinite(w[key])||w[key]<min||w[key]>max))errors.push(human(key)+': enter a value from '+min+' to '+max+'.');
 if(!validPages(w.pages))errors.push('Enter a valid page selection, such as 1,3-5.');$('wmErrors').textContent=errors.join(' ');$('saveWm').disabled=errors.length>0;return errors;
}
function previewWatermark(){if(!$('paper'))return;const w=state.wm,isImage=w.kind==='Image';$('wmTextFields').hidden=isImage;$('wmImageFields').hidden=!isImage;$('imageDimensions').hidden=!isImage;$('customPageLabel').hidden=$('pageMode').value!=='custom';$('opacityValue').textContent=Math.round(w.opacity*100)+'%';
 const layer=$('watermarkLayer');layer.replaceChildren();layer.style.zIndex=w.layer==='Behind'?'0':'2';const page=Number($('previewPage').value),included=selectedPage(w.pages,page,5);
 $('layerDescription').textContent=w.layer==='Behind'?'Behind content: the colored document panel covers the watermark.':'Over content: the watermark appears above the colored document panel.';$('pageDescriptionWm').textContent=included?'This preview page is included.':'This preview page is excluded by your page selection.';
 const expand=t=>(t||'').replaceAll('{page}',String(page)).replaceAll('{pages}','5');$('wmHeaderPreview').textContent=included?expand(w.header):'';$('wmFooterPreview').textContent=included?expand(w.footer||w.pageNumberFormat):'';
 for(const id of ['wmHeaderPreview','wmFooterPreview']){$(id).style.color=w.color;$(id).style.opacity=w.opacity;$(id).style.fontSize=(w.fontSize/595*100)+'cqw';}
 if(!included)return;const asset=state.imageUrls.get(w.imagePath);let width,height;
 if(isImage){if(!asset)return;width=w.width||(w.height?w.height*asset.width/asset.height:asset.width*.75);height=w.height||(w.width?w.width*asset.height/asset.width:asset.height*.75);}
 else{const canvas=document.createElement('canvas'),ctx=canvas.getContext('2d');ctx.font=`${w.fontSize}px ${w.fontFamily}`;width=ctx.measureText(w.text||'').width;height=w.fontSize*1.2;}
 const index=positions.indexOf(w.position),column=index%3,row=Math.floor(index/3),x=(595-width)*column/2+(w.offsetX||0),y=(842-height)*row/2+(w.offsetY||0);
 const draw=(left,top)=>{const item=isImage?document.createElement('img'):document.createElement('span');item.className='wm-item';if(isImage){item.src=asset.url;item.alt='';}else item.textContent=w.text;Object.assign(item.style,{left:(left/595*100)+'%',top:(top/842*100)+'%',width:width/595*100+'%',height:height/842*100+'%',fontSize:w.fontSize/595*100+'cqw',fontFamily:w.fontFamily,color:w.color,opacity:w.opacity,transform:`rotate(${w.rotation}deg)`});layer.append(item);};draw(x,y);
 if(w.tile){let count=0;for(let left=0;left<595;left+=Math.max(width+40,80))for(let top=0;top<842;top+=Math.max(height+40,80)){if(++count>200)break;draw(left,top);}}
}
new ResizeObserver(()=>{if(state.route==='watermarks')previewWatermark();}).observe($('main'));
async function boot(){await navigate();try{await loadConfig();if(!['overview','jobs','watermarks','security'].includes(state.route))renderSettings();}catch(error){notice(error.message,true);}await refresh();}
boot().catch(error=>notice(error.message,true));

// One search entry point for navigation, configuration, profiles, durable jobs, and operator logs.
const globalSearchHost=document.createElement('div');globalSearchHost.className='global-search';
globalSearchHost.innerHTML='<label class="global-search-input"><span aria-hidden="true">⌕</span><input id="globalSearch" type="search" maxlength="128" autocomplete="off" placeholder="Search everything…" aria-label="Search all settings, jobs, profiles, watch folders, logs, and docs" aria-controls="globalResults" aria-expanded="false"><kbd>⌘ K</kbd></label><div id="globalResults" class="global-results" hidden></div>';
document.querySelector('.topbar').insertBefore(globalSearchHost,document.querySelector('.top-actions'));

let searchTimer=null,searchVersion=0,searchResults=[];
function localSearch(query){
 const q=query.toLowerCase(),results=[];
 for(const key of Object.keys(defaultWatermark())){
  const title=human(key),detail='Watermark '+title+(key==='position'?' anchor placement':key==='tile'?' repeat grid':key==='layer'?' over behind content':'');
  if(detail.toLowerCase().includes(q))results.push({category:'Setting',title,detail,route:'watermarks',watermarkKey:key});
 }
 for(const [route,title,detail] of sections)if((title+' '+detail).toLowerCase().includes(q))results.push({category:'Section',title,detail,route});
 if(state.draft)for(const item of flatten(state.draft)){
  if(item.path.startsWith('CurrentDefaultsReference.'))continue;
  const title=human(item.path.split('.').at(-1)),detail=description(item.path);
  if((item.path+' '+title+' '+detail+' '+String(item.value)).toLowerCase().includes(q))results.push({category:'Setting',title,detail:item.path+' · '+inputHint(item.path,item.value),route:item.path.split('.')[0],path:item.path});
 }
 for(const [name,profile] of Object.entries(state.profiles))if((name+' '+profile.text).toLowerCase().includes(q))results.push({category:'Watermark',title:name,detail:profile.kind+' profile',route:'watermarks',profile:name});
 return results.slice(0,25);
}
function showSearchResults(results,message=''){
 searchResults=results;$('globalResults').hidden=false;$('globalSearch').setAttribute('aria-expanded','true');
 $('globalResults').innerHTML=(message?'<div class="search-caption">'+escapeHtml(message)+'</div>':'')+(results.length?results.map((r,i)=>`<button class="search-result" data-result="${i}"><span class="result-category">${escapeHtml(r.category)}</span><span><strong>${escapeHtml(r.title)}</strong><small>${escapeHtml(r.detail)}</small></span><span aria-hidden="true">↗</span></button>`).join(''):'<div class="empty">No matching results.</div>');
 for(const button of document.querySelectorAll('[data-result]'))button.onclick=()=>jumpToResult(searchResults[Number(button.dataset.result)]);
}
async function searchEverywhere(){
 const q=$('globalSearch').value.trim(),version=++searchVersion;if(!q){$('globalResults').hidden=true;return;}
 const local=localSearch(q);showSearchResults(local,q.length<2?'Type at least two characters to include jobs and logs.':'Searching jobs and logs…');
 if(q.length<2)return;
 try{
  const response=await api('/v1/admin/search?q='+encodeURIComponent(q));if(version!==searchVersion)return;
  const universal=(response.results||[]).map(result=>({
   category:human(result.kind),title:result.title,detail:result.summary,route:result.route,
   path:result.kind==='setting'?result.target:undefined,
   job:result.kind==='job'?result.target:undefined,
   profile:result.kind==='profile'&&result.route==='watermarks'?result.title:undefined,
   serviceProfile:result.kind==='profile'&&result.route==='Performance'?result.title:undefined,
   query:q
  }));
  const remote=[...universal,...response.jobs.map(j=>({category:'Job',title:j.jobId,detail:j.state+' · '+j.correlationId,route:'jobs',job:j.jobId})),
   ...response.executions.map(r=>({category:'Conversion',title:r.fileName||r.id,detail:r.source+' · '+r.state+' · '+ms(r.durationMs),route:'jobs',execution:r,query:q})),
   ...response.logs.rows.map(r=>({category:'Log',title:r.fileName||r.event,detail:r.event+' · '+r.utc,route:'jobs',log:r.utc+'-'+r.fileName,query:q}))];
  showSearchResults([...local,...remote].slice(0,60),'Settings, profiles, stored jobs and execution records. Watch logs: '+response.logs.coverage);
 }catch(error){if(version===searchVersion)showSearchResults(local,'Remote search unavailable: '+error.message);}
}
async function jumpToResult(result){
 $('globalResults').hidden=true;$('globalSearch').setAttribute('aria-expanded','false');
 if(result.job)state.selectedJobId=result.job;
 if(result.execution||result.log){state.logQuery=result.query;state.executionMatches=result.execution?[result.execution]:[];}
 if(result.profile){if(state.wmDirty&&!confirm('Discard unsaved watermark changes?'))return;state.wmName=result.profile;state.wm=clone(state.profiles[result.profile]);state.wmSaved=clone(state.wm);state.wmDirty=false;}
 state.pendingFocus=result;
 if(location.hash==='#'+result.route)await navigate();else location.hash=result.route;
}
function applyPendingFocus(){
 const result=state.pendingFocus;if(!result)return;state.pendingFocus=null;
 if(result.watermarkKey){
  const id=result.watermarkKey==='pages'?'pageMode':result.watermarkKey==='imagePath'?'wmAsset':'wm-'+result.watermarkKey;
  const target=$(id),visible=target?.getClientRects().length?target:$('wm-kind');
  if(visible){visible.scrollIntoView({block:'center',behavior:'smooth'});visible.classList.add('search-highlight');visible.focus({preventScroll:true});setTimeout(()=>visible.classList.remove('search-highlight'),2500);if(visible!==target)notice('This control applies to image watermarks. Select Image to edit it.');}
  return;
 }
 if(result.serviceProfile){
  const target=$('serviceProfileControls');
  if(target){target.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'center'});target.classList.remove('search-highlight');void target.offsetWidth;target.classList.add('search-highlight');setTimeout(()=>target.classList.remove('search-highlight'),2500);}
  return;
 }
 const element=result.path?$('setting-'+result.path)?.closest('.setting-row'):result.job?$('jobResult'):result.execution||result.log?$('logPanel'):result.profile?$('profileSelect'):$('pageTitle');
 if(element){element.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'center'});element.classList.remove('search-highlight');void element.offsetWidth;element.classList.add('search-highlight');setTimeout(()=>element.classList.remove('search-highlight'),2500);if(result.path)$('setting-'+result.path)?.focus({preventScroll:true});}
}
$('globalSearch').oninput=()=>{++searchVersion;clearTimeout(searchTimer);searchTimer=setTimeout(searchEverywhere,200);};
$('globalSearch').onfocus=()=>{if($('globalSearch').value.trim())searchEverywhere();};
$('globalSearch').onkeydown=e=>{if(e.key==='Escape'){$('globalResults').hidden=true;$('globalSearch').setAttribute('aria-expanded','false');}if(e.key==='ArrowDown'){e.preventDefault();$('globalResults').querySelector('button')?.focus();}if(e.key==='Enter'&&searchResults.length){e.preventDefault();jumpToResult(searchResults[0]);}};
$('globalResults').onkeydown=e=>{const buttons=[...$('globalResults').querySelectorAll('button')],index=buttons.indexOf(document.activeElement);if(e.key==='ArrowDown'||e.key==='ArrowUp'){e.preventDefault();buttons[Math.max(0,Math.min(buttons.length-1,index+(e.key==='ArrowDown'?1:-1)))]?.focus();}if(e.key==='Escape'){$('globalResults').hidden=true;$('globalSearch').focus();}};
document.addEventListener('keydown',e=>{if((e.metaKey||e.ctrlKey)&&e.key.toLowerCase()==='k'){e.preventDefault();$('globalSearch').focus();}});
document.addEventListener('click',e=>{if(!globalSearchHost.contains(e.target)){$('globalResults').hidden=true;$('globalSearch').setAttribute('aria-expanded','false');}});
setInterval(()=>{if(!state.lastSample)return;const age=Date.now()-new Date(state.lastSample).getTime();if(age>7000&&$('liveBadge').textContent.startsWith('Live')){$('liveBadge').textContent='Stale data';$('connectionLabel').textContent='No update received for '+Math.round(age/1000)+' seconds';notice('Stale data: the service has not delivered a live update.');}},1000);

(function(G) {
  'use strict';
  const C=G.C;
  const zero={requests:0,denied:0,errors:0,input:0,output:0,cost:0};
  const blocked=()=>['empty','loading','error'].includes(G.demo);
  const rangeFrom=state=>state.range==='Today'?'2026-10-07':'2026-10-01';
  const rangeLabel=state=>state.range==='Today'?'October 7, 2026':'October 1–7, 2026';
  const sumOf=rows=>G.aggregate(rows)[0]||zero;
  const tenantName=()=>G.data.tenants.find(tenant=>tenant.id===G.state.tenantId)?.name||'Tenant';
  const dayLabel=name=>`${['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'][Number(name.slice(5,7))-1]} ${Number(name.slice(8,10))}`;

  G.dashboardRows=()=>{
    const state=G.viewState('/',{range:'7 days'});
    const from=rangeFrom(state);
    return G.usageRows().filter(row=>row.time.slice(0,10)>=from&&row.time<=G.now);
  };

  /* Previous equal-length window from real seed rows; absent (null) when the seed has no history there. */
  function previousRows(state){
    const start=Date.parse(rangeFrom(state)+'T00:00:00Z'), end=Date.parse(G.now);
    const duration=end-start, prevStart=new Date(start-86400000*(state.range==='Today'?1:7)).toISOString();
    const prevEnd=new Date(Date.parse(prevStart)+duration).toISOString();
    return G.usageRows().filter(row=>row.time>=prevStart&&row.time<prevEnd);
  }
  function trend(current,previous){
    if(previous==null||previous===0) return '<span class="metric-trend trend-none" title="No prior-period data in the demo seed.">no prior data</span>';
    const delta=(current-previous)/previous*100, dir=delta>0.05?'up':delta<-0.05?'down':'flat';
    return `<span class="metric-trend trend-${dir}" title="Previous-period total: ${C.num(previous)}">${dir==='up'?'▲':dir==='down'?'▼':'–'} ${Math.abs(delta).toFixed(1)}%</span>`;
  }
  function sparkline(values){
    if(values.length<2) return '';
    const min=Math.min(...values), span=Math.max(...values)-min||1;
    const points=values.map((value,index)=>`${(index/(values.length-1)*108+2).toFixed(1)},${(22-(value-min)/span*20).toFixed(1)}`).join(' ');
    return `<svg class="metric-sparkline" viewBox="0 0 112 24" aria-hidden="true"><polyline points="${points}" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" stroke-linecap="round"/></svg>`;
  }
  function emptyState(text){
    return `<div class="area-state empty-state"><span class="empty-icon" aria-hidden="true">${G.icon?G.icon('states'):'—'}</span><strong>${C.e(text)}</strong><span>Results will appear here when available.</span></div>`;
  }
  function areaState(){
    if(G.demo==='error') return C.alert('error','Unable to load data. Please try again.');
    if(G.demo==='loading') return '<div class="area-state" aria-busy="true"><span class="skeleton" aria-hidden="true"></span><span class="skeleton" aria-hidden="true"></span></div>';
    if(G.demo==='empty') return emptyState('No data yet.');
    return '';
  }
  function metricValue(live,format){
    if(live) return format();
    if(G.demo==='loading') return '<span class="skeleton" aria-hidden="true"></span>';
    if(G.demo==='empty') return format();
    return '—';
  }
  function chartSpec(metric,day,colors){
    if(metric==='tokens') return {
      datasets:[{label:'Tokens (input + output)',data:day.map(row=>row.input+row.output),backgroundColor:colors[0]}],
      headers:['Day','Input tokens','Output tokens','Total'],
      cells:row=>[C.e(row.name),C.num(row.input),C.num(row.output),C.num(row.input+row.output)]
    };
    if(metric==='cost') return {
      datasets:[{label:'Cost (USD)',data:day.map(row=>row.cost),backgroundColor:colors[0]}],
      headers:['Day','Cost'],
      cells:row=>[C.e(row.name),C.money(row.cost)]
    };
    if(metric==='errors') return {
      datasets:[{label:'Errors',data:day.map(row=>row.errors),backgroundColor:colors[0]},{label:'Denied',data:day.map(row=>row.denied),backgroundColor:colors[1]}],
      headers:['Day','Errors','Denied'],
      cells:row=>[C.e(row.name),C.num(row.errors),C.num(row.denied)]
    };
    return {
      datasets:[{label:'Requests',data:day.map(row=>row.requests),backgroundColor:colors[0]}],
      headers:['Day','Requests'],
      cells:row=>[C.e(row.name),C.num(row.requests)]
    };
  }

  G.views['/']=()=>{
    const state=G.viewState('/',{range:'7 days'}), chart=G.viewState('/chart',{metric:'requests',table:false});
    const live=!blocked();
    const rows=live?G.dashboardRows():[];
    const sum=live?sumOf(rows):zero, prev=live?sumOf(previousRows(state)):null, obs=live?G.observability(rows):null;
    const day=G.aggregate(rows,row=>row.time.slice(0,10));

    const kpis=[
      ['Requests',()=>C.num(sum.requests),'requests'],
      ['Denied',()=>C.num(sum.denied),'denied'],
      ['Errors',()=>C.num(sum.errors),'errors'],
      ['Input tokens',()=>C.num(sum.input),'input'],
      ['Output tokens',()=>C.num(sum.output),'output'],
      ['Cost',()=>C.money(sum.cost),'cost']
    ];
    const strip=`<section class="metric-strip" aria-label="Usage statistics">${kpis.map(([label,format,key])=>`<div class="metric"><span class="metric-label">${label}</span><strong class="metric-value${label==='Cost'?' cost':''}">${metricValue(live,format)}</strong>${live?trend(sum[key],prev[key]):'<span class="metric-trend trend-none">—</span>'}${live?sparkline(day.map(row=>row[key])):''}<span class="metric-foot">${label==='Cost'?'Combined model usage':rangeLabel(state)}</span></div>`).join('')}</section>`;

    const observed=[
      ['Latency p50',obs&&obs.latency.p50!=null?C.num(obs.latency.p50)+' ms':'—','Forwarded requests'],
      ['Latency p95',obs&&obs.latency.p95!=null?C.num(obs.latency.p95)+' ms':'—','Includes failed requests'],
      ['TTFB p50',obs&&obs.ttfb.p50!=null?C.num(obs.ttfb.p50)+' ms':'—','Time to first byte'],
      ['TTFB p95',obs&&obs.ttfb.p95!=null?C.num(obs.ttfb.p95)+' ms':'—','Time to first byte'],
      ['Fallback rate',obs&&obs.fallbackRate!=null?(obs.fallbackRate*100).toFixed(1)+'%':'—','Backup-provider share']
    ];
    const obsStrip=`<section class="metric-strip secondary" aria-label="Observability">${observed.map(([label,value,foot])=>`<div class="metric"><span class="metric-label">${label}</span><strong class="metric-value">${metricValue(live,()=>value)}</strong><span class="metric-foot">${foot}</span></div>`).join('')}</section>`+
      (live?`<p class="hint">${C.num(obs.observedCount)} forwarded requests · ${C.num(obs.ttfbCount)} first-byte measurements · ${C.num(obs.fallbackCount)} fallback requests. Denied requests are excluded; failed upstream requests count toward latency and fallback rate.</p>`:'');

    function circuitBody(){
      if(!live) return areaState();
      const items=(G.data.providers||[]).map(provider=>{
        const served=rows.filter(row=>row.providerId===provider.id);
        const ok=served.filter(row=>row.status==='ok').length, errors=served.filter(row=>row.status==='error').length;
        const measured=ok+errors, errorRate=measured?errors/measured*100:0;
        const last=served.length?served.reduce((a,b)=>a.time>b.time?a:b).time:null;
        // ponytail: estimate circuit state from the demo sample; use provider_health snapshots when exposed by the backend.
        const breaker=!provider.enabled?['circuit-disabled','disabled']:errorRate>20?['circuit-open','open']:errorRate>5?['circuit-half-open','half-open']:['circuit-closed','closed'];
        const note=!provider.enabled
          ?`Disabled in configuration — not routed. Historical fallback target: last used ${last?C.date(last):'—'}, before its route was disabled.`
          :`${measured?(ok/measured*100).toFixed(2)+'% success on '+C.num(measured)+' measured requests':'No measured requests in this range'}. Last used ${last?C.date(last):'—'}.`;
        return `<div class="circuit-item ${breaker[0]}"><span class="circuit-dot" aria-hidden="true"></span><div><span class="circuit-name">${C.e(provider.name)}</span> <span class="circuit-state">${breaker[1]}</span><p class="circuit-note">${note}</p></div></div>`;
      }).join('');
      return `<div class="circuit-head"><span class="hint">Simulated circuit snapshot — derived from demo seed data, not live telemetry.</span></div><div class="circuit-strip">${items}</div>`;
    }

    function quotaBar(label,used,limit,format){
      const ratio=limit==null?0:limit===0?(used>0?100:0):used/limit*100;
      const pct=Math.min(100,ratio), cls=limit!=null&&ratio>=90?' critical':limit!=null&&ratio>=75?' warn':'';
      const meta=limit==null?`${format(used)} · uncapped`:`${format(used)} of ${format(limit)} · ${ratio.toFixed(1)}%`;
      return `<div class="quota-row"><span class="quota-label">${C.e(label)}</span><span class="quota-meta">${meta}</span></div><div class="quota-bar"><div class="quota-fill${cls}" style="width:${pct.toFixed(1)}%"></div></div>`;
    }
    function quotaBody(){
      if(!live) return areaState();
      const policies=G.data.policies||[];
      const month=G.usageRows().filter(row=>row.time.slice(0,7)==='2026-10');
      const monthTokens=month.reduce((n,row)=>n+row.input+row.output,0), monthCost=month.reduce((n,row)=>n+row.cost,0);
      const today=sumOf(month.filter(row=>row.time.slice(0,10)===G.now.slice(0,10)));
      const items=[];
      const tenantPolicy=policies.find(policy=>policy.scope==='tenant'&&policy.scopeId===G.state.tenantId&&policy.enabled);
      if(tenantPolicy){
        let body='';
        if(tenantPolicy.daily!=null) body+=quotaBar('Today tokens (tenant scope)',today.input+today.output,tenantPolicy.daily,C.num);
        if(tenantPolicy.monthly!=null) body+=quotaBar('Monthly tokens (tenant scope)',monthTokens,tenantPolicy.monthly,C.num);
        if(tenantPolicy.budget!=null) body+=quotaBar('Monthly budget (tenant scope)',monthCost,tenantPolicy.budget,C.money);
        if(!body) body='<p class="hint">No countable tenant limits configured.</p>';
        items.push(`<div class="quota-item"><div class="quota-head"><span class="quota-name">${C.e(tenantName())}</span>${C.badge('tenant policy')}</div>${body}</div>`);
      }
      for(const project of G.data.projects||[]){
        const projectRows=rows.filter(row=>row.projectId===project.id);
        const policy=policies.find(policy=>policy.scope==='project'&&policy.scopeId===project.id&&policy.enabled);
        if(!policy){
          items.push(`<div class="quota-item uncapped"><div class="quota-head"><span class="quota-name">${C.e(project.name)}</span>${C.badge(project.status)}</div><p class="hint">No project-scope policy — uncapped for this project. Tenant-level limits still apply.</p></div>`);
          continue;
        }
        const minutes=new Map();
        for(const row of projectRows){ if(row.status==='denied') continue; const key=row.time.slice(0,16); minutes.set(key,(minutes.get(key)||0)+1); }
        const peakRate=minutes.size?Math.max(...minutes.values()):0;
        const peakTokens=projectRows.reduce((n,row)=>Math.max(n,row.output),0);
        const projectMonth=sumOf(month.filter(row=>row.projectId===project.id));
        const projectToday=sumOf(month.filter(row=>row.projectId===project.id&&row.time.slice(0,10)===G.now.slice(0,10)));
        let body='';
        if(policy.monthly!=null) body+=quotaBar('Monthly tokens',projectMonth.input+projectMonth.output,policy.monthly,C.num);
        if(policy.daily!=null) body+=quotaBar('Today tokens',projectToday.input+projectToday.output,policy.daily,C.num);
        if(policy.budget!=null) body+=quotaBar('Monthly budget',projectMonth.cost,policy.budget,C.money);
        if(policy.rpm!=null) body+=quotaBar('Measured peak rate',peakRate,policy.rpm,value=>C.num(value)+'/min');
        if(policy.maxTokens!=null) body+=quotaBar('Largest output',peakTokens,policy.maxTokens,C.num);
        const chips=[];
        if(policy.maxTokens!=null) chips.push(`${C.num(policy.maxTokens)} max output tokens/request`);
        if(policy.allowed&&policy.allowed.length) chips.push(`${policy.allowed.length} allowed model${policy.allowed.length===1?'':'s'}`);
        items.push(`<div class="quota-item"><div class="quota-head"><span class="quota-name">${C.e(project.name)}</span>${C.badge(project.status)}</div>${body}${chips.length?`<div class="quota-chips">${chips.map(chip=>`<span class="quota-chip">${C.e(chip)}</span>`).join('')}</div>`:''}<p class="hint">Project-scope policy governs this project; tenant-level limits still apply.</p></div>`);
      }
      return `<div class="quota-list">${items.join('')}</div><p class="hint">Bars compare measured demo-seed values with the limits of the policy scope that actually sets them — tenant quotas are never billed to a project.</p>`;
    }

    function shareList(entries){
      const total=rows.length||1;
      return entries.map(entry=>{const pct=entry.value/total*100;return `<div class="share-item"><div class="share-head"><span class="share-name">${entry.name}</span><span class="share-value">${C.num(entry.value)} · ${pct.toFixed(1)}%</span></div><div class="share-bar"><div class="share-fill" style="width:${pct.toFixed(1)}%"></div></div><span class="share-meta">${entry.meta}</span></div>`;}).join('');
    }
    function shareBody(group){
      if(!live) return areaState();
      const grouped=group==='model'
        ?G.aggregate(rows,row=>G.data.models.find(model=>model.id===row.modelId)?.alias||'Unknown model')
        :G.aggregate(rows,row=>G.data.keys.find(key=>key.id===row.keyId)?.name||'Unknown key');
      grouped.sort((a,b)=>b.requests-a.requests);
      if(!grouped.length) return emptyState('No requests in this range.');
      return `<div class="share-list">${shareList(grouped.slice(0,4).map(entry=>({name:`<span class="mono">${C.e(entry.name)}</span>`,value:entry.requests,meta:`${C.num(entry.input+entry.output)} tokens · ${C.money(entry.cost)}`})))}</div>`;
    }

    const inspect=row=>G.inspectRequest(row);
    const recent=rows.slice().sort((a,b)=>b.time.localeCompare(a.time)).slice(0,8);
    const recentBody=`<div class="dashboard-table">${C.table(['Time','Request','Status','Model','Latency','Cost','Actions'],recent.map(row=>[
      C.e(C.date(row.time)),
      `<span class="mono">${C.e(String(row.request).slice(0,8))}</span>`,
      C.badge(row.status),
      `<span class="mono">${C.e(G.data.models.find(model=>model.id===row.modelId)?.alias||'—')}</span>`,
      row.latency==null?'—':C.num(row.latency)+' ms',
      C.money(row.cost),
      C.button('Inspect',()=>inspect(row),'ghost',`aria-label="Inspect request ${C.e(String(row.request).slice(0,8))}"`)
    ]),{empty:'No requests in this range.'})}</div>`;

    const chartTabs=[['requests','Requests'],['tokens','Tokens'],['cost','Cost'],['errors','Errors + denied']];
    const metric=chartTabs.some(([key])=>key===chart.metric)?chart.metric:'requests';
    const spec=chartSpec(metric,day,[]);
    const chartReady=typeof window.Chart!=='undefined';
    const showTable=!live||chart.table||!chartReady;
    const chartBody=`<div class="chart-heading"><div class="chart-tabs"><div class="segmented" role="group" aria-label="Chart metric">${chartTabs.map(([key,label])=>C.button(label,()=>{chart.metric=key;G.render();},'ghost',`aria-pressed="${metric===key}"`)).join('')}</div></div>${C.button(chart.table?'View as chart':'View as table',()=>{chart.table=!chart.table;G.render();},'ghost',`aria-pressed="${chart.table}"`)}</div><div class="chart-container"><canvas id="usage-chart" role="img" aria-label="Per-day usage chart; the same data is available as a table"${showTable?' hidden':''}></canvas></div><p id="chart-fallback" class="chart-fallback hint"${chartReady||chart.table||!live?' hidden':''}>Chart library unavailable — showing the per-day table instead.</p><div class="chart-table" id="chart-table"${showTable?'':' hidden'}>${C.table(spec.headers,day.map(spec.cells),{empty:'No usage in this range.'})}</div>`;

    return C.page('Dashboard',`Usage ${rangeLabel(state)}.`)+
      `<div class="dashboard-controls"><div class="inline"><span class="range-label">Time range</span><div class="segmented" role="group" aria-label="Time range">${['Today','7 days','This month'].map(range=>C.button(range,()=>{state.range=range;G.render();},'ghost',`aria-pressed="${state.range===range}"`)).join('')}</div></div><span class="hint">All amounts in USD</span></div>`+
      strip+obsStrip+
      C.card('Usage trend',chartBody)+
      `<div class="dash-grid">${C.card('Provider circuits',circuitBody())}${C.card('Quotas',quotaBody())}</div>`+
      `<div class="dash-grid">${C.card('Top API keys',shareBody('key'))}${C.card('Model mix',shareBody('model'))}</div>`+
      C.card('Recent requests',recentBody);
  };

  G.mounts['/']=()=>{
    const canvas=document.getElementById('usage-chart'); if(!canvas)return;
    const state=G.viewState('/chart',{metric:'requests',table:false});
    const table=document.getElementById('chart-table'), fallback=document.getElementById('chart-fallback');
    G.chart?.destroy(); G.chart=null;
    const chartable=!!window.Chart&&!blocked()&&!state.table;
    canvas.hidden=!chartable;
    if(table) table.hidden=chartable;
    if(fallback) fallback.hidden=chartable||state.table||blocked();
    if(!chartable) return;
    const day=G.aggregate(G.dashboardRows(),row=>row.time.slice(0,10));
    const css=getComputedStyle(document.documentElement);
    const read=(name,fallbackName)=>css.getPropertyValue(name).trim()||css.getPropertyValue(fallbackName).trim();
    const spec=chartSpec(state.metric,day,[read('--chart','--accent'),read('--chart-2','--muted')]);
    G.chart=new Chart(canvas,{
      type:'bar',
      data:{labels:day.map(row=>dayLabel(row.name)),datasets:spec.datasets},
      options:{
        animation:false,maintainAspectRatio:false,
        plugins:{legend:{display:spec.datasets.length>1,labels:{color:css.getPropertyValue('--muted').trim()}},tooltip:{displayColors:spec.datasets.length>1}},
        scales:{
          x:{grid:{display:false},border:{display:false},ticks:{color:css.getPropertyValue('--muted').trim(),font:{family:'system-ui',size:12}}},
          y:{beginAtZero:true,border:{display:false},grid:{color:css.getPropertyValue('--chart-grid').trim()},ticks:{color:css.getPropertyValue('--muted').trim(),maxTicksLimit:5}}
        }
      }
    });
  };

  G.views['/states']=()=>{
    function stateTable(mode){const previous=G.demo;G.demo=mode;const html=C.table(['Name','Status','Actions'],[],{empty:'No data yet.'});G.demo=previous;return html;}
    return C.page('State sheet','Shared empty, loading, error, and permission states.')+`<div class="state-grid">${C.card('Loading',stateTable('loading'))}${C.card('Empty',stateTable('empty'))}${C.card('Error',C.alert('error','Unable to load data. Please try again.'))}${C.card('Success',C.alert('success','Policy saved.'))}${C.card('Warning',C.alert('warning','Page “/x” was not found or is not available for your role.'))}${C.card('Info',C.alert('info','Content is only available if the project stores conversation content and the retention period has not passed.'))}${C.card('Read-only',C.table(['Name','Actions'],[['chatbot-prod','<span class="muted">read-only</span>']]))}${C.card('Confirmation',`<p>Revoke key "ci-key"? Clients using it will be rejected immediately.</p><div class="section-gap">${C.button('Revoke',()=>C.confirm('Revoke key "ci-key"? Clients using it will be rejected immediately.',()=>C.toast('Confirmation completed.'),'Revoke'),'danger')}</div>`)}</div>`;
  };
  G.mounts['/states']=()=>C.secret('New API key — copy it now; this value is shown only once.','gw_demo_state_sheet_7307');
})(window.GW);

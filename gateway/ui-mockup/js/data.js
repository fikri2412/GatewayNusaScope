(function (G) {
  'use strict';
  const created = '2026-09-15T09:00:00Z';
  function seed() {
    let randomState = 7307;
    const random = () => { randomState = (Math.imul(1664525, randomState) + 1013904223) >>> 0; return randomState / 4294967296; };
    const d = {
      tenants: [{id:1,name:'Acme Studio',slug:'acme-studio',status:'active',planId:3,created},{id:2,name:'Budi Labs',slug:'budi-labs',status:'active',planId:2,created},{id:3,name:'Demo Corp',slug:'demo-corp',status:'suspended',planId:1,created}],
      plans: [{id:1,name:'Default',maxProjects:null,maxKeys:null,maxRequests:null,maxTokens:null},{id:2,name:'Starter',maxProjects:3,maxKeys:10,maxRequests:100000,maxTokens:5000000},{id:3,name:'Pro',maxProjects:20,maxKeys:100,maxRequests:5000000,maxTokens:200000000}],
      templates: [{id:1,code:'opencode',name:'OpenCode',type:'openai',baseUrl:'https://opencode.ai/inference/openai/v1/',authHeader:'Authorization',authPrefix:'Bearer',modelsPath:'/models',syncKind:'opencode_config',syncUrl:'https://opencode.ai/console/api/v2/config',enabled:true}],
      providers: [{id:1,name:'opencode',type:'openai',templateId:1,baseUrl:'https://opencode.ai/inference/openai/v1/',keyHint:'9x2f',enabled:true,authHeader:'Authorization',authPrefix:'Bearer',modelsPath:'/models'},{id:2,name:'internal-llm',type:'openai',templateId:null,baseUrl:'https://llm.acme.id/v1/',keyHint:'',enabled:false,authHeader:'Authorization',authPrefix:'Bearer',modelsPath:'/models'}],
      models: [{id:1,alias:'deepseek-v4.1-flash',description:'Fast reasoning for production conversations',maxOutput:8192,enabled:true,routes:[{providerId:1,upstream:'deepseek-v4.1-flash',priority:1,weight:1,enabled:true},{providerId:2,upstream:'deepseek-v4.1-flash',priority:2,weight:1,enabled:false}],prices:[{input:0.3,output:1.2,cacheRead:0.03,cacheWrite:0,currency:'USD',tiers:[{starts:128000,input:0.6,output:2.4,cacheRead:0.06,cacheWrite:0}]}]},{id:2,alias:'glm-5.3-flash',description:'Low-latency internal assistant',maxOutput:4096,enabled:true,routes:[{providerId:1,upstream:'glm-5.3-flash',priority:1,weight:1,enabled:true}],prices:[{input:0.1,output:0.4,cacheRead:0.01,cacheWrite:0,currency:'USD',tiers:[]}]},{id:3,alias:'legacy-chat',description:'Retired sandbox model',maxOutput:2048,enabled:false,routes:[],prices:[]}],
      projects: [{id:1,name:'chatbot-prod',status:'active',logContent:true,retention:30,created},{id:2,name:'internal-tools',status:'active',logContent:false,retention:null,created},{id:3,name:'sandbox',status:'suspended',logContent:false,retention:null,created}],
      keys: [{id:1,name:'web-app',projectId:1,prefix:'gw_a1b2c3d4e5',ips:[],expires:null,lastUsed:G.now,revoked:null},{id:2,name:'ci-key',projectId:2,prefix:'gw_f6e5d4c3b2',ips:['203.0.113.10'],expires:'2026-12-31T23:59:00Z',lastUsed:'2026-10-07T11:02:00Z',revoked:null},{id:3,name:'old-key',projectId:3,prefix:'gw_1122334455',ips:[],expires:null,lastUsed:'2026-10-04T10:00:00Z',revoked:'2026-10-05T09:00:00Z'}],
      policies: [{id:1,scope:'tenant',scopeId:1,maxTokens:null,rpm:null,daily:null,monthly:200000000,budget:150,allowed:[],enabled:true},{id:2,scope:'project',scopeId:1,maxTokens:4096,rpm:120,daily:null,monthly:null,budget:null,allowed:['deepseek-v4.1-flash'],enabled:true}],
      users: [{id:1,email:'rina@acme.id',name:'Rina Putri',role:'owner',enabled:true,lastLogin:'2026-10-07T08:12:00Z',created},{id:2,email:'dimas@acme.id',name:'Dimas Pratama',role:'admin',enabled:true,lastLogin:'2026-10-07T07:40:00Z',created},{id:3,email:'sari@acme.id',name:'Sari Wulandari',role:'viewer',enabled:false,lastLogin:'2026-10-02T10:30:00Z',created}],
      alerts: [{id:1,name:'Monthly quota 80%',metric:'monthly_tokens',scope:'tenant',scopeId:1,threshold:80,webhookId:1,lastTriggered:'2026-10-06T14:00:00Z',enabled:true},{id:2,name:'Critical budget',metric:'monthly_budget',scope:'tenant',scopeId:1,threshold:100,webhookId:null,lastTriggered:null,enabled:true}],
      events: [{id:1,time:'2026-10-06T14:00:00Z',ruleId:1,metric:'monthly_tokens',scope:'tenant',scopeId:1,threshold:80,period:'2026-10',used:160200000,limit:200000000}],
      webhooks: [{id:1,name:'ops-slack',url:'https://hooks.acme.id/gateway',secretHint:'7890',enabled:true,updated:'2026-10-05T14:00:00Z'}],
      deliveries: [{id:1,webhookId:1,created:'2026-10-06T14:00:00Z',status:'succeeded',attempt:1,code:200,nextAttempt:null,completed:'2026-10-06T14:00:01Z',error:null},{id:2,webhookId:1,created:'2026-10-05T14:00:00Z',status:'failed',attempt:3,code:500,nextAttempt:null,completed:'2026-10-05T14:05:00Z',error:'HTTP 500'}],
      catalog: [], usage: [], audit: [], platformAudit: []
    };
    const catalogNames = ['deepseek-v4.1-flash','glm-5.3-flash','gpt-4o-mini','gpt-4o','gpt-4.1','gpt-4.1-mini','o3-mini','qwen3-235b','llama-4-maverick','gemini-2.5-flash','claude-sonnet-4','text-embedding-3-small'];
    d.catalog = catalogNames.map((upstream, i) => ({id:i+1,templateId:1,upstream,name:upstream,family:i===9?'google_generate':i===10?'anthropic_messages':'openai_chat',context:i===0?256000:128000,maxInput:128000,maxOutput:8192,inputPrice:i===0?0.3:i===1?0.1:0.15+i*0.1,outputPrice:i===0?1.2:i===1?0.4:0.6+i*0.2,cacheRead:0.03,cacheWrite:0,currency:'USD',tiers:i===0?[{starts:128000,input:0.6,output:2.4,cacheRead:0.06,cacheWrite:0}]:[],tools:i!==11,vision:[2,3,4,8,9,10].includes(i),reasoning:[0,1,6,7,9].includes(i),source:'opencode_config',enabled:i!==11}));
    const counts = [1240,1310,980,1505,1720,890,1110], total = counts.reduce((a,b)=>a+b,0);
    const part = (sum, i, count) => Math.floor(sum*(i+1)/count)-Math.floor(sum*i/count);
    let cursor = 0;
    for (let day = 1; day <= 7; day++) {
      for (let j=0; j<counts[day-1]; j++,cursor++) {
        const denied = cursor % 701 === 0 && cursor < 8412, error = [511,2511,4511,6511].includes(cursor);
        const projectId = cursor % 3 === 0 ? 2 : 1;
        const modelId = cursor%4===0?2:1;
        const hour = Math.floor(j/counts[day-1]*(day===7?16:24));
        const minute = Math.floor(random()*60), second = Math.floor(random()*60);
        const time = `2026-10-${String(day).padStart(2,'0')}T${String(hour).padStart(2,'0')}:${String(minute).padStart(2,'0')}:${String(second).padStart(2,'0')}Z`;
        /* Observability metadata: denied requests never reach a provider (no latency/TTFB), upstream failures have measured latency but no first byte (no TTFB). Rows on Oct 1-2 served by providerId 2 are the historical fallback snapshot of when internal-llm was still routed; it is disabled today. */
        const fallbackUsed = !denied && !error && day <= 2 && cursor % 97 === 0;
        const latency = denied ? null : 500 + Math.floor(random()*900) + (fallbackUsed ? 180 : 0);
        const ttfb = denied || error ? null : Math.round(latency * (0.24 + random()*0.26));
        d.usage.push({id:cursor+1,tenantId:1,time,request:(cursor+1).toString(16).padStart(8,'0')+'-demo-4a1b-9a00-730700000001',status:denied?'denied':error?'error':'ok',http:denied?429:error?502:200,reason:denied?'daily_quota_exceeded:key':error?'upstream_failed':null,input:part(3420000,cursor,total),output:part(1980000,cursor,total),cache:cursor%5===0?64:0,cost:part(34125,cursor,total)/10000,latency,ttfb,providerId:denied?null:fallbackUsed?2:1,attempt:denied?0:fallbackUsed?2:1,fallbackUsed,upstream:modelId===1?'deepseek-v4.1-flash':'glm-5.3-flash',upstreamStatus:denied?null:error?502:200,finishReason:denied||error?null:(cursor%13===0?'length':cursor%7===0?'tool_calls':'stop'),projectId,modelId,keyId:projectId===1?1:2,content:projectId===1&&!denied&&!error,expires:'2026-11-06T16:00:00Z'});
      }
    }
    // Keep the representative request from the prompt; redistribute tokens/cost to preserve the exact totals.
    const representative = d.usage[1], donor = d.usage[2];
    for (const [key,value] of Object.entries({input:842,output:311,cache:0,cost:0.0006,latency:812,ttfb:264})) {
      if (['input','output','cost'].includes(key)) donor[key] -= value-representative[key];
      representative[key]=value;
    }
    // Spread the token redistribution across healthy records so no record has negative tokens.
    for (const key of ['input','output','cost']) if (donor[key]<0) { const debt=-donor[key]; donor[key]=0; let remaining=debt; for (let i=3; remaining>0.000000001 && i<d.usage.length; i++) { const take=Math.min(remaining,d.usage[i][key]); d.usage[i][key]-=take; remaining-=take; } }
    for (const tenantId of [2,3]) for(let i=0;i<(tenantId===2?2100:350);i++) d.usage.push({id:d.usage.length+1,tenantId,time:`2026-10-${String(1+i%7).padStart(2,'0')}T10:00:00Z`,request:'b'+i.toString(16).padStart(7,'0')+'-demo',status:i%150===0?'denied':'ok',http:i%150===0?429:200,reason:i%150===0?'monthly_quota_exceeded:tenant':null,input:300+i%90,output:150+i%70,cache:0,cost:(4+i%3)/10000,latency:i%150===0?null:700,ttfb:i%150===0?null:228,providerId:i%150===0?null:1,attempt:i%150===0?0:1,fallbackUsed:false,upstream:'glm-5.3-flash',upstreamStatus:i%150===0?null:200,finishReason:i%150===0?null:'stop',projectId:1,modelId:2,keyId:1,content:false,expires:null});
    d.usage.sort((a,b)=>b.time.localeCompare(a.time)||b.id-a.id);
    const actions = ['provider.create','key.create','key.revoke','policy.update','auth.login','user.invite'];
    for (let tenantId=1;tenantId<=3;tenantId++) for(let i=0;i<25;i++) d.audit.push({id:d.audit.length+1,tenantId,time:`2026-10-${String(1+i%7).padStart(2,'0')}T${String(8+i%8).padStart(2,'0')}:15:00Z`,userId:1+i%3,action:actions[i%actions.length],entity:actions[i%actions.length].split('.')[0],entityId:1+i%3,ip:'203.0.113.10',detail:{changed:['name','enabled'],actor:'demo',tenantId}});
    for(let i=0;i<40;i++) d.platformAudit.push({id:i+1,tenantId:i%3===0?null:1+i%3,time:`2026-10-${String(1+i%7).padStart(2,'0')}T${String(8+i%8).padStart(2,'0')}:30:00Z`,userId:i%3===0?99:1,action:['tenant.create','plan.update','provider.create','auth.login'][i%4],entity:['tenant','plan','provider','user'][i%4],entityId:1+i%3,ip:'203.0.113.10',detail:{source:'demo'}});
    d.audit.sort((a,b)=>b.time.localeCompare(a.time)); d.platformAudit.sort((a,b)=>b.time.localeCompare(a.time));
    G.data = d; G.state.views = {};
  }
  G.resetData = seed;
  G.usageRows = () => G.data.usage.filter(row => row.tenantId === G.state.tenantId);
  G.aggregate = (rows, keyFn = () => 'Total') => {
    const groups = new Map();
    for (const row of rows) { const name=String(keyFn(row)); if(!groups.has(name)) groups.set(name,{name,requests:0,denied:0,errors:0,input:0,output:0,cost:0}); const group=groups.get(name); group.requests++; group.denied+=row.status==='denied'?1:0; group.errors+=row.status==='error'?1:0; group.input+=row.input; group.output+=row.output; group.cost+=row.cost; }
    return [...groups.values()].sort((a,b)=>a.name.localeCompare(b.name));
  };
  /* Nearest-rank percentile over measured values (p 0-100); null when nothing was measured. */
  const percentile = (values, p) => { if (!values.length) return null; const sorted = [...values].sort((a,b)=>a-b); return sorted[Math.min(sorted.length-1, Math.max(0, Math.ceil(p/100*sorted.length)-1))]; };
  /* Denials never reach upstream. Failed forwarded requests still count toward latency and fallback rate; TTFB needs an actual first-byte measurement. */
  G.observability = rows => {
    const observed = rows.filter(row => row.status !== 'denied');
    const latency = observed.map(row => row.latency).filter(Number.isFinite);
    const ttfb = observed.map(row => row.ttfb).filter(Number.isFinite);
    const fallbackCount = observed.filter(row => row.fallbackUsed === true).length;
    return {observedCount:observed.length,ttfbCount:ttfb.length,fallbackCount,fallbackRate:observed.length?fallbackCount/observed.length:null,latency:{p50:percentile(latency,50),p95:percentile(latency,95)},ttfb:{p50:percentile(ttfb,50),p95:percentile(ttfb,95)}};
  };
  seed();
})(window.GW);

'use strict';
// Optional deterministic data check: node gateway/ui-mockup/js/check-data.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const context = {window:{GW:{now:'2026-10-07T16:00:00Z',state:{tenantId:1}}}};
vm.runInNewContext(fs.readFileSync(__dirname + '/data.js','utf8'),context);
const G=context.window.GW;
const rows=G.usageRows(), summary=G.aggregate(rows)[0];
assert.equal(summary.requests,8755);
assert.equal(summary.denied,12);
assert.equal(summary.errors,4);
assert.equal(summary.input,3420000);
assert.equal(summary.output,1980000);
assert.ok(Math.abs(summary.cost-3.4125)<1e-9);
assert.ok(G.data.usage.every(row=>row.input>=0&&row.output>=0&&row.cost>=0&&row.time<=G.now));
const daily=G.aggregate(rows,row=>row.time.slice(0,10));
assert.deepEqual(Array.from(daily,row=>row.requests),[1240,1310,980,1505,1720,890,1110]);
for(const key of ['requests','denied','errors','input','output','cost']){
  const sum=daily.reduce((n,row)=>n+row[key],0);
  assert.ok(Math.abs(sum-summary[key])<1e-8,`daily ${key} disagrees with summary`);
}
// Observability: empty/single, nearest-rank boundary, denied exclusion, and failed upstream attempts.
const obs=G.observability;
const sample=extra=>Object.assign({status:'ok',ttfb:100,latency:200,fallbackUsed:false},extra);
assert.equal(JSON.stringify(obs([])),JSON.stringify({observedCount:0,ttfbCount:0,fallbackCount:0,fallbackRate:null,latency:{p50:null,p95:null},ttfb:{p50:null,p95:null}}));
assert.equal(JSON.stringify(obs([sample()])),JSON.stringify({observedCount:1,ttfbCount:1,fallbackCount:0,fallbackRate:0,latency:{p50:200,p95:200},ttfb:{p50:100,p95:100}}));
const boundary=obs(Array.from({length:20},(unused,index)=>sample({latency:index+1,ttfb:index+1})));
assert.equal(boundary.latency.p50,10);
assert.equal(boundary.latency.p95,19);
const mixed=obs([
  sample({latency:1000,ttfb:300,fallbackUsed:true}),
  sample({latency:400,ttfb:120}),
  {status:'denied',latency:null,ttfb:null,fallbackUsed:false,attempt:0},
  {status:'error',latency:2000,ttfb:null,upstreamStatus:502,fallbackUsed:true,attempt:2}
]);
assert.equal(mixed.observedCount,3,'forwarded failures count; denied rows do not');
assert.equal(mixed.ttfbCount,2,'only measured first bytes count toward TTFB');
assert.equal(mixed.fallbackRate,2/3,'failed fallback attempts must count in the rate');
assert.equal(mixed.latency.p95,2000);
assert.equal(mixed.ttfb.p95,300);
const original=JSON.stringify(G.data);
G.resetData();
assert.equal(JSON.stringify(G.data),original);
console.log('PASS: deterministic seed; exact sample totals; daily/summary agreement; percentile boundaries; denied exclusion; failed-attempt latency and fallback rate.');

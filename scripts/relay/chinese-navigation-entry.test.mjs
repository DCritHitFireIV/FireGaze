import assert from 'node:assert/strict';
import test from 'node:test';
import entry, { ChineseNavigationSubmission } from './chinese-navigation-entry.mjs';

const draft = () => ({schema:1,type:'chinese-navigation',action:'propose',identity:{internalName:'Example',
  sourceUrl:'https://repo.example/index',projectUrl:'https://github.com/mock/plugin',version:'1',apiLevel:15,client:'CN'},
  url:'https://github.com/mock/plugin-cn',clientVersion:'1.5.0.217'});
const request = (body=draft(),ip='192.0.2.1') => new Request('https://relay.invalid/chinese-navigation-submit',{
  method:'POST',headers:{'Content-Type':'application/json','CF-Connecting-IP':ip},body:typeof body==='string'?body:JSON.stringify(body)});
const memory = () => {const values=new Map();return {get:async key=>structuredClone(values.get(key)),put:async(key,v)=>values.set(key,structuredClone(v))};};
function deployed() {
  const env={REPO:'mock/repo',GITHUB_TOKEN:'test-secret-never-log',NAVIGATION_BOT_LOGIN:'mock-bot[bot]'};
  const objects=new Map();
  env.CHINESE_NAVIGATION={idFromName:name=>name,get:id=>{
    if(!objects.has(id))objects.set(id,new ChineseNavigationSubmission({storage:memory()},env));
    return objects.get(id);
  }};
  return env;
}
const reply=(body,status=200)=>Response.json(body,{status});

test('missing durable binding fails explicitly; does not fall through to legacy Issue creation',async()=>{
  const response=await entry.fetch(request(),{REPO:'mock/repo',GITHUB_TOKEN:'secret'});
  assert.equal(response.status,503);
  assert.equal((await response.json()).errorCode,'navigation-unavailable');
});

test('oversized, invalid and non-POST submissions do not reach a Durable Object',async()=>{
  let calls=0;
  const env=deployed(); env.CHINESE_NAVIGATION.get=()=>{calls++;throw Error('not allowed');};
  assert.equal((await entry.fetch(request('{'),env)).status,400);
  assert.equal((await entry.fetch(request('a'.repeat(17_000)),env)).status,413);
  assert.equal((await entry.fetch(new Request('https://relay.invalid/chinese-navigation-submit'),env)).status,405);
  assert.equal(calls,0);
});

test('actual entry and durable coordinator create correct marked Issue; never fetch submitted URL',async()=>{
  const original=globalThis.fetch;
  const requests=[];
  globalThis.fetch=async(url,options={})=>{
    assert.equal(new URL(url).hostname,'api.github.com');
    requests.push({url:String(url),options});
    assert.equal(options.method,'POST');
    return reply({number:71,html_url:'https://github.com/mock/repo/issues/71'},201);
  };
  try {
    const env=deployed();
    const first=await entry.fetch(request(draft(),'192.0.2.3'),env);
    const body=await first.json();
    assert.equal(first.status,200); assert.equal(body.accepted,true); assert.equal(body.issueNumber,71);
    const repeated=await entry.fetch(request({...draft(),note:'说明更新'},'192.0.2.4'),env);
    assert.equal((await repeated.json()).duplicate,true);
    assert.equal(requests.length,1);
    const sent=JSON.parse(requests[0].options.body);
    assert.deepEqual(sent.labels,['chinese-navigation','chinese-navigation-notify-pending']);
    assert.ok(sent.body.includes(`<!-- firegaze:chinese-navigation:v1:${body.submissionId} -->`));
    assert.match(sent.title,/^\[汉化导航投稿\]/);
    assert.ok(!JSON.stringify(body).includes(env.GITHUB_TOKEN));
  } finally {globalThis.fetch=original;}
});

test('upstream failures expose stage/status only and reconciliation never repeats POST',async()=>{
  const original=globalThis.fetch;
  const methods=[];
  globalThis.fetch=async(url,options={})=>{
    methods.push(options.method??'GET');
    if(options.method==='POST') return reply({message:'secret-token C:\\Users\\Fire'},403);
    return reply({incomplete_results:false,items:[]});
  };
  try {
    const env=deployed();
    const first=await entry.fetch(request(draft(),'192.0.2.5'),env);
    const failed=await first.json();
    assert.equal(first.status,202);assert.equal(failed.accepted,false);assert.equal(failed.githubStatus,403);
    const second=await entry.fetch(request(draft(),'192.0.2.6'),env);
    assert.equal((await second.json()).accepted,false);
    assert.deepEqual(methods,['POST','GET']);
    assert.ok(!JSON.stringify(failed).includes('secret-token'));assert.ok(!JSON.stringify(failed).includes('C:'));
  } finally {globalThis.fetch=original;}
});

test('per-IP submission limit is shared before durable side effects and old health remains unchanged',async()=>{
  const env=deployed(); let calls=0;
  env.CHINESE_NAVIGATION.get=()=>({fetch:async()=>{calls++;return reply({accepted:false});}});
  const statuses=[];
  for(let i=0;i<6;i++) statuses.push((await entry.fetch(request(draft(),'192.0.2.99'),env)).status);
  assert.deepEqual(statuses,[200,200,200,200,200,429]);assert.equal(calls,5);
  const old=await entry.fetch(new Request('https://relay.invalid/'),env);
  assert.equal((await old.json()).service,'firegaze-relay');
});

test('reconciliation accepts only the configured author and exact marker, with no duplicate creation',async()=>{
  const original=globalThis.fetch;let payload,posts=0;
  globalThis.fetch=async(url,options={})=>{
    if(options.method==='POST'){posts++;payload=JSON.parse(options.body);throw Error('lost reply');}
    return reply({incomplete_results:false,total_count:2,items:[
      {number:72,html_url:'https://github.com/mock/repo/issues/72',body:payload.body,user:{login:'mock-bot[bot]'}},
      {number:73,html_url:'https://github.com/mock/repo/issues/73',body:payload.body,user:{login:'attacker'}}]});
  };
  try {
    const env=deployed();
    const value={...draft(),note:'```\n<!-- fake marker -->\n@mention'};
    assert.equal((await entry.fetch(request(value,'192.0.2.21'),env)).status,202);
    const recovered=await (await entry.fetch(request(value,'192.0.2.22'),env)).json();
    assert.equal(recovered.accepted,true);assert.equal(recovered.issueNumber,72);assert.equal(posts,1);
    assert.equal((payload.body.match(/```/g)??[]).length,2,'user notes cannot close the structured evidence fence');
  } finally {globalThis.fetch=original;}
});

test('official source reaches durable Issue receipt unchanged and duplicates do not create again',async()=>{
  const original=globalThis.fetch;
  const created=[];
  globalThis.fetch=async(url,options={})=>{
    assert.equal(String(url),'https://api.github.com/repos/mock/repo/issues');
    assert.equal(options.method,'POST');
    created.push(JSON.parse(options.body));
    const number=80+created.length;
    return reply({number,html_url:`https://github.com/mock/repo/issues/${number}`},201);
  };
  try {
    const env=deployed();
    const official={...draft(),identity:{...draft().identity,sourceUrl:'OFFICIAL'}};
    const first=await entry.fetch(request(official,'192.0.2.31'),env);
    const accepted=await first.json();
    assert.equal(first.status,200); assert.equal(accepted.accepted,true);
    assert.equal(accepted.issueUrl,'https://github.com/mock/repo/issues/81');
    const repeated=await (await entry.fetch(request({...official,note:'same identity'},'192.0.2.32'),env)).json();
    assert.equal(repeated.accepted,true); assert.equal(repeated.duplicate,true);
    assert.equal(repeated.submissionId,accepted.submissionId);
    assert.equal(repeated.issueUrl,accepted.issueUrl); assert.equal(created.length,1);
    const report=await (await entry.fetch(request({...official,action:'report-broken',entryId:'nav-official',revision:'r2'},
      '192.0.2.33'),env)).json();
    assert.equal(report.accepted,true); assert.equal(report.issueUrl,'https://github.com/mock/repo/issues/82');
    assert.notEqual(report.submissionId,accepted.submissionId); assert.equal(created.length,2);
    for(const body of created){
      assert.deepEqual(body.labels,['chinese-navigation','chinese-navigation-notify-pending']);
      const structured=body.body.match(/```json\n([\s\S]*?)\n```/);
      assert.ok(structured,'structured Issue payload missing');
      assert.equal(JSON.parse(structured[1]).identity.sourceUrl,'OFFICIAL');
    }
  } finally {globalThis.fetch=original;}
});


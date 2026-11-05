import relay, { allow, githubToken, githubJson, submissionContext, withinSubmission, REPO_DEFAULT } from './worker.js';
import { validateNavigationSubmission, navigationSubmissionKey, navigationFailure, NavigationSubmissionCoordinator, validIssue } from './chinese-navigation.mjs';

const MAX_BYTES = 16_384;
const PATH = '/chinese-navigation-submit';
const response = (body,status=200) => Response.json(body,{status,headers:{'Cache-Control':'no-store'}});
const repository = env => env.NAVIGATION_REPO || env.REPO || REPO_DEFAULT;
const configured = env => env.CHINESE_NAVIGATION && /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository(env))
  && /^[A-Za-z0-9_-]+(?:\[bot\])?$/.test(env.NAVIGATION_BOT_LOGIN ?? '')
  && (env.GITHUB_TOKEN || (env.APP_ID && env.APP_PRIVATE_KEY));
const unavailable = () => response(navigationFailure('navigation-unavailable','configuration',
  '中文导航投稿尚未配置完整，未创建 Issue。','请稍后重试，或联系维护者启用导航投稿。'),503);

async function readDraft(request) {
  if (Number(request.headers.get('Content-Length')) > MAX_BYTES) throw {code:'body-too-large'};
  const context=submissionContext();
  const raw=await withinSubmission(context,'request',async signal=>{
    const reader=request.body?.getReader();
    if(!reader)return '';
    const cancel=()=>{reader.cancel().catch(()=>{});};
    signal.addEventListener('abort',cancel,{once:true});
    try {
      const decoder=new TextDecoder('utf-8',{fatal:true});let total=0,result='';
      for (;;) {
        const {done,value}=await reader.read();
        if(done)return result+decoder.decode();
        total+=value.byteLength;
        if(total>MAX_BYTES){cancel();throw {relay:true,code:'body-too-large'};}
        result+=decoder.decode(value,{stream:true});
      }
    } finally {signal.removeEventListener('abort',cancel);reader.releaseLock();}
  });
  try {return JSON.parse(raw);} catch {throw {code:'invalid-json'};}
}

function requestError(error) {
  const large=error?.code==='body-too-large';
  const code=large?'body-too-large':error?.code==='invalid-json'?'invalid-json':'relay-unavailable';
  return response(navigationFailure(code,'request',large?'投稿超过16 KiB，未创建 Issue。'
    :code==='invalid-json'?'投稿不是有效JSON，未创建 Issue。':'请求未能完成，未取得受理凭据。',
    large?'请缩短说明和网址后重试。':'请检查网络或投稿格式；不要重复提交不同内容。'),large?413:code==='invalid-json'?400:503);
}

class NavigationGitHub {
  constructor(env){this.env=env;this.repo=repository(env);}
  async credentials(context){
    const token=await githubToken(this.env,this.repo,context);
    if(!token)throw Error('missing-server-credentials');
    return {Authorization:`Bearer ${token}`,Accept:'application/vnd.github+json','User-Agent':'firegaze-chinese-navigation',
      'Content-Type':'application/json','X-GitHub-Api-Version':'2022-11-28'};
  }
  async create({value,key,marker}){
    const context=submissionContext();
    const headers=await this.credentials(context);
    const structured=JSON.stringify(value,null,2).replace(/</g,'\\u003c').replace(/>/g,'\\u003e')
      .replace(/&/g,'\\u0026').replace(/`/g,'\\u0060');
    const body=`${marker}\n\n### FireGaze 中文导航投稿\n\n未核实线索；不是安装源或安全背书。审核通过前不得加入已验证索引。\n\n\`\`\`json\n${structured}\n\`\`\`\n`;
    return githubJson(`https://api.github.com/repos/${this.repo}/issues`,{method:'POST',headers,
      body:JSON.stringify({title:`[汉化导航投稿] ${value.action==='report-broken'?'失效报告':'中文入口'} · ${value.identity.internalName}`,
        body,labels:['chinese-navigation','chinese-navigation-notify-pending']})},context,'issue-create');
  }
  async find(key){
    const context=submissionContext();
    const headers=await this.credentials(context);
    const query=`repo:${this.repo} is:issue in:body ${key}`;
    const result=await githubJson(`https://api.github.com/search/issues?q=${encodeURIComponent(query)}&per_page=100`,
      {headers},context,'reconciliation');
    if(result.incomplete_results || !Array.isArray(result.items) || result.total_count>100)throw Error('incomplete-search');
    return result.items.filter(item=>validIssue(item,this.repo)
      && item.user?.login===this.env.NAVIGATION_BOT_LOGIN);
  }
}

export class ChineseNavigationSubmission {
  constructor(state,env) {
    this.env=env;
    this.coordinator=new NavigationSubmissionCoordinator(state.storage,new NavigationGitHub(env),repository(env));
  }
  async fetch(request) {
    if(request.method!=='POST')return response({accepted:false,errorCode:'method-not-allowed'},405);
    let input;
    try {input=await readDraft(request);} catch(error){return requestError(error);}
    const result=await this.coordinator.submit(input);
    return response(result,result.accepted?200:result.errorCode==='outcome-unknown'?202:
      result.errorCode==='invalid-submission'?400:503);
  }
}

export default {
  async fetch(request,env,ctx) {
    if(new URL(request.url).pathname!==PATH)return relay.fetch(request,env,ctx);
    if(request.method!=='POST')return response(navigationFailure('method-not-allowed','validation',
      '此入口仅接受主动提交的POST请求，未创建 Issue。','请使用插件详情中的投稿网址入口。'),405);
    if(!configured(env))return unavailable();
    if(!allow(request.headers.get('CF-Connecting-IP')??'unknown',5,'chinese-navigation'))
      return response(navigationFailure('rate-limited','request','投稿过于频繁，当前请求未新建 Issue。','请一分钟后使用同一投稿重试。'),429);
    let raw;
    try {raw=await readDraft(request);} catch(error){return requestError(error);}
    const validated=validateNavigationSubmission(raw);
    if(!validated.ok)return response(validated,400);
    try {
      const key=await navigationSubmissionKey(validated.value);
      const id=env.CHINESE_NAVIGATION.idFromName(`${repository(env)}:${key}`);
      return await env.CHINESE_NAVIGATION.get(id).fetch(new Request(`https://navigation.internal${PATH}`,{
        method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(validated.value)}));
    } catch {return response(navigationFailure('relay-unavailable','deduplication',
      '未取得持久受理回执；服务可能已经收到请求，尚未加入导航索引。','请保留相同投稿查询原请求，不要更换网址反复提交。'),503);}
  },
  scheduled(controller,env,ctx) { return relay.scheduled(controller,env,ctx); },
};

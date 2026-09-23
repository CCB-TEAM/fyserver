import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
  if(!await Admin.ensureAuth())return;Admin.mount('server-config','服务器配置');
  const $=id=>document.getElementById(id), query=new URLSearchParams(location.search), editing=query.get('key')||'';
  const incomplete=new Set(['account_linking_url','discount_products','front_page_data','most_popular_products','wc_qualifiers_1','wc_steam_url']);
  let raw={},schema={},comments={};
  function pretty(v){if(typeof v==='string')return v;try{return JSON.stringify(v,null,2)}catch(_){return String(v??'')}}
  function showInfo(key){const info=schema[key]||{};$('description').textContent=info.description||'该配置项暂无镜像注释。';$('send-to-client').textContent=info.sendToClient===true?'镜像默认：发送给客户端（Client 已启用）':info.sendToClient===false?'镜像默认：不发送给客户端（Client 已关闭）':'';$('reference').textContent=info.default!==undefined&&info.default!==''?'镜像参考默认：'+info.default:''}
  async function load(){const r=await Admin.api('/server-config');if(!r)return;try{raw=JSON.parse(r.raw||'{}')}catch(_){raw={}};schema=Object.fromEntries((r.schema||[]).map(x=>[x.key,x]));comments=r.comments||{};const exists=Object.prototype.hasOwnProperty.call(raw,editing);const keyInput=$('key');if(editing){keyInput.value=editing;keyInput.readOnly=true;keyInput.classList.add('config-key-readonly');$('page-title').textContent='编辑配置项';$('type').value=(schema[editing]||{}).type||({number:Number.isInteger(raw[editing])?'int':'double',boolean:'json'}[typeof raw[editing]]||((typeof raw[editing]==='object')?'json':'string'));$('value').value=exists?pretty(raw[editing]):'';$('description-input').value=Object.prototype.hasOwnProperty.call(comments,editing)?comments[editing]:(schema[editing]?.description||'');showInfo(editing);if(editing==='websocketurl'){$('type').disabled=true;$('value').readOnly=true;Admin.banner('websocketurl 的值由服务器生成；可在此修改注释。','info')}else if(!exists)Admin.banner('该项只有镜像参考定义，保存后会新增实际值。','info')}else{$('value').value='';showInfo('')};$('type').onchange=()=>{if(!editing&&$('type').value==='json'&&!$('value').value.trim())$('value').value='{}'};}
  function parseValue(type,text){if(type==='string')return text;if(type==='int'){if(!/^[+-]?\d+$/.test(text.trim()))throw new Error('整数类型必须填写整数');return Number.parseInt(text.trim(),10)}if(type==='double'){if(!/^[+-]?(?:\d+\.?\d*|\.\d+)$/.test(text.trim()))throw new Error('小数类型必须填写数字');return Number.parseFloat(text.trim())}try{return JSON.parse(text)}catch(e){throw new Error('JSON 格式错误：'+e.message)}}
  $('config-form').onsubmit=async e=>{e.preventDefault();const key=$('key').value.trim();if(!/^[A-Za-z0-9_]+$/.test(key)){Admin.banner('配置名只能包含英文字母、数字和下划线','err');return}let value;try{value=parseValue($('type').value,$('value').value)}catch(err){Admin.banner(err.message,'err');return}const description=$('description-input').value;const result=await Admin.api('/server-config/item',{method:'PUT',body:{key,value,description}});Admin.banner(result.message||'保存失败',result.ok?'ok':'err');if(result.ok)setTimeout(()=>location.href='/admin-ui/server-config.html',350)};
  await load();
  if(incomplete.has(editing))Admin.banner('镜像只保存了截断值或未提供完整默认值。请填写完整值，再回列表启用发送。','info');
})();

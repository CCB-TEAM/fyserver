import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
if(!await Admin.ensureAuth())return;Admin.mount('server-config','服务器配置');
const $=id=>document.getElementById(id);let data={},schemaMap={},flags={},comments={};
const incomplete=new Set(['account_linking_url','discount_products','front_page_data','most_popular_products','wc_qualifiers_1','wc_steam_url']);
function typeOf(v){return v===null?'null':Array.isArray(v)?'array':typeof v}
function textValue(v){return typeof v==='object'?JSON.stringify(v):String(v)}
function render(){
 const query=$('filter').value.trim().toLowerCase(),keys=Array.from(new Set(Object.keys(data).concat(Object.keys(schemaMap)))).filter(k=>{const i=schemaMap[k]||{},description=Object.prototype.hasOwnProperty.call(comments,k)?comments[k]:i.description;return !query||(k+' '+(description||'')+' '+(i.default||'')).toLowerCase().includes(query)});
 $('count').textContent=keys.length+' / '+Object.keys(data).length+' 项已配置 · '+Object.keys(schemaMap).length+' 项镜像参考';
 $('rows').innerHTML=keys.map(k=>{
  const info=schemaMap[k]||{},exists=Object.prototype.hasOwnProperty.call(data,k),v=exists?data[k]:undefined,kind=exists?typeOf(v):(info.type||'未设置'),display=exists?textValue(v):'未设置',enabled=flags[k]!==false,reference=info.truncated?'镜像默认值（页面已截断，不可直接使用）：'+(info.default||''):info.default!==undefined&&info.default!==''?'镜像参考默认：'+info.default:'',description=Object.prototype.hasOwnProperty.call(comments,k)?comments[k]:info.description;
  return '<tr><td class="config-actions"><div class="config-actions-inner"><a class="btn btn--outline btn--small" href="/admin-ui/server-config-edit.html?key='+encodeURIComponent(k)+'">编辑</a><button class="btn btn--danger btn--small delete-config" data-key="'+Admin.esc(k)+'" '+(!exists||k==='websocketurl'?'disabled title="未配置或自动生成，不可删除"':'')+'>删除</button></div></td><td><label class="switch"><input class="config-toggle" type="checkbox" data-key="'+Admin.esc(k)+'" '+(enabled?'checked':'')+' '+(!exists?'disabled title="请先新增配置项"':'')+'><span></span></label></td><td class="mono">'+Admin.esc(k)+(incomplete.has(k)&&!enabled?' <span class="chip chip--err">待填完整值</span>':'')+'</td><td>'+Admin.esc(kind)+'</td><td class="config-value" title="'+Admin.esc(display)+'">'+Admin.esc(display)+'</td><td class="config-description">'+Admin.esc(description||'')+(reference?'<small class="mirror-default">'+Admin.esc(reference)+'</small>':'')+'</td></tr>'
 }).join('')||'<tr><td class="empty" colspan="6">没有匹配的配置项</td></tr>';
}
async function load(){const r=await Admin.api('/server-config');if(!r)return;data=JSON.parse(r.raw||'{}');schemaMap=Object.fromEntries((r.schema||[]).map(x=>[x.key,x]));flags=r.flags||{};comments=r.comments||{};render()}
$('filter').oninput=render;$('reload').onclick=load;
$('rows').onclick=async e=>{
 const toggle=e.target.closest('.config-toggle');if(toggle){const key=toggle.dataset.key;if(toggle.checked&&incomplete.has(key)&&(data[key]===''||(data[key]&&typeof data[key]==='object'&&!Object.keys(data[key]).length))&&!confirm('该项仍是空占位值，确定发送给客户端？')){toggle.checked=false;return}const result=await Admin.api('/server-config/item/'+encodeURIComponent(key)+'/enabled',{method:'PUT',body:{enabled:toggle.checked}});if(!result.ok){toggle.checked=!toggle.checked;Admin.banner(result.message||'开关保存失败','err')}else{flags[key]=toggle.checked;Admin.banner(toggle.checked?'已启用配置项':'已关闭发送（值仍保留）','ok');render()}return}
 const del=e.target.closest('.delete-config');if(!del||del.disabled)return;const key=del.dataset.key;if(!confirm('删除配置项 '+key+'？如只想停止发送，请关闭启用开关。'))return;const result=await Admin.api('/server-config/item/'+encodeURIComponent(key),{method:'DELETE'});Admin.banner(result.message||'删除失败',result.ok?'ok':'err');if(result.ok)await load()
};
await load();
})();

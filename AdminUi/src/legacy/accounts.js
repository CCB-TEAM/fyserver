import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
  if(!await Admin.ensureAuth())return;
  await Admin.mount('accounts','后台用户');
  const $=id=>document.getElementById(id);
  const labels={players:'玩家管理',content:'内容管理',matches:'对局管理',serverConfig:'服务器配置',systemSettings:'系统设置',patchPaks:'Patch Pak 管理',permissions:'权限管理'};
  $('new-permissions').innerHTML=Object.entries(labels).map(([key,label])=>'<label><input type="checkbox" data-permission="'+key+'"> '+label+'</label>').join('');
  async function load(){
    const response=await Admin.api('/accounts');
    if(!response?.accounts){Admin.banner(response?.message||'没有查看后台用户的权限','err');$('create-form').hidden=true;return}
    const accounts=response.accounts,online=accounts.filter(a=>a.online).length;
    $('summary').innerHTML='<div class="stat-card"><div class="stat-card__label">后台账号</div><div class="stat-card__value">'+accounts.length+'</div><div class="stat-card__hint">包括 Owner</div></div><div class="stat-card"><div class="stat-card__label">当前在线</div><div class="stat-card__value">'+online+'</div><div class="stat-card__hint">最近两分钟有活动的会话</div></div>';
    $('accounts').innerHTML='<table class="md-table"><thead><tr><th>头像</th><th>账号名称</th><th>身份</th><th>在线状态</th><th>最后登录</th><th>最近活动</th><th>权限数</th><th>操作</th></tr></thead><tbody>'+accounts.map(a=>'<tr><td><img class="account-avatar" src="'+Admin.esc(a.avatarUrl||'/admin-ui/assets/admin-avatar-placeholder.svg')+'" alt=""></td><td><a href="/admin-ui/account-detail.html?id='+encodeURIComponent(a.id)+'">'+Admin.esc(a.username)+'</a></td><td>'+(a.isOwner?'Owner':'成员')+'</td><td>'+(a.online?'<span class="chip chip--ok">在线</span>':a.enabled?'<span class="chip chip--off">离线</span>':'<span class="chip chip--err">已禁用</span>')+'</td><td>'+Admin.esc(a.lastLoginAt?new Date(a.lastLoginAt).toLocaleString():'从未登录')+'</td><td>'+Admin.esc(a.lastSeenAt?new Date(a.lastSeenAt).toLocaleString():'—')+'</td><td>'+(a.isOwner?'全部':(a.permissions||[]).length)+'</td><td><a class="btn btn--small btn--outline" href="/admin-ui/account-detail.html?id='+encodeURIComponent(a.id)+'">查看详情</a></td></tr>').join('')+'</tbody></table>';
  }
  $('create-form').onsubmit=async event=>{event.preventDefault();const permissions=Array.from(document.querySelectorAll('[data-permission]:checked')).map(node=>node.dataset.permission);const result=await Admin.api('/accounts',{method:'POST',body:{username:$('new-username').value.trim(),password:$('new-password').value,permissions}});Admin.banner(result?.message||'操作失败',result?.ok?'ok':'err');if(result?.ok){$('create-form').reset();await load()}};
  await load();
  setInterval(()=>{if(document.visibilityState==='visible')load().catch(()=>{})},45000);
})();

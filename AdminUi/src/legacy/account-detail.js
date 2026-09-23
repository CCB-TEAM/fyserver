import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
  if(!await Admin.ensureAuth())return;
  await Admin.mount('accounts','后台账号详情');
  const id=new URLSearchParams(location.search).get('id');if(!id){Admin.banner('账号 ID 无效','err');return}
  const $=name=>document.getElementById(name);
  const labels={players:'玩家管理',content:'内容管理',matches:'对局与队列管理',serverConfig:'服务器配置',systemSettings:'系统设置',permissions:'权限管理'};
  const fmt=value=>value?new Date(value).toLocaleString():'—';
  const kv=rows=>rows.map(([key,value])=>'<dt>'+Admin.esc(key)+'</dt><dd>'+Admin.esc(value)+'</dd>').join('');
  const self=await Admin.api('/session');let account;
  async function load(){
    account=await Admin.api('/accounts/'+encodeURIComponent(id));
    if(!account?.id){Admin.banner(account?.message||'无法读取账号','err');return}
    $('title').textContent='后台账号 · '+account.username;
    $('actions-link').href='/admin-ui/account-actions.html?id='+encodeURIComponent(id);
    $('logins-link').href='/admin-ui/account-logins.html?id='+encodeURIComponent(id);
    $('summary').innerHTML=[['在线状态',account.online?'在线':'离线',account.activeSessions+' 个活跃会话'],['账号身份',account.isOwner?'Owner':'成员',account.isOwner?'全部权限':(account.permissions||[]).length+' 项权限'],['登录状态',account.enabled?'已启用':'已禁用',account.enabled?'允许登录后台':'禁止新登录及当前会话']].map(item=>'<div class="stat-card"><div class="stat-card__label">'+Admin.esc(item[0])+'</div><div class="stat-card__value">'+Admin.esc(item[1])+'</div><div class="stat-card__hint">'+Admin.esc(item[2])+'</div></div>').join('');
    $('info').innerHTML=kv([['账号名称',account.username],['账号 ID',account.id],['创建时间',fmt(account.createdAt)],['最后登录日期',account.lastLoginAt?fmt(account.lastLoginAt):'从未登录'],['最后登录 IP',account.lastLoginIp||'—'],['最近活动',fmt(account.lastSeenAt)]]);
    $('current-password-field').hidden=!account.isOwner;
    $('current-password').required=account.isOwner;
    $('danger-card').hidden=account.isOwner;
    $('permissions-form').hidden=account.isOwner;
    $('owner-note').textContent=account.isOwner?'Owner 默认拥有全部权限，不可禁用或删除。':'';
    $('enabled').checked=!!account.enabled;
    $('permissions').innerHTML=Object.entries(labels).map(([key,label])=>'<label><input type="checkbox" data-permission="'+key+'" '+((account.permissions||[]).includes(key)?'checked':'')+'> '+label+'</label>').join('');
  }
  $('permissions-form').onsubmit=async event=>{event.preventDefault();const permissions=Array.from(document.querySelectorAll('[data-permission]:checked')).map(node=>node.dataset.permission);const result=await Admin.api('/accounts/'+encodeURIComponent(id),{method:'PUT',body:{enabled:$('enabled').checked,permissions}});Admin.banner(result?.message||'保存失败',result?.ok?'ok':'err');if(result?.ok)await load()};
  $('password-form').onsubmit=async event=>{event.preventDefault();if($('new-password').value!==$('confirm-password').value){Admin.banner('两次输入的新密码不一致','err');return}const result=await Admin.api('/accounts/'+encodeURIComponent(id)+'/reset-password',{method:'POST',body:{newPassword:$('new-password').value,currentPassword:account.isOwner?$('current-password').value:null}});Admin.banner(result?.message||'重置失败',result?.ok?'ok':'err');if(result?.ok){$('password-form').reset();if(self.username===account.username)location.href='/admin-ui/login.html'}};
  $('delete-account').onclick=async()=>{if(prompt('删除不可恢复，请输入账号名称确认：')!==account.username)return;const result=await Admin.api('/accounts/'+encodeURIComponent(id),{method:'DELETE'});if(result?.ok)location.href='/admin-ui/accounts.html';else Admin.banner(result?.message||'删除失败','err')};
  await load();
})();

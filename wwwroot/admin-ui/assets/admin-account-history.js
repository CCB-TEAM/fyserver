(async function(){
  if(!await Admin.ensureAuth())return;
  await Admin.mount('accounts','后台用户');
  const id=new URLSearchParams(location.search).get('id');
  const kind=document.body.dataset.history;
  if(!id||!['actions','logins'].includes(kind)){Admin.banner('页面参数无效','err');return}
  const $=name=>document.getElementById(name);
  $('back').href='/admin-ui/account-detail.html?id='+encodeURIComponent(id);
  const account=await Admin.api('/accounts/'+encodeURIComponent(id));
  if(!account?.id){Admin.banner(account?.message||'账号不存在','err');return}
  $('title').textContent=account.username+(kind==='actions'?' · 历史操作':' · 登录与 IP');
  let page=1;
  async function load(){
    const data=await Admin.api('/accounts/'+encodeURIComponent(id)+'/'+kind+'?page='+page);
    if(!data?.entries){Admin.banner(data?.message||'读取历史失败','err');return}
    const rows=data.entries;
    const headers=kind==='actions'?'<th>时间</th><th>操作</th><th>路径</th><th>结果</th><th>来源 IP</th>':'<th>时间</th><th>结果</th><th>来源 IP</th>';
    const cells=kind==='actions'?entry=>'<td>'+Admin.esc(new Date(entry.time).toLocaleString())+'</td><td>'+Admin.esc(entry.action)+'</td><td class="mono">'+Admin.esc(entry.path)+'</td><td>'+Admin.esc(entry.status)+'</td><td class="mono">'+Admin.esc(entry.address)+'</td>':entry=>'<td>'+Admin.esc(new Date(entry.time).toLocaleString())+'</td><td>'+(entry.success?'<span class="chip chip--ok">成功</span>':'<span class="chip chip--err">失败</span>')+'</td><td class="mono">'+Admin.esc(entry.address)+'</td>';
    $('history').innerHTML='<table class="md-table"><thead><tr>'+headers+'</tr></thead><tbody>'+(rows.length?rows.map(entry=>'<tr>'+cells(entry)+'</tr>').join(''):'<tr><td colspan="'+(kind==='actions'?5:3)+'" class="empty">暂无记录</td></tr>')+'</tbody></table>';
    const totalPages=Math.max(1,Math.ceil((data.total||0)/100));
    $('page-indicator').textContent='第 '+page+' / '+totalPages+' 页 · 共 '+(data.total||0)+' 条';
    $('previous').disabled=page<=1;
    $('next').disabled=page>=totalPages;
  }
  $('previous').onclick=async()=>{page--;await load()};
  $('next').onclick=async()=>{page++;await load()};
  await load();
})();

import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('users', '玩家详情');
  const id = Number(new URLSearchParams(location.search).get('id'));
  if (!Number.isInteger(id) || id <= 0) { Admin.banner('玩家 ID 无效', 'err'); return; }
  const $ = id => document.getElementById(id);
  const fmt = value => value ? new Date(value).toLocaleString() : '—';
  const kv = rows => rows.map(([key, value]) => '<dt>' + Admin.esc(key) + '</dt><dd>' + Admin.esc(value ?? '—') + '</dd>').join('');
  const roleDetails = {
    spectator: ['观战者', '允许客户端使用观战相关功能'],
    vip: ['VIP', '客户端 VIP 身份标记'],
    dev: ['开发者', '客户端开发者功能标记'],
    internal_tester: ['内部测试员', '内部测试功能标记'],
    support: ['客服支持', '客服相关客户端身份标记'],
    tester: ['测试员', '通用测试员身份标记']
  };
  const session = await Admin.api('/session');
  const canManageRoles = (session?.permissions || []).includes('players') && (session?.permissions || []).includes('permissions');
  let player;
  async function load() {
    player = await Admin.api('/users/' + id);
    if (!player || !player.id) { Admin.banner('无法读取玩家资料', 'err'); return; }
    const tag = String(player.tag ?? 0).padStart(4, '0').slice(-4);
    $('title').textContent = player.name + '#' + tag + ' · #' + player.id;
    $('summary').innerHTML = [
      ['账号状态', player.banned ? '已封禁' : '正常', player.banned ? player.banDescription : '可登录'],
      ['连接状态', player.online ? '在线' : '离线', player.activeMatchId ? '对局 #' + player.activeMatchId : '无进行中对局'],
      ['卡组', (player.decks || []).length, '已保存的卡组'],
      ['装备', player.equippedItemCount, '当前装备物品']
    ].map(x => '<div class="stat-card"><div class="stat-card__label">' + Admin.esc(x[0]) + '</div><div class="stat-card__value">' + Admin.esc(x[1]) + '</div><div class="stat-card__hint">' + Admin.esc(x[2]) + '</div></div>').join('');
    $('identity').innerHTML = kv([['玩家身份', player.name + '#' + tag], ['玩家 ID', player.id], ['注册时间', player.createdAt], ['最近登录', fmt(player.lastLoginAt)], ['登录 IP', player.lastLoginIp || '暂无记录'], ['登录设备', player.lastLoginDevice || '暂无记录'], ['资料更新时间', player.updatedAt]]);
    $('ban-status').innerHTML = kv([['状态', player.banned ? '已封禁' : '正常'], ['封禁时间', fmt(player.bannedAt)], ['解封时间', player.banned ? (player.banExpiresAt ? fmt(player.banExpiresAt) : '永久') : '—'], ['玩家看到的说明', player.banned ? player.banDescription : '—']]);
    $('name').value = player.name || '';
    $('tag').value = String(player.tag).padStart(4, '0');
    $('locale').value = player.locale || '';
    $('gold').value = player.gold;
    $('diamonds').value = player.diamonds;
    $('roles-panel').hidden = false;
    const availableRoles = Array.isArray(player.availableRoles) ? player.availableRoles : Object.keys(roleDetails);
    const selectedRoles = new Set(Array.isArray(player.roles) ? player.roles : []);
    $('roles-list').innerHTML = availableRoles.map(role => {
      const [label, description] = roleDetails[role] || [role, '客户端角色'];
      return '<label class="player-role-option"><input type="checkbox" data-player-role="' + Admin.esc(role) + '" ' + (selectedRoles.has(role) ? 'checked ' : '') + (!canManageRoles ? 'disabled ' : '') + '><span><b>' + Admin.esc(label) + ' <span class="mono">' + Admin.esc(role) + '</span></b><small>' + Admin.esc(description) + '</small></span></label>';
    }).join('');
    $('roles-help').textContent = canManageRoles
      ? '修改玩家角色需要同时具备“玩家管理”和“权限管理”权限。'
      : '当前账号仅可查看角色；编辑需要“玩家管理”和“权限管理”两项权限。';
    $('save-roles').hidden = !canManageRoles;
    $('reason').value = player.banned ? player.banReason || '' : '';
    $('permanent').checked = !!(player.banned && !player.banExpiresAt);
    $('expiry').disabled = $('permanent').checked;
    const localExpiry = player.banExpiresAt ? new Date(player.banExpiresAt) : new Date(Date.now() + 7 * 86400000);
    $('expiry').value = new Date(localExpiry.getTime() - localExpiry.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
    $('unban').disabled = !player.banned;
    const decks = player.decks || [];
    $('decks').innerHTML = '<table class="md-table"><thead><tr><th>ID</th><th>名称</th><th>主阵营</th><th>副阵营</th><th>收藏</th><th>最后使用</th></tr></thead><tbody>' +
      (decks.length ? decks.map(d => '<tr><td class="mono">' + d.id + '</td><td>' + Admin.esc(d.name) + '</td><td>' + Admin.esc(d.mainFaction) + '</td><td>' + Admin.esc(d.allyFaction) + '</td><td>' + (d.favorite ? '是' : '否') + '</td><td>' + Admin.esc(d.lastPlayed) + '</td></tr>').join('') : '<tr><td colspan="6" class="empty">暂无卡组</td></tr>') + '</tbody></table>';
    const recent = player.recentMatches || [];
    const displayPlayer = (name, playerTag) => (name || '<anon>') + '#' + String(playerTag ?? '0000').replace(/\D/g, '').slice(-4).padStart(4, '0');
    $('recent-matches').innerHTML = '<table class="md-table"><thead><tr><th>对局</th><th>状态</th><th>模式</th><th>开始时间</th><th>双方玩家</th><th>回合 / 动作</th></tr></thead><tbody>' +
      (recent.length ? recent.map(m => '<tr><td class="mono">#' + m.matchId + '</td><td>' + Admin.esc(m.status) + '</td><td>' + Admin.esc(m.matchType) + '</td><td>' + Admin.esc(fmt(m.startedAt)) + '</td><td><span class="mono">#' + m.leftPlayerId + '</span> ' + Admin.esc(displayPlayer(m.leftPlayerName, m.leftPlayerTag)) + '<br><span class="mono">#' + m.rightPlayerId + '</span> ' + Admin.esc(displayPlayer(m.rightPlayerName, m.rightPlayerTag)) + '</td><td>' + m.turns + ' / ' + m.actionCount + '</td></tr>').join('') : '<tr><td colspan="6" class="empty">暂无已持久化的对局记录</td></tr>') + '</tbody></table>';
  }
  async function action(path, options) { const result = await Admin.api('/users/' + id + path, options); Admin.banner(result?.message || '操作完成', result?.ok ? 'ok' : 'err'); if (result?.ok) await load(); return result; }
  $('profile-form').onsubmit = async event => { event.preventDefault(); await action('/profile', { method: 'PUT', body: { name: $('name').value, tag: Number($('tag').value), locale: $('locale').value } }); };
  $('wallet-form').onsubmit = async event => { event.preventDefault(); await action('/wallet', { method: 'PUT', body: { gold: Number($('gold').value), diamonds: Number($('diamonds').value) } }); };
  $('save-roles').onclick = async () => {
    const roles = Array.from(document.querySelectorAll('[data-player-role]:checked')).map(input => input.dataset.playerRole);
    const result = await Admin.api('/users/' + id + '/roles', { method: 'PUT', body: { roles } });
    Admin.banner(result?.message || '保存角色失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) await load();
  };
  $('permanent').onchange = () => { $('expiry').disabled = $('permanent').checked; };
  $('ban-form').onsubmit = async event => {
    event.preventDefault();
    const expiresAt = $('permanent').checked ? null : ($('expiry').value ? new Date($('expiry').value).toISOString() : null);
    if (!expiresAt && !$('permanent').checked) { Admin.banner('请选择解封时间或勾选永久封禁', 'err'); return; }
    if (!confirm('确认封禁玩家 #' + id + '？玩家当前连接将被断开。')) return;
    await action('/ban', { method: 'POST', body: { reason: $('reason').value, expiresAt } });
  };
  $('unban').onclick = async () => { if (confirm('确认解封此玩家？')) await action('/unban', { method: 'POST' }); };
  $('kick').onclick = async () => { const reason = prompt('踢出原因（可留空）', '您已被服务器断开连接'); if (reason !== null) await action('/kick?reason=' + encodeURIComponent(reason), { method: 'POST' }); };
  $('delete-user').onclick = async () => { if (prompt('删除不可撤销。请输入玩家 ID 确认：') !== String(id)) return; const result = await Admin.api('/users/' + id, { method: 'DELETE' }); if (result?.ok) location.href = '/admin-ui/users.html'; else Admin.banner(result?.message || '删除失败', 'err'); };
  await load();
})();

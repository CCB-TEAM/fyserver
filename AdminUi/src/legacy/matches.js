import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('matches', '对局监控');

  async function load() {
    const data = await Admin.api('/matches');
    if (!data) return;
    const matches = data.activeMatches || [];
    document.getElementById('summary').textContent =
      '进行中的真人对局 ' + matches.length + ' 条 · 队列等待玩家 ' +
      (data.queues || []).reduce((sum, q) => sum + (q.players || []).length, 0) + ' 人 · 在线连接 ' + data.onlineCount + ' 条';
    document.getElementById('matches').innerHTML = '<table class="md-table"><thead><tr>' +
      '<th>对局 ID</th><th>模式</th><th>状态</th><th>回合</th><th>动作数</th><th>左侧</th><th>右侧</th><th>操作</th>' +
      '</tr></thead><tbody>' +
      (matches.length === 0 ? '<tr><td colspan="8" class="empty">当前没有进行中的对局</td></tr>' : matches.map(m => {
        function side(id, displayName, status, online) {
          return '<span class="mono">#' + id + '</span> ' + Admin.esc(displayName) +
            (online ? ' <span class="chip chip--ok">在线</span>' : '') +
            ' <span class="muted">' + Admin.esc(status) + '</span>';
        }
        return '<tr><td class="mono">' + m.matchId + '</td><td>' + Admin.esc(m.matchType) + '</td>' +
          '<td>' + Admin.esc(m.status) + '</td><td>' + m.currentTurn + '</td><td>' + m.actionCount + '</td>' +
          '<td>' + side(m.leftPlayerId, m.leftPlayerName, m.leftStatus, m.leftOnline) + '</td>' +
          '<td>' + side(m.rightPlayerId, m.rightPlayerName, m.rightStatus, m.rightOnline) + '</td>' +
          '<td><button class="btn btn--small btn--danger" data-remove="' + m.matchId + '">移除</button></td></tr>';
      }).join('')) + '</tbody></table>';

    const queues = data.queues || [];
    document.getElementById('queues').innerHTML = '<table class="md-table"><thead><tr><th>队列</th><th>等待玩家</th></tr></thead><tbody>' +
      (queues.length === 0 ? '<tr><td colspan="2" class="empty">所有队列均为空</td></tr>' : queues.map(q =>
        '<tr><td>' + Admin.esc(q.name) + '（' + (q.players || []).length + '）</td><td>' +
        (q.players || []).map(p => '<span class="chip chip--off mono" style="margin-right:6px">#' + p.playerId + ' ' + Admin.esc(p.name) + '</span>').join('') +
        '</td></tr>').join('')) + '</tbody></table>';
  }

  document.getElementById('refresh').addEventListener('click', load);
  document.getElementById('clear-queues').addEventListener('click', async () => {
    if (!confirm('确认清空全部匹配队列？等待中的玩家需要重新加入。')) return;
    const result = await Admin.api('/queues/clear', { method: 'POST' });
    Admin.banner(result?.message || '完成', result?.ok ? 'ok' : 'err');
    await load();
  });
  document.addEventListener('click', async event => {
    const button = event.target.closest('[data-remove]');
    if (!button) return;
    const id = button.getAttribute('data-remove');
    if (!confirm('确认移除对局 ' + id + '？两侧玩家会被移出该对局。')) return;
    const result = await Admin.api('/matches/' + id + '/remove', { method: 'POST' });
    Admin.banner(result?.message || '完成', result?.ok ? 'ok' : 'err');
    await load();
  });
  await load();
})();

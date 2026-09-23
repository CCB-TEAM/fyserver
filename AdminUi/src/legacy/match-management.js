import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('match-management', '对局管理');
  const pageSize = 25;
  let page = 1;
  let total = 0;
  let openedId = 0;
  let actionCursor = 0;
  let cursorStack = [];
  const status = document.getElementById('history-status');
  const playerName = document.getElementById('history-player-name');
  const playerId = document.getElementById('history-player-id');
  const padTag = tag => String(tag ?? '0000').replace(/\D/g, '').slice(-4).padStart(4, '0');
  const display = (name, tag) => (name || '<anon>') + '#' + padTag(tag);
  const bytes = value => {
    let size = Number(value) || 0;
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let unit = 0;
    while (size >= 1024 && unit < units.length - 1) { size /= 1024; unit++; }
    return size.toFixed(unit === 0 ? 0 : 1) + ' ' + units[unit];
  };
  const date = value => value ? new Date(value).toLocaleString() : '—';

  async function loadStorage() {
    const data = await Admin.api('/matches/history/storage');
    if (!data) return;
    document.getElementById('history-storage-label').textContent = data.configured
      ? data.provider + ' · ' + data.location : '对局数据库尚未配置';
    document.getElementById('history-storage').innerHTML = [
      ['持久化对局', data.matchCount ?? 0], ['记录动作', data.actionCount ?? 0], ['数据体积', bytes(data.dataBytes)]
    ].map(([label, value]) => '<div class="metric-card"><div><small>' + Admin.esc(label) + '</small><strong>' + Admin.esc(value) + '</strong></div></div>').join('');
  }

  async function loadHistory() {
    const query = new URLSearchParams({ page: String(page), pageSize: String(pageSize), status: status.value });
    const name = playerName.value.trim();
    const id = Number(playerId.value) || 0;
    if (name) query.set('playerName', name);
    if (id > 0) query.set('playerId', String(id));
    const data = await Admin.api('/matches/history?' + query.toString());
    if (!data) return;
    total = data.total || 0;
    const pages = Math.max(1, Math.ceil(total / pageSize));
    document.getElementById('history-summary').textContent = '共 ' + total + ' 条持久化对局记录；可按玩家名称或 ID 筛选。';
    document.getElementById('history-page').textContent = '第 ' + page + ' / ' + pages + ' 页';
    document.getElementById('history-prev').disabled = page <= 1;
    document.getElementById('history-next').disabled = page >= pages;
    const rows = data.matches || [];
    document.getElementById('history-table').innerHTML = '<table class="md-table"><thead><tr><th>ID</th><th>状态</th><th>模式</th><th>开始 / 结束</th><th>玩家</th><th>回合</th><th>动作</th><th>操作</th></tr></thead><tbody>' +
      (rows.length ? rows.map(match => '<tr><td class="mono">' + match.matchId + '</td><td>' + Admin.esc(match.status) + '</td><td>' + Admin.esc(match.matchType) + '</td><td>' + Admin.esc(date(match.startedAt)) + '<br><span class="muted">' + Admin.esc(date(match.completedAt)) + '</span></td><td><span class="mono">#' + match.leftPlayerId + '</span> ' + Admin.esc(display(match.leftPlayerName, match.leftPlayerTag)) + '<br><span class="mono">#' + match.rightPlayerId + '</span> ' + Admin.esc(display(match.rightPlayerName, match.rightPlayerTag)) + '</td><td>' + match.turns + '</td><td>' + match.actionCount + '</td><td class="config-actions"><div class="config-actions-inner"><button class="btn btn--small btn--outline" data-history-view="' + match.matchId + '">查看数据</button><button class="btn btn--small btn--danger" data-history-delete="' + match.matchId + '">删除</button></div></td></tr>').join('') : '<tr><td colspan="8" class="empty">没有符合筛选条件的持久化记录</td></tr>') + '</tbody></table>';
  }

  async function loadActions(afterActionId) {
    const data = await Admin.api('/matches/history/' + openedId + '/actions?afterActionId=' + afterActionId + '&limit=250');
    if (!data) return;
    document.getElementById('history-actions').textContent = JSON.stringify(data.actions || [], null, 2);
    document.getElementById('actions-page-label').textContent = (data.actions || []).length + ' 条动作 · 下一游标 ' + data.nextActionId + (data.hasMore ? ' · 还有更多' : ' · 已到末尾');
    document.getElementById('actions-prev').disabled = cursorStack.length === 0;
    document.getElementById('actions-next').disabled = !data.hasMore;
    document.getElementById('actions-next').dataset.nextCursor = String(data.nextActionId);
  }
  async function openHistory(id) {
    const detail = await Admin.api('/matches/history/' + id);
    if (!detail) return;
    openedId = id;
    actionCursor = 0;
    cursorStack = [];
    document.getElementById('history-detail-title').textContent = '对局 #' + id + ' · 持久化详情';
    document.getElementById('history-starting-info').textContent = JSON.stringify({ summary: detail.summary, startingInfo: detail.startingInfo }, null, 2);
    document.getElementById('history-detail').hidden = false;
    await loadActions(0);
    document.getElementById('history-detail').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  document.getElementById('history-refresh').onclick = async () => { await Promise.all([loadStorage(), loadHistory()]); };
  document.getElementById('history-search').onclick = () => { page = 1; loadHistory(); };
  playerName.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); page = 1; loadHistory(); } });
  playerId.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); page = 1; loadHistory(); } });
  status.onchange = () => { page = 1; loadHistory(); };
  document.getElementById('history-prev').onclick = () => { if (page > 1) { page--; loadHistory(); } };
  document.getElementById('history-next').onclick = () => { if (page * pageSize < total) { page++; loadHistory(); } };
  document.getElementById('history-detail-close').onclick = () => { document.getElementById('history-detail').hidden = true; };
  document.getElementById('actions-next').onclick = async event => {
    const next = Number(event.currentTarget.dataset.nextCursor);
    if (!Number.isFinite(next) || next <= actionCursor) return;
    cursorStack.push(actionCursor);
    actionCursor = next;
    await loadActions(actionCursor);
  };
  document.getElementById('actions-prev').onclick = async () => {
    if (!cursorStack.length) return;
    actionCursor = cursorStack.pop();
    await loadActions(actionCursor);
  };
  document.getElementById('history-table').addEventListener('click', async event => {
    const view = event.target.closest('[data-history-view]');
    if (view) { await openHistory(Number(view.dataset.historyView)); return; }
    const remove = event.target.closest('[data-history-delete]');
    if (!remove) return;
    const id = Number(remove.dataset.historyDelete);
    if (!confirm('确定永久删除对局 #' + id + ' 的快照和全部动作吗？此操作不能撤销。')) return;
    const result = await Admin.api('/matches/history/' + id, { method: 'DELETE' });
    Admin.banner(result?.message || '删除失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) {
      if (openedId === id) document.getElementById('history-detail').hidden = true;
      await Promise.all([loadStorage(), loadHistory()]);
    }
  });
  await Promise.all([loadStorage(), loadHistory()]);
})();

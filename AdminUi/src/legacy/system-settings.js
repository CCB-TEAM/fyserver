import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('system-settings', '系统设置');
  const $ = id => document.getElementById(id);

  function renderNetwork(d) {
    $('listen-ip').value = d.listenIp;
    $('listen-port').value = d.listenPort;
    $('public-ip').value = d.publicIp;
    $('public-port').value = d.publicPort ?? '';
    $('public-scheme').value = d.publicScheme || 'http';
    $('restart-state').textContent = d.restartRequired
      ? '已保存的地址与当前运行地址不同，需要重启服务器后生效。'
      : '已保存配置与当前运行配置一致。';
    const port = value => value == null || value === '' ? '不拼接（标准端口）' : value;
    const rows = [['监听 IP', d.activeListenIp, d.listenIp], ['监听端口', d.activeListenPort, d.listenPort],
      ['对外协议', d.activePublicScheme || 'http', d.publicScheme || 'http'], ['对外 IP / 域名', d.activePublicIp, d.publicIp],
      ['对外端口', port(d.activePublicPort), port(d.publicPort)]];
    $('comparison').innerHTML = rows.map(r => '<tr><td>' + Admin.esc(r[0]) + '</td><td class="mono">' + Admin.esc(r[1]) + '</td><td class="mono">' + Admin.esc(r[2]) + '</td></tr>').join('');
  }

  async function loadNetwork() {
    const d = await Admin.api('/system-settings');
    if (d && d.listenIp !== undefined) renderNetwork(d);
    else Admin.banner(d?.message || '读取系统设置失败', 'err');
  }

  async function loadRetention() {
    const d = await Admin.api('/system-settings/match-retention');
    if (!d || !d.mode) { Admin.banner(d?.message || '读取对局保留策略失败', 'err'); return; }
    $('retention-mode').value = d.mode;
    $('retention-days').value = d.keepDays;
    $('retention-count').value = d.keepCount;
    $('cleanup-day').value = d.cleanupDayUtc;
    $('cleanup-hour').value = d.cleanupHourUtc;
  }

  let initialLibrary = { mode: 'selected', allGold: false, defaultCards: [] };
  async function loadPlayerLibrary() {
    const d = await Admin.api('/system-settings/player-library');
    if (!d || !Array.isArray(d.defaultCards)) { Admin.banner(d?.message || '读取新玩家卡牌设置失败', 'err'); return; }
    initialLibrary = d;
    $('new-player-card-mode').value = d.mode || 'selected';
    $('new-player-all-gold').value = String(!!d.allGold);
    renderDefaultCards();
  }
  function renderDefaultCards() {
    const rows = initialLibrary.defaultCards || [];
    $('default-library-card-list').innerHTML = '<table class="md-table"><thead><tr><th>卡牌</th><th>卡组</th><th>类型</th><th>Kredits</th><th>数量</th><th>操作</th></tr></thead><tbody>' +
      (rows.length ? rows.map((card, index) => '<tr><td><strong>' + Admin.esc(card.name || card.cardId) + '</strong><br><small class="muted mono">' + Admin.esc(card.cardId) + '</small></td><td>' + Admin.esc(card.cardSet || '—') + '</td><td>' + Admin.esc(card.type || '—') + '</td><td>' + Admin.esc(card.kredits ?? '—') + '</td><td><input type="number" min="1" max="1000" value="' + Number(card.count || 1) + '" data-default-count="' + index + '" aria-label="默认数量"></td><td><button type="button" class="btn btn--outline btn--small" data-remove-default="' + index + '">移除</button></td></tr>').join('') : '<tr><td colspan="6" class="muted">尚未配置默认卡牌，可到“卡牌列表”中筛选并批量加入。</td></tr>') +
      '</tbody></table>';
    document.querySelectorAll('[data-default-count]').forEach(input => input.onchange = () => {
      const item = initialLibrary.defaultCards[Number(input.dataset.defaultCount)];
      if (item) item.count = Number(input.value);
    });
    document.querySelectorAll('[data-remove-default]').forEach(button => button.onclick = () => {
      initialLibrary.defaultCards.splice(Number(button.dataset.removeDefault), 1);
      renderDefaultCards();
    });
  }

  $('reload').onclick = loadNetwork;
  $('settings-form').onsubmit = async e => {
    e.preventDefault();
    const publicPort = $('public-port').value.trim();
    const body = { listenIp: $('listen-ip').value.trim(), listenPort: Number($('listen-port').value), publicIp: $('public-ip').value.trim(), publicPort: publicPort || null, publicScheme: $('public-scheme').value };
    if (!confirm('保存后需要重启服务器才会生效。确定保存网络地址？')) return;
    $('save').disabled = true;
    try {
      const r = await Admin.api('/system-settings', { method: 'PUT', body });
      Admin.banner(r?.message || '保存失败', r?.ok ? 'ok' : 'err');
      if (r?.ok) await loadNetwork();
    } finally { $('save').disabled = false; }
  };

  $('match-retention-form').onsubmit = async e => {
    e.preventDefault();
    const body = { mode: $('retention-mode').value, keepDays: Number($('retention-days').value), keepCount: Number($('retention-count').value), cleanupDayUtc: Number($('cleanup-day').value), cleanupHourUtc: Number($('cleanup-hour').value) };
    $('retention-save').disabled = true;
    try {
      const r = await Admin.api('/system-settings/match-retention', { method: 'PUT', body });
      Admin.banner(r?.message || '保存失败', r?.ok ? 'ok' : 'err');
      if (r?.ok) await loadRetention();
    } finally { $('retention-save').disabled = false; }
  };

  $('player-library-form').onsubmit = async e => {
    e.preventDefault();
    const defaultCards = (initialLibrary.defaultCards || []).map(card => ({ cardId: card.cardId, count: Number(card.count) }));
    const body = { mode: $('new-player-card-mode').value, allGold: $('new-player-all-gold').value === 'true', defaultCards };
    $('player-library-save').disabled = true;
    try {
      const result = await Admin.api('/system-settings/player-library', { method: 'PUT', body });
      Admin.banner(result?.message || '保存失败', result?.ok ? 'ok' : 'err');
      if (result?.ok) await loadPlayerLibrary();
    } finally { $('player-library-save').disabled = false; }
  };

  await Promise.all([loadNetwork(), loadRetention(), loadPlayerLibrary()]);
})();

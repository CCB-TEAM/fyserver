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

  await Promise.all([loadNetwork(), loadRetention()]);
})();

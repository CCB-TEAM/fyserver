import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  const $ = id => document.getElementById(id);
  const session = await Admin.api('/session');
  if (!session) return;

  const firstRun = !session.authorized && !session.initialized && !session.databaseConfigured && session.loopback;
  if (!firstRun) {
    if (!await Admin.ensureAuth()) return;
    if (!session.isOwner) { Admin.banner('只有 Owner 可以完成玩家数据库初始化', 'err'); return; }
  }

  Admin.mount('system-settings', firstRun ? '首次初始化' : '玩家数据库初始化');
  const provider = $('provider');

  function toggle() {
    const remote = provider.value !== 'local';
    document.querySelectorAll('.remote').forEach(element => { element.hidden = !remote; });
    ['host', 'port', 'database', 'username'].forEach(id => { $(id).required = remote; });
    if (remote && !$('port').value) $('port').value = provider.value === 'mysql' ? 3306 : 5432;
  }

  provider.onchange = () => {
    $('port').value = provider.value === 'mysql' ? 3306 : provider.value === 'postgresql' ? 5432 : '';
    toggle();
  };

  async function load() {
    if (firstRun) {
      provider.value = 'local';
      $('state').textContent = '首次启动：先选择并初始化玩家数据存储，之后才会进入 Owner 账号设置。';
      toggle();
      return;
    }
    const data = await Admin.api('/database');
    if (!data) return;
    provider.value = data.provider || 'local';
    $('host').value = data.host || '127.0.0.1';
    $('port').value = data.port || (provider.value === 'mysql' ? 3306 : provider.value === 'postgresql' ? 5432 : '');
    $('database').value = data.database || 'fyserver';
    $('username').value = data.username || 'fyserver';
    $('require-ssl').value = String(data.requireSsl !== false);
    $('state').textContent = data.ready ? '玩家数据库已就绪，服务器正在正常提供服务。' :
      (data.initializationError ? '初始化失败：' + data.initializationError : '尚未完成配置，游戏接口当前返回 503。');
    toggle();
  }

  $('reload').onclick = load;
  $('database-form').onsubmit = async event => {
    event.preventDefault();
    $('save').disabled = true;
    const body = {
      provider: provider.value,
      host: $('host').value.trim(),
      port: Number($('port').value),
      database: $('database').value.trim(),
      username: $('username').value.trim(),
      password: $('password').value,
      requireSsl: $('require-ssl').value === 'true'
    };
    try {
      const result = await Admin.api('/database/configure', { method: 'POST', body });
      Admin.banner(result?.message || '初始化失败', result?.ok ? 'ok' : 'err');
      if (result?.ok) {
        $('password').value = '';
        setTimeout(() => location.replace(firstRun ? '/admin-ui/login.html' : '/admin-ui/index.html'), 600);
      }
    } finally { $('save').disabled = false; }
  };

  await load();
})();

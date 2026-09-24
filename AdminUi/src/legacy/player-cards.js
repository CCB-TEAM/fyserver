import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  Admin.mount('player-cards', '新玩家卡牌');
  const $ = id => document.getElementById(id);
  const pageSize = 50;
  let page = 1;
  let total = 0;
  let policy = { mode: 'selected', allGold: false, defaultCards: [] };

  function filters() {
    return { q: $('card-q').value.trim(), cardSet: $('card-set').value, type: $('card-type').value,
      minKredits: $('card-min-kredits').value, maxKredits: $('card-max-kredits').value };
  }
  function queryString(values) {
    const params = new URLSearchParams();
    for (const [key, value] of Object.entries(values)) if (value !== '' && value != null) params.set(key, value);
    return params.toString();
  }
  function setOptions(select, values, firstLabel) {
    const previous = select.value;
    select.innerHTML = '<option value="">' + Admin.esc(firstLabel) + '</option>' + values.map(value => '<option value="' + Admin.esc(value) + '">' + Admin.esc(value) + '</option>').join('');
    if (values.includes(previous)) select.value = previous;
  }
  function renderDefaultCards() {
    const rows = policy.defaultCards || [];
    $('default-library-card-list').innerHTML = '<table class="md-table"><thead><tr><th>卡牌</th><th>卡组</th><th>类型</th><th>Kredits</th><th>数量</th><th>操作</th></tr></thead><tbody>' +
      (rows.length ? rows.map((card, index) => '<tr><td><strong>' + Admin.esc(card.name || card.cardId) + '</strong><br><small class="muted mono">' + Admin.esc(card.cardId) + '</small></td><td>' + Admin.esc(card.cardSet || '—') + '</td><td>' + Admin.esc(card.type || '—') + '</td><td>' + Admin.esc(card.kredits ?? '—') + '</td><td><input type="number" min="1" max="1000" value="' + Number(card.count || 1) + '" data-default-count="' + index + '" aria-label="默认数量"></td><td><button type="button" class="btn btn--outline btn--small" data-remove-default="' + index + '">移除</button></td></tr>').join('') : '<tr><td colspan="6" class="muted">尚未配置默认卡牌，请在上方筛选后批量添加。</td></tr>') +
      '</tbody></table>';
    document.querySelectorAll('[data-default-count]').forEach(input => input.onchange = () => {
      const item = policy.defaultCards[Number(input.dataset.defaultCount)];
      const count = Number(input.value);
      if (item && Number.isInteger(count) && count >= 1 && count <= 1000) item.count = count;
      else { input.value = item?.count || 1; Admin.banner('数量必须是 1 到 1000 之间的整数。', 'err'); }
    });
    document.querySelectorAll('[data-remove-default]').forEach(button => button.onclick = () => {
      policy.defaultCards.splice(Number(button.dataset.removeDefault), 1);
      renderDefaultCards();
    });
  }
  async function loadPolicy() {
    const data = await Admin.api('/system-settings/player-library');
    if (!data || !Array.isArray(data.defaultCards)) { Admin.banner(data?.message || '读取新玩家卡牌设置失败', 'err'); return; }
    policy = data;
    $('new-player-card-mode').value = data.mode || 'selected';
    $('new-player-all-gold').value = String(!!data.allGold);
    renderDefaultCards();
  }
  function renderCards(items) {
    $('card-rows').innerHTML = items.length ? items.map(card => '<tr><td><strong>' + Admin.esc(card.title || card.name || card.id) + '</strong><br><small class="muted mono">' + Admin.esc(card.id) + '</small></td><td>' + Admin.esc(card.cardSet || card.cardset || '—') + '</td><td>' + Admin.esc(card.type || '—') + '</td><td>' + Admin.esc(card.kredits ?? '—') + '</td><td>' + Admin.esc(card.rarity || '—') + '</td><td>' + Admin.esc(card.faction || '—') + '</td></tr>').join('') : '<tr><td colspan="6" class="muted">没有符合条件的卡牌</td></tr>';
  }
  async function loadCards() {
    const params = queryString({ ...filters(), page, pageSize });
    const data = await Admin.api('/cards?' + params);
    if (!data || !Array.isArray(data.items)) { Admin.banner(data?.message || '读取卡牌目录失败', 'err'); return; }
    total = data.total;
    renderCards(data.items);
    $('card-count').textContent = '共 ' + total.toLocaleString() + ' 张符合条件的卡牌';
    $('card-page').textContent = '第 ' + page + ' / ' + Math.max(1, Math.ceil(total / pageSize)) + ' 页';
    $('card-prev').disabled = page <= 1;
    $('card-next').disabled = page >= Math.ceil(total / pageSize);
    setOptions($('card-set'), data.cardSets || [], '所有卡组');
    setOptions($('card-type'), data.types || [], '所有类型');
  }

  $('card-search').onclick = () => { page = 1; loadCards(); };
  $('card-q').onkeydown = event => { if (event.key === 'Enter') { event.preventDefault(); page = 1; loadCards(); } };
  $('card-prev').onclick = () => { page--; loadCards(); };
  $('card-next').onclick = () => { page++; loadCards(); };
  $('add-filtered-defaults').onclick = async () => {
    if (!total) return Admin.banner('当前筛选没有卡牌。', 'info');
    const count = Number($('default-card-count').value);
    if (!Number.isInteger(count) || count < 1 || count > 1000) return Admin.banner('卡牌数量必须在 1 到 1000 之间。', 'err');
    if (!confirm('将当前筛选命中的全部 ' + total.toLocaleString() + ' 张卡牌加入默认清单，每张 ' + count + ' 张？已配置的数量会被替换。')) return;
    const result = await Admin.api('/cards/defaults', { method: 'PUT', body: { ...filters(), count } });
    Admin.banner(result?.message || '添加失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) await loadPolicy();
  };
  $('player-library-form').onsubmit = async event => {
    event.preventDefault();
    const defaultCards = (policy.defaultCards || []).map(card => ({ cardId: card.cardId, count: Number(card.count) }));
    const body = { mode: $('new-player-card-mode').value, allGold: $('new-player-all-gold').value === 'true', defaultCards };
    $('player-library-save').disabled = true;
    try {
      const result = await Admin.api('/system-settings/player-library', { method: 'PUT', body });
      Admin.banner(result?.message || '保存失败', result?.ok ? 'ok' : 'err');
      if (result?.ok) await loadPolicy();
    } finally { $('player-library-save').disabled = false; }
  };

  await Promise.all([loadPolicy(), loadCards()]);
})();

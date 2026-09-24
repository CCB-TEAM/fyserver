import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  const $ = id => document.getElementById(id);
  const pageSize = 50;
  let page = 1;
  let total = 0;
  let currentCard = null;

  function filters() {
    return {
      q: $('card-q').value.trim(), cardSet: $('card-set').value, type: $('card-type').value,
      minKredits: $('card-min-kredits').value, maxKredits: $('card-max-kredits').value
    };
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
  function renderRows(items) {
    $('card-rows').innerHTML = items.length ? items.map(card => '<tr><td><strong>' + Admin.esc(card.title || card.name || card.id) + '</strong><br><small class="mono muted">' + Admin.esc(card.id) + '</small></td><td>' + Admin.esc(card.cardSet || card.cardset || '—') + '</td><td>' + Admin.esc(card.type || '—') + '</td><td>' + Admin.esc(card.kredits ?? '—') + '</td><td>' + Admin.esc(card.rarity || '—') + '</td><td>' + Admin.esc(card.faction || '—') + '</td><td><span class="status-pill ' + (card.packEligible ? 'status-pill--ok' : 'status-pill--muted') + '">' + (card.type === 'location' ? 'Location 禁止' : card.packEligible ? '普通包可用' : '不可开包') + '</span></td><td><button class="btn btn--outline btn--small" type="button" data-edit-card="' + Admin.esc(card.id) + '">编辑</button></td></tr>').join('') : '<tr><td colspan="8" class="muted">没有符合条件的卡牌</td></tr>';
    document.querySelectorAll('[data-edit-card]').forEach(button => button.onclick = () => openEditor(items.find(item => item.id === button.dataset.editCard)));
  }
  async function load() {
    const values = filters();
    const params = queryString({ ...values, page, pageSize });
    const data = await Admin.api('/cards?' + params);
    if (!data || !Array.isArray(data.items)) { Admin.banner(data?.message || '读取卡牌目录失败', 'err'); return; }
    total = data.total;
    renderRows(data.items);
    $('card-count').textContent = '共 ' + total.toLocaleString() + ' 张符合条件的卡牌';
    $('card-page').textContent = '第 ' + page + ' / ' + Math.max(1, Math.ceil(total / pageSize)) + ' 页';
    $('card-prev').disabled = page <= 1;
    $('card-next').disabled = page >= Math.ceil(total / pageSize);
    setOptions($('card-set'), data.cardSets || [], '所有卡组');
    setOptions($('card-type'), data.types || [], '所有类型');
  }
  function openEditor(card) {
    if (!card) return;
    currentCard = card;
    $('edit-card-id').textContent = card.id;
    $('edit-title').value = card.title || card.name || '';
    $('edit-set').value = card.cardSet || card.cardset || '';
    $('edit-type').value = card.type || '';
    $('edit-rarity').value = card.rarity || '';
    $('edit-faction').value = card.faction || '';
    $('edit-kredits').value = card.kredits ?? '';
    $('edit-reserved').value = String(!!(card.isReserved ?? card.is_reserved));
    $('edit-text').value = card.text || '';
    $('card-edit-dialog').showModal();
  }

  $('card-search').onclick = () => { page = 1; load(); };
  $('card-q').onkeydown = event => { if (event.key === 'Enter') { event.preventDefault(); page = 1; load(); } };
  $('card-prev').onclick = () => { page--; load(); };
  $('card-next').onclick = () => { page++; load(); };
  $('add-filtered-defaults').onclick = async () => {
    if (!total) return Admin.banner('当前筛选没有卡牌。', 'info');
    const count = Number($('default-card-count').value);
    if (!Number.isInteger(count) || count < 1 || count > 1000) return Admin.banner('卡牌数量必须在 1 到 1000 之间。', 'err');
    if (!confirm('将当前筛选命中的全部 ' + total + ' 张卡牌加入新玩家默认卡牌，每张 ' + count + ' 张？已配置的数量会被替换。')) return;
    const result = await Admin.api('/cards/defaults', { method: 'PUT', body: { ...filters(), count } });
    Admin.banner(result?.message || '操作失败', result?.ok ? 'ok' : 'err');
  };
  $('save-card').onclick = async () => {
    if (!currentCard) return;
    const value = $('edit-kredits').value.trim();
    const body = {
      title: $('edit-title').value.trim(), cardSet: $('edit-set').value.trim(), type: $('edit-type').value.trim(),
      rarity: $('edit-rarity').value || null, faction: $('edit-faction').value.trim(), kredits: value === '' ? null : Number(value),
      isReserved: $('edit-reserved').value === 'true', text: $('edit-text').value
    };
    const result = await Admin.api('/cards/' + encodeURIComponent(currentCard.id), { method: 'PUT', body });
    if (!result?.ok) return Admin.banner(result?.message || '保存卡牌失败', 'err');
    $('card-edit-dialog').close();
    Admin.banner(result.message || '卡牌资料已保存', 'ok');
    await load();
  };
  await load();
})();

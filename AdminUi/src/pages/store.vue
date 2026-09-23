<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const state = ref(null);
const path = ref('config/store.json');
const groupIndex = ref(-1);
const offerIndex = ref(-1);
const raw = ref('');
const rewardTypes = ['gold', 'diamonds', 'dust', 'pack', 'card', 'draft', 'medkit', 'prop', 'alt_art', 'avatar', 'cardback', 'emote', 'deck', 'token', 'equipment'];
const groupFields = [['groupId', 'groupId', 'number'], ['group', 'group', 'number'], ['startDate', '开始时间', 'text'], ['endDate', '结束时间', 'text']];
const offerFields = [['offerId', 'offerId', 'number'], ['offerName', 'offerName', 'text'], ['title', '标题', 'text'], ['gold', '金币价格', 'number'], ['diamonds', '钻石价格', 'number'], ['real', '真实货币价格', 'number'], ['limit', '限购次数（0=不限）', 'number'], ['priority', '优先级', 'number'], ['slotType', 'slotType', 'text'], ['slotValue', 'slotValue', 'text'], ['fulfilAfter', 'fulfilAfter', 'text'], ['thumbnail', '缩略图 URL', 'text'], ['mainImage', '主图 URL', 'text'], ['smallThumb', '小图 URL', 'text']];
const rewardFields = [['name', 'name', 'text'], ['cardSet', 'cardSet', 'text'], ['cardCount', 'cardCount', 'number'], ['duration', 'duration', 'number'], ['month', 'month', 'number'], ['year', 'year', 'number']];
const groups = computed(() => state.value ? [state.value.alwaysFeatured, ...state.value.groups] : []);
const group = computed(() => groupIndex.value === -1 ? state.value?.alwaysFeatured : state.value?.groups[groupIndex.value]);
const offer = computed(() => group.value?.offers?.[offerIndex.value]);

function normalize(c) {
  c = c || {};
  c.currency ||= 'USD';
  c.groups = Array.isArray(c.groups) ? c.groups : [];
  c.alwaysFeatured ||= { group: 1, groupId: -1, startDate: '2018-01-01T00:00:00Z', endDate: '2099-01-01T00:00:00Z', offers: [] };
  for (const g of [c.alwaysFeatured, ...c.groups]) {
    g.offers = Array.isArray(g.offers) ? g.offers : [];
    for (const o of g.offers) { o.items = Array.isArray(o.items) ? o.items : []; o.bonusItems = Array.isArray(o.bonusItems) ? o.bonusItems : []; }
  }
  return c;
}
function selectGroup(index) { groupIndex.value = index; offerIndex.value = -1; raw.value = ''; }
function selectOffer(index) { offerIndex.value = index; refreshRaw(); }
function refreshRaw() { raw.value = offer.value ? JSON.stringify(offer.value, null, 2) : ''; }
function addGroup() {
  const id = Math.max(-1, ...groups.value.map(g => Number(g.groupId) || -1)) + 1;
  state.value.groups.push({ groupId: id, group: 4, startDate: new Date().toISOString(), endDate: '2099-01-01T00:00:00Z', offers: [] });
  selectGroup(state.value.groups.length - 1);
}
function addOffer() {
  if (!group.value) return;
  const id = Math.max(0, ...groups.value.flatMap(g => g.offers.map(o => Number(o.offerId) || 0))) + 1;
  group.value.offers.push({ offerId: id, offerName: 'new_offer', title: '新商品', description: '', items: [{ data: { itemType: 'gold' }, qty: 1 }], bonusItems: [], gold: 0, diamonds: 0, limit: 0 });
  selectOffer(group.value.offers.length - 1);
}
function deleteOffer() { if (offer.value && confirm('删除当前商品？')) { group.value.offers.splice(offerIndex.value, 1); selectOffer(-1); } }
function addReward(kind) { offer.value[kind].push({ data: { itemType: 'gold' }, qty: 1 }); refreshRaw(); }
function deleteReward(kind, index) { offer.value[kind].splice(index, 1); refreshRaw(); }
async function load() {
  try {
    const r = await Admin.api('/store');
    if (!r?.ok) throw Error(r?.message || '读取失败');
    state.value = normalize(r.config); path.value = r.path || 'config/store.json'; selectGroup(-1);
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function save() {
  try {
    const r = await Admin.api('/store', { method: 'PUT', body: { config: state.value } });
    Admin.banner(r?.message || '保存失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) { state.value = normalize(r.config || state.value); refreshRaw(); }
  } catch (e) { Admin.banner(e.message, 'err'); }
}
function download() {
  const url = URL.createObjectURL(new Blob([JSON.stringify(state.value, null, 2)], { type: 'application/json' }));
  const a = document.createElement('a'); a.href = url; a.download = 'store.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function formatRaw() { try { raw.value = JSON.stringify(JSON.parse(raw.value), null, 2); } catch (e) { Admin.banner('商品 JSON 格式错误：' + e.message, 'err'); } }
function applyRaw() {
  try {
    const next = JSON.parse(raw.value);
    if (!next || typeof next !== 'object' || Array.isArray(next)) throw Error('必须是 JSON 对象');
    group.value.offers[offerIndex.value] = normalize({ alwaysFeatured: { offers: [next] } }).alwaysFeatured.offers[0];
    refreshRaw(); Admin.banner('已应用到当前商品，点击保存后写入文件', 'ok');
  } catch (e) { Admin.banner('商品 JSON 格式错误：' + e.message, 'err'); }
}
onMounted(async () => { if (await Admin.ensureAuth()) await load(); });
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>商店</h1><p>管理分组、商品与奖励；保存前服务器会备份 <span class="mono">config/store.json.bak</span>。</p></div>
    <section v-if="state" class="card"><div class="btn-row"><label class="field" style="margin:0;min-width:150px"><span>商店货币标识</span><input v-model="state.currency" maxlength="16"></label><button class="btn" type="button" @click="save">保存并生效</button><button class="btn btn--outline" type="button" @click="load">重新读取</button><button class="btn btn--outline" type="button" @click="download">导出 JSON</button><span class="muted">{{ path }}</span></div></section>
    <div v-if="state" class="store-layout">
      <section class="card"><div class="btn-row"><h2 style="flex:1;margin:0">分组</h2><button class="btn btn--small" type="button" @click="addGroup">新增</button></div><div class="store-list"><button v-for="(g,i) in groups" :key="i" :class="{ active: groupIndex === i-1 }" type="button" @click="selectGroup(i-1)"><span>{{ i === 0 ? '常驻精选' : '分组' }} {{ g.groupId }}</span><small>{{ g.offers.length }} 件</small></button></div></section>
      <section class="card"><div class="btn-row"><h2 style="flex:1;margin:0">商品</h2><button class="btn btn--small" type="button" @click="addOffer">新增</button></div><div class="store-list"><button v-for="(o,i) in group?.offers || []" :key="i" :class="{ active: offerIndex === i }" type="button" @click="selectOffer(i)"><span>{{ o.title || o.offerName || '未命名' }}</span><small>#{{ o.offerId }}</small></button><div v-if="!group?.offers?.length" class="empty">此分组暂无商品</div></div></section>
      <section class="card store-editor sticky-editor"><div class="btn-row"><h2 style="flex:1;margin:0">{{ offer ? `${offer.title || offer.offerName || '编辑商品'} · #${offer.offerId}` : '选择一个商品' }}</h2><button class="btn btn--danger btn--small" type="button" :disabled="!offer" @click="deleteOffer">删除</button></div>
        <div v-if="!offer" class="muted" style="padding:28px 0">从左侧选择分组和商品，或新建一个商品。</div>
        <template v-else>
          <div class="store-section" style="border-top:0;padding-top:0"><h3>分组设置</h3><div class="form-grid"><label v-for="[key,label,type] in groupFields" :key="key" class="field"><span>{{ label }}</span><input v-model="group[key]" :type="type"></label></div><div class="btn-row"><label><input v-model="group.hidden" type="checkbox"> 隐藏分组</label><span class="muted">{{ groupIndex === -1 ? 'alwaysFeatured' : '普通分组' }}</span></div></div>
          <div class="store-section"><h3>商品信息</h3><div class="form-grid"><label v-for="[key,label,type] in offerFields" :key="key" class="field"><span>{{ label }}</span><input v-model="offer[key]" :type="type"></label></div><label class="field"><span>描述</span><textarea v-model="offer.description"></textarea></label></div>
          <div v-for="kind in ['items','bonusItems']" :key="kind" class="store-section"><div class="btn-row"><h3 style="flex:1">{{ kind === 'items' ? '主奖励' : '奖励加成' }}</h3><button class="btn btn--outline btn--small" type="button" @click="addReward(kind)">添加奖励</button></div><div v-for="(item,i) in offer[kind]" :key="i" class="reward-row"><div class="reward-head"><b>#{{ i+1 }}</b><button class="btn btn--danger btn--small" type="button" @click="deleteReward(kind,i)">删除</button></div><div class="reward-grid"><label class="field"><span>itemType</span><select v-model="item.data.itemType"><option v-for="type in rewardTypes" :key="type">{{ type }}</option></select></label><label class="field"><span>数量</span><input v-model.number="item.qty" type="number" min="1"></label><label v-for="[key,label,type] in rewardFields" :key="key" class="field"><span>{{ label }}</span><input v-model="item.data[key]" :type="type"></label><label class="field"><span>闪卡</span><select :value="item.data.isGold === undefined ? '' : String(item.data.isGold)" @change="item.data.isGold = $event.target.value === '' ? undefined : $event.target.value === 'true'"><option value="">未设置</option><option value="true">是</option><option value="false">否</option></select></label></div></div></div>
          <details class="store-section" @toggle="$event.target.open && refreshRaw()"><summary><b>完整 JSON（高级编辑）</b></summary><p class="muted">用于保留或修改表单未覆盖的客户端字段。点击“应用 JSON”后再保存。</p><textarea v-model="raw" class="json store-json" spellcheck="false"></textarea><div class="btn-row" style="margin-top:8px"><button class="btn btn--outline btn--small" type="button" @click="applyRaw">应用 JSON</button><button class="btn btn--outline btn--small" type="button" @click="formatRaw">格式化</button></div></details>
        </template>
      </section>
    </div>
  </main>
</template>

<style>
.store-layout{display:grid;grid-template-columns:220px 250px minmax(0,1fr);gap:16px;align-items:start}.store-list{display:grid;gap:6px;max-height:620px;overflow:auto}.store-list button{display:flex;justify-content:space-between;gap:8px;text-align:left;border:1px solid var(--outline);border-radius:11px;padding:9px 10px;background:transparent;color:var(--text);cursor:pointer;font:13px var(--app-font)}.store-list button:hover,.store-list button.active{background:rgba(var(--primary-rgb),.1);border-color:var(--primary);color:var(--primary)}.store-list small{color:var(--muted);font-size:10px}.store-section{border-top:1px solid var(--outline);padding-top:15px;margin-top:15px}.reward-row{border:1px solid var(--outline);border-radius:12px;padding:10px;margin:8px 0;background:rgba(var(--primary-rgb),.025)}.reward-head{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}.reward-head b{font-size:12px}.reward-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:8px}.reward-grid .field{margin:0}.reward-grid .field>span{font-size:10px}.store-json{min-height:260px}.sticky-editor{position:sticky;top:86px}@media(max-width:1120px){.store-layout{grid-template-columns:190px 220px minmax(0,1fr)}}@media(max-width:900px){.store-layout{grid-template-columns:1fr 1fr}.store-editor{grid-column:1/-1}.sticky-editor{position:static}}@media(max-width:560px){.store-layout{grid-template-columns:1fr}}
</style>

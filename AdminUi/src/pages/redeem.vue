<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const types = ['diamonds', 'gold', 'pack', 'card', 'draft', 'medkit', 'prop', 'alt_art', 'avatar', 'cardback', 'emote', 'token', 'deck'];
const fields = [['name', 'name（卡牌/物品）', 'text'], ['cardSet', 'cardSet', 'text'], ['cardCount', 'cardCount', 'number'], ['duration', 'duration', 'number']];
const blank = () => ({ data: { itemType: 'diamonds' }, qty: 100 });
const codes = ref([]);
const rewards = ref([blank()]);
const filter = ref('');
const code = ref('');
const type = ref('single');
const expires = ref('');
const raw = ref('');
const visibleCodes = computed(() => codes.value.filter(c => !filter.value || String(c.code).toLowerCase().includes(filter.value.trim().toLowerCase())));

function syncRaw() { raw.value = JSON.stringify(rewards.value, null, 2); }
function addReward() { rewards.value.push({ data: { itemType: 'diamonds' }, qty: 1 }); syncRaw(); }
function deleteReward(i) { rewards.value.splice(i, 1); syncRaw(); }
function applyRaw() {
  try {
    const value = JSON.parse(raw.value);
    if (!Array.isArray(value) || !value.length) throw Error('rewards 必须是非空数组');
    rewards.value = value.map(r => ({ ...r, data: r.data || {} })); syncRaw();
    Admin.banner('已从 JSON 读取奖励', 'ok');
    return true;
  } catch (e) { Admin.banner('奖励 JSON 格式错误：' + e.message, 'err'); return false; }
}
function formatRaw() { try { raw.value = JSON.stringify(JSON.parse(raw.value), null, 2); } catch (e) { Admin.banner('JSON 格式错误：' + e.message, 'err'); } }
function rewardSummary(c) { return (c.rewards || []).map(r => (r.data?.itemType || '?') + ' × ' + (r.qty || 1)).join('、'); }
function usage(c) { return c.type === 'perUser' ? (c.usedByUsers || []).length : (c.usedBy ? 1 : 0); }
function status(c) { return c.expiresAt && new Date(c.expiresAt) < new Date() ? '已过期' : c.type === 'single' && c.usedBy ? '已使用' : '可用'; }
async function load() {
  try {
    const r = await Admin.api('/redeem');
    if (!r?.codes) throw Error(r?.message || '读取兑换码失败');
    codes.value = r.codes;
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function remove(c) {
  if (!confirm('确定删除兑换码 ' + c.code + '？')) return;
  try {
    const r = await Admin.api('/redeem/' + encodeURIComponent(c.code), { method: 'DELETE' });
    Admin.banner(r?.message || '删除失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function create() {
  if (!rewards.value.length) { Admin.banner('至少添加一项奖励', 'err'); return; }
  try {
    const expiry = expires.value ? new Date(expires.value).toISOString() : null;
    const r = await Admin.api('/redeem', { method: 'POST', body: { code: code.value.trim(), type: type.value, rewards: rewards.value, expiresAt: expiry } });
    Admin.banner(r?.message || '创建失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) { code.value = ''; type.value = 'single'; expires.value = ''; rewards.value = [blank()]; syncRaw(); await load(); }
  } catch (e) { Admin.banner(e.message, 'err'); }
}
function generate() { code.value = Array.from({ length: 3 }, () => Math.random().toString(36).slice(2, 6).toUpperCase()).join('-'); }
onMounted(async () => { if (await Admin.ensureAuth()) { syncRaw(); await load(); } });
</script>

<template>
  <main class="page"><div id="banner-host"></div><div class="page-head"><h1>兑换码</h1><p>创建和管理玩家兑换码。协议兼容 NestJS 的 <span class="mono">GET /redeem/{code}</span>，奖励会直接写入玩家数据。</p></div>
    <div class="redeem-layout">
      <section class="card"><div class="panel-head"><div><h2>兑换码列表</h2><p class="muted">single 全局只能使用一次；perUser 每个玩家只能使用一次。</p></div><button class="btn btn--outline btn--small" type="button" @click="load">刷新</button></div><div class="search-row redeem-search"><div class="field"><span>筛选兑换码</span><input v-model="filter" placeholder="输入兑换码"></div></div>
        <div class="table-wrap"><div v-if="!visibleCodes.length" class="empty">暂无兑换码</div><table v-else class="md-table"><thead><tr><th>兑换码</th><th>类型</th><th>奖励</th><th>使用情况</th><th>过期时间</th><th>操作</th></tr></thead><tbody><tr v-for="c in visibleCodes" :key="c.code"><td class="code-cell">{{ c.code }}</td><td>{{ c.type }}</td><td>{{ rewardSummary(c) }}</td><td>{{ usage(c) }} · <span class="chip" :class="status(c) === '可用' ? 'chip--ok' : 'chip--off'">{{ status(c) }}</span></td><td>{{ c.expiresAt ? new Date(c.expiresAt).toLocaleString() : '永不过期' }}</td><td><button class="btn btn--danger btn--small" type="button" @click="remove(c)">删除</button></td></tr></tbody></table></div>
      </section>
      <section class="card sticky"><div class="panel-head"><div><h2>创建兑换码</h2><p class="muted">奖励至少包含一项；不支持 dust。</p></div><button class="btn btn--outline btn--small" type="button" @click="generate">生成码</button></div>
        <form @submit.prevent="create"><div class="form-grid"><label class="field"><span>兑换码</span><input v-model="code" required maxlength="96" placeholder="例如 FY-2026-001"></label><label class="field"><span>使用类型</span><select v-model="type"><option value="single">single · 全局一次</option><option value="perUser">perUser · 每人一次</option></select></label><label class="field"><span>过期时间（可选）</span><input v-model="expires" type="datetime-local"></label></div>
          <div class="btn-row redeem-reward-toolbar"><h3>奖励</h3><button class="btn btn--outline btn--small" type="button" @click="addReward">添加奖励</button></div><div v-if="!rewards.length" class="empty">至少添加一项奖励</div><div v-for="(item,i) in rewards" :key="i" class="reward-row"><div class="reward-head"><b>奖励 #{{ i+1 }}</b><button class="btn btn--danger btn--small" type="button" @click="deleteReward(i)">删除</button></div><div class="reward-grid"><label class="field"><span>itemType</span><select v-model="item.data.itemType"><option v-for="t in types" :key="t">{{ t }}</option></select></label><label class="field"><span>数量</span><input v-model.number="item.qty" type="number" min="1"></label><label v-for="[key,label,inputType] in fields" :key="key" class="field"><span>{{ label }}</span><input v-model="item.data[key]" :type="inputType"></label><label class="field"><span>闪卡</span><select :value="item.data.isGold === undefined ? '' : String(item.data.isGold)" @change="item.data.isGold = $event.target.value === '' ? undefined : $event.target.value === 'true'"><option value="">未设置</option><option value="true">是</option><option value="false">否</option></select></label></div></div>
          <details class="redeem-advanced" @toggle="$event.target.open && syncRaw()"><summary><b>完整 JSON（高级编辑）</b></summary><p class="muted">可直接编辑 rewards 数组，适合暂未覆盖的客户端字段。</p><textarea v-model="raw" class="json-editor" spellcheck="false"></textarea><div class="btn-row"><button class="btn btn--outline btn--small" type="button" @click="applyRaw">从 JSON 读取奖励</button><button class="btn btn--outline btn--small" type="button" @click="formatRaw">格式化</button></div></details><button class="btn redeem-submit" type="submit">创建兑换码</button>
        </form>
      </section>
    </div>
  </main>
</template>

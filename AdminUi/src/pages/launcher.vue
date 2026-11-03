<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const accounts = ref([]);
const invites = ref([]);
const inviteFilter = ref('available');
const count = ref(1);
const expires = ref('');
const note = ref('');
const created = ref([]);
const statusText = { available: '可用', used: '已使用', revoked: '已作废', expired: '已过期' };
const visibleInvites = computed(() => invites.value.filter(i => inviteFilter.value === 'all' || i.status === inviteFilter.value));

function time(v) { return v ? new Date(v).toLocaleString() : '-'; }
async function load() {
  try {
    const [a, i] = await Promise.all([Admin.api('/launcher/accounts'), Admin.api('/launcher/invites')]);
    accounts.value = a?.accounts || [];
    invites.value = i?.invites || [];
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function createInvites() {
  try {
    const body = { count: Number(count.value) || 1, note: note.value, expiresAt: expires.value ? new Date(expires.value).toISOString() : null };
    const r = await Admin.api('/launcher/invites', { method: 'POST', body });
    Admin.banner(r?.message || '生成失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) { created.value = r.codes || []; note.value = ''; await load(); }
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function copyCreated() {
  try { await navigator.clipboard.writeText(created.value.join('\n')); Admin.banner('已复制到剪贴板', 'ok'); }
  catch { Admin.banner('复制失败，请手动选择复制', 'err'); }
}
async function revoke(i) {
  if (!confirm('作废邀请码 ' + i.code + '？')) return;
  try {
    const r = await Admin.api('/launcher/invites/' + encodeURIComponent(i.code), { method: 'DELETE' });
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function toggleDisabled(a) {
  if (!a.disabled && !confirm('禁用启动器账号 ' + a.username + '？该账号会被立即登出，且无法再登录或提交模组。')) return;
  try {
    const r = await Admin.api('/launcher/accounts/' + a.id + '/disabled', { method: 'POST', body: { disabled: !a.disabled } });
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function resetPassword(a) {
  if (!confirm('重置 ' + a.username + ' 的密码？该账号会被登出，并生成一个临时密码。')) return;
  try {
    const r = await Admin.api('/launcher/accounts/' + a.id + '/reset-password', { method: 'POST', body: {} });
    if (r?.ok) prompt('临时密码（只显示这一次，请转告用户并让其登录后修改）：', r.temporaryPassword);
    else Admin.banner(r?.message || '重置失败', 'err');
  } catch (e) { Admin.banner(e.message, 'err'); }
}
onMounted(async () => { if (await Admin.ensureAuth()) await load(); });
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>启动器账号</h1><p>启动器账号与游戏账号相互独立，用于在启动器中提交模组。注册必须使用这里生成的邀请码，每个邀请码只能使用一次。</p></div>
    <div class="redeem-layout">
      <section class="card">
        <div class="panel-head"><div><h2>邀请码</h2></div>
          <div class="btn-row"><select v-model="inviteFilter"><option value="available">可用</option><option value="used">已使用</option><option value="revoked">已作废</option><option value="expired">已过期</option><option value="all">全部</option></select>
            <button class="btn btn--outline btn--small" type="button" @click="load">刷新</button></div></div>
        <div class="table-wrap"><div v-if="!visibleInvites.length" class="empty">暂无邀请码</div>
          <table v-else class="md-table"><thead><tr><th>邀请码</th><th>状态</th><th>备注</th><th>创建</th><th>过期时间</th><th>使用者</th><th>操作</th></tr></thead>
            <tbody><tr v-for="i in visibleInvites" :key="i.code"><td class="code-cell">{{ i.code }}</td>
              <td><span class="chip" :class="i.status === 'available' ? 'chip--ok' : 'chip--off'">{{ statusText[i.status] || i.status }}</span></td>
              <td>{{ i.note || '-' }}</td><td>{{ time(i.createdAt) }}<div class="muted">{{ i.createdBy }}</div></td><td>{{ i.expiresAt ? time(i.expiresAt) : '永不过期' }}</td>
              <td>{{ i.usedByName || '-' }}<div v-if="i.usedAt" class="muted">{{ time(i.usedAt) }}</div></td>
              <td><button v-if="i.status === 'available'" class="btn btn--danger btn--small" type="button" @click="revoke(i)">作废</button></td></tr></tbody></table></div>
      </section>
      <section class="card sticky">
        <div class="panel-head"><div><h2>生成邀请码</h2><p class="muted">一次最多 100 个。</p></div></div>
        <form @submit.prevent="createInvites"><div class="form-grid">
          <label class="field"><span>数量</span><input v-model.number="count" type="number" min="1" max="100"></label>
          <label class="field"><span>过期时间（可选）</span><input v-model="expires" type="datetime-local"></label>
          <label class="field"><span>备注（可选）</span><input v-model="note" maxlength="200" placeholder="例如：发给测试群"></label>
        </div><button class="btn" type="submit">生成</button></form>
        <div v-if="created.length" style="margin-top:12px"><p class="muted">本次生成的邀请码：</p><textarea class="json-editor" readonly :value="created.join('\n')" :rows="Math.min(created.length, 10)"></textarea>
          <button class="btn btn--outline btn--small" type="button" @click="copyCreated">复制全部</button></div>
      </section>
    </div>
    <section class="card" style="margin-top:16px">
      <div class="panel-head"><div><h2>账号列表</h2><p class="muted">禁用或重置密码会让该账号立即登出。</p></div></div>
      <div class="table-wrap"><div v-if="!accounts.length" class="empty">暂无启动器账号</div>
        <table v-else class="md-table"><thead><tr><th>ID</th><th>账号名</th><th>注册时间</th><th>邀请码</th><th>最近登录</th><th>状态</th><th>操作</th></tr></thead>
          <tbody><tr v-for="a in accounts" :key="a.id"><td>{{ a.id }}</td><td><b>{{ a.username }}</b></td><td>{{ time(a.createdAt) }}</td><td class="mono">{{ a.inviteCode }}</td>
            <td>{{ time(a.lastLoginAt) }}<div class="muted">{{ a.lastLoginIp || '' }}</div></td>
            <td><span class="chip" :class="a.disabled ? 'chip--off' : 'chip--ok'">{{ a.disabled ? '已禁用' : '正常' }}</span></td>
            <td><div class="btn-row"><button class="btn btn--outline btn--small" type="button" @click="resetPassword(a)">重置密码</button>
              <button class="btn btn--small" :class="a.disabled ? '' : 'btn--danger'" type="button" @click="toggleDisabled(a)">{{ a.disabled ? '启用' : '禁用' }}</button></div></td></tr></tbody></table></div>
    </section>
  </main>
</template>

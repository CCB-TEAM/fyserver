<script setup>
import { computed, onMounted, onUnmounted, ref } from 'vue';

const Admin = window.Admin;
const data = ref(null);
const tunnel = ref(null);
const tunnelError = ref('');
const savingTunnel = ref(false);
const target = ref('');
const sendAll = ref(false);
const message = ref('');
const visibilityText = { public: '公开', password: '密码', friends: '仅好友' };
const roomCount = computed(() => data.value?.rooms?.length || 0);
const playerCount = computed(() => (data.value?.rooms || []).reduce((n, r) => n + (r.playerCount || 0), 0));
let timer = null;

async function load() {
  try {
    const r = await Admin.api('/launcher-cloud/overview');
    if (!r?.rooms) throw Error(r?.message || '读取房间列表失败');
    data.value = r;
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function loadTunnel() {
  try {
    const r = await Admin.api('/launcher-cloud/tunnel');
    if (r?.enabled === undefined) throw Error(r?.message || '读取中继设置失败');
    tunnel.value = r;
    tunnelError.value = '';
  } catch (e) { tunnelError.value = e.message; }
}
async function saveTunnel() {
  savingTunnel.value = true;
  try {
    const t = tunnel.value;
    const r = await Admin.api('/launcher-cloud/tunnel', { method: 'PUT', body: { enabled: t.enabled, relayBandwidthLimit: t.relayBandwidthLimit } });
    Admin.banner(r?.message || '保存失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) tunnel.value = r;
  } catch (e) { Admin.banner(e.message, 'err'); }
  finally { savingTunnel.value = false; }
}
async function closeRoom(room) {
  if (!confirm(`关闭房间「${room.name}」？房间里的 ${room.playerCount} 人会被移出。`)) return;
  try {
    const r = await Admin.api('/launcher-cloud/rooms/' + encodeURIComponent(room.id) + '/close', { method: 'POST', body: {} });
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function deletePack(pack) {
  if (!confirm(`删除 ${pack.ownerName} 的整合包「${pack.name}」？`)) return;
  try {
    const r = await Admin.api('/launcher-cloud/modpacks/' + encodeURIComponent(pack.id), { method: 'DELETE' });
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function send() {
  const text = message.value.trim();
  if (!text) return Admin.banner('请先填写消息内容', 'err');
  const body = sendAll.value ? { all: true, message: text } : { accountId: Number(target.value), message: text };
  if (!sendAll.value && !(body.accountId > 0)) return Admin.banner('请填写正确的启动器账号 ID', 'err');
  if (sendAll.value && !confirm(`以小助手身份发给所有在线的启动器（${data.value?.online || 0} 个）？`)) return;
  try {
    const r = await Admin.api('/launcher-cloud/message', { method: 'POST', body });
    Admin.banner(r?.message || '发送失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) message.value = '';
  } catch (e) { Admin.banner(e.message, 'err'); }
}
function time(v) { return v ? new Date(v).toLocaleString() : '-'; }
onMounted(async () => {
  Admin.hydrateIcons?.();
  if (!(await Admin.ensureAuth())) return;
  await Promise.all([load(), loadTunnel()]);
  timer = setInterval(load, 10000);
});
onUnmounted(() => clearInterval(timer));
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>启动器房间</h1><p>启动器的房间大厅：房主在自己电脑上运行游戏服务端，云端只负责房间列表、进房票据和中继（复用游戏端口，不需要 frp）。房间只存在内存里，服务器重启后房主的启动器会自动重新开房。</p></div>
    <section class="metric-grid">
      <article class="metric-card"><i class="metric-icon purple"><span data-icon="online"></span></i><div><small>在线启动器</small><strong>{{ data ? data.online : '—' }}</strong><em>控制通道长连接</em></div></article>
      <article class="metric-card"><i class="metric-icon blue"><span data-icon="matches"></span></i><div><small>房间</small><strong>{{ data ? roomCount : '—' }}</strong><em>{{ playerCount }} 人在房间里</em></div></article>
      <article class="metric-card"><i class="metric-icon amber"><span data-icon="users"></span></i><div><small>启动器好友关系</small><strong>{{ data?.stats ? data.stats.friendships : '—' }}</strong><em>{{ data?.stats ? data.stats.pending + ' 个待处理申请' : (data?.statsError || '') }}</em></div></article>
      <article class="metric-card"><i class="metric-icon green"><span data-icon="settings"></span></i><div><small>中继</small><strong>{{ data ? (data.tunnelEnabled ? '已启用' : '未启用') : '—' }}</strong><em>未启用时只能本机测试</em></div></article>
    </section>

    <section class="card">
      <div class="panel-head"><div><h2>房间列表</h2><p class="muted">每 10 秒自动刷新。房主 45 秒没有心跳房间会自动关闭。</p></div><button class="btn btn--outline btn--small" @click="load">刷新</button></div>
      <div class="table-wrap">
        <div v-if="data && !data.rooms.length" class="empty">现在没有房间</div>
        <table v-else-if="data" class="md-table"><thead><tr><th>房间</th><th>房主</th><th>可见性</th><th>人数</th><th>模组</th><th>创建时间</th><th></th></tr></thead>
          <tbody><tr v-for="r in data.rooms" :key="r.id">
            <td><strong>{{ r.name }}</strong><div class="muted mono">{{ r.id }}</div></td>
            <td>{{ r.host.username }}<div class="muted mono">#{{ r.host.id }}</div></td>
            <td><span class="chip">{{ visibilityText[r.visibility] || r.visibility }}</span></td>
            <td :title="r.members.map(m => m.username).join('、')">{{ r.playerCount }} / {{ r.maxPlayers }}</td>
            <td>{{ r.mods.length }}<span v-if="r.modpackId" class="muted mono"> · {{ r.modpackId }}</span></td>
            <td>{{ time(r.createdAt) }}</td>
            <td><button class="btn btn--outline btn--small" @click="closeRoom(r)">关闭</button></td>
          </tr></tbody></table>
      </div>
    </section>

    <div class="redeem-layout" style="margin-top:16px">
      <section class="card">
        <div class="panel-head"><div><h2>房间中继</h2><p class="muted">启动器内置的隧道，走游戏服务端同一个端口，不需要额外部署。成员的游戏连接经云端转给房主，每个房间按这里的限速限流。</p></div></div>
        <p v-if="tunnelError" class="muted">{{ tunnelError }}（需要“服务器设置”权限）</p>
        <form v-else-if="tunnel" @submit.prevent="saveTunnel">
          <label style="display:flex;gap:8px;align-items:center"><input v-model="tunnel.enabled" type="checkbox"> 启用中继（关闭后别人无法加入房间）</label>
          <div class="form-grid" style="margin-top:10px">
            <label class="field"><span>中继限速（每个房间）</span><input v-model="tunnel.relayBandwidthLimit" placeholder="512KB 或 1MB"></label>
          </div>
          <div class="btn-row" style="margin-top:12px"><button class="btn" type="submit" :disabled="savingTunnel">保存</button></div>
          <p class="muted" style="margin-top:12px">当前在线房主 {{ tunnel.stats?.hosts ?? 0 }} 个，中继连接 {{ tunnel.stats?.connections ?? 0 }} 条。票据公钥 ID：<span class="mono">{{ tunnel.ticketKeyId }}</span></p>
        </form>
      </section>
      <section class="card sticky">
        <div class="panel-head"><div><h2>以小助手身份发消息</h2><p class="muted">发到启动器的好友聊天里；发给单个账号时离线也会在上线后收到。</p></div></div>
        <form @submit.prevent="send">
          <label style="display:flex;gap:8px;align-items:center"><input v-model="sendAll" type="checkbox"> 发给所有在线启动器（{{ data?.online || 0 }} 个）</label>
          <label v-if="!sendAll" class="field" style="margin-top:10px"><span>启动器账号 ID</span><input v-model="target" inputmode="numeric" placeholder="在“启动器账号”页可以查到"></label>
          <label class="field" style="margin-top:10px"><span>消息内容</span><textarea v-model="message" rows="4" maxlength="2000"></textarea></label>
          <div class="btn-row" style="margin-top:12px"><button class="btn" type="submit">发送</button></div>
        </form>
      </section>
    </div>

    <section class="card" style="margin-top:16px">
      <div class="panel-head"><div><h2>云端整合包</h2><p class="muted">云端只保存模组清单（模组索引 id、版本和哈希），不保存文件。</p></div></div>
      <div class="table-wrap">
        <div v-if="data && !data.modpacks.length" class="empty">还没有人上传整合包</div>
        <table v-else-if="data" class="md-table"><thead><tr><th>名称</th><th>作者</th><th>可见性</th><th>更新时间</th><th></th></tr></thead>
          <tbody><tr v-for="p in data.modpacks" :key="p.id">
            <td><strong>{{ p.name }}</strong><div class="muted mono">{{ p.id }}</div></td>
            <td>{{ p.ownerName }}<div class="muted mono">#{{ p.ownerId }}</div></td>
            <td><span class="chip" :class="p.visibility === 'public' ? 'chip--ok' : 'chip--off'">{{ p.visibility === 'public' ? '公开' : '私有' }}</span></td>
            <td>{{ time(p.updatedAt) }}</td>
            <td><button class="btn btn--outline btn--small" @click="deletePack(p)">删除</button></td>
          </tr></tbody></table>
      </div>
    </section>
  </main>
</template>

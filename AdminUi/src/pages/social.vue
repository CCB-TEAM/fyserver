<script setup>
import { onMounted, ref } from 'vue';

const Admin = window.Admin;
const settings = ref(null);
const stats = ref(null);
const statsError = ref('');
const online = ref(0);
const saving = ref(false);
const target = ref('');
const sendAll = ref(false);
const message = ref('');
const lookupId = ref('');
const lookup = ref(null);
const statusText = { accepted: '好友', request_sent: '已发出申请', request_received: '收到申请' };

async function load() {
  try {
    const r = await Admin.api('/social');
    if (!r?.settings) throw Error(r?.message || '读取好友系统设置失败');
    settings.value = r.settings;
    stats.value = r.stats || null;
    statsError.value = r.statsError || '';
    online.value = r.online || 0;
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function save() {
  saving.value = true;
  try {
    const r = await Admin.api('/social/settings', { method: 'PUT', body: settings.value });
    Admin.banner(r?.message || '保存失败', r?.ok ? 'ok' : 'err');
    if (r?.settings) settings.value = r.settings;
  } catch (e) { Admin.banner(e.message, 'err'); }
  finally { saving.value = false; }
}
async function send() {
  const text = message.value.trim();
  if (!text) return Admin.banner('请先填写消息内容', 'err');
  const body = sendAll.value ? { all: true, message: text } : { playerId: Number(target.value), message: text };
  if (!sendAll.value && !(body.playerId > 0)) return Admin.banner('请填写正确的玩家 ID', 'err');
  if (sendAll.value && !confirm(`以小助手身份把这条消息发给当前所有在线玩家（${online.value} 人）？`)) return;
  try {
    const r = await Admin.api('/social/message', { method: 'POST', body });
    Admin.banner(r?.message || '发送失败', r?.ok ? 'ok' : 'err');
    if (r?.ok) message.value = '';
  } catch (e) { Admin.banner(e.message, 'err'); }
}
async function findFriends() {
  const id = Number(lookupId.value);
  if (!(id > 0)) return Admin.banner('请填写正确的玩家 ID', 'err');
  try {
    const r = await Admin.api('/social/users/' + id + '/friends');
    lookup.value = { id, friends: r?.friends || [] };
  } catch (e) { Admin.banner(e.message, 'err'); }
}
function time(v) { return v ? new Date(v).toLocaleString() : '-'; }
onMounted(async () => { Admin.hydrateIcons?.(); if (await Admin.ensureAuth()) await load(); });
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>好友与小助手</h1><p>游戏内好友系统、好友私聊记录和服务器小助手的设置。私聊会保存到玩家数据库，玩家重新登录后服务器会把最近的聊天记录重新推送给客户端，离线期间收到的消息在上线后补发。</p></div>
    <section class="metric-grid">
      <article class="metric-card"><i class="metric-icon purple"><span data-icon="online"></span></i><div><small>当前在线</small><strong>{{ online }}</strong><em>WebSocket 长连接</em></div></article>
      <article class="metric-card"><i class="metric-icon blue"><span data-icon="users"></span></i><div><small>好友关系</small><strong>{{ stats ? stats.friendships : '—' }}</strong><em>已互为好友的玩家对数</em></div></article>
      <article class="metric-card"><i class="metric-icon amber"><span data-icon="queue"></span></i><div><small>待处理申请</small><strong>{{ stats ? stats.pending : '—' }}</strong><em>尚未同意的好友申请</em></div></article>
      <article class="metric-card"><i class="metric-icon green"><span data-icon="matches"></span></i><div><small>聊天记录</small><strong>{{ stats ? stats.messages : '—' }}</strong><em>{{ statsError || '保存中的私聊消息条数' }}</em></div></article>
    </section>
    <div class="redeem-layout">
      <section class="card">
        <div class="panel-head"><div><h2>设置</h2><p class="muted">保存后立即生效，不需要重启服务器。</p></div></div>
        <form v-if="settings" @submit.prevent="save">
          <div class="form-grid">
            <label class="field"><span>小助手名字</span><input v-model="settings.assistantName" maxlength="32"></label>
            <label class="field"><span>好友上限</span><input v-model.number="settings.maxFriends" type="number" min="1" max="1000"></label>
            <label class="field"><span>待处理申请上限</span><input v-model.number="settings.maxPendingRequests" type="number" min="1" max="500"></label>
            <label class="field"><span>聊天记录保留天数</span><input v-model.number="settings.historyDays" type="number" min="1" max="3650"></label>
            <label class="field"><span>登录时每个好友补发最近几条</span><input v-model.number="settings.historyPerFriend" type="number" min="0" max="500"></label>
            <label class="field"><span>单条消息最大字数</span><input v-model.number="settings.maxMessageLength" type="number" min="10" max="2000"></label>
          </div>
          <label class="field" style="margin-top:12px"><span>登录欢迎语（留空则不发）</span><textarea v-model="settings.welcomeMessage" rows="3" maxlength="500"></textarea></label>
          <label style="display:flex;gap:8px;align-items:center;margin-top:12px"><input v-model="settings.replayOwnMessages" type="checkbox"> 补发记录时包含玩家自己发出的消息</label>
          <p class="muted">如果游戏里自己发出的历史消息显示在了错误的位置，关闭上面这一项，登录后就只补发收到的消息。</p>
          <div class="btn-row" style="margin-top:12px"><button class="btn" type="submit" :disabled="saving">保存设置</button></div>
        </form>
      </section>
      <section class="card sticky">
        <div class="panel-head"><div><h2>以小助手身份发消息</h2><p class="muted">发给单个玩家时会保存为聊天记录，玩家离线也能在上线后收到。</p></div></div>
        <form @submit.prevent="send">
          <label style="display:flex;gap:8px;align-items:center"><input v-model="sendAll" type="checkbox"> 发给所有在线玩家（{{ online }} 人）</label>
          <label v-if="!sendAll" class="field" style="margin-top:10px"><span>玩家 ID</span><input v-model="target" inputmode="numeric" placeholder="例如 123456"></label>
          <label class="field" style="margin-top:10px"><span>消息内容</span><textarea v-model="message" rows="4" maxlength="2000"></textarea></label>
          <div class="btn-row" style="margin-top:12px"><button class="btn" type="submit">发送</button></div>
        </form>
        <div class="panel-head" style="margin-top:20px"><div><h2>查看玩家好友</h2></div></div>
        <form class="btn-row" @submit.prevent="findFriends"><input v-model="lookupId" inputmode="numeric" placeholder="玩家 ID"><button class="btn btn--outline btn--small" type="submit">查询</button></form>
        <div v-if="lookup" class="table-wrap" style="margin-top:10px">
          <div v-if="!lookup.friends.length" class="empty">玩家 {{ lookup.id }} 还没有好友</div>
          <table v-else class="md-table"><thead><tr><th>玩家</th><th>关系</th><th>在线</th><th>时间</th></tr></thead>
            <tbody><tr v-for="f in lookup.friends" :key="f.playerId"><td><a :href="'/admin-ui/user-detail.html?id=' + f.playerId">{{ f.name }}#{{ String(f.tag).padStart(4, '0') }}</a><div class="muted mono">{{ f.playerId }}</div></td>
              <td>{{ statusText[f.status] || f.status }}</td><td><span class="chip" :class="f.online ? 'chip--ok' : 'chip--off'">{{ f.online ? '在线' : '离线' }}</span></td><td>{{ time(f.since) }}</td></tr></tbody></table>
        </div>
      </section>
    </div>
  </main>
</template>

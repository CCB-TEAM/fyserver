<template>

<main class="page">
  <div id="banner-host"></div>
  <div class="page-head"><h1 id="title">玩家详情</h1><p><a href="/admin-ui/users.html">← 返回玩家列表</a> · 资料、账号状态与管理操作</p></div>
  <div class="card-grid" id="summary"></div>
  <div class="split-2">
    <section class="card"><h2>基本资料</h2><dl class="kv" id="identity"></dl>
      <form id="profile-form" style="margin-top:20px"><div class="form-grid">
        <div class="field"><label for="name">昵称</label><input id="name" type="text" maxlength="32" required /></div>
        <div class="field"><label for="tag">Tag</label><input id="tag" type="text" inputmode="numeric" maxlength="4" pattern="[0-9]{4}" required /></div>
        <div class="field"><label for="locale">语言</label><input id="locale" type="text" maxlength="20" required /></div>
      </div><button class="btn" type="submit">保存资料</button></form>
    </section>
    <section class="card"><h2>封禁与连接</h2><dl class="kv" id="ban-status"></dl>
      <form id="ban-form" style="margin-top:20px">
        <div class="field"><label for="reason">封禁原因（可留空；留空时游戏显示解封时间）</label><textarea id="reason" maxlength="500" placeholder="输入给玩家看到的原因"></textarea></div>
        <div class="field"><label for="expiry">解封时间（本地时间）</label><input id="expiry" type="datetime-local" /></div>
        <div class="field"><label><input id="permanent" type="checkbox" /> 永久封禁</label></div>
        <div class="btn-row"><button class="btn btn--danger" type="submit">封禁 / 更新封禁</button><button class="btn btn--outline" id="unban" type="button">解封</button><button class="btn btn--outline" id="kick" type="button">踢出连接</button></div>
      </form>
    </section>
  </div>
  <section class="card"><h2>玩家货币</h2><p class="muted">余额保存在玩家账户中，游戏下次请求 /session 时会读到最新数值。</p>
    <form id="wallet-form"><div class="form-grid">
      <div class="field"><label for="gold">金币</label><input id="gold" type="number" min="0" step="1" required /></div>
      <div class="field"><label for="diamonds">钻石</label><input id="diamonds" type="number" min="0" step="1" required /></div>
    </div><button class="btn" type="submit">保存货币</button></form>
  </section>
  <section class="card" id="roles-panel" hidden>
    <div class="panel-head"><div><h2>客户端角色</h2><p class="muted">这些角色会写入 GET / 返回的 current_user.roles。</p></div><span class="chip chip--off">ROLES</span></div>
    <div id="roles-list" class="player-role-grid"></div>
    <div class="btn-row player-role-actions"><p id="roles-help" class="muted"></p><button class="btn" id="save-roles" type="button" hidden>保存角色</button></div>
  </section>
  <section class="card"><h2>卡组（只读）</h2><div class="table-wrap" id="decks"></div></section>
  <section class="card"><div class="panel-head"><div><h2>近期对局</h2><p class="muted">展示服务器已持久化保存的最近 10 场对局。</p></div><a class="btn btn--outline btn--small" href="/admin-ui/match-management.html">对局管理</a></div><div class="table-wrap" id="recent-matches"></div></section>
  <section class="card"><h2>危险操作</h2><p class="muted">删除玩家账户不可撤销。</p><button class="btn btn--danger" id="delete-user" type="button">删除玩家</button></section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/user-detail.js');
});
</script>
<style>
input[type=datetime-local]{font:14px var(--app-font);color:var(--text);background:color-mix(in srgb,var(--surface-custom) 78%,transparent);border:1px solid var(--outline);border-radius:12px;padding:10px 12px;width:100%}
.player-role-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(220px,1fr));gap:10px;margin-top:14px}.player-role-option{display:flex;align-items:flex-start;gap:10px;border:1px solid var(--outline);border-radius:12px;padding:12px;background:rgba(var(--primary-rgb),.025)}.player-role-option input{margin-top:4px;accent-color:var(--primary)}.player-role-option b,.player-role-option small{display:block}.player-role-option b{font-size:13px}.player-role-option small{color:var(--muted);font-size:11px;margin-top:3px}.player-role-actions{justify-content:space-between;margin-top:14px}.player-role-actions p{margin:0}
</style>

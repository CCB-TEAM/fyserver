<template>

<main class="page"><div id="banner-host"></div><div class="page-head"><h1 id="title">后台账号详情</h1><p><a href="/admin-ui/accounts.html">← 返回后台用户</a></p></div>
<div class="card-grid" id="summary"></div>
<div class="split-2">
  <section class="card"><h2>账号资料</h2><dl class="kv" id="info"></dl><div class="btn-row" style="margin-top:20px"><a class="btn btn--outline" id="actions-link">历史操作 →</a><a class="btn btn--outline" id="logins-link">登录与 IP →</a></div></section>
  <section class="card"><h2>权限与状态</h2><p class="muted" id="owner-note"></p><form id="permissions-form"><label><input id="enabled" type="checkbox"> 允许登录</label><div class="permission-list" id="permissions"></div><button class="btn" type="submit">保存权限与状态</button></form></section>
</div>
<section class="card"><h2>重置密码</h2><p class="muted">只能设置新密码，后台无法查看原密码。重置后该账号已登录的会话立即失效。</p><form id="password-form"><div class="form-grid"><div class="field" id="current-password-field" hidden><label for="current-password">当前密码（Owner 自行修改时必填）</label><input id="current-password" type="password" autocomplete="current-password"></div><div class="field"><label for="new-password">新密码</label><input id="new-password" type="password" minlength="10" maxlength="256" autocomplete="new-password" required></div><div class="field"><label for="confirm-password">确认新密码</label><input id="confirm-password" type="password" minlength="10" maxlength="256" autocomplete="new-password" required></div></div><button class="btn" type="submit">重置密码</button></form></section>
<section class="card" id="danger-card"><h2>危险操作</h2><p class="muted">删除后台账号不可撤销，不会删除该账号已有的操作与登录审计记录。</p><button type="button" class="btn btn--danger" id="delete-account">删除账号</button></section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/account-detail.js');
});
</script>
<style>
.permission-list{display:flex;flex-wrap:wrap;gap:10px 18px;margin:12px 0 18px}.permission-list label{font-size:13px;white-space:nowrap}
</style>

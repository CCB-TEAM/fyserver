<template>

<main class="page"><div id="banner-host"></div><div class="page-head"><h1>后台用户</h1><p>查看在线管理员与账号状态。点击名称进入独立详情页。</p></div>
<div class="card-grid" id="summary"></div>
<section class="card"><h2>新建后台账号</h2><p class="muted">新账号默认只能查看后台；不勾选权限即不能执行管理操作。</p><form id="create-form"><div class="form-grid"><div class="field"><label for="new-username">用户名</label><input id="new-username" type="text" minlength="3" maxlength="32" required></div><div class="field"><label for="new-password">初始密码（至少 10 位）</label><input id="new-password" type="password" minlength="10" autocomplete="new-password" required></div></div><div class="permission-list" id="new-permissions"></div><button class="btn" type="submit">创建账号</button></form></section>
<section class="card"><h2>账号列表</h2><div class="table-wrap" id="accounts"></div></section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/accounts.js');
});
</script>
<style>
.permission-list{display:flex;flex-wrap:wrap;gap:10px 18px;margin:10px 0 18px}.permission-list label{font-size:13px;white-space:nowrap}
</style>

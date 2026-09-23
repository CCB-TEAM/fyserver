<template>

<main class="page"><div id="banner-host"></div><div class="page-head"><h1 id="title">账号历史操作</h1><p><a id="back" href="/admin-ui/accounts.html">← 返回账号详情</a> · 不记录请求体、密码或密钥</p></div><section class="card"><div class="table-wrap" id="history"></div><div class="btn-row" style="margin-top:18px"><button class="btn btn--outline" id="previous" type="button">上一页</button><span class="muted" id="page-indicator"></span><button class="btn btn--outline" id="next" type="button">下一页</button></div></section></main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {
  document.body.dataset.history = 'actions';
  window.Admin.hydrateIcons();
  await import('../legacy/account-actions.js');
});
</script>

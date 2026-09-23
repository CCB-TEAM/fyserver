<template>

<main class="page">
  <div id="banner-host"></div>
  <div class="page-head"><p class="dashboard-eyebrow">SERVER OPTIONS</p><h1>服务器配置</h1><p>参考镜像站管理界面：每项都可以独立启用或关闭，关闭只停止发送，不会删除已保存的值；编辑和新增使用独立页面。</p></div>
  <section class="card"><div class="btn-row"><a class="btn" href="/admin-ui/server-config-edit.html">新增配置项</a><label class="field" style="margin:0;min-width:240px"><span>筛选配置项</span><input type="text" id="filter" placeholder="按名称、说明或默认值搜索"></label><button class="btn btn--outline" id="reload" type="button">重新读取文件</button><span class="muted mono" id="count"></span></div></section>
  <section class="card"><h2>配置项与注释</h2><p class="muted">操作按钮位于最左侧。关闭开关会保留配置值，但下次登录不再发送该项。镜像抓取中 6 项缺少完整默认值，已以空值占位且默认关闭；请填入有效值后再启用。</p><div class="table-wrap"><table class="md-table config-table"><thead><tr><th>操作</th><th>启用</th><th>配置项</th><th>类型</th><th>当前值</th><th>注释 / 参考默认值</th></tr></thead><tbody id="rows"></tbody></table></div></section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/server-config.js');
});
</script>

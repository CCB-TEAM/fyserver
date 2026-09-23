<template>

<main class="page">
  <div id="banner-host"></div>
  <div class="page-head"><p class="dashboard-eyebrow">FIRST-RUN SETUP</p><h1>配置玩家数据库</h1><p>完成连接测试与数据表初始化后，游戏接口才会开始提供服务。数据库密码会加密保存在服务器本地，不会回传到浏览器。</p></div>
  <section class="card">
    <form id="database-form">
      <div class="form-grid database-grid">
        <label class="field"><span>存储类型</span><select id="provider"><option value="local">FASTER（本地存储）</option><option value="mysql">MySQL</option><option value="postgresql">PostgreSQL</option></select><small>首次启动先选择玩家数据存储。FASTER 无需外部数据库；MySQL / PostgreSQL 需预先创建数据库。</small></label>
        <label class="field remote"><span>主机 / IP</span><input id="host" autocomplete="off" placeholder="127.0.0.1"></label>
        <label class="field remote"><span>端口</span><input id="port" type="number" min="1" max="65535"></label>
        <label class="field remote"><span>数据库名</span><input id="database" autocomplete="off" placeholder="fyserver"></label>
        <label class="field remote"><span>数据库用户名</span><input id="username" autocomplete="username" placeholder="fyserver"></label>
        <label class="field remote"><span>数据库密码</span><input id="password" type="password" autocomplete="new-password" placeholder="留空可沿用已保存的密码"><small>密码不会显示或由接口返回。</small></label>
        <label class="field remote"><span>传输加密</span><select id="require-ssl"><option value="true">要求 SSL/TLS</option><option value="false">优先使用加密（允许本地无 TLS）</option></select></label>
      </div>
      <div class="btn-row"><button class="btn" id="save" type="submit">测试连接并完成初始化</button><button class="btn btn--outline" id="reload" type="button">读取已保存配置</button></div>
    </form>
  </section>
  <section class="card"><h2>初始化说明</h2><p class="muted">目标数据库需要预先存在，所填账号需要在该数据库中创建表的权限。FYServer 会自动创建 <span class="mono">fy_users</span> 表，不会修改数据库中的其它表。切换存储不会自动迁移已有玩家数据。</p><p class="muted" id="state">正在读取状态…</p></section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/database-setup.js');
});
</script>

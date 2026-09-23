<template>

<main class="page">
  <div id="banner-host"></div>
  <div class="page-head"><p class="dashboard-eyebrow">SERVER OPTIONS / EDITOR</p><h1 id="page-title">新增配置项</h1><p>每个配置项单独保存。关闭“启用”不会删除值，只会让客户端下次登录时不再收到该项。</p></div>
  <section class="card config-edit-grid">
    <form id="config-form">
      <label class="field"><span>配置项名称</span><input id="key" name="key" required pattern="[A-Za-z0-9_]+" maxlength="96" placeholder="例如 battle_wait_time"></label>
      <label class="field"><span>值类型</span><select id="type" name="type"><option value="string">string · 文本</option><option value="int">int · 整数</option><option value="double">double · 小数</option><option value="json">json · JSON 值</option></select></label>
      <label class="field"><span>配置值</span><textarea id="value" name="value" class="json" spellcheck="false" placeholder="输入配置值"></textarea></label>
      <label class="field"><span>注释（可修改；自定义配置项也可填写）</span><textarea id="description-input" name="description" maxlength="2000" placeholder="说明该配置项的用途"></textarea></label>
      <div class="btn-row"><button class="btn" type="submit">保存配置</button><a class="btn btn--outline" href="/admin-ui/server-config.html">取消并返回</a></div>
    </form>
    <aside class="config-edit-help"><h3>镜像参考</h3><p id="description">镜像已有配置项的原始说明会显示在这里，供编辑时参考。</p><p id="send-to-client"></p><p class="mirror-default" id="reference"></p><p class="muted">自定义注释保存在 config/serverOptions.comments.json；更新镜像参考数据不会覆盖它。键名只允许英文字母、数字和下划线。websocketurl 由 FYServer 自动生成，只允许编辑注释。</p></aside>
  </section>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/server-config-edit.js');
});
</script>

<template>
<main class="page">
  <div id="banner-host"></div>
  <div class="page-head"><p class="dashboard-eyebrow">CARD CATALOG</p><h1>卡牌列表</h1><p>查找并维护服务器卡牌元数据。Location 卡不能通过开包或万能卡合成获得。</p></div>
  <section class="card">
    <div class="form-grid card-filters">
      <label class="field"><span>搜索卡牌名称 / ID</span><input id="card-q" type="search" placeholder="输入卡牌名称或 ID"></label>
      <label class="field"><span>卡组</span><select id="card-set"><option value="">所有卡组</option></select></label>
      <label class="field"><span>类型</span><select id="card-type"><option value="">所有类型</option></select></label>
      <label class="field"><span>Kredits 最低</span><input id="card-min-kredits" type="number" min="0" max="100" placeholder="不限"></label>
      <label class="field"><span>Kredits 最高</span><input id="card-max-kredits" type="number" min="0" max="100" placeholder="不限"></label>
    </div>
    <div class="btn-row"><button class="btn btn--outline" id="card-search" type="button">筛选</button><span class="muted" id="card-count"></span></div>
  </section>
  <section class="card">
    <div class="table-wrap"><table class="md-table"><thead><tr><th>卡牌 ID / 名称</th><th>卡组</th><th>类型</th><th>Kredits</th><th>稀有度</th><th>阵营</th><th>卡包资格</th><th>操作</th></tr></thead><tbody id="card-rows"></tbody></table></div>
    <div class="btn-row" style="justify-content:space-between;margin-top:16px"><button class="btn btn--outline" id="card-prev" type="button">上一页</button><span class="muted" id="card-page"></span><button class="btn btn--outline" id="card-next" type="button">下一页</button></div>
  </section>
  <dialog id="card-edit-dialog" class="card-edit-dialog">
    <form method="dialog" id="card-edit-form">
      <h2>编辑卡牌资料</h2><p class="muted mono" id="edit-card-id"></p>
      <div class="form-grid">
        <label class="field"><span>显示名称</span><input id="edit-title" maxlength="512"></label>
        <label class="field"><span>卡组</span><input id="edit-set" maxlength="128"></label>
        <label class="field"><span>类型</span><input id="edit-type" maxlength="128"></label>
        <label class="field"><span>稀有度</span><select id="edit-rarity"><option value="">未设置</option><option>Common</option><option>Uncommon</option><option>Rare</option><option>Unique</option></select></label>
        <label class="field"><span>阵营</span><input id="edit-faction" maxlength="128"></label>
        <label class="field"><span>Kredits</span><input id="edit-kredits" type="number" min="0" max="100"></label>
        <label class="field"><span>保留卡（影响 Core 卡池）</span><select id="edit-reserved"><option value="false">否</option><option value="true">是</option></select></label>
        <label class="field" style="grid-column:1/-1"><span>规则文本</span><textarea id="edit-text" rows="4" maxlength="2048"></textarea></label>
      </div>
      <div class="btn-row"><button class="btn" id="save-card" type="button">保存</button><button class="btn btn--outline" type="submit">取消</button></div>
    </form>
  </dialog>
</main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {
  window.Admin.hydrateIcons();
  await import('../legacy/cards.js');
});
</script>

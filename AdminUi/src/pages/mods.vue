<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const mods = ref([]);
const filter = ref('');
const loading = ref(false);
const visibleMods = computed(() => {
  const q = filter.value.trim().toLowerCase();
  return mods.value.filter(m => !q || [m.id, m.name, m.uploaderName].some(v => String(v || '').toLowerCase().includes(q)));
});

function size(n) {
  if (!n) return '-';
  return n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1024).toFixed(1) + ' KB';
}
function hashLabel(s) { return { verified: 'sha256 已校验', pending: 'sha256 校验中', mismatch: 'sha256 不一致（已对玩家隐藏）', failed: 'sha256 校验失败' }[s] || 'sha256 未校验'; }
function time(v) { return v ? new Date(v).toLocaleString() : '-'; }
function iconUrl(m) { return '/mods/' + encodeURIComponent(m.id) + '/icon?t=' + encodeURIComponent(m.updatedAt || ''); }
async function load() {
  loading.value = true;
  try {
    const r = await Admin.api('/mods');
    if (!r?.mods) throw Error(r?.message || '读取模组列表失败');
    mods.value = r.mods;
  } catch (e) { Admin.banner(e.message, 'err'); }
  finally { loading.value = false; }
}
async function act(promise, reload = true) {
  try {
    const r = await promise;
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    if (reload) await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
function toggleHidden(m) {
  return act(Admin.api('/mods/' + encodeURIComponent(m.id) + '/hidden', { method: 'POST', body: { hidden: !m.hidden } }));
}
function recheck(m) {
  Admin.banner('正在检查 ' + m.id + ' 的下载链接…', 'info');
  return act(Admin.api('/mods/' + encodeURIComponent(m.id) + '/recheck', { method: 'POST', body: {} }));
}
function remove(m) {
  if (!confirm('确定删除模组 ' + m.name + '（' + m.id + '）？此操作不可恢复。')) return;
  return act(Admin.api('/mods/' + encodeURIComponent(m.id), { method: 'DELETE' }));
}
onMounted(async () => { if (await Admin.ensureAuth()) await load(); });
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>模组索引</h1><p>管理玩家上传的模组信息。服务端只保存模组 manifest 与图标，Pak 文件由上传者提供的下载直链托管。玩家接口：<span class="mono">GET /mods</span>、<span class="mono">GET /mods/{id}/download</span>、<span class="mono">POST /mods</span>。</p></div>
    <section class="card">
      <div class="panel-head"><div><h2>模组列表</h2><p class="muted">隐藏的模组不会出现在玩家接口中；sha256 不一致的模组也不会对玩家显示。“重新检查”会再次验证下载直链可访问、文件为纯 Pak，并重新下载校验 sha256。</p></div><button class="btn btn--outline btn--small" type="button" :disabled="loading" @click="load">刷新</button></div>
      <div class="search-row"><div class="field"><span>筛选模组</span><input v-model="filter" placeholder="按 id、名称或上传者搜索"></div></div>
      <div class="table-wrap">
        <div v-if="!visibleMods.length" class="empty">暂无模组</div>
        <table v-else class="md-table">
          <thead><tr><th>图标</th><th>模组</th><th>上传者</th><th>下载直链</th><th>状态</th><th>下载次数</th><th>更新时间</th><th>操作</th></tr></thead>
          <tbody>
            <tr v-for="m in visibleMods" :key="m.id">
              <td><img :src="iconUrl(m)" :alt="m.name" width="48" height="48" style="object-fit:cover;border-radius:8px"></td>
              <td><b>{{ m.name }}</b><div class="mono muted">{{ m.id }}</div><div class="muted" style="max-width:280px;white-space:pre-wrap">{{ m.description }}</div></td>
              <td>{{ m.uploaderName }}<div class="muted">ID {{ m.uploaderId }}</div></td>
              <td style="max-width:260px;word-break:break-all"><a :href="m.downloadUrl" target="_blank" rel="noopener noreferrer">{{ m.downloadUrl }}</a><div class="muted">{{ size(m.fileSize) }} · Pak v{{ m.pakVersion || '?' }}</div></td>
              <td>
                <span class="chip" :class="m.linkValid ? 'chip--ok' : 'chip--off'">{{ m.linkValid ? '链接有效' : '链接失效' }}</span>
                <span v-if="m.hidden" class="chip chip--off">已隐藏</span>
                <span class="chip" :class="m.hashStatus === 'verified' ? 'chip--ok' : 'chip--off'">{{ hashLabel(m.hashStatus) }}</span>
                <div class="mono muted" style="word-break:break-all">sha256 {{ m.sha256 || '-' }}</div>
                <div v-if="m.hashMessage" class="muted">{{ m.hashMessage }}</div>
                <div class="muted">检查于 {{ time(m.linkCheckedAt) }}</div>
                <div v-if="m.linkMessage" class="muted">{{ m.linkMessage }}</div>
              </td>
              <td>{{ m.downloads || 0 }}</td>
              <td>{{ time(m.updatedAt) }}</td>
              <td><div class="btn-row">
                <button class="btn btn--outline btn--small" type="button" @click="recheck(m)">重新检查</button>
                <button class="btn btn--outline btn--small" type="button" @click="toggleHidden(m)">{{ m.hidden ? '显示' : '隐藏' }}</button>
                <button class="btn btn--danger btn--small" type="button" @click="remove(m)">删除</button>
              </div></td>
            </tr>
          </tbody>
        </table>
      </div>
    </section>
  </main>
</template>

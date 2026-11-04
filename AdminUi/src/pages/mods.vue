<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const mods = ref([]);
const filter = ref('');
const view = ref('pending');
const loading = ref(false);
const fields = [['name', '名称'], ['version', '版本'], ['description', '简介'], ['downloadUrl', '下载直链'], ['sha256', 'sha256'], ['fileSize', '文件大小'], ['platforms', '各平台下载']];

const pendingCount = computed(() => mods.value.filter(m => m.submission?.status === 'pending').length);
const visibleMods = computed(() => {
  const q = filter.value.trim().toLowerCase();
  return mods.value
    .filter(m => view.value === 'all' || m.submission?.status === 'pending')
    .filter(m => !q || [m.id, m.ownerName, m.published?.name, m.submission?.name].some(v => String(v || '').toLowerCase().includes(q)));
});

function time(v) { return v ? new Date(v).toLocaleString() : '-'; }
function size(n) { if (!n) return '-'; return n > 1048576 ? (n / 1048576).toFixed(1) + ' MB' : (n / 1024).toFixed(1) + ' KB'; }
function platforms(v) { return Object.entries(v).map(([p, x]) => p + '\n' + x.downloadUrl + '\n' + x.sha256 + '  ' + size(x.fileSize)).join('\n\n'); }
function show(key, v) { return key === 'platforms' && v ? platforms(v) : key === 'fileSize' ? size(v) : (v === undefined || v === null || v === '' ? '-' : v); }
function changed(m, key) { return m.published && m.submission && JSON.stringify(m.published[key] ?? '') !== JSON.stringify(m.submission[key] ?? ''); }
function iconUrl(m, which) { return '/admin/api/mods/' + encodeURIComponent(m.id) + '/icon?which=' + which + '&t=' + encodeURIComponent((which === 'pending' ? m.submission?.submittedAt : m.published?.approvedAt) || ''); }
function status(m) {
  if (m.submission?.status === 'pending') return m.published ? ['更新待审核', 'chip--off'] : ['新模组待审核', 'chip--off'];
  if (m.hidden) return ['已隐藏', 'chip--off'];
  if (m.published) return ['已发布', 'chip--ok'];
  return ['首次提交被驳回', 'chip--off'];
}
async function load() {
  loading.value = true;
  try {
    const r = await Admin.api('/mods');
    if (!r?.mods) throw Error(r?.message || '读取模组列表失败');
    mods.value = r.mods;
  } catch (e) { Admin.banner(e.message, 'err'); }
  finally { loading.value = false; }
}
async function act(promise) {
  try {
    const r = await promise;
    Admin.banner(r?.message || '操作失败', r?.ok ? 'ok' : 'err');
    await load();
  } catch (e) { Admin.banner(e.message, 'err'); }
}
const path = m => '/mods/' + encodeURIComponent(m.id);
function approve(m) {
  if (!confirm('通过 ' + m.id + ' 的这次提交？通过后玩家将立即下载新版本。')) return;
  return act(Admin.api(path(m) + '/approve', { method: 'POST', body: {} }));
}
function reject(m) {
  const reason = prompt('驳回理由（会展示给上传者，可留空）：', '');
  if (reason === null) return;
  return act(Admin.api(path(m) + '/reject', { method: 'POST', body: { reason } }));
}
function toggleHidden(m) { return act(Admin.api(path(m) + '/hidden', { method: 'POST', body: { hidden: !m.hidden } })); }
function purge(m) {
  if (!confirm('彻底删除模组 ' + m.id + '？已发布版本和所有提交都会删除，id 将被释放，此操作不可恢复。')) return;
  return act(Admin.api(path(m), { method: 'DELETE' }));
}
onMounted(async () => { if (await Admin.ensureAuth()) await load(); });
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>模组审核</h1><p>启动器账号提交的新模组和更新都需要在这里人工审核。服务端不检查 Pak 文件，请核对下载直链和 sha256 后再通过。更新通过前，玩家仍下载当前已发布版本。</p></div>
    <section class="card">
      <div class="panel-head"><div><h2>{{ view === 'pending' ? '待审核' : '全部模组' }}</h2><p class="muted">驳回只作废这次提交，id 仍归原上传者；彻底删除会删除整个模组并释放 id。</p></div>
        <div class="btn-row">
          <button class="btn btn--small" :class="view === 'pending' ? '' : 'btn--outline'" type="button" @click="view = 'pending'">待审核（{{ pendingCount }}）</button>
          <button class="btn btn--small" :class="view === 'all' ? '' : 'btn--outline'" type="button" @click="view = 'all'">全部（{{ mods.length }}）</button>
          <button class="btn btn--outline btn--small" type="button" :disabled="loading" @click="load">刷新</button>
        </div></div>
      <div class="search-row"><div class="field"><span>筛选</span><input v-model="filter" placeholder="按 id、名称或上传者搜索"></div></div>
      <div v-if="!visibleMods.length" class="empty">{{ view === 'pending' ? '没有待审核的提交' : '暂无模组' }}</div>
      <article v-for="m in visibleMods" :key="m.id" class="card" style="margin-top:12px">
        <div class="panel-head">
          <div><h2 class="mono">{{ m.id }}</h2><p class="muted">上传者 {{ m.ownerName }}（启动器账号 #{{ m.ownerId }}）· 创建于 {{ time(m.createdAt) }} · 下载 {{ m.downloads || 0 }} 次 · 点赞 {{ (m.likedBy || []).length }}</p></div>
          <span class="chip" :class="status(m)[1]">{{ status(m)[0] }}</span>
        </div>
        <div class="table-wrap"><table class="md-table">
          <thead><tr><th>字段</th><th>当前已发布</th><th>{{ m.submission?.status === 'rejected' ? '最近一次提交（已驳回）' : '本次提交' }}</th></tr></thead>
          <tbody>
            <tr><td>图标</td>
              <td><img v-if="m.published?.iconFile" :src="iconUrl(m, 'published')" width="56" height="56" style="object-fit:cover;border-radius:8px" alt=""><span v-else class="muted">-</span></td>
              <td><img v-if="m.submission?.iconFile" :src="iconUrl(m, 'pending')" width="56" height="56" style="object-fit:cover;border-radius:8px" alt=""><span v-else class="muted">{{ m.submission ? '沿用当前图标' : '-' }}</span></td></tr>
            <tr v-for="[key, label] in fields" :key="key" :style="changed(m, key) ? 'background:rgba(255,170,0,.12)' : ''">
              <td>{{ label }}<span v-if="changed(m, key)" class="chip chip--off" style="margin-left:6px">有变化</span></td>
              <td style="max-width:320px;word-break:break-all;white-space:pre-wrap"><a v-if="key === 'downloadUrl' && m.published?.downloadUrl" :href="m.published.downloadUrl" target="_blank" rel="noopener noreferrer">{{ m.published.downloadUrl }}</a><span v-else :class="key === 'sha256' ? 'mono' : ''">{{ m.published ? show(key, m.published[key]) : '-' }}</span></td>
              <td style="max-width:320px;word-break:break-all;white-space:pre-wrap"><a v-if="key === 'downloadUrl' && m.submission?.downloadUrl" :href="m.submission.downloadUrl" target="_blank" rel="noopener noreferrer">{{ m.submission.downloadUrl }}</a><span v-else :class="key === 'sha256' ? 'mono' : ''">{{ m.submission ? show(key, m.submission[key]) : '-' }}</span></td>
            </tr>
            <tr><td>时间</td><td>{{ m.published ? '通过于 ' + time(m.published.approvedAt) + '（' + m.published.approvedBy + '）' : '-' }}</td>
              <td>{{ m.submission ? '提交于 ' + time(m.submission.submittedAt) : '-' }}<template v-if="m.submission?.status === 'rejected'"><br>驳回于 {{ time(m.submission.reviewedAt) }}（{{ m.submission.reviewedBy }}）：{{ m.submission.rejectReason || '未填写理由' }}</template></td></tr>
          </tbody>
        </table></div>
        <div class="btn-row" style="margin-top:10px">
          <template v-if="m.submission?.status === 'pending'">
            <button class="btn btn--small" type="button" @click="approve(m)">通过</button>
            <button class="btn btn--outline btn--small" type="button" @click="reject(m)">驳回</button>
          </template>
          <button v-if="m.published" class="btn btn--outline btn--small" type="button" @click="toggleHidden(m)">{{ m.hidden ? '取消隐藏' : '隐藏' }}</button>
          <button class="btn btn--danger btn--small" type="button" @click="purge(m)">彻底删除</button>
        </div>
      </article>
    </section>
  </main>
</template>

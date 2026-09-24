<script setup>
import { computed, onMounted, ref } from 'vue';

const Admin = window.Admin;
const items = ref([]);
const busy = ref(false);
const file = ref(null);
const version = ref('');
const description = ref('');
const canManage = ref(false);
const totalSize = computed(() => items.value.reduce((sum, item) => sum + Number(item.size || 0), 0));

function size(bytes) {
  if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
  if (bytes < 1024 * 1024 * 1024) return (bytes / 1024 / 1024).toFixed(2) + ' MB';
  return (bytes / 1024 / 1024 / 1024).toFixed(2) + ' GB';
}
function date(value) { return value ? new Date(value).toLocaleString() : '—'; }
async function load() {
  try {
    const response = await Admin.api('/patch-paks');
    if (!response?.patches) throw Error(response?.message || '读取 Pak 列表失败');
    items.value = response.patches;
  } catch (error) { Admin.banner(error.message, 'err'); }
}
async function upload() {
  if (!file.value) { Admin.banner('请选择 .pak 文件', 'err'); return; }
  if (!canManage.value) { Admin.banner('需要 Patch Pak 管理权限', 'err'); return; }
  const form = new FormData();
  form.append('pak', file.value);
  form.append('version', version.value.trim());
  form.append('description', description.value.trim());
  busy.value = true;
  try {
    const response = await fetch('/admin/api/patch-paks', { method: 'POST', body: form, credentials: 'same-origin' });
    const result = await response.json();
    if (!response.ok || !result.ok) throw Error(result.message || `上传失败 (${response.status})`);
    Admin.banner(`${result.patch.fileName} 上传完成 · SHA-256 ${result.patch.sha256}`, 'ok');
    file.value = null; version.value = ''; description.value = '';
    document.getElementById('pak-file').value = '';
    await load();
  } catch (error) { Admin.banner(error.message, 'err'); }
  finally { busy.value = false; }
}
async function remove(item) {
  if (!canManage.value || !confirm(`确定删除 ${item.fileName}？启动器将不再从清单获取它。`)) return;
  try {
    const result = await Admin.api('/patch-paks/' + encodeURIComponent(item.id), { method: 'DELETE' });
    Admin.banner(result?.message || '删除失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) await load();
  } catch (error) { Admin.banner(error.message, 'err'); }
}
async function copy(value) {
  try { await navigator.clipboard.writeText(value); Admin.banner('SHA-256 已复制', 'ok'); }
  catch { Admin.banner('无法访问剪贴板，请手动复制哈希值', 'err'); }
}
onMounted(async () => {
  if (!(await Admin.ensureAuth())) return;
  const session = await fetch('/admin/api/session', { cache: 'no-store' }).then(response => response.json()).catch(() => null);
  canManage.value = !!session?.isOwner || (session?.permissions || []).includes('patchPaks');
  if (!canManage.value) { Admin.banner('此页面需要 Patch Pak 管理权限。', 'err'); return; }
  await load();
});
</script>

<template>
  <main class="page"><div id="banner-host"></div>
    <div class="page-head"><h1>Patch Pak 更新</h1><p>上传游戏补丁包并为启动器提供 SHA-256 校验清单。文件和清单均保存在当前配置的数据存储中。</p></div>
    <div class="card-grid"><div class="stat-card"><div class="stat-card__label">已发布 Pak</div><div class="stat-card__value">{{ items.length }}</div><div class="stat-card__hint">启动器可通过公开清单读取</div></div><div class="stat-card"><div class="stat-card__label">数据占用</div><div class="stat-card__value">{{ size(totalSize) }}</div><div class="stat-card__hint">最多 128 MB / 文件</div></div></div>
    <section v-if="canManage" class="card"><h2>上传 Patch Pak</h2><p class="muted">文件上传后立即发布。允许 .pak 文件，单个文件最大 128 MB；SHA-256 由服务器计算。</p>
      <form class="pak-upload" @submit.prevent="upload"><div class="form-grid"><label class="field"><span>Pak 文件</span><input id="pak-file" type="file" accept=".pak,application/octet-stream" required @change="file = $event.target.files?.[0] || null"></label><label class="field"><span>版本 / 标识（可选）</span><input v-model="version" maxlength="64" placeholder="例如 1.2.0"></label><label class="field"><span>说明（可选）</span><input v-model="description" maxlength="500" placeholder="简要说明本次补丁"></label></div><div v-if="file" class="selected-file"><strong>{{ file.name }}</strong><span>{{ size(file.size) }}</span></div><button class="btn" type="submit" :disabled="busy">{{ busy ? '正在上传并计算哈希…' : '上传并发布' }}</button></form>
    </section>
    <section class="card"><div class="panel-head"><div><h2>已发布列表</h2><p class="muted">SHA-256 可供启动器对已下载文件进行本地校验；清单不要求后台登录。</p></div><button class="btn btn--outline btn--small" type="button" @click="load">刷新</button></div>
      <div class="table-wrap"><div v-if="!items.length" class="empty">尚未上传 Patch Pak</div><table v-else class="md-table"><thead><tr><th>文件</th><th>版本</th><th>大小</th><th>SHA-256</th><th>上传时间</th><th>下载</th><th v-if="canManage">操作</th></tr></thead><tbody><tr v-for="item in items" :key="item.id"><td><strong>{{ item.fileName }}</strong><small v-if="item.description" class="pak-description">{{ item.description }}</small></td><td>{{ item.version || '—' }}</td><td>{{ size(item.size) }}</td><td><button class="hash-button" type="button" :title="item.sha256" @click="copy(item.sha256)">{{ item.sha256 }}</button></td><td>{{ date(item.createdAt) }}</td><td><a class="btn btn--outline btn--small" :href="item.downloadUrl">下载</a></td><td v-if="canManage"><button class="btn btn--danger btn--small" type="button" @click="remove(item)">删除</button></td></tr></tbody></table></div>
    </section>
    <section class="card"><h2>启动器接口</h2><dl class="patch-api"><dt>清单</dt><dd><code>GET /patch-paks</code> · 返回文件名、版本、大小、SHA-256 与下载地址</dd><dt>下载</dt><dd><code>GET /patch-paks/{id}/download</code> · 返回 Pak 二进制，带 ETag（SHA-256）响应头</dd></dl></section>
  </main>
</template>

<style>
.selected-file{display:flex;gap:12px;align-items:center;margin:0 0 14px;color:var(--muted)}.selected-file strong{color:var(--text);overflow-wrap:anywhere}.pak-description{display:block;max-width:260px;white-space:normal;color:var(--muted);font-size:11px}.hash-button{border:0;padding:0;background:none;color:var(--primary);font:11px ui-monospace,Consolas,monospace;max-width:245px;overflow:hidden;text-overflow:ellipsis;cursor:pointer}.patch-api{display:grid;grid-template-columns:70px 1fr;gap:9px;margin:0}.patch-api dt{color:var(--muted)}.patch-api dd{margin:0;overflow-wrap:anywhere}.patch-api code{color:var(--primary)}@media(max-width:780px){.patch-api{grid-template-columns:1fr}.patch-api dd{margin-bottom:8px}.md-table td:nth-child(4){min-width:140px}}
</style>

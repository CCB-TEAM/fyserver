<script setup>
import { computed, defineAsyncComponent, onMounted, onUnmounted, ref } from 'vue';
import AppIcon from './AppIcon.vue';

const route = location.pathname.split('/').pop()?.replace(/\.html$/, '') || 'index';
const pages = import.meta.glob('./pages/*.vue');
const loader = pages[`./pages/${route}.vue`] || pages['./pages/index.vue'];
const Page = defineAsyncComponent(loader);
const isLogin = route === 'login';
const isStandalone = isLogin || route === 'database-setup';
const links = [
  ['index', 'home', '基础信息', '监控'],
  ['matches', 'matches', '对局监控', '监控'],
  ['match-management', 'matches', '对局管理', '监控'],
  ['users', 'users', '用户管理', '运营'],
  ['content', 'content', '内容配置', '运营'],
  ['store', 'store', '商店', '运营'],
  ['redeem', 'redeem', '兑换码', '运营'],
  ['server-config', 'settings', '服务器设置', '系统'],
  ['system-settings', 'cpu', '系统设置', '系统'],
  ['accounts', 'users', '后台用户', '系统'],
  ['profile', 'users', '个人主页', '系统'],
];
const active = { 'user-detail': 'users', 'account-detail': 'accounts', 'account-actions': 'accounts', 'account-logins': 'accounts', 'server-config-edit': 'server-config', 'database-setup': 'system-settings' }[route] || route;
const title = ({ 'user-detail': '玩家详情', 'account-detail': '后台账号详情', 'account-actions': '历史操作', 'account-logins': '登录与 IP', 'server-config-edit': '编辑服务器配置', 'database-setup': '玩家数据库初始化' })[route] || links.find(x => x[0] === active)?.[2] || '基础信息';
const sidebarClosed = ref(matchMedia('(max-width:720px)').matches);
const username = ref('服务运行中');
const avatarUrl = ref('');
const permissions = ref([]);
const dark = ref(localStorage.getItem('fyserver.admin.dark') === '1');
const theme = ref(window.Admin.readTheme());
const brandTitle = computed(() => theme.value.title || 'FYServer');
const brandSubtitle = computed(() => theme.value.subtitle || 'SERVER CONTROL CENTER');
const visibleLinks = computed(() => links.filter(x => x[0] !== 'accounts' || permissions.value.includes('permissions')));
let heartbeat;
let permissionNotified = false;

function toggleTheme() {
  dark.value = !dark.value;
  localStorage.setItem('fyserver.admin.dark', dark.value ? '1' : '0');
  window.Admin.applyTheme({ ...theme.value, dark: dark.value });
}
async function logout() {
  try { await window.Admin.api('/logout', { method: 'POST' }); } finally { location.href = '/admin-ui/login.html'; }
}
async function session() {
  try {
    const s = await fetch('/admin/api/session', { cache: 'no-store' }).then(r => r.json());
    username.value = s.username || '管理员';
    avatarUrl.value = s.avatarUrl || '';
    permissions.value = s.permissions || [];
    const required = { users: 'players', matches: 'matches', content: 'content', store: 'content', redeem: 'content', 'server-config': 'serverConfig', 'system-settings': 'systemSettings', accounts: 'permissions' }[active];
    if (required && !permissions.value.includes(required) && !permissionNotified && document.getElementById('banner-host')) {
      permissionNotified = true;
      window.Admin.banner('当前账号对此页面只有查看权限，修改操作会被服务器拒绝。', 'info');
    }
  } catch { /* A page-level guard handles unavailable sessions. */ }
}

onMounted(() => {
  document.body.classList.toggle('auth-body', isStandalone);
  window.Admin.applyTheme({ ...theme.value, dark: dark.value });
  if (!isLogin) {
    session();
    heartbeat = setInterval(() => { if (document.visibilityState === 'visible') session(); }, 45000);
  }
});
onUnmounted(() => clearInterval(heartbeat));
</script>

<template>
  <Page v-if="isStandalone" />
  <div v-else class="admin-shell" :class="{ 'sidebar-collapsed': sidebarClosed }">
    <button type="button" class="sidebar-scrim" aria-label="关闭侧边栏" @click="sidebarClosed = true"></button>
    <aside class="admin-sidebar">
      <div class="admin-brand"><img src="/assets/fyserver-logo-transparent.png" alt="FYServer"><span class="brand-copy"><b>{{ brandTitle }}</b><small>{{ brandSubtitle }}</small></span></div>
      <nav aria-label="后台导航">
        <template v-for="(link, i) in visibleLinks" :key="link[0]">
          <span v-if="i === 0 || visibleLinks[i - 1][3] !== link[3]" class="side-group-label">{{ link[3] }}</span>
          <a class="side-link" :class="{ 'is-active': active === link[0] }" :href="`/admin-ui/${link[0]}.html`" :title="link[2]" :aria-current="active === link[0] ? 'page' : undefined"><span class="side-icon"><AppIcon :name="link[1]" /></span><span class="side-label">{{ link[2] }}</span><i></i></a>
        </template>
      </nav>
      <div class="sidebar-actions"><button class="side-action" type="button" @click="toggleTheme"><span><AppIcon name="sun" /></span><span>切换主题</span></button><button class="side-action danger" type="button" @click="logout"><span><AppIcon name="logout" /></span><span>退出登录</span></button></div>
    </aside>
    <div class="admin-stage">
      <header class="admin-topbar"><button class="round-btn" type="button" aria-label="切换侧边栏" :aria-expanded="!sidebarClosed" @click="sidebarClosed = !sidebarClosed"><AppIcon name="menu" /></button><div class="breadcrumbs"><span>FYServer</span><b>/</b><strong>{{ title }}</strong></div><a class="top-status profile-shortcut" href="/admin-ui/profile.html" title="个人主页"><i></i><span>{{ username }}</span><img class="top-avatar" :src="avatarUrl || '/admin-ui/assets/admin-avatar-placeholder.svg'" alt="个人头像"></a></header>
      <Page />
      <footer class="page-footer">FYServer · Server Control Center</footer>
    </div>
  </div>
</template>

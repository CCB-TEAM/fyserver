<template>
<button type="button" class="auth-theme" id="auth-theme" aria-label="切换主题"><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M4.93 4.93l1.42 1.42M17.65 17.65l1.42 1.42M2 12h2M20 12h2M4.93 19.07l1.42-1.42M17.65 6.35l1.42-1.42"/></svg></button>
<main class="auth-wrap"><section class="auth-card">
<header class="auth-head"><div class="auth-title"><img src="/assets/fyserver-logo-transparent.png" alt="FYServer"><div><span>Admin</span> <strong id="title-mode">Login</strong></div></div><p id="subtitle">登录 FYServer 服务器控制中心</p></header>
<div id="banner-host"></div>
<form id="auth-form" class="auth-form" autocomplete="on">
<label class="auth-field"><span>管理员用户名</span><div><i><svg viewBox="0 0 24 24" aria-hidden="true"><circle cx="12" cy="8" r="4"/><path d="M4.8 20c.7-4 3.1-6 7.2-6s6.5 2 7.2 6"/></svg></i><input id="username" name="username" autocomplete="username" placeholder="请输入管理员用户名" required maxlength="32"></div></label>
<label class="auth-field"><span>密码</span><div><i><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="3"/><path d="M8.5 10V7.5a3.5 3.5 0 0 1 7 0V10M12 14v3"/></svg></i><input id="password" name="password" type="password" autocomplete="current-password" placeholder="请输入密码" required minlength="10"><button type="button" id="reveal" aria-label="显示密码"><svg class="eye-open" viewBox="0 0 24 24" aria-hidden="true"><path d="M2.5 12s3.5-6 9.5-6 9.5 6 9.5 6-3.5 6-9.5 6-9.5-6-9.5-6Z"/><circle cx="12" cy="12" r="2.6"/></svg><svg class="eye-closed" viewBox="0 0 24 24" aria-hidden="true"><path d="M3 3l18 18M10.6 6.1A10.7 10.7 0 0 1 12 6c6 0 9.5 6 9.5 6a17 17 0 0 1-2.3 3M6.2 6.3C3.8 8 2.5 12 2.5 12s3.5 6 9.5 6a9.8 9.8 0 0 0 3.2-.5M9.9 9.8a3 3 0 0 0 4.3 4.3"/></svg></button></div></label>
<label class="auth-field" id="confirm-field" hidden style="display:none"><span>确认密码</span><div><i><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="5" y="10" width="14" height="11" rx="3"/><path d="M8.5 10V7.5a3.5 3.5 0 0 1 7 0V10M12 14v3"/></svg></i><input id="confirm" name="confirm" type="password" autocomplete="new-password" placeholder="请再次输入密码" minlength="10" disabled></div></label>
<p class="auth-tip" id="hint">正在检查服务器状态…</p>
<button class="auth-submit" id="submit" type="submit"><img src="/assets/fyserver-logo-transparent.png" alt=""><span>登录</span></button>
</form></section></main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {

  window.Admin.hydrateIcons();
  await import('../legacy/login.js');
});
</script>

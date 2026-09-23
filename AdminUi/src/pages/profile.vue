<template>
  <main class="page">
    <div id="banner-host"></div>
    <div class="page-head"><h1>管理员个人主页</h1><p>管理自己的后台账号名称、头像和登录密码。</p></div>
    <section class="card profile-hero">
      <div class="profile-avatar-wrap"><img id="avatar-current" src="/assets/admin-avatar-placeholder.svg" alt="管理员头像"></div>
      <div><h2 id="profile-heading">管理员</h2><p class="muted" id="profile-meta">加载账号信息中…</p></div>
    </section>
    <div class="split-2">
      <section class="card"><h2>更换头像</h2><p class="muted">上传图片后可拖动、缩放并裁剪成 1:1 圆形头像。头像图片会保存到服务器。</p>
        <label class="field"><span>选择图片</span><input id="avatar-file" type="file" accept="image/png,image/jpeg,image/webp,image/gif"></label>
        <div class="crop-layout"><div class="crop-frame"><canvas id="crop-canvas" width="320" height="320" aria-label="头像裁剪预览"></canvas></div><div class="crop-controls"><label class="field"><span>缩放</span><input id="crop-zoom" type="range" min="1" max="3" step="0.01" value="1" disabled></label><p class="muted">在圆形预览中拖动图片调整位置。</p><button class="btn" id="avatar-save" type="button" disabled>保存头像</button></div></div>
      </section>
      <section class="card"><h2>账号名称</h2><p class="muted">修改名称需要当前密码确认，名称不能与其他后台账号重复。</p>
        <form id="profile-form"><label class="field"><span>账号名称</span><input id="profile-username" minlength="3" maxlength="32" required autocomplete="username"></label><label class="field"><span>当前密码</span><input id="profile-current-password" type="password" required autocomplete="current-password"></label><button class="btn" type="submit">保存账号名称</button></form>
      </section>
    </div>
    <section class="card"><h2>修改密码</h2><p class="muted">修改成功后会退出当前会话，需要使用新密码重新登录。</p>
      <form id="password-form"><div class="form-grid"><label class="field"><span>当前密码</span><input id="password-current" type="password" required autocomplete="current-password"></label><label class="field"><span>新密码（至少 10 位）</span><input id="password-new" type="password" minlength="10" maxlength="256" required autocomplete="new-password"></label><label class="field"><span>确认新密码</span><input id="password-confirm" type="password" minlength="10" maxlength="256" required autocomplete="new-password"></label></div><button class="btn" type="submit">更新密码</button></form>
    </section>
  </main>
</template>
<script setup>
import { onMounted } from 'vue';
onMounted(async () => {
  window.Admin.hydrateIcons();
  await import('../legacy/profile.js');
});
</script>
<style>
.profile-hero{display:flex;align-items:center;gap:18px}.profile-hero h2{margin:0 0 5px}.profile-hero p{margin:0}.profile-avatar-wrap,.top-avatar,.account-avatar{border-radius:50%;overflow:hidden;object-fit:cover;background:var(--surface-custom)}.profile-avatar-wrap{width:96px;height:96px;flex:none;border:3px solid color-mix(in srgb,var(--primary) 24%,white);box-shadow:0 8px 24px rgba(20,50,90,.12)}.profile-avatar-wrap img{display:block;width:100%;height:100%;object-fit:cover}.crop-layout{display:flex;gap:22px;align-items:center;margin-top:18px}.crop-frame{width:220px;height:220px;border-radius:50%;overflow:hidden;border:4px solid color-mix(in srgb,var(--primary) 32%,white);box-shadow:0 10px 28px rgba(20,50,90,.14);background:#e8edf5;flex:none}.crop-frame canvas{display:block;width:100%;height:100%;touch-action:none;cursor:grab}.crop-frame canvas:active{cursor:grabbing}.crop-controls{flex:1;min-width:150px}.profile-shortcut{text-decoration:none;color:inherit}.top-avatar{width:34px;height:34px;border:2px solid var(--outline)}.account-avatar{width:34px;height:34px;vertical-align:middle;margin-right:9px}.profile-shortcut:hover{opacity:.85}@media(max-width:760px){.crop-layout{flex-direction:column;align-items:flex-start}.crop-frame{width:190px;height:190px}}
</style>

import '../admin-v4.js';
const Admin = window.Admin;

(async function () {
  if (!await Admin.ensureAuth()) return;
  await Admin.mount('profile', '管理员个人主页');
  const $ = id => document.getElementById(id);
  const canvas = $('crop-canvas');
  const context = canvas.getContext('2d');
  const size = canvas.width;
  let profile;
  let image;
  let imageUrl = '';
  let x = 0, y = 0, width = 0, height = 0;
  let dragging = false;
  let lastPoint = null;

  async function load() {
    profile = await Admin.api('/me');
    if (!profile?.id) { Admin.banner(profile?.message || '无法读取管理员账号资料', 'err'); return; }
    $('profile-heading').textContent = profile.username;
    $('profile-meta').textContent = (profile.isOwner ? 'Owner' : '后台成员') + ' · 最后登录：' + (profile.lastLoginAt ? new Date(profile.lastLoginAt).toLocaleString() : '暂无记录');
    $('profile-username').value = profile.username;
    const avatar = profile.avatarUrl || '/admin-ui/assets/admin-avatar-placeholder.svg';
    $('avatar-current').src = avatar;
    const topAvatar = document.querySelector('.top-avatar');
    if (topAvatar) topAvatar.src = avatar;
  }
  function clampPosition() {
    x = Math.min(0, Math.max(size - width, x));
    y = Math.min(0, Math.max(size - height, y));
  }
  function draw() {
    context.clearRect(0, 0, size, size);
    if (image) context.drawImage(image, x, y, width, height);
  }
  $('avatar-file').onchange = () => {
    const file = $('avatar-file').files?.[0];
    if (!file) return;
    if (file.size > 15 * 1024 * 1024) { Admin.banner('原始图片不能超过 15 MB', 'err'); $('avatar-file').value = ''; return; }
    image = null;
    $('avatar-save').disabled = true;
    $('crop-zoom').disabled = true;
    draw();
    if (imageUrl) URL.revokeObjectURL(imageUrl);
    imageUrl = URL.createObjectURL(file);
    const nextImage = new Image();
    nextImage.onload = () => {
      if (nextImage.naturalWidth > 12000 || nextImage.naturalHeight > 12000) { Admin.banner('图片尺寸过大，请换一张较小的图片', 'err'); return; }
      image = nextImage;
      const scale = Math.max(size / image.naturalWidth, size / image.naturalHeight);
      width = image.naturalWidth * scale;
      height = image.naturalHeight * scale;
      x = (size - width) / 2;
      y = (size - height) / 2;
      $('crop-zoom').value = '1';
      $('crop-zoom').disabled = false;
      $('avatar-save').disabled = false;
      draw();
    };
    nextImage.onerror = () => Admin.banner('无法读取这张图片', 'err');
    nextImage.src = imageUrl;
  };
  $('crop-zoom').oninput = event => {
    if (!image) return;
    const zoom = Number(event.currentTarget.value) || 1;
    const centerX = x + width / 2, centerY = y + height / 2;
    const baseScale = Math.max(size / image.naturalWidth, size / image.naturalHeight);
    width = image.naturalWidth * baseScale * zoom;
    height = image.naturalHeight * baseScale * zoom;
    x = centerX - width / 2;
    y = centerY - height / 2;
    clampPosition();
    draw();
  };
  canvas.addEventListener('pointerdown', event => {
    if (!image) return;
    dragging = true;
    lastPoint = { x: event.clientX, y: event.clientY };
    canvas.setPointerCapture(event.pointerId);
  });
  canvas.addEventListener('pointermove', event => {
    if (!dragging || !lastPoint) return;
    const rect = canvas.getBoundingClientRect();
    const ratio = size / rect.width;
    x += (event.clientX - lastPoint.x) * ratio;
    y += (event.clientY - lastPoint.y) * ratio;
    lastPoint = { x: event.clientX, y: event.clientY };
    clampPosition();
    draw();
  });
  const stopDragging = () => { dragging = false; lastPoint = null; };
  canvas.addEventListener('pointerup', stopDragging);
  canvas.addEventListener('pointercancel', stopDragging);

  $('avatar-save').onclick = async () => {
    if (!image) return;
    const output = document.createElement('canvas');
    output.width = output.height = 512;
    output.getContext('2d').drawImage(image, x * 512 / size, y * 512 / size, width * 512 / size, height * 512 / size);
    const blob = await new Promise(resolve => output.toBlob(resolve, 'image/png'));
    if (!blob) { Admin.banner('头像裁剪失败，请重试', 'err'); return; }
    const form = new FormData();
    form.append('avatar', blob, 'avatar.png');
    $('avatar-save').disabled = true;
    try {
      const response = await fetch('/admin/api/me/avatar', { method: 'POST', body: form, credentials: 'same-origin' });
      const result = await response.json();
      Admin.banner(result?.message || (response.ok ? '头像已更新' : '头像上传失败'), result?.ok ? 'ok' : 'err');
      if (response.ok && result?.avatarUrl) {
        $('avatar-current').src = result.avatarUrl;
        const topAvatar = document.querySelector('.top-avatar');
        if (topAvatar) topAvatar.src = result.avatarUrl;
      }
    } catch { Admin.banner('头像上传失败，请检查网络连接', 'err'); }
    finally { $('avatar-save').disabled = !image; }
  };

  $('profile-form').onsubmit = async event => {
    event.preventDefault();
    const result = await Admin.api('/me/profile', { method: 'PUT', body: { username: $('profile-username').value.trim(), currentPassword: $('profile-current-password').value } });
    Admin.banner(result?.message || '账号名称修改失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) {
      $('profile-current-password').value = '';
      await load();
      const topName = document.querySelector('.profile-shortcut span');
      if (topName) topName.textContent = profile.username;
    }
  };
  $('password-form').onsubmit = async event => {
    event.preventDefault();
    if ($('password-new').value !== $('password-confirm').value) { Admin.banner('两次输入的新密码不一致', 'err'); return; }
    const result = await Admin.api('/me/password', { method: 'POST', body: { currentPassword: $('password-current').value, newPassword: $('password-new').value } });
    Admin.banner(result?.message || '密码修改失败', result?.ok ? 'ok' : 'err');
    if (result?.ok) {
      $('password-form').reset();
      setTimeout(() => { location.href = '/admin-ui/login.html'; }, 1200);
    }
  };
  await load();
})();

// fyserver 静态后台 —— 共用工具（无框架、无 CDN 依赖）
window.Admin = (function () {
    'use strict';

    function esc(value) {
        if (value === null || value === undefined) return '';
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    async function api(path, options) {
        const opts = Object.assign({ headers: {} }, options || {});
        if (opts.body !== undefined && typeof opts.body !== 'string') {
            opts.headers['Content-Type'] = 'application/json';
            opts.body = JSON.stringify(opts.body);
        }
        const response = await fetch('/admin/api' + path, opts);
        if (response.status === 401) {
            location.href = '/admin-ui/login.html?next=' + encodeURIComponent(location.pathname + location.search);
            throw new Error('未授权');
        }
        const text = await response.text();
        let data = null;
        try { data = text ? JSON.parse(text) : null; } catch (e) { data = { ok: false, message: text }; }
        if (!response.ok && data && data.message === undefined) data.message = 'HTTP ' + response.status;
        return data;
    }

    function banner(message, kind) {
        const host = document.getElementById('banner-host');
        if (!host) { alert(message); return; }
        host.innerHTML = '<div class="banner banner--' + (kind || 'ok') + '">' + esc(message) + '</div>';
        if (kind !== 'err') setTimeout(() => { if (host.innerHTML.indexOf(esc(message)) >= 0) host.innerHTML = ''; }, 6000);
    }

    /** 顶栏：active 为概览/用户/对局/内容 之一 */
    function nav(active) {
        const items = [
            ['overview', '/admin-ui/index.html', '概览'],
            ['users', '/admin-ui/users.html', '用户'],
            ['matches', '/admin-ui/matches.html', '对局'],
            ['content', '/admin-ui/content.html', '内容'],
        ];
        const links = items.map(([key, href, label]) =>
            '<a class="nav-link' + (key === active ? ' is-active' : '') + '" href="' + href + '">' + label + '</a>').join('');
        return '<header class="app-bar">' +
            '<div class="app-bar__title"><span class="brand-dot"></span><span>fyserver 后台</span></div>' +
            '<nav class="app-bar__nav">' + links + '</nav>' +
            '<div class="app-bar__end"><span id="whoami" class="mono"></span>' +
            '<a class="nav-link" href="#" id="logout">退出</a></div>' +
            '</header>';
    }

    function mount(active, title) {
        document.body.insertAdjacentHTML('afterbegin', nav(active));
        const logout = document.getElementById('logout');
        if (logout) {
            logout.addEventListener('click', async function (event) {
                event.preventDefault();
                await api('/logout', { method: 'POST' });
                location.href = '/admin-ui/login.html';
            });
        }
        return fetch('/admin/api/session').then(r => r.json()).then(function (s) {
            const who = document.getElementById('whoami');
            if (who && s) who.textContent = (s.loopback ? '本机' : '远程') + (s.keyConfigured ? ' · 已配密钥' : ' · 仅本机');
        }).catch(() => {});
    }

    async function ensureAuth() {
        try {
            const s = await api('/session');
            if (s && s.authorized) return true;
        } catch (e) { return false; }
        location.href = '/admin-ui/login.html?next=' + encodeURIComponent(location.pathname);
        return false;
    }

    return { esc, api, banner, nav, mount, ensureAuth };
})();

import '../admin-v4.js';
const Admin = window.Admin;

    (async function () {
        if (!await Admin.ensureAuth()) return;
        Admin.mount('users', '用户');

        const params = new URLSearchParams(location.search);
        const table = document.getElementById('user-table');

        async function act(id, action, label) {
            if (!confirm('确认对用户 ' + id + ' 执行「' + label + '」？')) return;
            let path = '/users/' + id;
            let method = 'POST';
            if (action === 'delete') { method = 'DELETE'; }
            else { path += '/' + action + (action === 'kick' ? '?reason=' + encodeURIComponent(prompt('踢出原因（可留空）', '您已被服务器断开连接') || '') : ''); }
            const r = await Admin.api(path, { method: method });
            Admin.banner((r && r.message) || '完成', r && r.ok ? 'ok' : 'err');
            await load();
        }

        function actionsFor(u) {
            return '<form class="inline-form" onsubmit="return false">' +
                '<a class="btn btn--small btn--outline" href="/admin-ui/user-detail.html?id=' + u.id + '">详情 / 封禁</a>' +
                '<button class="btn btn--small btn--outline" data-act="kick" data-id="' + u.id + '">踢出</button>' +
                '<button class="btn btn--small btn--danger" data-act="delete" data-id="' + u.id + '">删除</button>' +
                '</form>';
        }

        async function load() {
            const q = document.getElementById('q').value.trim();
            const data = await Admin.api('/users' + (q ? '?q=' + encodeURIComponent(q) : ''));
            const users = (data && data.users) || [];
            table.innerHTML = '<table class="md-table"><thead><tr>' +
                '<th>ID</th><th>玩家名称</th><th>最近登录</th><th>登录 IP</th><th>登录设备</th><th>卡组</th><th>状态</th><th>创建时间</th><th>操作</th>' +
                '</tr></thead><tbody>' +
                (users.length === 0 ? '<tr><td colspan="9" class="empty">没有匹配的用户</td></tr>' : users.map(function (u) {
                    return '<tr>' +
                        '<td class="mono">' + u.id + '</td>' +
                        '<td><a href="/admin-ui/user-detail.html?id=' + u.id + '">' + Admin.esc(u.displayName || (u.name + '#' + String(u.tag).padStart(4, '0'))) + '</a></td>' +
                        '<td class="mono">' + Admin.esc(u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : '—') + '</td>' +
                        '<td class="mono">' + Admin.esc(u.lastLoginIp || '—') + '</td>' +
                        '<td><span title="' + Admin.esc(u.lastLoginDevice || '暂无记录') + '">' + Admin.esc(u.lastLoginDevice || '暂无记录') + '</span></td>' +
                        '<td>' + u.deckCount + '</td>' +
                        '<td>' + (u.banned ? '<span class="chip chip--err">已封禁</span> ' : '') +
                                  (u.online ? '<span class="chip chip--ok">在线</span>' : (u.banned ? '' : '<span class="chip chip--off">离线</span>')) + '</td>' +
                        '<td class="mono">' + Admin.esc(u.createdAt) + '</td>' +
                        '<td>' + actionsFor(u) + '</td>' +
                        '</tr>';
                }).join('')) + '</tbody></table>';
        }

        document.addEventListener('click', function (event) {
            const btn = event.target.closest('[data-act]');
            if (btn) { event.preventDefault(); act(btn.getAttribute('data-id'), btn.getAttribute('data-act'), btn.textContent.trim()); return; }
        });

        document.getElementById('search-form').addEventListener('submit', function (event) { event.preventDefault(); load(); });
        if (params.get('q')) document.getElementById('q').value = params.get('q');

        await load();
        const focus = params.get('id');
        if (focus) location.replace('/admin-ui/user-detail.html?id=' + encodeURIComponent(focus));
    })();

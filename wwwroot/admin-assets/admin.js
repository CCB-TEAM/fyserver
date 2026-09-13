// fyserver 后台前端脚本：危险操作二次确认 + 输入框自动提交
(function () {
    'use strict';

    // data-confirm="提示文案" 的表单/按钮点击时确认
    document.addEventListener('click', function (event) {
        var el = event.target.closest('[data-confirm]');
        if (!el) {
            return;
        }
        var message = el.getAttribute('data-confirm') || '确认执行该操作？';
        if (!window.confirm(message)) {
            event.preventDefault();
            event.stopPropagation();
        }
    });

    // data-autosubmit 的输入框回车/失焦后提交所在表单
    document.querySelectorAll('input[data-autosubmit]').forEach(function (input) {
        input.addEventListener('keydown', function (event) {
            if (event.key === 'Enter') {
                event.preventDefault();
                input.form && input.form.submit();
            }
        });
    });
})();

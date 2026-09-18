// fyserver 后台 —— 首页/公告条目的类游戏预览（纯客户端 SVG，无服务端渲染）
// 移植自 qa-1939api 后台的 frontpage 编辑器：画布尺寸与游戏客户端一致
//   0 轮播 1540×770 / 1 侧栏按钮 614×307 / 2 弹窗 1232×564
(function () {
    'use strict';

    var TYPE_CAROUSEL = 0, TYPE_BUTTON = 1, TYPE_POPUP = 2;
    var TEMPLATE_ID = { 0: 'fp-template-carousel', 1: 'fp-template-button', 2: 'fp-template-popup' };
    var TYPE_HEIGHT = { 0: 770, 1: 307, 2: 564 };
    var PLACEHOLDER_SIZE = { 0: [1540, 770], 1: [600, 300], 2: [1232, 564] };

    var state = { language: 'zh-hans', entry: {} };

    function placeholder(w, h) {
        return 'data:image/svg+xml,' + encodeURIComponent(
            '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '">' +
            '<rect width="100%" height="100%" fill="#3c3c3c"/>' +
            '<text x="50%" y="50%" fill="#9aa0a6" font-size="48" text-anchor="middle" dominant-baseline="middle">' +
            w + '×' + h + '</text></svg>');
    }

    // 兼容双模式：单串 或 {"_":…,"zh-hans":…}
    function localised(field) {
        if (field === null || field === undefined) return '';
        if (typeof field === 'string' || typeof field === 'number') return String(field);
        if (typeof field === 'object') {
            if (field[state.language] !== undefined && field[state.language] !== null) return String(field[state.language]);
            if (field['_'] !== undefined && field['_'] !== null) return String(field['_']);
            var keys = Object.keys(field);
            for (var i = 0; i < keys.length; i++) {
                if (field[keys[i]] !== null && field[keys[i]] !== undefined) return String(field[keys[i]]);
            }
        }
        return '';
    }

    function parseJson(text) {
        if (!text || !text.trim()) return {};
        try {
            return JSON.parse(text);
        } catch (e) {
            return null;
        }
    }

    // 把编辑页表单读成 camelCase 条目（与 dev 后台的 currentEntry 对应）
    function readEntry() {
        var container = document.getElementById('fp-entry-json');
        var typeField = document.getElementById('Type');
        var entry = parseJson(container ? container.value : '{}');
        if (entry === null) return null;
        if (!entry.content || typeof entry.content !== 'object') entry.content = {};
        if (typeField) entry.content.type = parseInt(typeField.value, 10) || 0;
        entry.imageUrl = document.getElementById('ImageUrl') ? document.getElementById('ImageUrl').value : '';
        entry.link = document.getElementById('Link') ? document.getElementById('Link').value : '';
        entry.headingText = document.getElementById('HeadingText') ? document.getElementById('HeadingText').value : '';
        entry.headingFontSize = document.getElementById('HeadingSize') ? document.getElementById('HeadingSize').value : '';
        entry.subHeadingText = document.getElementById('SubHeadingText') ? document.getElementById('SubHeadingText').value : '';
        entry.subHeadingFontSize = document.getElementById('SubHeadingSize') ? document.getElementById('SubHeadingSize').value : '';
        return entry;
    }

    function generatePreview(entry) {
        if (!entry || !entry.content) return null;
        var type = parseInt(entry.content.type, 10);
        if (isNaN(type)) type = TYPE_CAROUSEL;

        var template = document.getElementById(TEMPLATE_ID[type]);
        if (!template) return null;

        var node = template.content.firstElementChild.cloneNode(true);
        var height = TYPE_HEIGHT[type];

        var image = node.querySelector('[data-field=image]');
        if (image) {
            var size = PLACEHOLDER_SIZE[type];
            image.setAttribute('href', entry.imageUrl || placeholder(size[0], size[1]));
        }

        var heading = node.querySelector('[data-field=heading]');
        var subHeading = node.querySelector('[data-field=sub_heading]');

        var headingSize = parseInt(localised(entry.headingFontSize) || entry.content.heading && localised(entry.content.heading.font_size), 10) || 56;
        var subSize = parseInt(localised(entry.subHeadingFontSize) || entry.content.sub_heading && localised(entry.content.sub_heading.font_size), 10) || 30;
        var subText = entry.subHeadingText !== undefined && entry.subHeadingText !== ''
            ? entry.subHeadingText
            : localised(entry.content.sub_heading && entry.content.sub_heading.text);

        var yOffset = height / 2;
        var subYOffset = height / 2 + headingSize / 2;

        if (type === TYPE_BUTTON) {
            var icon = node.querySelector('[data-field=icon]');
            if (icon) icon.setAttribute('href', entry.content.icon && localised(entry.content.icon.icon_url) || '');
            if (subText) yOffset = height / 2 - subSize / 2;
            if (subHeading) subHeading.setAttribute('y', subYOffset);
        } else if (type === TYPE_CAROUSEL) {
            if (heading) yOffset = parseInt(heading.getAttribute('y'), 10) || height / 2;
            if (subText) yOffset -= subSize * 1.05;
        }

        if (type !== TYPE_POPUP) {
            if (heading) {
                heading.setAttribute('y', yOffset);
                heading.setAttribute('font-size', headingSize);
                heading.textContent = entry.headingText || localised(entry.content.heading && entry.content.heading.text);
            }
            if (subHeading) {
                subHeading.setAttribute('font-size', subSize);
                subHeading.textContent = subText;
            }
        }

        return node;
    }

    function render() {
        var stage = document.getElementById('fp-preview-stage');
        var meta = document.getElementById('fp-preview-meta');
        if (!stage) return;

        var entry = readEntry();
        if (entry === null) {
            stage.innerHTML = '<p class="muted" style="margin:0">JSON 格式错误，预览已暂停</p>';
            if (meta) meta.textContent = '';
            return;
        }

        var node = generatePreview(entry);
        stage.innerHTML = '';
        if (node) stage.appendChild(node);

        if (meta) {
            var type = parseInt(entry.content.type, 10) || 0;
            var names = { 0: '轮播 1540×770', 1: '侧栏按钮 614×307', 2: '弹窗 1232×564' };
            meta.textContent = (names[type] || '未知类型') + ' · 语言 ' + state.language;
        }
    }

    document.addEventListener('DOMContentLoaded', function () {
        var languageSelect = document.getElementById('fp-preview-language');
        if (languageSelect) {
            state.language = languageSelect.value || state.language;
            languageSelect.addEventListener('change', function () {
                state.language = languageSelect.value;
                render();
            });
        }

        var form = document.getElementById('fp-form');
        if (form) {
            form.addEventListener('input', render);
            form.addEventListener('change', render);
        }

        render();
    });
})();

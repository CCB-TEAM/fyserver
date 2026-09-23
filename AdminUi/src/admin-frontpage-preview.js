// fyserver 静态后台 —— 首页条目的类游戏预览（纯客户端 SVG）
// 画布尺寸与游戏客户端一致：轮播 1540×770 / 侧栏按钮 614×307 / 弹窗 1232×564
// 支持两种字段结构：单串 与 多语言映射 {"_": …, "zh-hans": …}
window.FpPreview = (function () {
    'use strict';

    var TYPE_CAROUSEL = 0, TYPE_BUTTON = 1, TYPE_POPUP = 2;
    var TEMPLATE_ID = { 0: 'fp-template-carousel', 1: 'fp-template-button', 2: 'fp-template-popup' };
    var TYPE_HEIGHT = { 0: 770, 1: 307, 2: 564 };
    var TYPE_SIZE = { 0: [1540, 770], 1: [614, 307], 2: [1232, 564] };
    var TYPE_LABEL = { 0: '轮播 1540×770', 1: '侧栏按钮 614×307', 2: '弹窗 1232×564' };

    // Keep the templates with the renderer. The old static HTML page defined
    // these separately, but the Vue migration dropped those nodes; without
    // them generate() returned null and left only the dark preview container.
    var TEMPLATE_MARKUP = {
        0: '<div class="fp-carousel"><svg viewBox="0 0 1540 770" preserveAspectRatio="xMinYMid meet" xmlns="http://www.w3.org/2000/svg"><defs><linearGradient id="fp-blacktint" x1="0" x2="0" y1="0" y2="1"><stop offset="0%" stop-color="black" stop-opacity="0"/><stop offset="100%" stop-color="black"/></linearGradient></defs><image data-field="image" href="" x="0" y="0" width="100%" height="100%"/><rect x="9.2%" y="66.66%" width="100%" height="33.34%" fill="url(#fp-blacktint)"/><text data-field="heading" x="13%" y="717.64" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="56"/><text data-field="sub_heading" x="13%" y="93.2%" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="30"/></svg></div>',
        1: '<div class="fp-button"><svg viewBox="0 0 614 307" preserveAspectRatio="xMinYMid meet" xmlns="http://www.w3.org/2000/svg"><image data-field="image" href="" x="0" y="0" width="100%" height="100%"/><image data-field="icon" href="" x="16" y="16" width="96" height="96"/><text data-field="heading" x="13%" y="150" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="56"/><text data-field="sub_heading" x="13%" y="210" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="30"/></svg></div>',
        2: '<div class="fp-popup"><svg viewBox="0 0 1232 564" preserveAspectRatio="xMinYMid meet" xmlns="http://www.w3.org/2000/svg"><image data-field="image" href="" x="0" y="0" width="100%" height="100%"/><text data-field="heading" x="13%" y="300" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="56"/><text data-field="sub_heading" x="13%" y="360" font-family="Roboto Condensed, sans-serif" fill="#e4e4d1" font-size="30"/></svg></div>'
    };
    var templates = {};

    function getTemplate(type) {
        var existing = document.getElementById(TEMPLATE_ID[type]);
        if (existing) return existing;
        if (!templates[type] && TEMPLATE_MARKUP[type]) {
            var template = document.createElement('template');
            template.innerHTML = TEMPLATE_MARKUP[type];
            templates[type] = template;
        }
        return templates[type] || null;
    }

    var language = 'zh-hans';

    function placeholder(w, h) {
        return 'data:image/svg+xml,' + encodeURIComponent(
            '<svg xmlns="http://www.w3.org/2000/svg" width="' + w + '" height="' + h + '">' +
            '<rect width="100%" height="100%" fill="#3c3c3c"/>' +
            '<text x="50%" y="50%" fill="#9aa0a6" font-size="48" text-anchor="middle" dominant-baseline="middle">' +
            w + '×' + h + '</text></svg>');
    }

    function localised(field) {
        if (field === null || field === undefined) return '';
        if (typeof field === 'string' || typeof field === 'number') return String(field);
        if (typeof field === 'object') {
            if (field[language] !== undefined && field[language] !== null) return String(field[language]);
            if (field['_'] !== undefined && field['_'] !== null) return String(field['_']);
            for (var k in field) { if (field[k] !== null && field[k] !== undefined) return String(field[k]); }
        }
        return '';
    }

    /**
     * entry: 完整条目 JSON（dev 结构：{content:{type,image_url,heading:{text,font_size},sub_heading,link,icon}}）
     */
    function generate(entry) {
        if (!entry || !entry.content) return null;
        var type = parseInt(entry.content.type, 10);
        if (isNaN(type)) type = TYPE_CAROUSEL;

        var template = getTemplate(type);
        if (!template) return null;

        var node = template.content.firstElementChild.cloneNode(true);
        var height = TYPE_HEIGHT[type];

        var image = node.querySelector('[data-field=image]');
        if (image) {
            var size = TYPE_SIZE[type];
            image.setAttribute('href', localised(entry.content.image_url) || placeholder(size[0], size[1]));
        }

        var heading = node.querySelector('[data-field=heading]');
        var subHeading = node.querySelector('[data-field=sub_heading]');
        var headingSize = parseInt(localised(entry.content.heading && entry.content.heading.font_size), 10) || 56;
        var subSize = parseInt(localised(entry.content.sub_heading && entry.content.sub_heading.font_size), 10) || 30;
        var subText = localised(entry.content.sub_heading && entry.content.sub_heading.text);

        var yOffset = height / 2;
        var subYOffset = height / 2 + headingSize / 2;

        if (type === TYPE_BUTTON) {
            var icon = node.querySelector('[data-field=icon]');
            if (icon) icon.setAttribute('href', localised(entry.content.icon && entry.content.icon.icon_url) || '');
            if (subText) yOffset = height / 2 - subSize / 2;
            if (subHeading) subHeading.setAttribute('y', subYOffset);
        } else if (type === TYPE_CAROUSEL) {
            if (heading) yOffset = parseInt(heading.getAttribute('y'), 10) || height / 2;
            if (subText) yOffset -= subSize * 1.05;
        }

        if (heading) {
            if (type !== TYPE_POPUP) heading.setAttribute('y', yOffset);
            heading.setAttribute('font-size', headingSize);
            heading.textContent = localised(entry.content.heading && entry.content.heading.text);
        }
        if (subHeading) {
            subHeading.setAttribute('font-size', subSize);
            subHeading.textContent = subText;
        }

        return node;
    }

    /** 在有 #fp-preview-stage 与模板的页面上渲染；jsonText 为条目 JSON 原文 */
    function render(jsonText, stageId, metaId) {
        var stage = document.getElementById(stageId || 'fp-preview-stage');
        var meta = document.getElementById(metaId || 'fp-preview-meta');
        if (!stage) return false;

        var entry = null;
        try { entry = jsonText && jsonText.trim() ? JSON.parse(jsonText) : {}; } catch (e) { entry = null; }

        if (entry === null) {
            stage.innerHTML = '<p class="muted" style="margin:0">JSON 格式错误，预览已暂停</p>';
            if (meta) meta.textContent = '';
            return false;
        }

        var node = generate(entry);
        stage.innerHTML = '';
        if (node) stage.appendChild(node);
        if (meta) {
            var type = parseInt(entry.content && entry.content.type, 10) || 0;
            meta.textContent = (TYPE_LABEL[type] || '未知类型') + ' · 语言 ' + language;
        }
        return true;
    }

    function setLanguage(next) { language = next || language; }

    return { render: render, generate: generate, setLanguage: setLanguage, types: TYPE_LABEL };
})();

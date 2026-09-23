import '../admin-v4.js';
import '../admin-frontpage-preview.js';
import '../admin-skirmish-editor.js';
const Admin = window.Admin;
const FpPreview = window.FpPreview;
const SkirmishEditor = window.SkirmishEditor;

    (async function () {
        if (!await Admin.ensureAuth()) return;
        Admin.mount('content', '内容');

        const kindSelect = document.getElementById('kind');
        const form = document.getElementById('entry-form');
        const raw = document.getElementById('raw');
        const previewCard = document.getElementById('preview-card');
        const fpFields = ['type','heading','heading-size','sub-heading','sub-heading-size','banner-text','banner-size','link','priority','slot','image-url','icon-url'];
        const $fp = key => document.getElementById('fp-' + key);
        SkirmishEditor.init(raw, Admin.banner);
        let currentId = 0;
        let calendarEntries = [];
        let calendarMonth = new Date(Date.UTC(new Date().getUTCFullYear(), new Date().getUTCMonth(), 1));

        function statusText(status) { return ({active:'生效中',scheduled:'待投放',expired:'已结束',unpublished:'未发布',invalid_date:'日期无效'})[status] || status; }
        function typeText(type){return ({0:'轮播',1:'侧栏按钮',2:'弹窗'})[type]||'—'}
        function parseUtcDate(value,fallback){const text=String(value||fallback).trim().replace(' ','T');return Date.parse(/(?:Z|[+-]\d\d:?\d\d)$/i.test(text)?text:text+'Z')}
        function dateInput(value){return String(value||'').replace(' ','T').replace(/Z$/i,'').slice(0,19)}
        function dateOutput(value){return value?value+'Z':''}
        function renderSchedule() {
            const card = document.getElementById('schedule-card'); card.hidden = !isFrontpage(); if (!isFrontpage()) return;
            const year=calendarMonth.getUTCFullYear(),month=calendarMonth.getUTCMonth();
            document.getElementById('schedule-month').textContent=year+' 年 '+(month+1)+' 月';
            const first=new Date(Date.UTC(year,month,1)),offset=(first.getUTCDay()+6)%7,grid=document.getElementById('schedule-grid');
            let html=['一','二','三','四','五','六','日'].map(day=>'<span class="schedule-weekday">'+day+'</span>').join('');
            for(let day=1-offset;day<=42-offset;day++){
                const date=new Date(Date.UTC(year,month,day)),stamp=date.getTime(),matches=calendarEntries.filter(e=>{const start=parseUtcDate(e.startDate,'0001-01-01T00:00:00Z'),end=parseUtcDate(e.endDate,'9999-12-31T23:59:00Z');return !Number.isNaN(start)&&!Number.isNaN(end)&&stamp<=end&&stamp+86400000>start});
                const outside=date.getUTCMonth()!==month,today=new Date().toISOString().slice(0,10)===date.toISOString().slice(0,10);
                html+='<div class="schedule-day'+(outside?' outside':'')+(today?' today':'')+'"><b>'+date.getUTCDate()+'</b>'+matches.slice(0,4).map(e=>'<span class="schedule-event'+(e.isTargeted?' targeted':'')+(!e.isPublished?' unpublished':'')+'" data-edit="'+e.id+'" title="'+Admin.esc(e.name)+'">'+Admin.esc(e.name)+'</span>').join('')+(matches.length>4?'<small>+'+(matches.length-4)+'</small>':'')+'</div>';
            }
            grid.innerHTML=html;
        }

        function kind() { return kindSelect.value; }

        function isFrontpage() { return kind() === 'frontpage'; }

        function localized(value, language) {
            if (value && typeof value === 'object' && !Array.isArray(value)) return value[language] ?? (language === '_' ? '' : value._ ?? '');
            return language === '_' ? (value ?? '') : '';
        }
        function setLocalized(value, language, next) {
            if (language === '_' && (!value || typeof value !== 'object' || Array.isArray(value))) return next;
            const copy = value && typeof value === 'object' && !Array.isArray(value) ? {...value} : {_ : value ?? ''};
            copy[language] = next;
            return copy;
        }
        function readSimpleFields() {
            if (!isFrontpage()) return;
            let item;try { item=JSON.parse(raw.value||'{}'); } catch (_) { return; }
            const c=item.content||{},lang=$fp('language').value;
            const fields={type:c.type??0,heading:localized(c.heading?.text,lang),'heading-size':localized(c.heading?.font_size,lang),'sub-heading':localized(c.sub_heading?.text,lang),'sub-heading-size':localized(c.sub_heading?.font_size,lang),'banner-text':localized(c.banner_text?.text,lang),'banner-size':localized(c.banner_text?.font_size,lang),link:c.link??'',priority:c.priority??'',slot:c.slot??'','image-url':localized(c.image_url,'_'),'icon-url':localized(c.icon?.icon_url,'_')};
            for(const key of fpFields)$fp(key).value=fields[key];
        }
        function writeSimpleFields(changedKey) {
            if (!isFrontpage()) return;
            let item;try { item=JSON.parse(raw.value||'{}'); } catch (_) { Admin.banner('请先修正 JSON 格式，表单暂不能同步','err'); return; }
            const c=item.content||(item.content={}),lang=$fp('language').value;
            if(changedKey==='type')c.type=Number($fp('type').value);
            for(const [key,property] of [['heading','heading'],['sub-heading','sub_heading'],['banner-text','banner_text']]){
                if(changedKey!==key&&changedKey!==key+'-size')continue;
                const target=c[property]&&typeof c[property]==='object'?c[property]:(c[property]={});
                if(changedKey===key)target.text=setLocalized(target.text,lang,$fp(key).value);
                if(changedKey===key+'-size'&&$fp(key+'-size').value!=='')target.font_size=setLocalized(target.font_size,lang,Number($fp(key+'-size').value));
            }
            if(changedKey==='link')c.link=$fp('link').value;
            if(changedKey==='image-url')c.image_url=setLocalized(c.image_url,'_',$fp('image-url').value);
            if(changedKey==='icon-url'){const icon=c.icon&&typeof c.icon==='object'?c.icon:(c.icon={});icon.icon_url=setLocalized(icon.icon_url,'_',$fp('icon-url').value)}
            if(changedKey==='priority'||changedKey==='slot'){if($fp(changedKey).value!=='')c[changedKey]=Number($fp(changedKey).value)}
            raw.value=JSON.stringify(item,null,2);updatePreview();
        }

        async function uploadImage(input, target) {
            const file=input.files?.[0];if(!file)return;
            if(file.size>5*1024*1024){Admin.banner('图片不能超过 5 MB','err');input.value='';return}
            const body=new FormData();body.append('image',file);
            try{const response=await fetch('/admin/api/content/frontpage/upload-image',{method:'POST',body});const result=await response.json();if(!response.ok||!result.ok)throw new Error(result.message||'上传失败');$fp(target).value=result.url;writeSimpleFields(target);Admin.banner('图片已上传并填入地址，点击保存条目后内容才会发布','ok')}
            catch(error){Admin.banner(error.message||'上传失败','err')}
            finally{input.value=''}
        }

        function updatePreview() {
            if (!isFrontpage()) { previewCard.style.display = 'none'; return; }
            previewCard.style.display = '';
            FpPreview.render(raw.value, 'fp-preview-stage', 'fp-preview-meta');
        }

        async function loadList() {
            const data = await Admin.api('/content/' + kind());
            document.getElementById('content-path').textContent = 'config/' + kind() + '.json';
            const entries = (data && data.entries) || [];
            calendarEntries=entries;renderSchedule();
            document.getElementById('frontpage-filters').hidden=!isFrontpage();
            const statusFilter=document.getElementById('status-filter').value,typeFilter=document.getElementById('type-filter').value,groupFilter=document.getElementById('group-filter').value;
            const visible=isFrontpage()?entries.filter(e=>(!statusFilter||e.status===statusFilter)&&(!typeFilter||String(e.type)===typeFilter)&&(!groupFilter||(groupFilter==='targeted')===e.isTargeted)):entries;
            document.getElementById('entry-table').innerHTML = '<table class="md-table"><thead><tr>' +
                '<th>ID</th><th>名称</th><th>类型</th><th>优先级 / 槽位</th><th>投放状态</th><th>分组</th><th>开始</th><th>结束</th><th>操作</th></tr></thead><tbody>' +
                (visible.length === 0 ? '<tr><td colspan="9" class="empty">没有匹配的条目</td></tr>' : visible.map(function (e) {
                    return '<tr><td class="mono">' + e.id + '</td><td>' + Admin.esc(e.name) + '</td>' +
                        '<td>'+(isFrontpage()?typeText(e.type):'—')+'</td><td>'+(isFrontpage()?'P'+(e.priority??'—')+' / S'+(e.slot??'—'):'—')+'</td>'+
                        '<td>'+ (isFrontpage()?'<span class="chip '+(e.status==='active'?'chip--ok':e.status==='invalid_date'?'chip--err':'chip--off')+'">'+statusText(e.status)+'</span>':'—') +'</td><td>'+(isFrontpage()?(e.isTargeted?'定向':'普通'):'—')+'</td>'+
                        '<td class="mono">' + Admin.esc(e.startDate) + '</td><td class="mono">' + Admin.esc(e.endDate) + '</td>' +
                        '<td><div class="btn-row"><button class="btn btn--small btn--outline" data-edit="' + e.id + '">编辑</button>' +
                        (isFrontpage()?'<button class="btn btn--small btn--outline" data-publish="'+e.id+'" data-next="'+(!e.isPublished)+'">'+(e.isPublished?'取消发布':'发布')+'</button>':'')+
                        '<button class="btn btn--small btn--danger" data-del="' + e.id + '">删除</button></div></td></tr>';
                }).join('')) + '</tbody></table>';
        }

        async function openEntry(id) {
            currentId = id;
            if (id > 0) {
                const entry = await Admin.api('/content/' + kind() + '/' + id);
                if (!entry) { Admin.banner('条目不存在', 'err'); return; }
                document.getElementById('name').value = entry.name || '';
                document.getElementById('startDate').value = dateInput(entry.startDate);
                document.getElementById('endDate').value = dateInput(entry.endDate);
                try { const item=JSON.parse(entry.raw||'{}'); document.getElementById('isPublished').checked=item.isPublished??item.is_published??true; document.getElementById('isTargeted').checked=item.isTargeted??item.is_targeted??false; } catch (_) {}
                try { raw.value = JSON.stringify(JSON.parse(entry.raw || '{}'), null, 2); } catch (_) { raw.value = entry.raw || '{}'; }
                document.getElementById('editor-title').textContent = '编辑条目 #' + id;
                document.getElementById('entry-id').textContent = '条目 #' + id;
            } else {
                document.getElementById('name').value = '';
                document.getElementById('startDate').value = '';
                document.getElementById('endDate').value = '';
                document.getElementById('isPublished').checked = true;
                document.getElementById('isTargeted').checked = false;
                raw.value = isFrontpage()
                    ? JSON.stringify({ content: { type: 0, heading: { text: '', font_size: 56 }, sub_heading: { text: '', font_size: 30 }, banner_text: {text:'',font_size:56}, icon:{icon_url:''}, image_url: '', link: '', priority: 1 } }, null, 2)
                    : (kind() === 'skirmish'
                        ? JSON.stringify({ rules: { localization: { EN: { rules_list: [], description: '' }, 'ZH-HANS': { rules_list: [], description: '' } }, blacklist: [], reward: { type: 'gold', amount: 100 }, turn_length: 90 } }, null, 2)
                        : JSON.stringify({ entry_prices: { gold: 0, diamonds: 0, ticket: 0, points: 0 }, deck_rules: { allow_reserved: false, allow_more_ally_cards: false, allow_unowned: false }, turn_timers: { turn_length: 90, length_after_timeout: 30 }, rewards: [], texts: {}, active_period: { days: [], hour_from: 0, hour_to: 24 } }, null, 2));
                document.getElementById('editor-title').textContent = '新建条目';
                document.getElementById('entry-id').textContent = '（新建）';
            }
            document.getElementById('raw-hint').textContent = isFrontpage() ? 'content 之下为条目内容' : 'rules 之下为该类型的规则';
            document.getElementById('publication-fields').hidden = !isFrontpage();
            document.getElementById('frontpage-editor').hidden = !isFrontpage();
            document.getElementById('skirmish-editor').hidden = kind() !== 'skirmish';
            document.getElementById('json-details').open = kind() === 'knockout';
            $fp('language').value = '_';
            readSimpleFields();
            document.getElementById('sk-language').value = 'EN';
            if(kind()==='skirmish')SkirmishEditor.fill();
            form.style.display = '';
            updatePreview();
            form.scrollIntoView({ behavior: 'smooth', block: 'start' });
        }

        form.addEventListener('submit', async function (event) {
            event.preventDefault();
            const r = await Admin.api('/content/' + kind() + (currentId > 0 ? '/' + currentId : ''), {
                method: 'POST',
                body: {
                    name: document.getElementById('name').value,
                    startDate: dateOutput(document.getElementById('startDate').value),
                    endDate: dateOutput(document.getElementById('endDate').value),
                    raw: raw.value,
                    isPublished: document.getElementById('isPublished').checked,
                    isTargeted: document.getElementById('isTargeted').checked
                }
            });
            Admin.banner((r && r.message) || '完成', r && r.ok ? 'ok' : 'err');
            if (r && r.ok) { await loadList(); form.style.display = 'none'; }
        });

        document.getElementById('format').addEventListener('click', function () {
            try {
                raw.value = JSON.stringify(JSON.parse(raw.value), null, 2);
                if(kind()==='skirmish')SkirmishEditor.fill();
                if(isFrontpage())readSimpleFields();
                updatePreview();
                Admin.banner('已格式化（尚未保存）', 'ok');
            } catch (e) { Admin.banner('JSON 格式错误：' + e.message, 'err'); }
        });

        document.getElementById('delete-entry').addEventListener('click', async function () {
            if (currentId <= 0) return;
            if (!confirm('确认删除条目 #' + currentId + '？原文件会备份为 .bak。')) return;
            const r = await Admin.api('/content/' + kind() + '/' + currentId, { method: 'DELETE' });
            Admin.banner((r && r.message) || '完成', r && r.ok ? 'ok' : 'err');
            form.style.display = 'none';
            await loadList();
        });

        document.getElementById('cancel').addEventListener('click', function () { form.style.display = 'none'; });
        document.getElementById('new-entry').addEventListener('click', function () { openEntry(0); });
        document.getElementById('refresh').addEventListener('click', loadList);
        for(const id of ['status-filter','type-filter','group-filter'])document.getElementById(id).onchange=loadList;
        document.getElementById('prev-month').onclick=()=>{calendarMonth=new Date(Date.UTC(calendarMonth.getUTCFullYear(),calendarMonth.getUTCMonth()-1,1));renderSchedule()};
        document.getElementById('next-month').onclick=()=>{calendarMonth=new Date(Date.UTC(calendarMonth.getUTCFullYear(),calendarMonth.getUTCMonth()+1,1));renderSchedule()};
        document.getElementById('today-month').onclick=()=>{calendarMonth=new Date(Date.UTC(new Date().getUTCFullYear(),new Date().getUTCMonth(),1));renderSchedule()};
        document.getElementById('export-content').onclick=async()=>{const response=await fetch('/admin/api/content/'+kind()+'/export');if(!response.ok){Admin.banner('导出失败：HTTP '+response.status,'err');return}const blob=await response.blob(),link=document.createElement('a');link.href=URL.createObjectURL(blob);link.download=kind()+'-'+new Date().toISOString().slice(0,10)+'.json';link.click();setTimeout(()=>URL.revokeObjectURL(link.href),1000)};
        document.getElementById('import-content').onclick=()=>document.getElementById('import-file').click();
        document.getElementById('import-file').onchange=async event=>{const file=event.target.files[0];if(!file)return;if(file.size>1048576){Admin.banner('文件超过 1 MB','err');return}if(!confirm('导入将替换 '+kind()+' 的全部条目。服务器会备份原文件，确定继续？'))return;const result=await Admin.api('/content/'+kind()+'/import',{method:'POST',body:{raw:await file.text()}});Admin.banner(result.message||'导入失败',result.ok?'ok':'err');event.target.value='';if(result.ok){form.style.display='none';await loadList()}};
        for(const key of fpFields)$fp(key).addEventListener('input',()=>writeSimpleFields(key));
        $fp('language').addEventListener('change',()=>{readSimpleFields();if($fp('language').value!=='_'){document.getElementById('preview-language').value=$fp('language').value;FpPreview.setLanguage($fp('language').value)}updatePreview()});
        $fp('image-file').addEventListener('change',()=>uploadImage($fp('image-file'),'image-url'));
        $fp('icon-file').addEventListener('change',()=>uploadImage($fp('icon-file'),'icon-url'));
        raw.addEventListener('input',()=>{readSimpleFields();if(kind()==='skirmish')SkirmishEditor.fill();updatePreview()});
        document.getElementById('preview-language').addEventListener('change', function (event) {
            FpPreview.setLanguage(event.target.value);
            updatePreview();
        });

        kindSelect.addEventListener('change', async function () {
            form.style.display = 'none';
            await loadList();
        });

        document.addEventListener('click', async function (event) {
            const edit = event.target.closest('[data-edit]');
            if (edit) { event.preventDefault(); await openEntry(parseInt(edit.getAttribute('data-edit'), 10)); return; }
            const publish=event.target.closest('[data-publish]');
            if(publish){const id=Number(publish.dataset.publish),published=publish.dataset.next==='true';const result=await Admin.api('/content/frontpage/'+id+'/published',{method:'PUT',body:{published}});Admin.banner(result.message||'发布状态更新失败',result.ok?'ok':'err');if(result.ok)await loadList();return}
            const del = event.target.closest('[data-del]');
            if (del) {
                event.preventDefault();
                const id = parseInt(del.getAttribute('data-del'), 10);
                if (!confirm('确认删除条目 #' + id + '？原文件会备份为 .bak。')) return;
                const r = await Admin.api('/content/' + kind() + '/' + id, { method: 'DELETE' });
                Admin.banner((r && r.message) || '完成', r && r.ok ? 'ok' : 'err');
                await loadList();
            }
        });

        FpPreview.setLanguage(document.getElementById('preview-language').value);
        await loadList();
    })();

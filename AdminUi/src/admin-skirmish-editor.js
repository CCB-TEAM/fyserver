// 乱斗表单只改动当前操作的字段；完整 JSON 始终是单一数据源。
window.SkirmishEditor = (function () {
    'use strict';
    const $ = id => document.getElementById(id);
    const cardTypes = ['infantry','tank','artillery','fighter','bomber','order','gotcha'];
    const blacklist = ['Germany','Britain','USA','Japan','Soviet','ally','orders','units','ground','air',...cardTypes];
    const effects = [
        ['start_of_turn_damage','回合开始：造成伤害',['amount','types','turns']],
        ['start_of_turn_cards','回合开始：抽牌',['amount','turns']],
        ['choose_spawn_card_on_start_of_turn','回合开始：选择生成卡牌',['cost','cards','turns']],
        ['start_of_turn_kredit_slots','回合开始：增加 kredits 槽位',['amount','turns']],
        ['give_random_ability_on_deployed','部署时：随机能力',['types','abilities']],
        ['set_defense_on_deployed','部署时：设置防御',['amount','types']],
        ['add_attack_on_deployed','部署时：增加攻击',['amount','types']],
        ['add_defense_on_deployed','部署时：增加防御',['amount','types']],
        ['set_operation_cost_on_enter_play','入场时：设置行动费用',['amount','types']],
        ['operation_cost_buff','行动费用调整',['amount','types']]
    ];
    const labels = {amount:'数值',cost:'费用',turns:'触发回合（逗号分隔）',types:'作用类型（逗号分隔）',abilities:'能力（逗号分隔）',cards:'卡牌 ID（逗号分隔）'};
    const simple = [
        ['sk-reward-type','reward.type','string'],['sk-reward-amount','reward.amount','number'],
        ['sk-starting-kredits','starting_kredits','number'],['sk-hq-starting-defense','hq_starting_defense','number'],
        ['sk-turn-length','turn_length','number'],['sk-card-min-cost','card_min_cost','number'],
        ['sk-officer-pack','reward.is_officer_pack','boolean'],['sk-skip-draw','skip_start_of_turn_draw','boolean'],
        ['sk-skip-kredit','skip_start_of_turn_kredit_gain','boolean']
    ];
    let raw, notify;
    function get(obj,path) { return path.split('.').reduce((value,key)=>value&&typeof value==='object'?value[key]:undefined,obj); }
    function set(obj,path,value) {
        const keys=path.split('.');let current=obj;
        for(const key of keys.slice(0,-1)){if(!current[key]||typeof current[key]!=='object'||Array.isArray(current[key]))current[key]={};current=current[key]}
        if(value===undefined)delete current[keys.at(-1)];else current[keys.at(-1)]=value;
    }
    function csv(value,numeric) {
        if(!value.trim())return undefined;
        const values=value.split(',').map(x=>x.trim()).filter(Boolean);
        if(numeric && values.some(x=>!/^[1-9]\d*$/.test(x)))throw Error('回合编号必须是正整数，用逗号分隔');
        return numeric?values.map(Number):values;
    }
    function display(value) { return Array.isArray(value)?value.every(x=>typeof x!=='object')?value.join(', '):JSON.stringify(value):value??''; }
    function parse() { const item=JSON.parse(raw.value||'{}');if(!item||typeof item!=='object'||Array.isArray(item))throw Error('JSON 根节点必须是对象');return item; }
    function change(callback) {
        try { const item=parse();const rules=item.rules&&typeof item.rules==='object'?item.rules:(item.rules={});callback(rules);raw.value=JSON.stringify(item,null,2); }
        catch(error){notify(error.message||'JSON 格式错误','err')}
    }
    function renderStatic() {
        $('sk-blacklist').innerHTML=blacklist.map(value=>'<label><input type="checkbox" data-blacklist="'+value+'"> '+value+'</label>').join('');
        $('sk-card-limits').innerHTML=['min_cards_of_type','max_cards_of_type'].flatMap(group=>cardTypes.map(type=>'<label class="field"><span>'+(group.startsWith('min')?'最少':'最多')+' '+type+'</span><input type="number" min="0" step="1" data-sk-path="'+group+'.'+type+'" data-sk-kind="number"></label>')).join('');
        $('sk-effects').innerHTML=effects.map(([group,title,fields])=>'<details><summary>'+title+'</summary><div class="form-grid">'+fields.map(field=>'<label class="field"><span>'+labels[field]+'</span><input '+(field==='amount'||field==='cost'?'type="number" min="0" step="1"':'type="text"')+' data-sk-path="'+group+'.'+field+'" data-sk-kind="'+(field==='amount'||field==='cost'?'number':field==='turns'?'turns':'list')+'"></label>').join('')+'</div></details>').join('');
    }
    function fill() {
        let item;try{item=parse()}catch(_){return}const rules=item.rules||{};
        for(const [id,path,kind] of simple){const element=$(id),value=get(rules,path);if(kind==='boolean')element.checked=value===true;else element.value=value??''}
        for(const input of document.querySelectorAll('[data-sk-path]'))input.value=display(get(rules,input.dataset.skPath));
        const blocked=Array.isArray(rules.blacklist)?rules.blacklist:[];
        for(const input of document.querySelectorAll('[data-blacklist]'))input.checked=blocked.includes(input.dataset.blacklist);
        const decks=rules.random_decks;$('sk-random-decks').value=Array.isArray(decks)&&decks.every(x=>typeof x==='string')?decks.join('\n'):'';
        const locale=get(rules,'localization.'+$('sk-language').value)||{};
        $('sk-rules-list').value=Array.isArray(locale.rules_list)?locale.rules_list.join('\n'):locale.rules_list||'';
        $('sk-description').value=locale.description||'';
    }
    function init(rawElement,banner) {
        raw=rawElement;notify=banner;renderStatic();
        for(const [id,path,kind] of simple){$(id).addEventListener(kind==='boolean'||id==='sk-reward-type'?'change':'input',()=>change(rules=>{const element=$(id);set(rules,path,kind==='boolean'?element.checked:element.value===''?undefined:kind==='number'?Number(element.value):element.value)}))}
        $('sk-language').addEventListener('change',fill);
        for(const [id,path] of [['sk-rules-list','rules_list'],['sk-description','description']])$(id).addEventListener('input',()=>change(rules=>{const value=$(id).value,lang=$('sk-language').value;set(rules,'localization.'+lang+'.'+path,path==='rules_list'?value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean):value)}));
        $('sk-random-decks').addEventListener('change',()=>change(rules=>set(rules,'random_decks',$('sk-random-decks').value.split(/\r?\n/).map(x=>x.trim()).filter(Boolean))));
        $('sk-blacklist').addEventListener('change',()=>change(rules=>{const known=new Set(blacklist),old=Array.isArray(rules.blacklist)?rules.blacklist:[];rules.blacklist=old.filter(x=>!known.has(x)).concat([...document.querySelectorAll('[data-blacklist]:checked')].map(x=>x.dataset.blacklist))}));
        for(const input of document.querySelectorAll('[data-sk-path]'))input.addEventListener('change',()=>change(rules=>{const value=input.value,kind=input.dataset.skKind;set(rules,input.dataset.skPath,value===''?undefined:kind==='number'?Number(value):csv(value,kind==='turns'))}));
    }
    return {init,fill};
})();

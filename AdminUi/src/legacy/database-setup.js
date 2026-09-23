import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
if(!await Admin.ensureAuth())return;const session=await Admin.api('/session');if(!session?.isOwner){Admin.banner('只有 Owner 可以完成玩家数据库初始化','err');return}Admin.mount('system-settings','玩家数据库初始化');
const $=id=>document.getElementById(id),provider=$('provider');
function toggle(){const remote=provider.value!=='local';document.querySelectorAll('.remote').forEach(x=>x.hidden=!remote);['host','port','database','username'].forEach(id=>$(id).required=remote);if(remote&&!$('port').value)$('port').value=provider.value==='mysql'?3306:5432}
provider.onchange=()=>{$('port').value=provider.value==='mysql'?3306:provider.value==='postgresql'?5432:'';toggle()};
async function load(){const d=await Admin.api('/database');if(!d)return;provider.value=d.provider||'postgresql';$('host').value=d.host||'127.0.0.1';$('port').value=d.port||((provider.value==='mysql')?3306:5432);$('database').value=d.database||'fyserver';$('username').value=d.username||'fyserver';$('require-ssl').value=String(d.requireSsl!==false);$('state').textContent=d.ready?'玩家数据库已就绪，服务器正在正常提供服务。':(d.initializationError?'初始化失败：'+d.initializationError:'尚未完成配置，游戏接口当前返回 503。');toggle()}
$('reload').onclick=load;$('database-form').onsubmit=async e=>{e.preventDefault();$('save').disabled=true;const body={provider:provider.value,host:$('host').value.trim(),port:Number($('port').value),database:$('database').value.trim(),username:$('username').value.trim(),password:$('password').value,requireSsl:$('require-ssl').value==='true'};try{const r=await Admin.api('/database/configure',{method:'POST',body});Admin.banner(r?.message||'初始化失败',r?.ok?'ok':'err');if(r?.ok){$('password').value='';setTimeout(()=>location.replace('/admin-ui/index.html'),600)}}finally{$('save').disabled=false}};await load();
})();

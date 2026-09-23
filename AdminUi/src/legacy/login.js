import '../admin-v4.js';
const Admin = window.Admin;

(async function(){
const $=id=>document.getElementById(id),params=new URLSearchParams(location.search),next=params.get('next')||'/admin-ui/index.html';let setup=false;
let dark=localStorage.getItem('fyserver.admin.dark')==='1';
const applyAppearance=()=>Admin.applyTheme({...Admin.readTheme(),dark});
applyAppearance();
$('auth-theme').onclick=()=>{dark=!dark;localStorage.setItem('fyserver.admin.dark',dark?'1':'0');applyAppearance()};
$('reveal').onclick=()=>{const hidden=$('password').type==='password';$('password').type=hidden?'text':'password';$('reveal').classList.toggle('is-revealed',hidden);$('reveal').setAttribute('aria-label',hidden?'隐藏密码':'显示密码')};
try{const res=await fetch('/admin/api/session',{cache:'no-store'});if(!res.ok)throw Error('无法获取登录状态');const s=await res.json();if(s.authorized){location.replace(s.serverReady?next:'/admin-ui/database-setup.html');return}setup=s.initialized===false;$('confirm-field').hidden=!setup;$('confirm-field').style.display=setup?'grid':'none';$('confirm').disabled=!setup;$('confirm').required=setup;
if(setup){$('title-mode').textContent='Setup';$('subtitle').textContent='首次启动 · 创建后台管理员';$('confirm-field').hidden=false;$('confirm').required=true;$('password').autocomplete='new-password';$('hint').textContent=s.loopback?'密码至少 10 个字符，创建后即可进入控制台。':'请在服务器本机打开此页面完成首次设置。';$('submit').querySelector('span').textContent='完成设置';$('submit').disabled=!s.loopback}else $('hint').textContent='请输入初始化时创建的管理员账户。'}catch(_){$('hint').textContent='无法连接后台接口，请确认服务器正在运行。'}
$('auth-form').addEventListener('submit',async e=>{e.preventDefault();const username=$('username').value.trim(),password=$('password').value;if(setup&&password!==$('confirm').value){Admin.banner('两次输入的密码不一致','err');return}$('submit').disabled=true;$('submit').classList.add('is-loading');
try{const res=await fetch('/admin/api/'+(setup?'setup':'login'),{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({username,password})});const data=await res.json();if(!res.ok){Admin.banner(data.message||'操作失败','err');return}const state=await fetch('/admin/api/session',{cache:'no-store'}).then(r=>r.json());location.replace(state.serverReady?next:'/admin-ui/database-setup.html')}catch(_){Admin.banner('无法连接服务器','err')}finally{$('submit').disabled=false;$('submit').classList.remove('is-loading')}})
})();

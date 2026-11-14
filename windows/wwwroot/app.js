const params = new URLSearchParams(location.search);
const code = params.get('code') || '';
const isDesktop = params.get('desktop') === '1';
const sessionId = sessionStorage.getItem('nearlink-session') || `s-${Math.random().toString(36).slice(2, 14)}`;
sessionStorage.setItem('nearlink-session', sessionId);
const storedName = localStorage.getItem('nearlink-device-name');
const useDetectedName = !storedName || /^手机-[A-Z0-9]{3}$/.test(storedName);
let deviceName = storedName || (isDesktop ? '电脑' : '手机');
const feed = document.querySelector('#feed');
const input = document.querySelector('#message');
const statusText = document.querySelector('#connection');
const fileInput = document.querySelector('#file');
let socket;
let reconnectTimer;
let reconnectAttempt = 0;
let messageSending = false;
const seenItems = new Set();

const fmtSize = n => n < 1024 ? `${n} B` : n < 1048576 ? `${(n/1024).toFixed(1)} KB` : `${(n/1048576).toFixed(1)} MB`;
const time = t => new Intl.DateTimeFormat('zh-CN',{hour:'2-digit',minute:'2-digit'}).format(new Date(t));
function clearWelcome(){feed.querySelector('.welcome')?.remove()}
function add(item){
  if(item.id&&seenItems.has(item.id))return;
  if(item.id)seenItems.add(item.id);
  clearWelcome(); const mine = item.fromId ? item.fromId === sessionId : item.from === deviceName;
  const row = document.createElement('article'); row.className = `msg ${mine?'mine':''}`;
  const meta = document.createElement('div'); meta.className='msg-meta'; meta.textContent = `${item.from} · ${time(item.time)}`; row.append(meta);
  if(item.kind==='message'){const b=document.createElement('div');b.className='bubble';b.textContent=item.text;row.append(b)}
  else {const group=document.createElement('div');group.className='file-message';const fileName=item.name||item.originalName||item.id;const isImage=(item.contentType||'').toLowerCase().startsWith('image/')||/\.(png|jpe?g|gif|webp|bmp|tiff?|avif|heic|heif)$/i.test(fileName);if(isImage){const img=document.createElement('img');img.className='image-preview';img.src=`/api/preview/${encodeURIComponent(item.id)}?code=${encodeURIComponent(code)}&session=${encodeURIComponent(sessionId)}`;img.alt=fileName;img.loading='lazy';img.onerror=()=>img.remove();group.append(img)}const a=document.createElement('a');a.className='file-card';a.href=`/api/file/${encodeURIComponent(item.id)}?code=${encodeURIComponent(code)}&name=${encodeURIComponent(fileName)}&session=${encodeURIComponent(sessionId)}`;a.download=fileName;const mark=document.createElement('span');mark.className='file-mark';mark.textContent='↧';const d=document.createElement('span');d.className='file-details';const name=document.createElement('b');name.textContent=fileName;const size=document.createElement('small');size.textContent=fmtSize(item.size);d.append(name,size);const dl=document.createElement('span');dl.className='download';dl.textContent='↓';a.append(mark,d,dl);group.append(a);row.append(group)}
  feed.append(row);feed.scrollTop=feed.scrollHeight;
}
function connect(){
  clearTimeout(reconnectTimer);
  const query = new URLSearchParams({ code, session: sessionId, name: deviceName, kind: isDesktop ? 'desktop' : 'phone' });
  socket=new WebSocket(`${location.protocol==='https:'?'wss':'ws'}://${location.host}/ws?${query}`);
  socket.onopen=()=>{reconnectAttempt=0;document.querySelector('.top-status').classList.add('connected');statusText.textContent='已连接'};
  socket.onclose=()=>{
    document.querySelector('.top-status').classList.remove('connected');
    statusText.textContent='连接中断，正在重连';
    reconnectAttempt=Math.min(reconnectAttempt+1,8);
    const delay=Math.min(30000,750*(2**reconnectAttempt))+Math.random()*1000;
    reconnectTimer=setTimeout(connect,delay);
  };
  socket.onerror=()=>socket.close();
  socket.onmessage=e=>{
    try{
      const data=JSON.parse(e.data);
      if((data.type==='message'||data.type==='file')&&data.item)add(data.item);
      if(data.type==='history')(data.items||[]).forEach(add);
      if(data.type==='presence')renderPeers(data.devices||[]);
    }catch{statusText.textContent='收到无效数据，连接仍保持'}
  };
}
async function detectDeviceName(){
  if(isDesktop)return '电脑';
  const ua=navigator.userAgent||'';
  if(/iPhone/i.test(ua))return 'iPhone';
  if(/iPad/i.test(ua))return 'iPad';
  if(/Android/i.test(ua)){
    let model=ua.match(/Android[^;)]*;\s*([^;)]+)/i)?.[1]?.replace(/\s+Build\/.*$/i,'').trim()||'';
    try{
      const hints=await navigator.userAgentData?.getHighEntropyValues?.(['model']);
      if(hints?.model?.trim())model=hints.model.trim();
    }catch{}
    if(model&&!/^(k|wv|unknown|generic|android|mobile)$/i.test(model))return model.replace(/^SAMSUNG\s+/i,'Samsung ');
    return 'Android 手机';
  }
  return '手机';
}
async function initializeDeviceName(){
  if(useDetectedName){
    deviceName=await detectDeviceName();
    localStorage.setItem('nearlink-device-name',deviceName);
  }
  connect();
}
function renderPeers(devices){
  const list=document.querySelector('#peer-list');list.replaceChildren();
  const computers=devices.filter(device=>device.kind==='desktop');
  document.querySelector('#peer-label').textContent=computers.length?`接收设备在线 · ${computers[0].name}`:'接收电脑尚未连接';
  if(!computers.length){const empty=document.createElement('span');empty.className='peer-empty';empty.textContent='请保持电脑端 Nearlink 运行';list.append(empty);return}
  for(const device of computers){const chip=document.createElement('span');chip.className='peer-chip';const dot=document.createElement('i');const label=document.createElement('span');label.textContent=`接收设备：${device.name}`;chip.append(dot,label);list.append(chip)}
}
async function send(){const text=input.value.trim();if(!text||messageSending)return;if(socket?.readyState!==WebSocket.OPEN){statusText.textContent='连接尚未恢复，消息仍保留在输入框';return}messageSending=true;const button=document.querySelector('#send');const preview=document.querySelector('#file-preview');button.disabled=true;preview.textContent='已向电脑发送接收请求，等待确认…';preview.classList.remove('hidden');try{const r=await fetch(`/api/message?code=${encodeURIComponent(code)}&from=${encodeURIComponent(deviceName)}&session=${encodeURIComponent(sessionId)}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({text})});if(!r.ok)throw new Error(await responseError(r,'电脑暂未接收这条消息'));if(input.value.trim()===text){input.value='';input.style.height='auto'}preview.textContent='电脑已接收消息';setTimeout(()=>preview.classList.add('hidden'),1400)}catch(err){preview.textContent=err.message||'发送失败，请稍后重试'}finally{messageSending=false;button.disabled=false}}
async function responseError(response,fallback){try{const data=await response.json();return data.error||fallback}catch{return response.status===403?'电脑拒绝了本次接收':fallback}}
document.querySelector('#rename-device').onclick=()=>{const next=prompt('设置这台设备的名称（最多 24 个字符）',deviceName);if(next?.trim()){localStorage.setItem('nearlink-device-name',next.trim().slice(0,24));location.reload()}};
document.querySelector('#send').onclick=send;
input.addEventListener('keydown',e=>{if(e.key==='Enter'&&!e.shiftKey){e.preventDefault();send()}});
input.addEventListener('input',()=>{input.style.height='auto';input.style.height=`${Math.min(input.scrollHeight,110)}px`});
fileInput.onchange=async()=>{const files=[...fileInput.files];if(!files.length)return;const preview=document.querySelector('#file-preview');preview.classList.remove('hidden');for(let i=0;i<files.length;i++){const file=files[i];try{preview.textContent=`正在请求电脑接收（${i+1}/${files.length}）：${file.name}`;const approval=await fetch(`/api/transfer/request?code=${encodeURIComponent(code)}&session=${encodeURIComponent(sessionId)}`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({sessionId,from:deviceName,name:file.name,size:file.size})});if(!approval.ok)throw new Error(await responseError(approval,'电脑暂未接收这个文件'));const grant=await approval.json();preview.textContent=`电脑已同意，正在发送（${i+1}/${files.length}）：${file.name}`;const body=new FormData();body.append('file',file);const upload=await fetch(`/api/upload?code=${encodeURIComponent(code)}&from=${encodeURIComponent(deviceName)}&session=${encodeURIComponent(sessionId)}&ticket=${encodeURIComponent(grant.ticket)}`,{method:'POST',body});if(!upload.ok)throw new Error(await responseError(upload,'文件发送失败，请重试'))}catch(err){preview.textContent=err.message||'发送失败，请稍后重试';setTimeout(()=>preview.classList.add('hidden'),3500);fileInput.value='';return}}preview.textContent=`电脑已接收 ${files.length} 个文件`;setTimeout(()=>preview.classList.add('hidden'),1600);fileInput.value=''};
if(isDesktop){fetch(`/api/pair?code=${encodeURIComponent(code)}`).then(r=>{if(!r.ok)throw new Error('配对信息不可用');return r.json()}).then(data=>{document.querySelector('#qr').src=data.qr;document.querySelector('#pair-code').textContent=data.code;document.querySelector('#pair-url').textContent=data.url;document.querySelector('#copy-link').onclick=async()=>{try{await navigator.clipboard.writeText(data.url);document.querySelector('#copy-link').textContent='已复制';setTimeout(()=>document.querySelector('#copy-link').textContent='复制',1500)}catch{document.querySelector('#pair-url').textContent=data.url}}}).catch(()=>{document.querySelector('#pair-url').textContent='配对信息不可用'})}
if(!isDesktop&&code)fetch(`/api/target?code=${encodeURIComponent(code)}`).then(r=>r.ok?r.json():null).then(target=>{if(target?.name)document.querySelector('#peer-label').textContent=`正在连接 · ${target.name}`}).catch(()=>{});
initializeDeviceName();

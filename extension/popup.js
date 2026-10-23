'use strict';
const DEFAULTS = { port: 9614, intercept: true, minSizeKB: 1024, exts: 'zip rar 7z tar gz xz iso exe msi apk dmg deb mp4 mkv avi mov webm mp3 flac wav m4a pdf epub', showButton: true };
const $ = (id) => document.getElementById(id);
const fmtSize = (b) => !b ? '' : b > 1073741824 ? (b / 1073741824).toFixed(2) + ' GB' : b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.round(b / 1024) + ' KB';
const shortName = (u) => { try { const x = new URL(u); return decodeURIComponent(x.pathname.split('/').filter(Boolean).pop() || x.hostname); } catch { return u; } };

let tab;

function toast(t) {
  const el = $('toast'); el.textContent = t; el.style.display = 'block';
  setTimeout(() => (el.style.display = 'none'), 2200);
}

async function ping() {
  const r = await chrome.runtime.sendMessage({ type: 'ping' });
  $('dot').className = 'dot ' + (r?.ok ? 'on' : 'off');
  $('statusText').textContent = r?.ok ? 'uygulama bağlı' : 'uygulama kapalı';
}

async function render() {
  const info = await chrome.runtime.sendMessage({ type: 'getMedia', tabId: tab.id });
  const list = $('list');
  list.innerHTML = '';
  $('best').textContent = info.ytdlpSite ? 'Bu videoyu en iyi kalitede indir' : 'Bu sayfadaki videoyu indir';
  const subNote = document.getElementById('subNote');
  subNote.textContent = info.subs?.length ? `${info.subs.length} altyazı dosyası yakalandı — videoyla birlikte MKV'ye eklenir.` : '';
  if (!info.items.length) {
    list.innerHTML = '<div class="empty">Henüz bir şey yakalanmadı. Videoyu oynatmayı deneyin.</div>';
    return;
  }
  for (const m of info.items) {
    const row = document.createElement('div');
    row.className = 'row';
    const tagText = m.type === 'hls' ? 'HLS' : m.type === 'dash' ? 'DASH' : m.type === 'audio' ? 'Ses' : (m.mime?.split('/')[1] || 'video');
    const meta = m.drm ? 'DRM korumalı — indirilemez' : m.type === 'hls' ? (m.master ? 'çoklu kalite' + (m.quality ? ' · en iyi ' + m.quality : '') : 'akış') : fmtSize(m.size);
    row.innerHTML = `<span class="tag ${m.type === 'audio' ? 'audio' : ''}"></span><div class="name"><div></div><small></small></div><button>İndir</button>`;
    row.querySelector('.tag').textContent = tagText;
    row.querySelector('.name div').textContent = shortName(m.url);
    row.querySelector('.name div').title = m.url;
    row.querySelector('small').textContent = meta;
    const b = row.querySelector('button');
    b.disabled = !!m.drm;
    b.onclick = async () => {
      const kind = m.type === 'hls' ? 'hls' : m.type === 'dash' ? 'ytdlp' : 'direct';
      const r = await chrome.runtime.sendMessage({ type: 'send', tabId: tab.id, req: { url: m.url, kind } });
      toast(r?.ok ? 'TurboDM\'e gönderildi' : 'Uygulama çalışmıyor');
    };
    list.appendChild(row);
  }
}

async function loadSettings() {
  const s = { ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) };
  $('port').value = s.port;
  $('intercept').checked = s.intercept;
  $('showButton').checked = s.showButton;
  $('exts').value = s.exts;
  $('minSizeKB').value = s.minSizeKB;
  const save = () => chrome.storage.sync.set({
    port: Math.min(65535, Math.max(1024, parseInt($('port').value, 10) || 9614)),
    intercept: $('intercept').checked,
    showButton: $('showButton').checked,
    exts: $('exts').value.trim(),
    minSizeKB: Math.max(0, parseInt($('minSizeKB').value, 10) || 0),
  }).then(ping);
  for (const id of ['port', 'intercept', 'showButton', 'exts', 'minSizeKB']) $(id).addEventListener('change', save);
}

(async () => {
  [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  $('best').onclick = async () => {
    const r = await chrome.runtime.sendMessage({ type: 'sendBest', tabId: tab.id });
    toast(r?.ok ? 'TurboDM\'e gönderildi' : 'Uygulama çalışmıyor');
  };
  $('clear').onclick = async () => { await chrome.runtime.sendMessage({ type: 'clear', tabId: tab.id }); render(); };
  loadSettings();
  ping();
  render();
})();

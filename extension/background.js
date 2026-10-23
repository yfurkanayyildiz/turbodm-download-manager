// TurboDM arka plan servisi: medya yakalama, indirme yakalama, uygulamaya gönderme
'use strict';

const DEFAULTS = {
  port: 9614,
  intercept: true,          // tarayıcı indirmelerini TurboDM'e devret
  minSizeKB: 1024,          // listede olmayan uzantılar için bu boyutun üstü devredilir
  exts: 'zip rar 7z tar gz xz iso exe msi apk dmg deb mp4 mkv avi mov webm mp3 flac wav m4a pdf epub',
  showButton: true,         // video üzerinde "İndir" butonu
};

const YTDLP_HOSTS = ['youtube.com', 'youtu.be', 'instagram.com', 'tiktok.com', 'twitter.com', 'x.com', 'vimeo.com',
  'dailymotion.com', 'twitch.tv', 'facebook.com', 'fb.watch', 'reddit.com', 'soundcloud.com', 'bilibili.com',
  'kick.com', 'rumble.com', 'pinterest.com', 'linkedin.com', 'threads.net', 'bsky.app'];

const RX_HLS = /\.m3u8(\?|#|$)/i;
const RX_DASH = /\.mpd(\?|#|$)/i;
const RX_MEDIA = /\.(mp4|webm|mkv|mov|m4v|flv|avi|mp3|m4a|aac|ogg|oga|opus|flac|wav)(\?|#|$)/i;
const RX_AUDIO = /\.(mp3|m4a|aac|ogg|oga|opus|flac|wav)(\?|#|$)/i;
// HLS/DASH parçaları, YouTube'un parça istekleri vb. — tek başına anlamlı değil
const RX_SKIP = /\.(ts|m4s|m4f|cmfv|cmfa|key|vtt|srt)(\?|#|$)|googlevideo\.com\/videoplayback|[?&](range|bytestart)=|\/seg-\d+|\/segment\d*[-_/.]/i;

// Oynatıcıların ayrı yüklediği altyazı dosyaları
const RX_SUB = /\.(vtt|srt|ass|ssa)(\?|#|$)/i;
const SUB_TYPES = ['text/vtt', 'application/x-subrip', 'text/srt', 'application/x-ass', 'text/x-ssa'];
const RX_NOT_SUB = /thumb|sprite|preview|storyboard|chapters?\.vtt/i;

const cfg = async () => ({ ...DEFAULTS, ...(await chrome.storage.sync.get(DEFAULTS)) });

const isYtDlpSite = (url) => {
  try {
    const h = new URL(url).hostname.toLowerCase();
    return YTDLP_HOSTS.some((d) => h === d || h.endsWith('.' + d));
  } catch { return false; }
};

// ---------------- Sekme bazlı medya listesi ----------------
// Servis çalışanı uykuya geçebileceği için storage.session'a da yazılır.
const media = new Map();      // tabId -> [{url,type,size,mime,ts,master,label}]
const children = new Map();   // tabId -> Set(master'ın alt playlist url'leri)
const subsByTab = new Map();  // tabId -> [{url, lang, name}]

const loading = new Map();    // tabId -> Promise (aynı anda gelen isteklerin birbirini ezmemesi için)
async function getList(tabId) {
  if (!media.has(tabId)) {
    if (!loading.has(tabId)) {
      loading.set(tabId, (async () => {
        const k = 't' + tabId;
        const o = await chrome.storage.session.get(k).catch(() => ({}));
        if (!media.has(tabId)) {
          media.set(tabId, o[k]?.items || []);
          children.set(tabId, new Set(o[k]?.children || []));
          subsByTab.set(tabId, o[k]?.subs || []);
        }
      })().finally(() => loading.delete(tabId)));
    }
    await loading.get(tabId);
  }
  return media.get(tabId);
}

function persist(tabId) {
  chrome.storage.session.set({ ['t' + tabId]: { items: media.get(tabId) || [], children: [...(children.get(tabId) || [])], subs: subsByTab.get(tabId) || [] } }).catch(() => {});
}

function clearTab(tabId) {
  media.delete(tabId); children.delete(tabId); subsByTab.delete(tabId);
  chrome.storage.session.remove('t' + tabId).catch(() => {});
  updateBadge(tabId);
}

function visible(tabId, list) {
  const ch = children.get(tabId) || new Set();
  return list.filter((m) => !ch.has(m.url));
}

function updateBadge(tabId) {
  const list = media.get(tabId) || [];
  const n = visible(tabId, list).length;
  chrome.action.setBadgeText({ tabId, text: n ? String(n) : '' }).catch(() => {});
  chrome.action.setBadgeBackgroundColor({ tabId, color: '#2563EB' }).catch(() => {});
}

async function addMedia(tabId, entry) {
  const list = await getList(tabId);
  const ex = list.find((m) => m.url === entry.url);
  if (ex) { if (entry.size > ex.size) ex.size = entry.size; return; }
  list.push(entry);
  if (list.length > 60) list.shift();
  if (entry.type === 'hls') inspectHls(tabId, entry);
  persist(tabId);
  updateBadge(tabId);
}

async function addSub(tabId, sub) {
  await getList(tabId);
  const list = subsByTab.get(tabId) || [];
  if (list.some((s) => s.url === sub.url)) return;
  list.push(sub);
  if (list.length > 30) list.shift();
  subsByTab.set(tabId, list);
  persist(tabId);
}

// HLS altyazı playlist'lerinin parçalarını (aynı klasördeki .vtt'ler) ayrı altyazı sayma
function tabSubs(tabId) {
  const dirs = new Set([...(children.get(tabId) || [])].map((u) => u.replace(/[^/]*$/, '')));
  return (subsByTab.get(tabId) || []).filter((s) => !dirs.has(s.url.replace(/[^/?#]*([?#].*)?$/, '')));
}

// Master playlist ise kaliteleri oku ve alt playlist'leri listeden gizle
async function inspectHls(tabId, entry) {
  try {
    const r = await fetch(entry.url, { credentials: 'include' });
    const text = await r.text();
    if (!text.includes('#EXT-X-STREAM-INF')) {
      if (/#EXT-X-KEY:.*METHOD=SAMPLE-AES|KEYFORMAT="(?!identity)/i.test(text)) entry.drm = true;
      return;
    }
    entry.master = true;
    const ch = children.get(tabId) || new Set();
    let best = 0;
    const lines = text.split(/\r?\n/);
    lines.forEach((l, i) => {
      const res = /RESOLUTION=\d+x(\d+)/.exec(l);
      if (res) best = Math.max(best, +res[1]);
      if (l.startsWith('#EXT-X-STREAM-INF')) {
        const next = lines.slice(i + 1).find((x) => x && !x.startsWith('#'));
        if (next) ch.add(new URL(next.trim(), entry.url).href);
      }
      const uri = /#EXT-X-MEDIA:.*URI="([^"]+)"/.exec(l);
      if (uri) ch.add(new URL(uri[1], entry.url).href);
    });
    if (best) entry.quality = best + 'p';
    children.set(tabId, ch);
    persist(tabId); updateBadge(tabId);
  } catch { /* erişilemedi, olduğu gibi kalsın */ }
}

chrome.webRequest.onHeadersReceived.addListener((d) => {
  if (d.tabId < 0 || d.statusCode >= 400) return;
  const url = d.url;
  if (!/^https?:/i.test(url)) return;
  const h = {};
  for (const x of d.responseHeaders || []) h[x.name.toLowerCase()] = x.value || '';
  const ct = (h['content-type'] || '').toLowerCase().split(';')[0].trim();

  if ((RX_SUB.test(url) || SUB_TYPES.includes(ct)) && !RX_NOT_SUB.test(url)) {
    const origin = d.initiator && d.initiator !== 'null' ? d.initiator : undefined;
    addSub(d.tabId, { url, ts: Date.now(), origin, referer: origin ? origin + '/' : undefined });
    return;
  }

  let type = null;
  if (RX_HLS.test(url) || ct.includes('mpegurl')) type = 'hls';
  else if (RX_DASH.test(url) || ct === 'application/dash+xml') type = 'dash';
  else if (RX_SKIP.test(url) || ct === 'video/mp2t' || ct === 'video/iso.segment') return;
  else if (ct.startsWith('video/') || ct.startsWith('audio/') || (RX_MEDIA.test(url) && (!ct || ct.includes('octet-stream'))))
    type = ct.startsWith('audio/') || RX_AUDIO.test(url) ? 'audio' : 'video';
  if (!type) return;

  let size = parseInt(h['content-length'] || '0', 10) || 0;
  const cr = /\/(\d+)\s*$/.exec(h['content-range'] || '');
  if (cr) size = parseInt(cr[1], 10);
  if ((type === 'video' || type === 'audio') && size > 0 && size < 200 * 1024) return; // reklam/önizleme parçaları

  addMedia(d.tabId, { url, type, size, mime: ct, ts: Date.now() });
}, { urls: ['<all_urls>'], types: ['media', 'xmlhttprequest', 'other', 'object', 'sub_frame'] }, ['responseHeaders']);

// Sayfa değişince listeyi temizle; ama son 3 sn'de yakalananları koru (yeni sayfanın ilk istekleri
// "adres değişti" olayından önce gelebiliyor)
chrome.tabs.onUpdated.addListener(async (tabId, info) => {
  if (!info.url) return;
  const cutoff = Date.now() - 3000;
  const keepM = (await getList(tabId)).filter((m) => m.ts > cutoff);
  const keepS = (subsByTab.get(tabId) || []).filter((x) => x.ts > cutoff);
  clearTab(tabId);
  if (keepM.length || keepS.length) {
    media.set(tabId, keepM); children.set(tabId, new Set()); subsByTab.set(tabId, keepS);
    persist(tabId); updateBadge(tabId);
  }
});
chrome.tabs.onRemoved.addListener((tabId) => clearTab(tabId));

function sortedMedia(tabId, list) {
  const rank = (m) => (m.type === 'hls' ? (m.master ? 0 : 1) : m.type === 'video' ? 2 : m.type === 'dash' ? 3 : 4);
  return visible(tabId, list).slice().sort((a, b) => rank(a) - rank(b) || (b.size || 0) - (a.size || 0));
}

// ---------------- Uygulama ile iletişim ----------------

let aliveCache = { t: 0, ok: false };
async function alive(force = false) {
  if (!force && Date.now() - aliveCache.t < 3000) return aliveCache.ok;
  const { port } = await cfg();
  let ok = false;
  try {
    const ctl = new AbortController();
    const timer = setTimeout(() => ctl.abort(), 900);
    const r = await fetch(`http://localhost:${port}/ping`, { signal: ctl.signal });
    clearTimeout(timer);
    ok = r.ok && (await r.json()).app === 'TurboDM';
  } catch { ok = false; }
  aliveCache = { t: Date.now(), ok };
  return ok;
}

async function cookieHeader(url) {
  try {
    const list = await chrome.cookies.getAll({ url });
    return list.map((c) => `${c.name}=${c.value}`).join('; ');
  } catch { return ''; }
}

const cleanTitle = (t) => (t || '').replace(/^\(\d+\)\s*/, '').replace(/\s*[-|–]\s*(YouTube|Vimeo|Dailymotion|Twitch|TikTok|Instagram|Facebook|X)\s*$/i, '').trim();

function notify(title, message) {
  chrome.notifications.create({ type: 'basic', iconUrl: 'icons/icon128.png', title, message }).catch(() => {});
}

async function send(req, tab) {
  const { port } = await cfg();
  const body = {
    url: req.url,
    kind: req.kind || 'auto',
    fileName: req.fileName || undefined,
    referer: req.referer || tab?.url || undefined,
    pageTitle: req.pageTitle || cleanTitle(tab?.title) || undefined,
    userAgent: navigator.userAgent,
    cookies: await cookieHeader(req.url),
    silent: !!req.silent,
    audioOnly: !!req.audioOnly,
  };
  // video ile birlikte yakalanan altyazıları gönder (yt-dlp kendi altyazısını bulur)
  if (body.kind !== 'ytdlp' && tab?.id != null) {
    await getList(tab.id);
    const all = [...(req.subtitles || []), ...tabSubs(tab.id)];
    const seen = new Set();
    body.subtitles = all.filter((s) => s.url && /^https?:/i.test(s.url) && !seen.has(s.url) && seen.add(s.url))
      .map((s) => ({ url: s.url, lang: s.lang, name: s.name, referer: s.referer, origin: s.origin }));
    for (const s of body.subtitles) s.cookies = (await cookieHeader(s.url)) || undefined;
    if (!body.subtitles.length) delete body.subtitles;
  }
  try {
    const r = await fetch(`http://localhost:${port}/add`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    });
    if (!r.ok) throw new Error('HTTP ' + r.status);
    aliveCache = { t: Date.now(), ok: true };
    return { ok: true };
  } catch (e) {
    aliveCache = { t: Date.now(), ok: false };
    notify('TurboDM çalışmıyor', 'Masaüstü uygulamasını başlatın (TurboDM.exe) ve tekrar deneyin.');
    return { ok: false, error: String(e) };
  }
}

// Sayfa için en iyi seçeneği bul: video sitesi → yt-dlp, yakalanan akış → o, yoksa sayfayı yt-dlp ile dene
async function bestRequestForTab(tab, frameUrl) {
  if (frameUrl && isYtDlpSite(frameUrl)) return { url: frameUrl, kind: 'ytdlp' };
  if (isYtDlpSite(tab.url)) return { url: tab.url, kind: 'ytdlp' };
  const list = sortedMedia(tab.id, await getList(tab.id));
  const m = list.find((x) => !x.drm);
  if (m) return { url: m.url, kind: m.type === 'hls' ? 'hls' : m.type === 'dash' ? 'ytdlp' : 'direct' };
  return { url: tab.url, kind: 'ytdlp' };
}

// ---------------- Tarayıcı indirmelerini yakalama ----------------
const bypass = new Set();

chrome.downloads.onCreated.addListener(async (item) => {
  try {
    const c = await cfg();
    if (!c.intercept || item.byExtensionId === chrome.runtime.id) return;
    const url = item.finalUrl || item.url;
    if (!/^https?:/i.test(url) || bypass.has(url)) return;
    if (item.state !== 'in_progress') return;

    const name = (item.filename || '').split(/[\\/]/).pop() || decodeURIComponent(new URL(url).pathname.split('/').pop() || '');
    const ext = (name.match(/\.([a-z0-9]{1,6})$/i) || [])[1]?.toLowerCase() || '';
    const exts = c.exts.toLowerCase().split(/[\s,;]+/).filter(Boolean);
    const size = item.totalBytes > 0 ? item.totalBytes : item.fileSize;
    const byExt = ext && exts.includes(ext);
    const bySize = size > 0 && size >= c.minSizeKB * 1024;
    if (!byExt && !bySize) return;
    if (!(await alive())) return; // uygulama kapalı → tarayıcı indirsin

    await chrome.downloads.cancel(item.id).catch(() => {});
    chrome.downloads.erase({ id: item.id }).catch(() => {});
    const tab = item.referrer ? null : await chrome.tabs.query({ active: true, lastFocusedWindow: true }).then((t) => t[0]).catch(() => null);
    const r = await send({ url, kind: 'direct', referer: item.referrer || tab?.url, fileName: name && ext ? name : undefined }, null);
    if (!r.ok) { bypass.add(url); chrome.downloads.download({ url }); }
  } catch (e) { console.warn('TurboDM intercept', e); }
});

// ---------------- Sağ tık menüsü ----------------
chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: 'tdm-link', title: 'TurboDM ile indir', contexts: ['link', 'video', 'audio', 'image'] });
    chrome.contextMenus.create({ id: 'tdm-page', title: 'Bu sayfadaki videoyu TurboDM ile indir', contexts: ['page', 'frame', 'video'] });
  });
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId === 'tdm-link') {
    const url = info.linkUrl || info.srcUrl;
    if (url && /^https?:/i.test(url)) {
      const kind = isYtDlpSite(url) ? 'ytdlp' : RX_HLS.test(url) ? 'hls' : 'direct';
      return send({ url, kind, referer: info.frameUrl || info.pageUrl }, tab);
    }
  }
  // blob: video veya sayfa → en iyi tahmin
  const req = await bestRequestForTab(tab, info.frameUrl);
  send(req, tab);
});

// ---------------- Mesajlar (content script & popup) ----------------
chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  (async () => {
    const tabId = msg.tabId ?? sender.tab?.id;
    const tab = msg.tabId != null ? await chrome.tabs.get(msg.tabId).catch(() => null) : sender.tab;
    switch (msg.type) {
      case 'getMedia': {
        const list = tabId != null ? sortedMedia(tabId, await getList(tabId)) : [];
        reply({ items: list, subs: tabId != null ? tabSubs(tabId) : [], pageUrl: tab?.url, pageTitle: cleanTitle(tab?.title), ytdlpSite: isYtDlpSite(tab?.url || '') });
        break;
      }
      case 'send':
        reply(await send(msg.req, tab));
        break;
      case 'sendBest':
        reply(tab ? await send(await bestRequestForTab(tab, msg.frameUrl), tab) : { ok: false });
        break;
      case 'ping':
        reply({ ok: await alive(true) });
        break;
      case 'clear':
        if (tabId != null) clearTab(tabId);
        reply({ ok: true });
        break;
      default:
        reply({ ok: false });
    }
  })();
  return true; // async yanıt
});

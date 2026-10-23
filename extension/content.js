// TurboDM: videoların üzerinde "İndir" butonu gösterir (IDM'deki video yakalama paneli gibi)
(() => {
  'use strict';
  if (window.__turbodm) return;
  window.__turbodm = true;

  const YT_HOSTS = /(^|\.)(youtube\.com|youtu\.be|instagram\.com|tiktok\.com|twitter\.com|x\.com|vimeo\.com|dailymotion\.com|twitch\.tv|facebook\.com|reddit\.com|bilibili\.com|kick\.com|rumble\.com)$/i;
  let enabled = true;
  try {
    chrome.storage.sync.get({ showButton: true }, (o) => { enabled = o.showButton !== false; });
    chrome.storage.onChanged.addListener((c) => { if (c.showButton) { enabled = c.showButton.newValue !== false; if (!enabled) hide(true); } });
  } catch { /* eklenti yeniden yüklendi */ }

  // ---- Shadow DOM içinde arayüz (sitenin CSS'inden etkilenmesin) ----
  const host = document.createElement('turbodm-ui');
  host.style.cssText = 'all:initial;position:fixed;z-index:2147483647;top:0;left:0;display:none;';
  const root = host.attachShadow({ mode: 'open' });
  root.innerHTML = `
    <style>
      :host { all: initial; }
      .btn { display:flex; align-items:center; gap:6px; font:600 12px/1 "Segoe UI",system-ui,sans-serif; color:#fff;
             background:rgba(37,99,235,.95); border:0; border-radius:7px; padding:7px 11px 7px 9px; cursor:pointer;
             box-shadow:0 2px 10px rgba(0,0,0,.35); white-space:nowrap; }
      .btn:hover { background:#1D4ED8; }
      .btn svg { width:14px; height:14px; flex:none; }
      .btn.ok { background:#16A34A; } .btn.err { background:#DC2626; }
      .menu { position:absolute; right:0; top:34px; min-width:280px; max-width:420px; background:#fff; color:#111827;
              border-radius:8px; box-shadow:0 8px 28px rgba(0,0,0,.35); padding:4px; display:none;
              font:13px/1.35 "Segoe UI",system-ui,sans-serif; }
      .menu.open { display:block; }
      .item { display:flex; gap:8px; align-items:center; padding:7px 9px; border-radius:6px; cursor:pointer; }
      .item:hover { background:#EEF2FF; }
      .tag { flex:none; font:700 10px/1 "Segoe UI",sans-serif; padding:3px 5px; border-radius:4px; background:#DBEAFE; color:#1D4ED8; text-transform:uppercase; }
      .tag.yt { background:#FEE2E2; color:#B91C1C; } .tag.audio { background:#DCFCE7; color:#15803D; }
      .name { overflow:hidden; text-overflow:ellipsis; white-space:nowrap; flex:1; }
      .meta { flex:none; color:#6B7280; font-size:11px; }
      .x { position:absolute; left:-8px; top:-8px; width:16px; height:16px; border-radius:50%; background:#111827; color:#fff;
           font:700 10px/16px sans-serif; text-align:center; cursor:pointer; display:none; }
      .wrap:hover .x { display:block; }
      .wrap { position:relative; }
    </style>
    <div class="wrap">
      <span class="x" title="Bu sayfada gizle">✕</span>
      <button class="btn" title="TurboDM ile indir">
        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.6" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12M6 10l6 6 6-6M5 21h14"/></svg>
        <span class="label">TurboDM ile indir</span>
      </button>
      <div class="menu"></div>
    </div>`;
  const btn = root.querySelector('.btn');
  const label = root.querySelector('.label');
  const menu = root.querySelector('.menu');
  const closeX = root.querySelector('.x');

  let target = null, hideTimer = null, overUi = false, dismissed = false, busy = false;

  const attach = () => {
    const parent = document.fullscreenElement && document.fullscreenElement.tagName !== 'VIDEO' ? document.fullscreenElement : document.documentElement;
    if (host.parentNode !== parent) parent.appendChild(host);
  };
  document.addEventListener('fullscreenchange', () => { attach(); if (target) place(target); });

  function place(v) {
    const r = v.getBoundingClientRect();
    const w = btn.offsetWidth || 150;
    const top = Math.max(r.top + 10, 6);
    const left = Math.min(Math.max(r.right - w - 10, r.left + 6), window.innerWidth - w - 6);
    host.style.top = top + 'px';
    host.style.left = left + 'px';
  }

  function show(v) {
    if (!enabled || dismissed) return;
    clearTimeout(hideTimer);
    if (target !== v) { menu.classList.remove('open'); }
    target = v;
    attach();
    host.style.display = 'block';
    place(v);
  }

  function hide(now) {
    clearTimeout(hideTimer);
    const run = () => { if (overUi || busy) return; host.style.display = 'none'; menu.classList.remove('open'); target = null; };
    if (now) run(); else hideTimer = setTimeout(run, 900);
  }

  host.addEventListener('mouseenter', () => { overUi = true; clearTimeout(hideTimer); });
  host.addEventListener('mouseleave', () => { overUi = false; hide(); });
  closeX.addEventListener('click', (e) => { e.stopPropagation(); dismissed = true; hide(true); });

  // Videolar sıklıkla saydam katmanların altında kalır; bu yüzden fare konumunu dikdörtgenlerle karşılaştırıyoruz
  let lastMove = 0;
  document.addEventListener('mousemove', (e) => {
    const now = performance.now();
    if (now - lastMove < 120) return;
    lastMove = now;
    if (overUi) return;
    let found = null;
    for (const v of document.getElementsByTagName('video')) {
      const r = v.getBoundingClientRect();
      if (r.width < 160 || r.height < 90) continue;
      if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) { found = v; break; }
    }
    if (found) show(found); else if (target) hide();
  }, { passive: true, capture: true });

  window.addEventListener('scroll', () => { if (target && host.style.display !== 'none') place(target); }, { passive: true, capture: true });

  // ---- Seçenekler ----
  const fmtSize = (b) => !b ? '' : b > 1073741824 ? (b / 1073741824).toFixed(2) + ' GB' : b > 1048576 ? (b / 1048576).toFixed(1) + ' MB' : Math.round(b / 1024) + ' KB';
  const shortName = (u) => { try { const p = new URL(u).pathname.split('/').filter(Boolean).pop() || new URL(u).hostname; return decodeURIComponent(p); } catch { return u; } };

  function msg(m) {
    return new Promise((res) => {
      try { chrome.runtime.sendMessage(m, (r) => res(chrome.runtime.lastError ? null : r)); }
      catch { res(null); }
    });
  }

  async function buildOptions(v) {
    const info = (await msg({ type: 'getMedia' })) || { items: [] };
    const opts = [];
    const frameIsVideoSite = YT_HOSTS.test(location.hostname);
    if (frameIsVideoSite || info.ytdlpSite) {
      opts.push({ tag: 'yt', tagText: 'En iyi', name: 'En yüksek kalite (video + ses)', meta: 'yt-dlp', req: { url: frameIsVideoSite ? location.href : info.pageUrl, kind: 'ytdlp' } });
      opts.push({ tag: 'audio', tagText: 'Ses', name: 'Yalnızca ses', meta: 'yt-dlp', req: { url: frameIsVideoSite ? location.href : info.pageUrl, kind: 'ytdlp', audioOnly: true } });
    }
    const src = v && (v.currentSrc || v.src);
    const seen = new Set();
    if (src && /^https?:/i.test(src) && !frameIsVideoSite) {
      seen.add(src);
      opts.push({ tag: '', tagText: 'Video', name: shortName(src), meta: 'bu oynatıcı', req: { url: src, kind: 'direct' } });
    }
    for (const m of info.items || []) {
      if (seen.has(m.url) || (frameIsVideoSite && m.type !== 'hls')) continue;
      seen.add(m.url);
      const tagText = m.type === 'hls' ? (m.master ? 'HLS' : 'HLS') : m.type === 'dash' ? 'DASH' : m.type === 'audio' ? 'Ses' : (m.mime?.split('/')[1] || 'video');
      const meta = m.drm ? 'DRM — indirilemez' : m.type === 'hls' ? (m.master ? (m.quality ? 'çoklu kalite, en iyi ' + m.quality : 'çoklu kalite') : 'akış') : fmtSize(m.size);
      opts.push({ tag: m.type === 'audio' ? 'audio' : '', tagText, name: shortName(m.url), meta, disabled: m.drm,
        req: { url: m.url, kind: m.type === 'hls' ? 'hls' : m.type === 'dash' ? 'ytdlp' : 'direct' } });
      if (opts.length >= 10) break;
    }
    if (!opts.length) opts.push({ tag: 'yt', tagText: 'Dene', name: 'Sayfayı yt-dlp ile analiz et', meta: '', req: { url: location.href, kind: 'ytdlp' } });
    return { opts, info };
  }

  function flash(text, cls) {
    label.textContent = text;
    btn.classList.add(cls);
    setTimeout(() => { label.textContent = 'TurboDM ile indir'; btn.classList.remove(cls); busy = false; hide(); }, 1800);
  }

  // <track> etiketleri: oynatıcının tanımladığı altyazılar (kullanıcı açmasa da)
  function pageTracks() {
    return [...document.querySelectorAll('track[src]')]
      .filter((t) => !t.kind || t.kind === 'subtitles' || t.kind === 'captions')
      .map((t) => ({ url: t.src, lang: t.srclang || undefined, name: t.label || undefined, referer: location.href, origin: location.origin }))
      .filter((t) => /^https?:/i.test(t.url));
  }

  async function sendReq(req, info) {
    menu.classList.remove('open');
    busy = true;
    label.textContent = 'Gönderiliyor…';
    const r = await msg({ type: 'send', req: { ...req, subtitles: req.kind === 'ytdlp' ? undefined : pageTracks(), pageTitle: info?.pageTitle || document.title, referer: info?.pageUrl || location.href } });
    flash(r && r.ok ? 'TurboDM\'e gönderildi ✓' : 'Uygulama kapalı!', r && r.ok ? 'ok' : 'err');
  }

  btn.addEventListener('click', async (e) => {
    e.preventDefault(); e.stopPropagation();
    if (menu.classList.contains('open')) { menu.classList.remove('open'); return; }
    const { opts, info } = await buildOptions(target);
    const usable = opts.filter((o) => !o.disabled);
    if (usable.length === 1 && !opts.some((o) => o.tag === 'yt' && o.tagText === 'En iyi')) return sendReq(usable[0].req, info);
    menu.innerHTML = '';
    for (const o of opts) {
      const el = document.createElement('div');
      el.className = 'item';
      if (o.disabled) el.style.opacity = '.45';
      el.innerHTML = `<span class="tag ${o.tag}"></span><span class="name"></span><span class="meta"></span>`;
      el.querySelector('.tag').textContent = o.tagText;
      el.querySelector('.name').textContent = o.name;
      el.querySelector('.name').title = o.req.url;
      el.querySelector('.meta').textContent = o.meta;
      if (!o.disabled) el.addEventListener('click', (ev) => { ev.stopPropagation(); sendReq(o.req, info); });
      menu.appendChild(el);
    }
    menu.classList.add('open');
  }, true);
})();

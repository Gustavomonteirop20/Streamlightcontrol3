namespace StreamLightControl2.Core.Services;

/// <summary>
/// HTML van de kerst-TikTok-overlays (kerstversies van BetterTok-onderdelen: giftalert, Gift Gallery, leaderboard,
/// grootste gift, laatste volger/sub). Namen en giftnamen van kijkers komen alleen via textContent op de pagina.
/// </summary>
public static class TikTokPages
{
    public static string Page(string view) => Template.Replace("__VIEW__", view);

    // Kerst-iconen per waarde van de gift (1 = goedkoop … 5 = heel duur). Vaste eigen SVG's, geen kijkersdata.
    private const string Icons = """
<svg style="display:none" aria-hidden="true">
<symbol id="t1" viewBox="0 0 48 48"><path d="M30 44 V16 a8 8 0 0 0 -16 0" fill="none" stroke="#F4F7FF" stroke-width="7" stroke-linecap="round"/><path d="M30 44 V16 a8 8 0 0 0 -16 0" fill="none" stroke="#E5484D" stroke-width="7" stroke-linecap="round" stroke-dasharray="5 6"/></symbol>
<symbol id="t2" viewBox="0 0 48 48"><rect x="7" y="20" width="34" height="23" rx="3" fill="#2FA36B"/><rect x="5" y="14" width="38" height="9" rx="2" fill="#38B97A"/><rect x="21" y="14" width="6" height="29" fill="#FFD600"/><path d="M24 14 C18 4 10 8 14 13 Z M24 14 C30 4 38 8 34 13 Z" fill="#FFD600"/></symbol>
<symbol id="t3" viewBox="0 0 48 48"><rect x="19" y="3" width="10" height="7" rx="2" fill="#C9D2EC"/><circle cx="24" cy="27" r="17" fill="#E5484D"/><path d="M8 24 C16 28 32 20 40 24" stroke="#FFD600" stroke-width="3" fill="none"/><path d="M9 32 C17 36 31 28 39 32" stroke="#F4F7FF" stroke-width="2" fill="none" opacity=".8"/><circle cx="17" cy="20" r="3" fill="#fff" opacity=".55"/></symbol>
<symbol id="t4" viewBox="0 0 48 48"><path d="M24 3 L10 20 h6 L6 32 h12 L4 42 h40 L30 32 h12 L32 20 h6 Z" fill="#2FA36B"/><rect x="21" y="42" width="6" height="5" fill="#8A5A2B"/><circle cx="20" cy="22" r="2.3" fill="#E5484D"/><circle cx="28" cy="29" r="2.3" fill="#FFD600"/><circle cx="17" cy="35" r="2.3" fill="#6CB8FF"/><circle cx="31" cy="38" r="2.3" fill="#E5484D"/><path d="M24 0 L25.5 4 L29.5 4 L26.3 6.5 L27.5 10.5 L24 8 L20.5 10.5 L21.7 6.5 L18.5 4 L22.5 4 Z" fill="#FFD600"/></symbol>
<symbol id="t5" viewBox="0 0 48 48"><circle cx="24" cy="24" r="22" fill="#FFB800" opacity=".18"/><path d="M24 4 L29 18 L44 18.5 L32 27.5 L36.5 42 L24 33.5 L11.5 42 L16 27.5 L4 18.5 L19 18 Z" fill="#FFD600" stroke="#B37F00" stroke-width="1.5" stroke-linejoin="round"/><circle cx="24" cy="24" r="4" fill="#fff" opacity=".7"/></symbol>
<symbol id="ifollow" viewBox="0 0 48 48"><circle cx="24" cy="24" r="22" fill="#E5484D"/><path d="M14 22 C14 12 34 12 34 22 Z" fill="#F4F7FF"/><circle cx="24" cy="10" r="4" fill="#F4F7FF"/><circle cx="19" cy="27" r="2" fill="#121833"/><circle cx="29" cy="27" r="2" fill="#121833"/><path d="M18 33 C21 36 27 36 30 33" stroke="#121833" stroke-width="2" fill="none" stroke-linecap="round"/></symbol>
<symbol id="isub" viewBox="0 0 48 48"><path d="M6 36 L10 14 L18 24 L24 8 L30 24 L38 14 L42 36 Z" fill="#FFD600" stroke="#B37F00" stroke-width="1.5" stroke-linejoin="round"/><rect x="6" y="36" width="36" height="6" rx="2" fill="#FFB800"/><circle cx="24" cy="30" r="3" fill="#E5484D"/><circle cx="14" cy="31" r="2.2" fill="#2FA36B"/><circle cx="34" cy="31" r="2.2" fill="#6CB8FF"/></symbol>
</svg>
""";

    private static readonly string Template = """
<!doctype html>
<html lang="nl">
<head>
<meta charset="utf-8">
<title>Kerst-overlay TikTok</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Baloo+2:wght@600;800&family=Inter:wght@400;600;700&display=swap" rel="stylesheet">
<style>
:root{--night:#0A0F24;--card:rgba(18,24,51,.95);--gold:#FFB800;--name:#FFD600;--red:#E5484D;--green:#2FA36B;--blue:#6CB8FF;--snow:#F4F7FF;--soft:#C9D2EC;--muted:#AEB8D6}
html,body{margin:0;background:transparent;color:var(--snow);font-family:Inter,Arial,sans-serif;overflow:hidden}
body.bg{background:var(--night)}
.view{display:none}
body[data-view="tiktok-alert"] .w-alert,body[data-view="tiktok-gallery"] .w-gallery,body[data-view="tiktok-ranking"] .w-ranking,
body[data-view="tiktok-topgift"] .w-top,body[data-view="tiktok-volger"] .w-volger{display:flex}
body[data-view="tiktok-alles"] .view{display:flex}
body[data-view="tiktok-alles"] #stack{width:432px;padding:20px 16px;box-sizing:border-box;gap:22px}
body[data-view="tiktok-alles"] .view{width:100%;justify-content:center}
body[data-view="tiktok-alles"] .list,body[data-view="tiktok-alles"] .sup,body[data-view="tiktok-alles"] .top{width:100%!important}
body[data-view="tiktok-alles"] .w-alert{position:fixed;left:0;right:0;top:120px;z-index:5;width:432px!important}
body[data-view="tiktok-alles"] .alert{width:400px;padding:44px 22px 22px}
body[data-view="tiktok-alles"] .a-name{font-size:34px}
#stack{display:flex;flex-direction:column;align-items:center;gap:24px;padding:24px;transform-origin:0 0}
.head{font-family:'Baloo 2',Inter,sans-serif;font-weight:800;line-height:1.05}
.card{position:relative;box-sizing:border-box;background:var(--card);border:1.5px solid rgba(255,184,0,.55);border-radius:16px}
.snow{position:absolute;left:-1px;top:-10px;width:calc(100% + 2px);height:22px}
.empty{color:var(--muted);font-size:15px;text-align:center;padding:6px 0}
.ico{flex:none}
/* alert */
.w-alert{justify-content:center}
.alert{width:440px;padding:46px 30px 24px;border:2px solid var(--gold);border-radius:26px;display:flex;flex-direction:column;align-items:center;gap:6px;text-align:center;overflow:visible;
  opacity:0;transform:translateY(-30px) scale(.92);transition:opacity .45s ease,transform .45s cubic-bezier(.2,1.4,.4,1)}
.alert.on{opacity:1;transform:none}
.alert.big{box-shadow:0 0 0 3px rgba(255,184,0,.35),0 0 40px rgba(255,184,0,.35)}
.alert .snow{top:-14px;height:30px}
.lights{position:absolute;left:34px;right:34px;top:22px;display:flex;justify-content:space-between}
.bulb{width:10px;height:14px;border-radius:50% 50% 45% 45%;animation:tw 1.6s ease-in-out infinite}
.bulb:nth-child(2n){animation-delay:.4s}.bulb:nth-child(3n){animation-delay:.8s}.bulb:nth-child(5n){animation-delay:1.2s}
@keyframes tw{0%,100%{opacity:1}50%{opacity:.3}}
.a-ico{width:84px;height:84px;margin-top:4px;animation:bob 1.8s ease-in-out infinite}
@keyframes bob{0%,100%{transform:translateY(0) rotate(-4deg)}50%{transform:translateY(-6px) rotate(4deg)}}
.pill{display:flex;align-items:center;gap:8px;padding:4px 14px;border-radius:999px;background:var(--red);color:#fff;font-size:14px;font-weight:700}
.a-name{font-size:40px;color:var(--name);margin-top:2px;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.a-gift{font-size:22px;font-weight:700;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.a-coins{font-size:16px;font-weight:600;color:var(--soft)}
.flake{position:absolute;top:-8px;color:#fff;opacity:.9;font-size:14px;animation:fall 2.6s linear infinite;pointer-events:none}
@keyframes fall{from{transform:translateY(0)}to{transform:translateY(300px);opacity:0}}
/* lijsten */
.list{width:380px;padding:26px 18px 18px;display:flex;flex-direction:column;gap:12px;border-radius:18px}
.items{display:flex;flex-direction:column;gap:8px}
.rk{display:flex;align-items:center;gap:12px;padding:10px 12px;border-radius:12px;background:#161D42}
.rk.r1{background:#1B2350;border:1.5px solid var(--gold)}
.rk .n{flex:1;font-size:17px;font-weight:700;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.rk .v{font-size:16px;font-weight:700;flex:none}
.rk.r1 .v{color:var(--name)}
.grid{display:grid;grid-template-columns:repeat(3,1fr);gap:10px}
.tile{display:flex;flex-direction:column;align-items:center;gap:3px;padding:12px 6px 10px;border-radius:12px;background:#161D42;text-align:center;min-width:0}
.tile .gn{font-size:14px;font-weight:700;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.tile .gc{font-family:'Baloo 2',Inter,sans-serif;font-weight:800;font-size:22px;color:var(--name);line-height:1}
.tile .gt{font-size:12px;color:var(--muted);max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.top{width:420px;padding:16px 18px;display:flex;align-items:center;gap:14px;border-radius:16px;border-color:var(--gold)}
.sup{width:380px;display:flex;flex-direction:column;gap:16px}
.sc{padding:16px;border-radius:16px;display:flex;align-items:center;gap:14px}
.lab{font-size:12px;font-weight:700;letter-spacing:.08em}
.big{font-size:24px;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.sub{font-size:14px;color:var(--soft);min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.col{display:flex;flex-direction:column;gap:2px;min-width:0;flex:1}
.new{animation:pop .6s cubic-bezier(.2,1.4,.4,1)}
@keyframes pop{from{transform:scale(.85);opacity:0}to{transform:none;opacity:1}}
body.still *{animation:none!important;transition:none!important}
</style>
</head>
<body data-view="__VIEW__">
__ICONS__
<div id="stack">

<div class="view w-alert">
<div class="card alert" id="alert" aria-live="polite">
__SNOWALERT__
<div class="lights" aria-hidden="true"><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span></div>
<svg class="a-ico" aria-hidden="true"><use id="a-ico" href="#t2"/></svg>
<div class="pill" id="a-kind">Kerstcadeau</div>
<div class="head a-name" id="a-name"></div>
<div class="a-gift" id="a-gift"></div>
<div class="a-coins" id="a-coins"></div>
<div id="flakes" aria-hidden="true"></div>
</div>
</div>

<div class="view w-gallery">
<div class="card list">
__SNOW__
<div style="display:flex;flex-direction:column;align-items:center"><span class="head" style="font-size:26px;color:var(--name)">Cadeaus onder de boom</span><span style="font-size:13px;font-weight:600;color:var(--muted);letter-spacing:.06em">GIFT GALLERY · DEZE STREAM</span></div>
<div class="grid" id="gallery"></div>
<div class="empty" id="gallery-empty">Nog geen cadeaus deze stream</div>
</div>
</div>

<div class="view w-ranking">
<div class="card list">
__SNOW__
<div style="display:flex;flex-direction:column;align-items:center"><span class="head" style="font-size:26px;color:var(--name)">Lijst van de Kerstman</span><span style="font-size:13px;font-weight:600;color:var(--muted);letter-spacing:.06em">TOP GIFTERS VAN DEZE STREAM</span></div>
<div class="items" id="ranking"></div>
</div>
</div>

<div class="view w-top">
<div class="card top" id="top">
__SNOW__
<svg class="ico" width="58" height="58" aria-hidden="true"><use id="tg-ico" href="#t5"/></svg>
<div class="col"><span class="lab" style="color:var(--name)">GROOTSTE CADEAU</span><span class="head big" id="tg-name">—</span><span class="sub" id="tg-gift">Nog geen cadeaus deze stream</span></div>
</div>
</div>

<div class="view w-volger">
<div class="sup">
<div class="card sc">
__SNOW__
<svg class="ico" width="46" height="46" aria-hidden="true"><use href="#ifollow"/></svg>
<div class="col"><span class="lab" style="color:var(--red)">LAATSTE VOLGER</span><span class="head big" id="f-name">—</span><span class="sub" id="f-ago">Nog geen nieuwe volgers</span></div>
</div>
<div class="card sc" style="border-color:var(--gold)">
<svg class="ico" width="46" height="46" aria-hidden="true"><use href="#isub"/></svg>
<div class="col"><span class="lab" style="color:var(--name)">LAATSTE SUBSCRIBER</span><span class="head big" id="s-name">—</span><span class="sub" id="s-ago">Nog geen subs</span></div>
</div>
</div>
</div>

</div>
<script>
(function(){
  var q = new URLSearchParams(location.search);
  if (q.get('bg') === '1') document.body.classList.add('bg');
  if (q.get('still') === '1') document.body.classList.add('still');
  var sc = parseFloat(q.get('scale') || '1');
  if (sc > 0 && sc !== 1) document.getElementById('stack').style.transform = 'scale(' + sc + ')';
  var $ = function(id){ return document.getElementById(id); };
  var NS = 'http://www.w3.org/2000/svg';
  var state = null, lastVersion = -1;
  var tierName = ['', 'Zuurstokje', 'Kerstcadeau', 'Kerstbal', 'Kerstboom', 'Kerstster'];

  function el(tag, cls, text){ var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function icon(tier, size){
    var s = document.createElementNS(NS, 'svg'); s.setAttribute('width', size); s.setAttribute('height', size); s.setAttribute('aria-hidden', 'true'); s.setAttribute('class', 'ico');
    var u = document.createElementNS(NS, 'use'); u.setAttribute('href', '#t' + Math.min(5, Math.max(1, tier|0))); s.appendChild(u); return s;
  }
  function ago(at, now){ var m = Math.max(0, Math.round((now - at) / 60000)); return m < 1 ? 'zojuist' : m < 60 ? m + ' min geleden' : Math.round(m / 60) + ' uur geleden'; }
  function giftText(g, n){ return g + (n > 1 ? ' ×' + n : ''); }

  function render(s){
    var r = $('ranking'); r.textContent = '';
    if (!s.ranking.length) r.appendChild(el('div','empty','Nog geen gifters deze stream'));
    var ball = ['#FFB800','#C9D2EC','#E5484D','#2FA36B','#2FA36B'];
    s.ranking.forEach(function(x){
      var row = el('div','rk' + (x.rank === 1 ? ' r1' : ''));
      var b = document.createElementNS(NS,'svg'); b.setAttribute('viewBox','0 0 32 36'); b.setAttribute('width','30'); b.setAttribute('height','34'); b.setAttribute('aria-hidden','true');
      var cap = document.createElementNS(NS,'rect'); cap.setAttribute('x','12'); cap.setAttribute('y','1'); cap.setAttribute('width','8'); cap.setAttribute('height','5'); cap.setAttribute('rx','1.5'); cap.setAttribute('fill','#7D879F');
      var c = document.createElementNS(NS,'circle'); c.setAttribute('cx','16'); c.setAttribute('cy','21'); c.setAttribute('r','14'); c.setAttribute('fill', ball[x.rank-1]);
      var t = document.createElementNS(NS,'text'); t.setAttribute('x','16'); t.setAttribute('y','26'); t.setAttribute('text-anchor','middle'); t.setAttribute('font-size','12'); t.setAttribute('font-weight','800'); t.setAttribute('fill', x.rank <= 2 ? '#2E3A70' : '#fff'); t.textContent = x.rank;
      b.appendChild(cap); b.appendChild(c); b.appendChild(t);
      row.appendChild(b); row.appendChild(el('span','n',x.name)); row.appendChild(el('span','v',x.coinsText));
      r.appendChild(row);
    });

    var g = $('gallery'); g.textContent = '';
    $('gallery-empty').style.display = s.gallery.length ? 'none' : 'block';
    s.gallery.forEach(function(x){
      var t = el('div','tile');
      t.appendChild(icon(x.tier, 40)); t.appendChild(el('span','gc','×' + x.count)); t.appendChild(el('span','gn',x.gift)); t.appendChild(el('span','gt',x.top));
      g.appendChild(t);
    });

    if (s.topGift) {
      $('tg-ico').setAttribute('href', '#t' + s.topGift.tier);
      $('tg-name').textContent = s.topGift.name;
      $('tg-gift').textContent = giftText(s.topGift.gift, s.topGift.count) + ' · ' + s.topGift.coinsText;
    } else { $('tg-name').textContent = '—'; $('tg-gift').textContent = 'Nog geen cadeaus deze stream'; }
    people(s);
    lastVersion = s.version;
  }

  function people(s){
    if (s.lastFollower) { $('f-name').textContent = s.lastFollower.name; $('f-ago').textContent = ago(s.lastFollower.at, s.now) + (s.lastFollower.test ? ' · test' : ''); }
    else { $('f-name').textContent = '—'; $('f-ago').textContent = 'Nog geen nieuwe volgers'; }
    if (s.lastSub) { $('s-name').textContent = s.lastSub.name; $('s-ago').textContent = ago(s.lastSub.at, s.now) + (s.lastSub.test ? ' · test' : ''); }
    else { $('s-name').textContent = '—'; $('s-ago').textContent = 'Nog geen subs'; }
  }

  var current = null;
  function tickAlert(){
    if (!state) return;
    var now = Date.now() + (state.skew || 0), a = null;
    for (var i = 0; i < state.alerts.length; i++) { var x = state.alerts[i]; if (now >= x.showAt && now < x.showAt + x.durationMs) { a = x; break; } }
    var box = $('alert');
    if (a && (!current || current.id !== a.id)) {
      current = a;
      $('a-ico').setAttribute('href', '#t' + a.tier);
      $('a-kind').textContent = tierName[a.tier] + (a.test ? ' · test' : '');
      $('a-name').textContent = a.name;
      $('a-gift').textContent = 'stuurt ' + giftText(a.gift, a.count);
      $('a-coins').textContent = a.coinsText;
      box.classList.toggle('big', a.tier >= 4);
      var fl = $('flakes'); fl.textContent = '';
      if (a.tier >= 3) for (var k = 0; k < (a.tier >= 4 ? 18 : 9); k++) {
        var f = el('span','flake','❄'); f.style.left = (4 + Math.random() * 92) + '%'; f.style.animationDelay = (Math.random() * 2.4) + 's'; f.style.fontSize = (10 + Math.random() * 10) + 'px'; fl.appendChild(f);
      }
      box.classList.remove('on'); void box.offsetWidth; box.classList.add('on');
    } else if (!a && current) { current = null; box.classList.remove('on'); }
  }

  function poll(){
    fetch('/api/tiktok', {cache:'no-store'}).then(function(r){ return r.json(); }).then(function(s){
      s.skew = s.now - Date.now();   // klok van de pagina gelijk aan die van de app
      state = s; if (s.version !== lastVersion) render(s); else people(s);
    }).catch(function(){}).finally(function(){ setTimeout(poll, 700); });
  }
  if (document.body.dataset.view === 'tiktok-alles' && !q.get('scale')) {
    var fit = function(){
      var st = $('stack'); st.style.transform = '';
      var k = Math.min(1, window.innerHeight / st.scrollHeight, window.innerWidth / st.scrollWidth);
      if (k < 1) st.style.transform = 'scale(' + k + ')';
    };
    window.addEventListener('resize', fit); setInterval(fit, 1500);
  }
  poll(); setInterval(tickAlert, 100);
})();
</script>
</body>
</html>
""".Replace("__SNOWALERT__", OverlayPages.Snow.Replace("viewBox=\"0 0 400 22\"", "viewBox=\"0 0 400 30\""))
   .Replace("__SNOW__", OverlayPages.Snow)
   .Replace("__ICONS__", Icons);
}

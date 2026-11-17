using System.Net;

namespace StreamLightControl2.Core.Services;

/// <summary>HTML van de kerst-overlays. Namen en berichten van kijkers komen alleen via textContent op de pagina.</summary>
public static class OverlayPages
{
    public static string Index(IEnumerable<(string Path, string Name, string Size)> views)
    {
        var rows = string.Join("", views.Select(v =>
            $"<li><a href=\"/kerst/{v.Path}.html?bg=1\">{WebUtility.HtmlEncode(v.Name)}</a> <code>/kerst/{v.Path}.html</code> <span>{v.Size}</span></li>"));
        return $$"""
<!doctype html><html lang="nl"><head><meta charset="utf-8"><title>Kerst-overlays</title>
<style>body{margin:0;background:#0A0F24;color:#F4F7FF;font:16px/1.5 Inter,Segoe UI,Arial,sans-serif;padding:32px}
a{color:#FFD600}code{color:#C9D2EC;margin:0 8px}span{color:#AEB8D6;font-size:14px}li{margin:8px 0}</style></head>
<body><h1>Kerst-overlays</h1><p>Klik om een voorbeeld te zien. Zet het adres (zonder <code>?bg=1</code>) als browserbron in OBS of als linkbron in TikTok LIVE Studio.</p>
<ul>{{rows}}</ul></body></html>
""";
    }

    public static string Page(string view) => Template.Replace("__VIEW__", view);

    internal const string Snow = """<svg class="snow" aria-hidden="true" viewBox="0 0 400 22" preserveAspectRatio="none"><path d="M8 14 C12 4 28 4 40 9 C56 2 76 4 86 10 C104 2 126 5 136 10 C156 2 178 4 188 10 C208 2 230 5 240 10 C260 2 282 4 292 10 C312 2 334 5 344 10 C364 3 386 5 392 13 L392 18 C380 22 366 17 352 21 C336 16 322 22 308 19 C292 23 278 17 264 21 C248 16 234 22 220 19 C204 23 190 17 176 21 C160 16 146 22 132 19 C116 23 102 17 88 21 C72 16 58 22 44 19 C30 23 18 17 8 20 Z" fill="#F4F7FF"/></svg>""";

    private const string Gift = """<svg aria-hidden="true" viewBox="0 0 24 24" width="20" height="20" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="8" width="18" height="4" rx="1"/><path d="M12 8v13"/><path d="M19 12v7a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2v-7"/><path d="M7.5 8a2.5 2.5 0 0 1 0-5C10 3 12 8 12 8s2-5 4.5-5a2.5 2.5 0 0 1 0 5"/></svg>""";

    private static readonly string Template = """
<!doctype html>
<html lang="nl">
<head>
<meta charset="utf-8">
<title>Kerst-overlay</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link href="https://fonts.googleapis.com/css2?family=Baloo+2:wght@600;800&family=Inter:wght@400;600;700&display=swap" rel="stylesheet">
<style>
:root{--night:#0A0F24;--card:rgba(18,24,51,.95);--gold:#FFB800;--name:#FFD600;--red:#E5484D;--green:#2FA36B;--blue:#6CB8FF;--snow:#F4F7FF;--soft:#C9D2EC;--muted:#AEB8D6}
html,body{margin:0;background:transparent;color:var(--snow);font-family:Inter,Arial,sans-serif;overflow:hidden}
body.bg{background:var(--night)}
.view{display:none}
body[data-view="alert"] .w-alert,body[data-view="doel"] .w-doel,body[data-view="ranking"] .w-ranking,
body[data-view="recent"] .w-recent,body[data-view="supporters"] .w-supporters{display:flex}
body[data-view="alles"] .view{display:flex}
body[data-view="alles"] #stack{width:432px;padding:20px 16px;box-sizing:border-box;gap:22px}
body[data-view="alles"] .view{width:100%;justify-content:center}
body[data-view="alles"] .goal,body[data-view="alles"] .list,body[data-view="alles"] .sup,body[data-view="alles"] .w-recent>div{width:100%!important}
body[data-view="alles"] .w-alert{width:432px!important;right:auto}
body[data-view="alles"] .alert{width:400px;padding:40px 22px 22px}
body[data-view="alles"] .a-name{font-size:34px}
#stack{display:flex;flex-direction:column;align-items:center;gap:24px;padding:24px;transform-origin:0 0}
.head{font-family:'Baloo 2',Inter,sans-serif;font-weight:800;line-height:1.05}
.card{position:relative;box-sizing:border-box;background:var(--card);border:1.5px solid rgba(255,184,0,.55);border-radius:16px}
.snow{position:absolute;left:-1px;top:-10px;width:calc(100% + 2px);height:22px}
.empty{color:var(--muted);font-size:15px;text-align:center;padding:6px 0}
/* alert */
.w-alert{justify-content:center}
body[data-view="alles"] .w-alert{position:fixed;left:0;right:0;top:120px;z-index:5}
.alert{width:440px;padding:42px 30px 24px;border:2px solid var(--gold);border-radius:26px;display:flex;flex-direction:column;align-items:center;gap:6px;text-align:center;
  opacity:0;transform:translateY(-30px) scale(.92);transition:opacity .45s ease,transform .45s cubic-bezier(.2,1.4,.4,1)}
.alert.on{opacity:1;transform:none}
.alert .snow{top:-14px;height:30px}
.lights{position:absolute;left:34px;right:34px;top:22px;display:flex;justify-content:space-between}
.bulb{width:10px;height:14px;border-radius:50% 50% 45% 45%;animation:tw 1.6s ease-in-out infinite}
.bulb:nth-child(2n){animation-delay:.4s}.bulb:nth-child(3n){animation-delay:.8s}.bulb:nth-child(5n){animation-delay:1.2s}
@keyframes tw{0%,100%{opacity:1}50%{opacity:.3}}
.holly{position:absolute;left:-20px;top:-22px;width:72px;height:62px}
.pill{display:flex;align-items:center;gap:8px;margin-top:10px;padding:4px 12px 4px 8px;border-radius:999px;background:var(--red);color:#fff;font-size:14px;font-weight:700}
.a-name{font-size:40px;color:var(--name);margin-top:4px;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.a-amount{font-size:22px;font-weight:700}
.a-msg{font-size:20px;line-height:1.35;color:#DCE3F5;margin-top:2px;max-width:100%;overflow-wrap:anywhere}
.brand{display:flex;align-items:center;gap:6px;margin-top:8px;font-size:13px;font-weight:600;color:var(--muted)}
/* doel */
.goal{width:420px;padding:18px 20px 16px;display:flex;flex-direction:column;gap:10px;border-radius:14px}
.row{display:flex;align-items:center;justify-content:space-between;gap:12px}
.bar{position:relative;height:22px;border-radius:999px;background:#1E2650;border:1.5px solid #2E3A70;overflow:hidden}
.cane{height:100%;border-radius:999px;width:0;transition:width 1.2s cubic-bezier(.2,.9,.3,1);
  background:repeating-linear-gradient(-45deg,var(--red) 0 10px,var(--snow) 10px 20px);background-size:28px 28px;animation:cane 1.2s linear infinite}
@keyframes cane{from{background-position:0 0}to{background-position:28px 0}}
.goal.done{border-color:var(--gold);box-shadow:0 0 0 2px rgba(255,184,0,.35)}
/* lijsten */
.list{width:360px;padding:26px 18px 18px;display:flex;flex-direction:column;gap:12px;border-radius:18px}
.items{display:flex;flex-direction:column;gap:8px}
.rk{display:flex;align-items:center;gap:12px;padding:10px 12px;border-radius:12px;background:#161D42}
.rk.r1{background:#1B2350;border:1.5px solid var(--gold)}
.rk .n{flex:1;font-size:17px;font-weight:700;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.rk .v{font-size:16px;font-weight:700}
.rk.r1 .v{color:var(--name)}
.tag{display:flex;align-items:stretch;border-radius:12px;overflow:hidden;background:var(--card);border:1.5px solid var(--c)}
.tag .ic{width:46px;flex:none;background:var(--c);display:flex;align-items:center;justify-content:center;color:var(--ic)}
.tag .tx{flex:1;padding:9px 14px;display:flex;flex-direction:column;gap:2px;min-width:0}
.tag .t1{display:flex;justify-content:space-between;gap:8px;font-size:16px;font-weight:700}
.tag .t1 .n{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.tag .t1 .v{color:var(--name);flex:none}
.tag .t2{font-size:14px;color:var(--soft);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.new{animation:pop .6s cubic-bezier(.2,1.4,.4,1)}
@keyframes pop{from{transform:scale(.85);opacity:0}to{transform:none;opacity:1}}
.sup{width:360px;display:flex;flex-direction:column;gap:16px}
.sc{padding:16px;border-radius:16px;display:flex;align-items:center;gap:14px}
.sc .lab{font-size:12px;font-weight:700;letter-spacing:.08em}
.sc .big{font-size:24px;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.sc .sub{font-size:14px;color:var(--soft)}
body.still *{animation:none!important;transition:none!important}
</style>
</head>
<body data-view="__VIEW__">
<div id="stack">

<div class="view w-alert">
<div class="card alert" id="alert" aria-live="polite">
__SNOWALERT__
<div class="lights" aria-hidden="true"><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span><span class="bulb" style="background:#E5484D"></span><span class="bulb" style="background:#FFD600"></span><span class="bulb" style="background:#2FA36B"></span><span class="bulb" style="background:#6CB8FF"></span></div>
<svg class="holly" aria-hidden="true" viewBox="0 0 64 56"><path d="M30 30 C20 18 6 22 2 12 C10 14 14 8 12 2 C22 8 26 4 30 0 C30 10 36 14 32 22 C34 24 32 28 30 30 Z" fill="#2FA36B"/><path d="M34 30 C46 22 58 28 62 18 C54 18 52 12 54 6 C44 10 40 6 36 2 C36 12 30 16 34 24 Z" fill="#1F7A4F"/><circle cx="30" cy="34" r="6" fill="#E5484D"/><circle cx="40" cy="36" r="5.5" fill="#E5484D"/><circle cx="34" cy="44" r="5.5" fill="#C93A3F"/></svg>
<div class="pill">__GIFT__<span id="a-kind">Kersttip</span></div>
<div class="head a-name" id="a-name"></div>
<div class="a-amount" id="a-amount"></div>
<div class="a-msg" id="a-msg"></div>
<div class="brand"><svg aria-hidden="true" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="#AEB8D6" stroke-width="2" stroke-linecap="round"><path d="M12 2v20M2 12h20M5 5l14 14M19 5L5 19"/></svg>DonateWave</div>
</div>
</div>

<div class="view w-doel">
<div class="card goal" id="goal">
__SNOW__
<div class="row"><div style="display:flex;align-items:center;gap:8px;color:var(--name)">__GIFT__<span class="head" style="font-size:22px;color:var(--snow)" id="g-title">Kerstdoel</span></div><span style="font-size:15px;font-weight:700;color:var(--name)" id="g-pct">0%</span></div>
<div class="bar"><div class="cane" id="g-bar"></div></div>
<div class="row" style="font-size:14px;color:var(--soft)"><span><strong style="color:var(--snow)" id="g-cur">€ 0,00</strong> van <span id="g-target">€ 0,00</span></span><span id="g-rest"></span></div>
</div>
</div>

<div class="view w-ranking">
<div class="card list">
__SNOW__
<div style="display:flex;flex-direction:column;align-items:center"><span class="head" style="font-size:26px;color:var(--name)">Lijst van de Kerstman</span><span style="font-size:13px;font-weight:600;color:var(--muted);letter-spacing:.06em">TOP SUPPORTERS VAN DEZE MAAND</span></div>
<div class="items" id="ranking"></div>
</div>
</div>

<div class="view w-recent">
<div style="width:360px;display:flex;flex-direction:column;gap:12px">
<div style="display:flex;align-items:center;gap:8px"><svg aria-hidden="true" viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="#2FA36B" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2 L4 13 h4 L5 19 h14 l-3-6 h4 Z"/><path d="M12 19v3"/></svg><span class="head" style="font-size:24px">Onder de boom</span><span style="margin-left:auto;font-size:13px;font-weight:600;color:var(--muted)">recente tips</span></div>
<div class="items" id="recent"></div>
</div>
</div>

<div class="view w-supporters">
<div class="sup">
<div class="card sc">
__SNOW__
<svg aria-hidden="true" viewBox="0 0 48 48" width="46" height="46" style="flex:none"><circle cx="24" cy="24" r="22" fill="#E5484D"/><path d="M14 22 C14 12 34 12 34 22 Z" fill="#F4F7FF"/><circle cx="24" cy="10" r="4" fill="#F4F7FF"/><circle cx="19" cy="27" r="2" fill="#121833"/><circle cx="29" cy="27" r="2" fill="#121833"/><path d="M18 33 C21 36 27 36 30 33" stroke="#121833" stroke-width="2" fill="none" stroke-linecap="round"/></svg>
<div style="display:flex;flex-direction:column;gap:2px;min-width:0"><span class="lab" style="color:var(--red)">LAATSTE SUPPORTER</span><span class="head big" id="s-last">—</span><span class="sub" id="s-last2">Nog geen tips</span></div>
</div>
<div class="card sc" style="border-color:var(--gold)">
<svg aria-hidden="true" viewBox="0 0 48 48" width="46" height="46" style="flex:none"><path d="M24 3 L29 17 L44 17.5 L32 26.5 L36.5 41 L24 32.5 L11.5 41 L16 26.5 L4 17.5 L19 17 Z" fill="#FFD600" stroke="#B37F00" stroke-width="1.5" stroke-linejoin="round"/></svg>
<div style="display:flex;flex-direction:column;gap:2px;min-width:0"><span class="lab" style="color:var(--name)">KERSTKAMPIOEN VAN DE WEEK</span><span class="head big" id="s-week">—</span><span class="sub" id="s-week2">Nog geen tips deze week</span></div>
</div>
<div class="card sc" style="border-color:var(--green)">
<svg aria-hidden="true" viewBox="0 0 48 48" width="46" height="46" style="flex:none"><path d="M24 4 L12 20 h6 L8 32 h12 v8 h8 v-8 h12 L30 20 h6 Z" fill="#2FA36B"/><circle cx="20" cy="22" r="2.2" fill="#E5484D"/><circle cx="28" cy="28" r="2.2" fill="#FFD600"/><circle cx="18" cy="30" r="2.2" fill="#6CB8FF"/></svg>
<div style="flex:1;display:flex;flex-direction:column;gap:2px"><span class="lab" style="color:var(--green)">VANDAAG</span><div style="display:flex;align-items:baseline;justify-content:space-between"><span class="head big" style="color:var(--name)" id="s-today">€ 0,00</span><span class="sub" id="s-today2">0 tips</span></div></div>
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
  var GIFT = '__GIFTJS__';
  var state = null, lastVersion = -1, seen = {};
  var colors = [['#E5484D','#fff'],['#2FA36B','#fff'],['#FFB800','#121833'],['#6CB8FF','#121833']];

  function el(tag, cls, text){ var e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; }
  function kind(t){ return t.wheel ? 'Lucky Wheel' : t.special ? 'Shout-out' + (t.quantity > 1 ? ' ×' + t.quantity : '') : (t.message || ''); }

  function render(s){
    var g = s.goal;
    $('g-title').textContent = g.title; $('g-pct').textContent = Math.round(g.percent) + '%';
    $('g-bar').style.width = g.percent + '%'; $('g-cur').textContent = g.current; $('g-target').textContent = g.target;
    $('g-rest').textContent = g.reached ? 'Doel gehaald!' : 'Nog ' + g.remaining;
    $('goal').classList.toggle('done', g.reached);

    var r = $('ranking'); r.textContent = '';
    if (!s.ranking.length) r.appendChild(el('div','empty','Nog geen supporters deze maand'));
    var ball = ['#FFB800','#C9D2EC','#E5484D','#2FA36B','#2FA36B'];
    s.ranking.forEach(function(x){
      var row = el('div','rk' + (x.rank === 1 ? ' r1' : ''));
      var b = document.createElementNS('http://www.w3.org/2000/svg','svg'); b.setAttribute('viewBox','0 0 32 36'); b.setAttribute('width','30'); b.setAttribute('height','34'); b.setAttribute('aria-hidden','true');
      var cap = document.createElementNS(b.namespaceURI,'rect'); cap.setAttribute('x','12'); cap.setAttribute('y','1'); cap.setAttribute('width','8'); cap.setAttribute('height','5'); cap.setAttribute('rx','1.5'); cap.setAttribute('fill','#7D879F');
      var c = document.createElementNS(b.namespaceURI,'circle'); c.setAttribute('cx','16'); c.setAttribute('cy','21'); c.setAttribute('r','14'); c.setAttribute('fill', ball[x.rank-1]);
      var t = document.createElementNS(b.namespaceURI,'text'); t.setAttribute('x','16'); t.setAttribute('y','26'); t.setAttribute('text-anchor','middle'); t.setAttribute('font-size','12'); t.setAttribute('font-weight','800'); t.setAttribute('fill', x.rank === 2 || x.rank === 1 ? '#2E3A70' : '#fff'); t.textContent = x.rank;
      b.appendChild(cap); b.appendChild(c); b.appendChild(t);
      row.appendChild(b); row.appendChild(el('span','n',x.name)); row.appendChild(el('span','v',x.amount));
      r.appendChild(row);
    });

    var rc = $('recent'); rc.textContent = '';
    if (!s.recent.length) rc.appendChild(el('div','empty','Nog geen tips onder de boom'));
    s.recent.forEach(function(x, i){
      var cl = colors[i % 4];
      var tag = el('div','tag' + (lastVersion >= 0 && !seen[x.id] ? ' new' : ''));
      tag.style.setProperty('--c', cl[0]); tag.style.setProperty('--ic', cl[1]);
      var ic = el('div','ic'); ic.innerHTML = GIFT;   // vaste eigen SVG, geen kijkersdata
      var tx = el('div','tx'); var t1 = el('div','t1'); t1.appendChild(el('span','n',x.name)); t1.appendChild(el('span','v',x.amount));
      tx.appendChild(t1); var k = kind(x); var sub = x.special || x.wheel ? k : (k ? '“' + k + '”' : '');
      if (x.test) sub = sub ? sub + ' · test' : 'Testtip';
      if (sub) tx.appendChild(el('span','t2', sub));
      tag.appendChild(ic); tag.appendChild(tx); rc.appendChild(tag);
    });
    s.recent.forEach(function(x){ seen[x.id] = 1; });

    if (s.last) { $('s-last').textContent = s.last.name; $('s-last2').textContent = s.last.amount + ' · ' + ago(s.last.at, s.now); }
    if (s.weekChampion) { $('s-week').textContent = s.weekChampion.name; $('s-week2').textContent = s.weekChampion.amount + ' deze week'; }
    else { $('s-week').textContent = '—'; $('s-week2').textContent = 'Nog geen tips deze week'; }
    $('s-today').textContent = s.today.amount; $('s-today2').textContent = s.today.count + (s.today.count === 1 ? ' tip' : ' tips');
    lastVersion = s.version;
  }

  function ago(at, now){ var m = Math.max(0, Math.round((now - at) / 60000)); return m < 1 ? 'zojuist' : m < 60 ? m + ' min geleden' : Math.round(m / 60) + ' uur geleden'; }

  var current = null;
  function tickAlert(){
    if (!state) return;
    var now = Date.now(), a = null;
    for (var i = 0; i < state.alerts.length; i++) { var x = state.alerts[i]; if (now >= x.showAt && now < x.showAt + x.durationMs) { a = x; break; } }
    var box = $('alert');
    if (a && (!current || current.id !== a.id)) {
      current = a;
      $('a-name').textContent = a.name;
      $('a-amount').textContent = 'stuurt ' + a.amount;
      $('a-kind').textContent = a.wheel ? 'Lucky Wheel' : a.special ? 'Kerst shout-out' + (a.quantity > 1 ? ' ×' + a.quantity : '') : 'Kersttip';
      $('a-msg').textContent = a.message ? '“' + a.message + '”' : '';
      box.classList.add('on');
    } else if (!a && current) { current = null; box.classList.remove('on'); }
  }

  function poll(){
    fetch('/api/state', {cache:'no-store'}).then(function(r){ return r.json(); }).then(function(s){
      state = s; if (s.version !== lastVersion) render(s); else if (s.last) $('s-last2').textContent = s.last.amount + ' · ' + ago(s.last.at, s.now);
    }).catch(function(){}).finally(function(){ setTimeout(poll, 700); });
  }
  // "alles": automatisch passend maken in de hoogte van de bron (bijv. 432 × 768)
  if (document.body.dataset.view === 'alles' && !q.get('scale')) {
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
""".Replace("__SNOWALERT__", Snow.Replace("viewBox=\"0 0 400 22\"", "viewBox=\"0 0 400 30\""))
   .Replace("__SNOW__", Snow)
   .Replace("__GIFTJS__", Gift.Replace("'", "\\'"))
   .Replace("__GIFT__", Gift);
}

namespace Nomad.App.ViewModels;

/// <summary>The phone view's single page: the chat list and one chat, polling the JSON API with the key from the link.</summary>
internal static class PhoneViewPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="referrer" content="no-referrer">
<title>Nomad</title>
<style>
:root { --bg:#f4f3ef; --fg:#1c1c1a; --muted:#6d6a63; --card:#ffffff; --line:#dcd8ce; --accent:#c96a3d; --you:#e9e4d8;
  --ok:#3d8a4f; --warn:#c08a1e; --bad:#b23b3b; --code:#efece4; }
@media (prefers-color-scheme: dark) { :root { --bg:#191918; --fg:#ecebe7; --muted:#9b988f; --card:#232321; --line:#383733;
  --accent:#e08a5c; --you:#302e2a; --ok:#6cc07e; --warn:#e0b04a; --bad:#e06b6b; --code:#2c2b28; } }
* { box-sizing:border-box; }
html, body { margin:0; height:100%; overflow-x:hidden; background:var(--bg); color:var(--fg); font:16px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
header { position:sticky; top:0; z-index:2; display:flex; align-items:center; gap:10px; padding:10px 16px; background:var(--bg); border-bottom:1px solid var(--line); }
header h1 { font-size:17px; margin:0; flex:1; min-width:0; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
button { font:inherit; color:inherit; background:var(--card); border:1px solid var(--line); border-radius:8px; padding:8px 12px; }
button.primary { background:var(--accent); border-color:var(--accent); color:#fff; font-weight:600; }
button:disabled { opacity:.5; }
#back { padding:6px 10px; }
body { display:flex; flex-direction:column; min-height:100dvh; }
main { flex:1 0 auto; width:100%; padding:8px 16px 16px; max-width:760px; margin:0 auto; }
.chat { display:block; width:100%; min-width:0; overflow:hidden; text-align:left; margin:8px 0; padding:12px 14px; background:var(--card); border:1px solid var(--line); border-radius:10px; }
.chat .top { display:flex; align-items:center; gap:8px; }
.chat .name { font-weight:600; flex:1; min-width:0; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.chat .last { color:var(--muted); font-size:14px; margin-top:4px; display:-webkit-box; -webkit-line-clamp:2; -webkit-box-orient:vertical; overflow:hidden; }
.chat .folder { color:var(--muted); font-size:12px; margin-top:2px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.agent { font-size:11px; padding:2px 7px; border-radius:999px; border:1px solid var(--line); color:var(--muted); white-space:nowrap; }
.dot { width:9px; height:9px; border-radius:50%; flex:none; background:var(--muted); }
.dot.working { background:var(--ok); } .dot.waiting { background:var(--warn); } .dot.stopped { background:transparent; border:1px solid var(--muted); }
.state { font-size:12px; color:var(--muted); }
.item { margin:10px 0; overflow-wrap:anywhere; }
.user { background:var(--you); border-radius:12px; padding:9px 12px; margin-left:12%; white-space:pre-wrap; }
.user .from { font-size:12px; color:var(--accent); font-weight:600; margin-bottom:2px; }
.assistant { white-space:pre-wrap; }
.assistant pre { white-space:pre-wrap; background:var(--code); border-radius:8px; padding:8px 10px; font-size:13px; overflow-wrap:anywhere; }
.assistant code { background:var(--code); border-radius:4px; padding:0 4px; font-size:.92em; }
.activity, .notice { color:var(--muted); font-size:14px; }
.activity::before { content:"› "; }
.error { color:var(--bad); font-size:14px; }
.card { background:var(--card); border:1px solid var(--line); border-radius:10px; padding:12px 14px; }
.card.pending { border-color:var(--warn); }
.card .title { font-weight:600; }
.card pre { white-space:pre-wrap; background:var(--code); border-radius:8px; padding:8px 10px; font-size:13px; max-height:40vh; overflow:auto; }
.card .row { display:flex; gap:8px; flex-wrap:wrap; margin-top:10px; }
.card .answer { color:var(--muted); font-size:14px; margin-top:6px; }
.q { margin:10px 0; } .q .qt { font-weight:600; margin-bottom:4px; }
.q label { display:flex; gap:8px; align-items:flex-start; padding:5px 0; }
.q input[type=checkbox], .q input[type=radio] { width:20px; height:20px; flex:none; margin-top:1px; }
.q .desc { color:var(--muted); font-size:13px; }
.q input[type=text] { width:100%; font:inherit; padding:7px 9px; border:1px solid var(--line); border-radius:8px; background:var(--bg); color:var(--fg); }
footer { position:sticky; bottom:0; background:var(--bg); border-top:1px solid var(--line); padding:8px 16px calc(8px + env(safe-area-inset-bottom)); }
footer .row { display:flex; gap:8px; max-width:760px; margin:0 auto; align-items:flex-end; }
textarea { flex:1; font:inherit; min-height:44px; max-height:40vh; resize:none; padding:9px 11px; border:1px solid var(--line); border-radius:10px; background:var(--card); color:var(--fg); }
#toast { max-width:760px; margin:0 auto 6px; font-size:13px; color:var(--muted); min-height:0; }
#toast.bad { color:var(--bad); }
.empty { color:var(--muted); text-align:center; margin-top:40px; }
.hidden { display:none !important; }
</style>
</head>
<body>
<header>
  <button id="back" class="hidden" aria-label="Back to chats">‹ Chats</button>
  <h1 id="title">Nomad</h1>
  <span id="state" class="state"></span>
</header>
<main id="list"></main>
<main id="chat" class="hidden"></main>
<footer id="composer" class="hidden">
  <div id="toast"></div>
  <div class="row">
    <textarea id="text" rows="1" placeholder="Message"></textarea>
    <button id="send" class="primary">Send</button>
  </div>
</footer>
<script>
(() => {
  const $ = id => document.getElementById(id);
  const params = new URLSearchParams(location.hash.slice(1));
  let key = params.get('k');
  try { if (key) localStorage.setItem('nomad-key', key); else key = localStorage.getItem('nomad-key'); } catch (e) {}
  const startChat = params.get('c');
  if (params.get('k') || startChat) history.replaceState(null, '', location.pathname);
  let current = null, lastJson = '', timer = null, sending = false;

  async function api(path, body) {
    const r = await fetch(path, { method: body ? 'POST' : 'GET', headers: { 'X-Nomad-Key': key || '', 'Content-Type': 'application/json' },
      body: body ? JSON.stringify(body) : undefined, cache: 'no-store' });
    const text = await r.text();
    if (!r.ok) throw new Error(text || ('Error ' + r.status));
    return r.headers.get('Content-Type')?.includes('json') ? JSON.parse(text) : text;
  }
  const esc = s => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
  function md(s) {
    const parts = s.split(/```[^\n]*\n?/);
    return parts.map((p, i) => i % 2 ? '<pre>' + esc(p.replace(/\n$/, '')) + '</pre>'
      : esc(p).replace(/`([^`\n]+)`/g, '<code>$1</code>').replace(/\*\*([^*\n]+)\*\*/g, '<b>$1</b>').replace(/^#{1,6} (.*)$/gm, '<b>$1</b>')).join('');
  }
  const stateText = { working: 'working', waiting: 'needs you', idle: 'idle', stopped: 'stopped' };
  function toast(text, bad) { $('toast').textContent = text || ''; $('toast').className = bad ? 'bad' : ''; }

  function schedule(ms) { clearTimeout(timer); timer = setTimeout(refresh, ms); }
  async function refresh() {
    if (document.hidden) return schedule(3000);
    try { current ? await showChat() : await showList(); }
    catch (e) { (current ? toast : listError)(e.message, true); }
    schedule(current ? 2000 : 4000);
  }
  function listError(msg) { $('list').innerHTML = '<p class="empty">' + esc(msg) + '</p>'; }

  async function showList() {
    const chats = await api('api/chats');
    const json = JSON.stringify(chats);
    if (json === lastJson) return;
    lastJson = json;
    $('list').innerHTML = chats.length ? chats.map(c => `<button class="chat" data-id="${esc(c.id)}">
      <div class="top"><span class="dot ${c.state}"></span><span class="name">${esc(c.title)}</span><span class="agent">${esc(c.agent)}</span></div>
      <div class="folder">${esc(c.folder)} · ${stateText[c.state] || c.state}</div>
      ${c.last ? `<div class="last">${esc(c.last)}</div>` : ''}</button>`).join('') : '<p class="empty">No chats are open in Nomad.</p>';
  }

  function itemHtml(it) {
    switch (it.kind) {
      case 'user': return `<div class="item user">${it.from ? `<div class="from">From ${esc(it.from)}</div>` : ''}${esc(it.text)}</div>`;
      case 'assistant': return `<div class="item assistant">${md(it.text)}</div>`;
      case 'activity': return `<div class="item activity">${esc(it.text)}</div>`;
      case 'notice': return `<div class="item notice">${esc(it.text)}</div>`;
      case 'error': return `<div class="item error">${esc(it.text)}</div>`;
      case 'permission': return `<div class="item card${it.pending ? ' pending' : ''}"><div class="title">${esc(it.title)}</div>
        ${it.description ? `<div class="answer">${esc(it.description)}</div>` : ''}${it.detail ? `<pre>${esc(it.detail)}</pre>` : ''}
        ${it.pending ? `<div class="row"><button class="primary" data-choice="allow">${esc(it.allowText)}</button>
          ${it.canSession ? '<button data-choice="session">Allow for this session</button>' : ''}<button data-choice="deny">${esc(it.denyText)}</button></div>`
          : it.answer ? `<div class="answer">${esc(it.answer)}</div>` : ''}</div>`;
      case 'question': return `<div class="item card${it.pending ? ' pending' : ''}">${it.questions.map((q, qi) => `<div class="q" data-q="${qi}">
          <div class="qt">${esc(q.question)}</div>${it.pending ? q.options.map(o => `<label><input type="${q.multi ? 'checkbox' : 'radio'}" name="q${qi}" value="${esc(o.label)}">
          <span>${esc(o.label)}${o.description ? `<div class="desc">${esc(o.description)}</div>` : ''}</span></label>`).join('')
          + `<input type="text" placeholder="Or type an answer">` : ''}</div>`).join('')}
        ${it.pending ? '<div class="row"><button class="primary" data-choice="allow">Submit</button><button data-choice="deny">Skip</button></div>'
          : it.answer ? `<div class="answer">${esc(it.answer)}</div>` : ''}</div>`;
    }
    return '';
  }

  async function showChat() {
    const chat = await api('api/chat?id=' + encodeURIComponent(current));
    $('title').textContent = chat.title;
    $('state').textContent = chat.agent + ' · ' + (stateText[chat.state] || chat.state) + (chat.queued ? ` · ${chat.queued} queued` : '');
    const json = JSON.stringify(chat.items) + chat.truncated;
    if (json === lastJson) return;
    // A question being filled in is not redrawn under the user's fingers.
    if (document.querySelector('#chat .card.pending .q input:checked, #chat .card.pending .q input[type=text]:not(:placeholder-shown)')) return;
    const atBottom = innerHeight + scrollY >= document.body.scrollHeight - 80;
    lastJson = json;
    $('chat').innerHTML = (chat.truncated ? '<p class="notice">Earlier messages are on the PC.</p>' : '') + chat.items.map(itemHtml).join('');
    if (atBottom) scrollTo(0, document.body.scrollHeight);
  }

  function open(id) {
    current = id; lastJson = '';
    $('list').classList.add('hidden'); $('chat').classList.remove('hidden'); $('chat').innerHTML = '';
    $('composer').classList.remove('hidden'); $('back').classList.remove('hidden'); toast('');
    refresh().then(() => scrollTo(0, document.body.scrollHeight));
  }
  function close() {
    current = null; lastJson = '';
    $('chat').classList.add('hidden'); $('list').classList.remove('hidden'); $('composer').classList.add('hidden');
    $('back').classList.add('hidden'); $('title').textContent = 'Nomad'; $('state').textContent = '';
    refresh();
  }

  $('list').addEventListener('click', e => { const b = e.target.closest('.chat'); if (b) { history.pushState({ chat: b.dataset.id }, ''); open(b.dataset.id); } });
  $('back').addEventListener('click', () => history.back());
  addEventListener('popstate', e => e.state?.chat ? open(e.state.chat) : close());

  $('chat').addEventListener('click', async e => {
    const b = e.target.closest('button[data-choice]'); if (!b) return;
    const card = b.closest('.card'); const body = { choice: b.dataset.choice };
    if (card.querySelector('.q')) body.answers = [...card.querySelectorAll('.q')].map(q => ({
      options: [...q.querySelectorAll('input:checked')].map(i => i.value), other: q.querySelector('input[type=text]')?.value || '' }));
    card.querySelectorAll('button').forEach(x => x.disabled = true);
    try { toast(await api('api/answer?id=' + encodeURIComponent(current), body)); }
    catch (err) { toast(err.message, true); card.querySelectorAll('button').forEach(x => x.disabled = false); }
    lastJson = ''; refresh();
  });

  const text = $('text');
  text.addEventListener('input', () => { text.style.height = 'auto'; text.style.height = text.scrollHeight + 'px'; });
  $('send').addEventListener('click', async () => {
    const t = text.value.trim(); if (!t || sending) return;
    sending = true; $('send').disabled = true;
    try { toast(await api('api/send?id=' + encodeURIComponent(current), { text: t })); text.value = ''; text.style.height = 'auto'; lastJson = ''; refresh(); }
    catch (err) { toast('Not sent: ' + err.message, true); }
    finally { sending = false; $('send').disabled = false; }
  });

  document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
  if (!key) listError('Open the link copied from Nomad (Settings › Remote Control › Phone view): it carries the key.');
  else if (startChat) { history.pushState({ chat: startChat }, ''); open(startChat); }
  else refresh();
})();
</script>
</body>
</html>
""";
}

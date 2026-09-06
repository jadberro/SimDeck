"""SimDeck web UI and firmware host.

Serves three things on one port:
  /            status page
  /api/*       JSON control surface
  /fw/*.bin    firmware images, fetched by modules during OTA
"""

import json
import os
import posixpath
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

import builder

PORT = 27502

PAGE = """<!doctype html>
<meta charset="utf-8">
<title>SimDeck</title>
<style>
 :root{--bg:#0d1013;--card:#161b20;--line:#252c33;--fg:#dfe5ea;
       --dim:#7b8894;--ok:#2fbf5f;--warn:#e0a531;--bad:#d9534f;--acc:#4a9fd8}
 *{box-sizing:border-box}
 body{margin:0;background:var(--bg);color:var(--fg);
      font:14px/1.5 ui-sans-serif,system-ui,-apple-system,sans-serif}
 header{padding:18px 22px;border-bottom:1px solid var(--line);
        display:flex;gap:18px;align-items:baseline;flex-wrap:wrap}
 h1{font-size:17px;margin:0;letter-spacing:.5px;font-weight:600}
 .pill{font-size:12px;padding:3px 9px;border-radius:999px;
       background:#1d242b;color:var(--dim)}
 .pill.on{background:#12301d;color:var(--ok)}
 .pill.off{background:#331b1b;color:var(--bad)}
 main{padding:22px;display:grid;gap:16px;
      grid-template-columns:repeat(auto-fill,minmax(330px,1fr))}
 .card{background:var(--card);border:1px solid var(--line);
       border-radius:10px;padding:16px}
 .card h2{font-size:15px;margin:0 0 2px;font-weight:600}
 .sub{color:var(--dim);font-size:12px;margin-bottom:12px}
 table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
 td{padding:4px 0;border-bottom:1px solid #1e242a}
 td.v{text-align:right;color:var(--acc)}
 td.n{color:var(--dim)}
 .bar{height:6px;background:#1e252c;border-radius:3px;overflow:hidden;
      margin-top:10px}
 .bar>i{display:block;height:100%;background:var(--acc);width:0}
 button{background:#1d242b;color:var(--fg);border:1px solid var(--line);
        border-radius:6px;padding:6px 12px;font-size:12px;cursor:pointer;
        margin-right:6px;margin-top:12px}
 button:hover{border-color:var(--acc)}
 button:disabled{opacity:.4;cursor:default}
 .empty{color:var(--dim);padding:40px 22px}
 code{color:var(--dim);font-size:12px}
</style>
<header>
  <h1>SimDeck</h1>
  <span class="pill" id="src">source</span>
  <span class="pill" id="ac">aircraft</span>
  <span class="pill" id="prof">profile</span>
</header>
<main id="mods"></main>
<div class="empty" id="empty">No modules connected. Power one on — it will
announce itself.</div>
<script>
const fmt = v => (v===null||v===undefined||Number.isNaN(v)) ? '—' : v.toFixed(0);

async function tick(){
  let s;
  try { s = await (await fetch('/api/status')).json(); } catch(e){ return; }

  const src = document.getElementById('src');
  src.textContent = s.source + (s.connected ? ' · live' : ' · no data');
  src.className = 'pill ' + (s.connected ? 'on' : 'off');
  document.getElementById('ac').textContent = s.aircraft || 'no aircraft';
  const pf = document.getElementById('prof');
  pf.textContent = s.profile || 'no profile';
  pf.className = 'pill ' + (s.profile ? 'on' : 'off');

  const host = document.getElementById('mods');
  document.getElementById('empty').style.display =
      s.modules.length ? 'none' : 'block';

  host.innerHTML = s.modules.map(m => {
    const rows = m.values.map(v =>
      `<tr><td class="n">${v.name}</td><td class="v">${fmt(v.value)}</td></tr>`
    ).join('');
    const o = m.ota || {};
    const busy = o.state==='offered' || o.state==='running';
    const otaLine = o.state && o.state!=='idle'
      ? `<div class="sub" style="margin-top:10px">OTA: ${o.state}
         ${o.pct?('· '+o.pct+'%'):''} ${o.message?('· '+o.message):''}</div>
         <div class="bar"><i style="width:${o.pct||0}%"></i></div>` : '';
    return `<div class="card">
      <h2>${m.name}</h2>
      <div class="sub">${m.id} · ${m.ip} · ${m.rate}Hz · fw ${m.fw||'?'}
        ${m.update_available?'· <span style="color:var(--warn)">update '+m.update_available+'</span>':''}</div>
      <table>${rows}</table>
      ${otaLine}
      <button onclick="post('/api/identify/${m.id}')">Identify</button>
      <button onclick="post('/api/ota/${m.id}')" ${busy?'disabled':''}>Push firmware</button>
    </div>`;
  }).join('');
}
async function post(url){
  try { const r = await (await fetch(url,{method:'POST'})).json();
        if(r.error) alert(r.error); } catch(e){ alert(e); }
  tick();
}
tick(); setInterval(tick, 500);
</script>
"""


class _Handler(BaseHTTPRequestHandler):
    hub = None                      # injected below

    def log_message(self, *_):
        pass                        # keep the console for hub events only

    # -- helpers ------------------------------------------------------------

    def _send(self, code, body, ctype="application/json", extra=None):
        if isinstance(body, (dict, list)):
            body = json.dumps(body).encode()
        elif isinstance(body, str):
            body = body.encode()
        self.send_response(code)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        for k, v in (extra or {}).items():
            self.send_header(k, v)
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

    # -- routes -------------------------------------------------------------

    def do_GET(self):
        path = urlparse(self.path).path

        if path == "/":
            return self._send(200, PAGE, "text/html; charset=utf-8")

        if path == "/api/status":
            return self._send(200, self.hub.status())

        if path == "/api/firmware":
            return self._send(200, {
                "manifest": self.hub.repo.all(),
                "arduino_cli": builder.version(),
            })

        if path.startswith("/fw/"):
            return self._serve_firmware(path)

        return self._send(404, {"error": "not found"})

    def _serve_firmware(self, path):
        # normalise hard: this directory is reachable from the LAN
        name = posixpath.basename(posixpath.normpath(path))
        if not name.endswith(".bin") or "/" in name or "\\" in name:
            return self._send(400, {"error": "bad name"})
        full = os.path.join(self.hub.repo.dir, name)
        if not os.path.isfile(full):
            return self._send(404, {"error": "no such image"})
        with open(full, "rb") as fh:
            blob = fh.read()
        self._send(200, blob, "application/octet-stream",
                   {"Content-Disposition": 'attachment; filename="%s"' % name})

    def do_POST(self):
        path = urlparse(self.path).path
        parts = [p for p in path.split("/") if p]

        if len(parts) == 3 and parts[0] == "api" and parts[1] == "ota":
            ok, msg = self.hub.force_ota(parts[2])
            return self._send(200 if ok else 400,
                              {"ok": ok} if ok else {"error": msg})

        if len(parts) == 3 and parts[0] == "api" and parts[1] == "identify":
            self.hub.identify(parts[2])
            return self._send(200, {"ok": True})

        if len(parts) == 3 and parts[0] == "api" and parts[1] == "build":
            return self._send(200, self.hub.build_and_publish(parts[2]))

        return self._send(404, {"error": "not found"})


class _Server(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def start(hub, port: int = PORT):
    """Returns the server, or None if the port was unavailable.

    A busy port must not take the hub down with it - gauges streaming is the
    job, the web UI is a convenience.
    """
    _Handler.hub = hub
    try:
        srv = _Server(("0.0.0.0", port), _Handler)
    except OSError as exc:
        print("[deck] web ui disabled: port %d unavailable (%s)" % (port, exc))
        return None
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return srv

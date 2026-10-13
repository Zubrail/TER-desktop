#!/usr/bin/env python3
"""Local fake of the termitus.online app endpoints, for live checks of the TER client (0.5).
Serves on 127.0.0.1:PORT (default 18080), sub token TESTTOKEN:
  /subscription/TESTTOKEN            base64 vless list (servers on closed ports) + Subscription-Userinfo
  /subscription/TESTTOKEN/info       account json
  /subscription/TESTTOKEN/plans      plans (set PLANS=404 to emulate "backend not ready")
  POST .../pay                       {payment_id, pay_url}
  .../pay/<id>                       pending x PENDING_POLLS (default 3) then PAY_RESULT (paid|failed|expired)
  .../support                        chat: GET returns what was POSTed, POST stores it (and appends to $POST_LOG as JSON lines)
  /app/latest.json, /app/<file>      update manifest + binaries from $APP_DIR (0.6; no folder = 404)
Run: python3 termitus/tools/fake_backend.py [port]
Env: PLANS=404|500, PENDING_POLLS=3, PAY_RESULT=paid|failed|expired, EXPIRED=1 (subscription ended 2 days ago),
     EXPIRES_IN_DAYS=20, SHUTDOWN_AFTER=SECONDS (server vanishes: offline checks)
     SUPPORT_POST=403|404|422|429|500 (every support POST answers with it), SUPPORT_FAIL_FIRST=1 (first POST 503, then ok),
     POST_LOG=/path (support POST bodies), REQ_LOG=/path (GET /app/* requests: path, User-Agent, header names), APP_DIR=/path (serves latest.json + files under /app/), WITH_H2=0 (no Hysteria2 entries)
"""
import base64, json, os, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18080
TOK = "TESTTOKEN"
DAYS = -2 if os.environ.get("EXPIRED") == "1" else int(os.environ.get("EXPIRES_IN_DAYS", "20"))
polls = {}

def vless(name, port):
    return f"vless://11111111-2222-3333-4444-555555555555@127.0.0.1:{port}?security=none&type=tcp&encryption=none#{name}"

def hy2(name, port):
    return f"hysteria2://pw1234567890@127.0.0.1:{port}?insecure=1&sni=example.org#{name}"

def sub_body():
    names = [("🇳🇱 Нидерланды 1", 9), ("🇩🇪 Германия 1", 9), ("🇷🇺 Россия", 9)]
    links = [vless(n.replace(" ", "%20"), p) for n, p in names]
    if os.environ.get("WITH_H2", "1") != "0":
        links += [hy2((n + " · H2").replace(" ", "%20"), p) for n, p in names[:2]]
    return base64.b64encode("\n".join(links).encode()).decode()

chat = []
posts = {"n": 0}

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def send(self, code, body="", ctype="application/json", headers=None):
        b = body.encode() if isinstance(body, str) else body
        self.send_response(code)
        self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b)))
        for k, v in (headers or {}).items(): self.send_header(k, v)
        self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        p = self.path.split("?")[0]
        if p.startswith("/app/") and os.environ.get("REQ_LOG"):
            with open(os.environ["REQ_LOG"], "a") as f:
                f.write(f"GET {self.path} UA={self.headers.get('User-Agent')} headers={sorted(k for k in self.headers.keys())}\n")
        base = f"/subscription/{TOK}"
        if p == base:
            exp = int(time.time()) + DAYS * 86400
            return self.send(200, sub_body(), "text/plain", {
                "Subscription-Userinfo": f"upload=1000; download=5000000; total=0; expire={exp}",
                "Profile-Title": "base64:" + base64.b64encode("TER тест".encode()).decode()})
        if p == base + "/info":
            return self.send(200, json.dumps({"expire": int(time.time()) + DAYS * 86400, "plan": "Месяц", "devices_used": 1, "devices_allowed": 3,
                "devices": [{"name": "MacBook", "platform": "macOS", "is_active": True}], "balance": 150,
                "renew_url": "https://t.me/TErpiUsis_bot", "telegram": {"username": "tester", "first_name": "Тест"}}))
        if p == base + "/plans":
            if os.environ.get("PLANS") in ("404", "500"): return self.send(int(os.environ["PLANS"]), "{}")
            return self.send(200, json.dumps({"plans": [
                {"id": "m1", "title": "1 месяц", "price_rub": 199, "days": 30},
                {"id": "m3", "title": "3 месяца", "price_rub": 499, "days": 90},
                {"id": "y1", "title": "12 месяцев", "price_rub": 1590, "days": 365}],
                "methods": [{"id": "card", "title": "Банковская карта"}, {"id": "sbp", "title": "СБП"}, {"id": "usdt", "title": "USDT"}],
                "offer_url": "https://termitus.online/offer"}))
        if p.startswith(base + "/pay/"):
            pid = p.rsplit("/", 1)[1]; polls[pid] = polls.get(pid, 0) + 1
            n = int(os.environ.get("PENDING_POLLS", "3"))
            st = "pending" if polls[pid] <= n else os.environ.get("PAY_RESULT", "paid")
            return self.send(200, json.dumps({"status": st}))
        if p == base + "/support":
            return self.send(200, json.dumps(chat))
        if p.startswith("/app/") and os.environ.get("APP_DIR"):
            fn = os.path.basename(p)
            path = os.path.join(os.environ["APP_DIR"], fn)
            if os.path.isfile(path):
                data = open(path, "rb").read()
                return self.send(200, data, "application/json" if fn.endswith(".json") else "application/octet-stream")
        self.send(404, "{}")
    def do_POST(self):
        p = self.path.split("?")[0]
        n = int(self.headers.get("Content-Length") or 0); raw = self.rfile.read(n)
        if p == f"/subscription/{TOK}/support":
            posts["n"] += 1
            forced = os.environ.get("SUPPORT_POST")
            if forced: return self.send(int(forced), "{}", headers={"Retry-After": "30"} if forced == "429" else None)
            if os.environ.get("SUPPORT_FAIL_FIRST") == "1" and posts["n"] == 1: return self.send(503, "{}")
            text = json.loads(raw or b"{}").get("text", "")
            msg = {"id": len(chat) + 1, "from": "user", "text": text, "at": int(time.time())}
            chat.append(msg)
            if os.environ.get("POST_LOG"):
                with open(os.environ["POST_LOG"], "a") as f: f.write(json.dumps({"text": text}, ensure_ascii=False) + "\n")
            return self.send(201, json.dumps(msg))
        if p == f"/subscription/{TOK}/pay":
            return self.send(201, json.dumps({"payment_id": f"pay{int(time.time())}", "pay_url": "https://termitus.online/pay/test"}))
        self.send(404, "{}")

if os.environ.get("SHUTDOWN_AFTER"):
    threading.Timer(float(os.environ["SHUTDOWN_AFTER"]), lambda: os._exit(0)).start()
ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()

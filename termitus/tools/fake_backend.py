#!/usr/bin/env python3
"""Local fake of the termitus.online app endpoints, for live checks of the TER client (0.5).
Serves on 127.0.0.1:PORT (default 18080), sub token TESTTOKEN:
  /subscription/TESTTOKEN            base64 vless list (servers on closed ports) + Subscription-Userinfo
  /subscription/TESTTOKEN/info       account json
  /subscription/TESTTOKEN/plans      plans (set PLANS=404 to emulate "backend not ready")
  POST .../pay                       {payment_id, pay_url}
  .../pay/<id>                       pending x PENDING_POLLS (default 3) then PAY_RESULT (paid|failed|expired)
  .../support                        chat: GET returns what was POSTed, POST stores it (and appends to $POST_LOG as JSON lines)
  .../account/devices, devices/<id> (PATCH name, DELETE), devices/<id>/manage, devices/invite, referrals (GET/PATCH),
                                     payments, rotate-link -- Account 2.0 (0.6.3), stateful in memory
  /app/latest.json, /app/<file>      update manifest + binaries from $APP_DIR (0.6; no folder = 404)
Run: python3 termitus/tools/fake_backend.py [port]
Env: PLANS=404|500, PENDING_POLLS=3, PAY_RESULT=paid|failed|expired, EXPIRED=1 (subscription ended 2 days ago),
     EXPIRES_IN_DAYS=20, SHUTDOWN_AFTER=SECONDS (server vanishes: offline checks)
     SUPPORT_POST=403|404|422|429|500 (every support POST answers with it), SUPPORT_FAIL_FIRST=1 (first POST 503, then ok),
     ACCOUNT_GUEST=1 (every /account/ call answers 403 = this device is not a manager), INVITE_FREE=1 (invite without a payment step),
     INVITE_BALANCE=150 (balance for the paid-device check; below 50 -> invite answers 402), ACCOUNT_EMPTY=1 (no payments, no referrals yet), ACCOUNT_MANY=1 (14 payments),
     TRIAL_NOTE=code_unavailable|cooldown|error (login poll with a trial code answers trial_note instead of trial_attached),
     TRIAL=ip|busy|subnet|install (POST /app/trial answers 429 + X-Trial-Reason) or 400|422|503; default = 200 with a 2-day trial,
     POST_LOG=/path (support POST bodies), REQ_LOG=/path (GET /app/* requests: path, User-Agent, header names), APP_DIR=/path (serves latest.json + files under /app/), WITH_H2=0 (no Hysteria2 entries)
"""
import base64, datetime, json, os, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 18080
TOK = "TESTTOKEN"
DAYS = -2 if os.environ.get("EXPIRED") == "1" else int(os.environ.get("EXPIRES_IN_DAYS", "20"))
polls = {}
login = {}

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

def iso_ago(**kw):
    return (datetime.datetime.utcnow() - datetime.timedelta(**kw)).replace(microsecond=0).isoformat()  # naive UTC, like the backend

acct = {
    "devices": [
        {"id": 1, "name": "MacBook Pro", "platform": "macos", "last_seen": iso_ago(seconds=20), "is_this_device": True, "can_manage": True, "paid_slot": False, "is_active": True},
        {"id": 2, "name": "iPhone Лены", "platform": "ios", "last_seen": iso_ago(hours=2), "is_this_device": False, "can_manage": False, "paid_slot": False, "is_active": True},
        {"id": 3, "name": "Pixel 8", "platform": "android", "last_seen": iso_ago(days=3), "is_this_device": False, "can_manage": False, "paid_slot": True, "is_active": True},
    ],
    "reward": "money",
    "invites": 0,
}
ACCOUNT_PAYMENTS = [
    {"date": iso_ago(days=2), "amount": 199.0, "currency": "RUB", "provider": "Карта / СБП", "kind": "purchase", "plan_days": 30, "status": "paid"},
    {"date": iso_ago(days=9), "amount": 300.0, "currency": "RUB", "provider": "CryptoBot", "kind": "topup", "plan_days": 0, "status": "paid"},
    {"date": iso_ago(days=33), "amount": 50.0, "currency": "RUB", "provider": "С баланса", "kind": "device", "plan_days": 0, "status": "paid"},
    {"date": iso_ago(days=34), "amount": 199.0, "currency": "RUB", "provider": "Карта / СБП", "kind": "purchase", "plan_days": 30, "status": "expired"},
    {"date": iso_ago(days=64), "amount": 1590.0, "currency": "RUB", "provider": "Криптовалюта", "kind": "purchase", "plan_days": 365, "status": "paid"},
]

def account(handler, method, p, raw):
    """Account 2.0 endpoints under /subscription/TESTTOKEN/account/. Returns True when handled."""
    pre = f"/subscription/{TOK}/account/"
    if not p.startswith(pre):
        return False
    rest = p[len(pre):].strip("/").split("/")
    if os.environ.get("ACCOUNT_GUEST") == "1":
        handler.send(403, json.dumps({"detail": "Управлять подпиской может только её владелец"})); return True
    body = json.loads(raw or b"{}") if raw else {}
    devs = acct["devices"]
    if rest == ["devices"] and method == "GET":
        handler.send(200, json.dumps({"devices": devs})); return True
    if rest == ["devices", "invite"] and method == "POST":
        balance = float(os.environ.get("INVITE_BALANCE", "150"))
        cost = 0 if os.environ.get("INVITE_FREE") == "1" or len(devs) < 3 else 50
        if cost and not body.get("confirm_pay"):
            handler.send(200, json.dumps({"needs_payment": True, "price_rub": cost, "free_limit": 3, "devices": len(devs), "balance_rub": balance})); return True
        if cost and balance < cost:
            handler.send(402, json.dumps({"detail": "Не хватает баланса на доплату за устройство — пополните баланс.", "price_rub": cost, "balance_rub": balance})); return True
        acct["invites"] += 1
        exp = (datetime.datetime.utcnow() + datetime.timedelta(minutes=10)).replace(microsecond=0).isoformat()
        handler.send(200, json.dumps({"join_url": f"https://termitus.online/app/join/FAKE{acct['invites']:04d}ABCDEFGH", "expires_at": exp, "price_rub": cost})); return True
    if len(rest) >= 2 and rest[0] == "devices" and rest[1].isdigit():
        d = next((x for x in devs if x["id"] == int(rest[1])), None)
        if d is None:
            handler.send(404, json.dumps({"detail": "Not found"})); return True
        if len(rest) == 2 and method == "PATCH":
            d["name"] = (body.get("name") or d["name"])[:100]; handler.send(200, json.dumps({"id": d["id"], "name": d["name"]})); return True
        if len(rest) == 2 and method == "DELETE":
            if d["is_this_device"] and len([x for x in devs if x["can_manage"]]) <= 1:
                handler.send(409, json.dumps({"detail": "Это единственное устройство с управлением подпиской — его можно удалить только через бота."})); return True
            devs.remove(d); handler.send(200, json.dumps({"status": "removed", "id": d["id"]})); return True
        if len(rest) == 3 and rest[2] == "manage" and method == "POST":
            if not body.get("on") and d["is_this_device"]:
                handler.send(409, json.dumps({"detail": "Нельзя забрать управление у устройства, с которого вы работаете."})); return True
            d["can_manage"] = bool(body.get("on")); handler.send(200, json.dumps({"id": d["id"], "can_manage": d["can_manage"]})); return True
    if rest == ["referrals"] and method == "GET":
        empty = os.environ.get("ACCOUNT_EMPTY") == "1"
        handler.send(200, json.dumps({"link": "https://t.me/TErpiUsis_bot?start=ref_123456789", "site_link": "https://termitus.online/?ref=123456789",
            "invited_count": 0 if empty else 4, "bonus_rub": 0 if empty else 238.5, "bonus_days": 0 if empty else 8, "reward": acct["reward"],
            "bonus_percent": 15, "bonus_days_per_payment": 4})); return True
    if rest == ["referrals"] and method == "PATCH":
        if body.get("reward") not in ("money", "days"):
            handler.send(422, json.dumps({"detail": "bad reward"})); return True
        acct["reward"] = body["reward"]; handler.send(200, json.dumps({"reward": acct["reward"]})); return True
    if rest == ["payments"] and method == "GET":
        pays = [] if os.environ.get("ACCOUNT_EMPTY") == "1" else ACCOUNT_PAYMENTS
        if os.environ.get("ACCOUNT_MANY") == "1":  # 14 rows: the «Показать все» expander
            pays = [dict(ACCOUNT_PAYMENTS[i % 5], date=iso_ago(days=2 + 7 * i)) for i in range(14)]
        handler.send(200, json.dumps({"payments": pays})); return True
    if rest == ["rotate-link"] and method == "POST":
        handler.send(200, json.dumps({"status": "rotated", "subscription_url": "https://termitus.online/subscription/FAKEROTATEDUUID"})); return True
    handler.send(404, "{}"); return True

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
        if account(self, "GET", p, None): return
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
                "renew_url": "https://t.me/TErpiUsis_bot", "topup_url": "https://termitus.online/topup", "telegram": {"username": "tester", "first_name": "Тест"}}))
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
        if p.startswith("/app/login/poll/"):
            login["polls"] += 1
            if login["polls"] <= 2: return self.send(202, json.dumps({"status": "pending"}))
            body = {"status": "ok", "subscription_url": f"http://127.0.0.1:{PORT}/subscription/{TOK}"}
            if login.get("trial_code"):  # TRIAL_NOTE=code_unavailable|cooldown|error -> trial_note; default = attached
                note = os.environ.get("TRIAL_NOTE")
                if note: body["trial_note"] = note
                else: body["trial_attached"] = True
            return self.send(200, json.dumps(body))
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
        if account(self, "POST", p, raw): return
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
        if p == "/app/login/start":
            body = json.loads(raw or b"{}")
            login["code"] = "LOGINCODE1234"; login["polls"] = 0; login["trial_code"] = body.get("trial_code")
            if os.environ.get("REQ_LOG"):
                with open(os.environ["REQ_LOG"], "a") as f:  # field names only
                    f.write(json.dumps({"path": p, "fields": sorted(body.keys()), "has_trial_code": bool(body.get("trial_code"))}) + "\n")
            return self.send(200, json.dumps({"code": login["code"], "bot_url": "https://t.me/TErpiUsis_bot?start=login_LOGINCODE1234", "expires_in": 300, "poll_interval": 1}))
        if p == "/app/trial":
            # 0.6.5: in-app trial. TRIAL=ip|busy|subnet|install (429 + X-Trial-Reason), 400|422|503 (plain status); default 200.
            body = json.loads(raw or b"{}")
            if os.environ.get("REQ_LOG"):
                with open(os.environ["REQ_LOG"], "a") as f:  # field names only, never the install id
                    f.write(json.dumps({"path": p, "fields": sorted(body.keys()), "consent": body.get("consent"), "platform": body.get("platform"), "install_id_len": len(body.get("install_id") or "")}) + "\n")
            mode = os.environ.get("TRIAL", "ok")
            texts = {"ip": "С вашего адреса сегодня уже брали пробный доступ. Возьмите его в нашем Telegram-боте.",
                     "busy": "Сегодня пробный доступ уже раздан. Загляните завтра или возьмите его в нашем Telegram-боте.",
                     "subnet": "С вашей сети сегодня уже брали пробный доступ. Возьмите его в нашем Telegram-боте.",
                     "install": "Пробный период на этом устройстве уже брали — возьмите его в нашем Telegram-боте."}
            if mode in texts:
                return self.send(429, json.dumps({"detail": texts[mode]}), headers={"X-Trial-Reason": mode})
            if mode in ("400", "422", "503"):
                return self.send(int(mode), json.dumps({"detail": "x"}))
            if not body.get("consent"): return self.send(400, json.dumps({"detail": "consent"}))
            exp = datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(days=2)
            return self.send(200, json.dumps({"subscription_url": f"http://127.0.0.1:{PORT}/subscription/{TOK}", "code": "TRIALCODE1234", "days": 2,
                                              "expires_at": exp.strftime("%Y-%m-%dT%H:%M:%SZ")}))
        if p == f"/subscription/{TOK}/pay":
            return self.send(201, json.dumps({"payment_id": f"pay{int(time.time())}", "pay_url": "https://termitus.online/pay/test"}))
        self.send(404, "{}")

    def do_PATCH(self):
        p = self.path.split("?")[0]
        n = int(self.headers.get("Content-Length") or 0); raw = self.rfile.read(n)
        if not account(self, "PATCH", p, raw): self.send(404, "{}")
    def do_DELETE(self):
        p = self.path.split("?")[0]
        if not account(self, "DELETE", p, None): self.send(404, "{}")

if os.environ.get("SHUTDOWN_AFTER"):
    threading.Timer(float(os.environ["SHUTDOWN_AFTER"]), lambda: os._exit(0)).start()
ThreadingHTTPServer((os.environ.get("FAKE_BIND", "127.0.0.1"), PORT), H).serve_forever()

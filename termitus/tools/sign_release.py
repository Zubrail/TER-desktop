#!/usr/bin/env python3
"""sign_release.py -- sign the TER update manifest (latest.json) with Ed25519.  Python 3, standard library only.

  python3 sign_release.py keygen [--key FILE] [--patch-app PATH_TO_TermitusUpdate.cs]
        ONE TIME. Creates the private key (default ~/.ter-release/update_ed25519.key, mode 0600, refuses to write
        inside a git checkout) and prints the PUBLIC key.  --patch-app puts the public key into the app source
        (TermitusUpdateKey.PublicKeyBase64), then rebuild the app.  NEVER commit or share the private key;
        losing it means shipping a new app build with a new public key (old installs can then only be updated by hand).

  python3 sign_release.py sign --version 0.6.1 --notes-file notes_ru.txt \\
        --mac-arm64 TER-0.6.1-osx-arm64.dmg --url-base https://termitus.online/app/ [--key FILE] [--out latest.json]
        [--mac-x64 FILE] [--win-x64 FILE]
        Computes sha256 of every file, builds latest.json, signs it.  Upload latest.json LAST (after the binaries).

  python3 sign_release.py verify latest.json --pubkey BASE64
        Checks a manifest the same way the app does.

  python3 sign_release.py selftest      RFC 8032 test vectors + canonical-message check.

The signed message is the length-prefixed text documented in TermitusUpdate.cs (TermitusUpdateManifest.CanonicalMessage):
    "TER-UPDATE-1\\n" + for each of version, notes_ru, url_mac_arm64, url_mac_x64, url_win_x64,
    sha256_mac_arm64, sha256_mac_x64, sha256_win_x64:   "<name>:<utf8 byte length>:<value>\\n"   (absent = "")
"""
import argparse, base64, hashlib, json, os, re, secrets, stat, subprocess, sys
from pathlib import Path

# ---------------------------------------------------------------- Ed25519 (RFC 8032 reference algorithm)
P = 2**255 - 19
L = 2**252 + 27742317777372353535851937790883648493
D = -121665 * pow(121666, P - 2, P) % P
I = pow(2, (P - 1) // 4, P)


def _inv(x): return pow(x, P - 2, P)


def _recover_x(y, sign):
    if y >= P: return None
    x2 = (y * y - 1) * _inv(D * y * y + 1) % P
    if x2 == 0: return None if sign else 0
    x = pow(x2, (P + 3) // 8, P)
    if (x * x - x2) % P: x = x * I % P
    if (x * x - x2) % P: return None
    if x & 1 != sign: x = P - x
    return x


_BY = 4 * _inv(5) % P
_B = (_recover_x(_BY, 0), _BY, 1, _recover_x(_BY, 0) * _BY % P)


def _add(p, q):
    a = (p[1] - p[0]) * (q[1] - q[0]) % P
    b = (p[1] + p[0]) * (q[1] + q[0]) % P
    c = 2 * p[3] * q[3] * D % P
    d = 2 * p[2] * q[2] % P
    e, f, g, h = b - a, d - c, d + c, b + a
    return (e * f % P, g * h % P, f * g % P, e * h % P)


def _mul(s, p):
    q = (0, 1, 1, 0)
    while s:
        if s & 1: q = _add(q, p)
        p = _add(p, p)
        s >>= 1
    return q


def _enc(p):
    zi = _inv(p[2])
    x, y = p[0] * zi % P, p[1] * zi % P
    return (y | ((x & 1) << 255)).to_bytes(32, "little")


def _dec(s):
    y = int.from_bytes(s, "little")
    sign = y >> 255
    y &= (1 << 255) - 1
    x = _recover_x(y, sign)
    return None if x is None else (x, y, 1, x * y % P)


def _h(m): return hashlib.sha512(m).digest()


def _expand(seed):
    h = _h(seed)
    a = int.from_bytes(h[:32], "little")
    a &= (1 << 254) - 8
    a |= 1 << 254
    return a, h[32:]


def public_key(seed): return _enc(_mul(_expand(seed)[0], _B))


def sign(seed, msg):
    a, prefix = _expand(seed)
    pk = _enc(_mul(a, _B))
    r = int.from_bytes(_h(prefix + msg), "little") % L
    R = _enc(_mul(r, _B))
    k = int.from_bytes(_h(R + pk + msg), "little") % L
    return R + ((r + k * a) % L).to_bytes(32, "little")


def verify(pk, msg, sig):
    if len(pk) != 32 or len(sig) != 64: return False
    A, R = _dec(pk), _dec(sig[:32])
    if A is None or R is None: return False
    s = int.from_bytes(sig[32:], "little")
    if s >= L: return False
    k = int.from_bytes(_h(sig[:32] + pk + msg), "little") % L
    lhs, rhs = _mul(s, _B), _add(R, _mul(k, A))
    return (lhs[0] * rhs[2] - rhs[0] * lhs[2]) % P == 0 and (lhs[1] * rhs[2] - rhs[1] * lhs[2]) % P == 0


# ---------------------------------------------------------------- manifest
PLATFORMS = ["mac_arm64", "mac_x64", "win_x64"]


def canonical(version, notes, urls, hashes):
    out = "TER-UPDATE-1\n"
    def field(name, value):
        nonlocal out
        value = value or ""
        out += f"{name}:{len(value.encode('utf-8'))}:{value}\n"
    field("version", version)
    field("notes_ru", notes)
    for k in PLATFORMS: field("url_" + k, urls.get(k, ""))
    for k in PLATFORMS: field("sha256_" + k, hashes.get(k, ""))
    return out.encode("utf-8")


def sha256_file(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
    return h.hexdigest()


def in_git_checkout(path):
    p = Path(path).resolve().parent
    while True:
        if (p / ".git").exists(): return True
        if p.parent == p: return False
        p = p.parent


DEFAULT_KEY = Path.home() / ".ter-release" / "update_ed25519.key"


def load_seed(path):
    raw = Path(path).expanduser().read_text().strip()
    seed = base64.b64decode(raw)
    if len(seed) != 32: sys.exit("key file must hold 32 bytes (base64)")
    return seed


def cmd_keygen(a):
    key = Path(a.key).expanduser()
    if key.exists(): sys.exit(f"{key} exists -- refusing to overwrite a signing key (move it away on purpose if you really want a new one)")
    if in_git_checkout(key): sys.exit("refusing to put the PRIVATE key inside a git checkout; choose --key outside the repository")
    key.parent.mkdir(parents=True, exist_ok=True)
    seed = secrets.token_bytes(32)
    fd = os.open(key, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as f: f.write(base64.b64encode(seed).decode() + "\n")
    pub = base64.b64encode(public_key(seed)).decode()
    print(f"private key: {key}  (mode 0600) -- BACK IT UP somewhere safe, offline; never commit it.")
    print(f"public key : {pub}")
    if a.patch_app:
        p = Path(a.patch_app)
        s = p.read_text()
        s2, n = re.subn(r'(public const string PublicKeyBase64 = ")[^"]*(";)', lambda m: m.group(1) + pub + m.group(2), s)
        if n != 1: sys.exit("could not find PublicKeyBase64 in " + str(p))
        p.write_text(s2)
        print(f"patched {p}; rebuild the app so the new key is embedded.")
    else:
        print("put it into TermitusUpdateKey.PublicKeyBase64 (termitus/overlay/ServiceLib/Services/TermitusUpdate.cs) and rebuild, or rerun keygen with --patch-app.")


def cmd_sign(a):
    seed = load_seed(a.key)
    notes = Path(a.notes_file).read_text(encoding="utf-8").strip()
    urls, hashes = {}, {}
    base = a.url_base if a.url_base.endswith("/") else a.url_base + "/"
    for plat, fn in (("mac_arm64", a.mac_arm64), ("mac_x64", a.mac_x64), ("win_x64", a.win_x64)):
        if fn:
            urls[plat] = base + Path(fn).name
            hashes[plat] = sha256_file(fn)
    if not urls: sys.exit("give at least one of --mac-arm64 / --mac-x64 / --win-x64")
    sig = sign(seed, canonical(a.version, notes, urls, hashes))
    doc = {"version": a.version, "notes_ru": notes}
    for k in PLATFORMS:
        if k in urls: doc["url_" + k] = urls[k]
    doc["sha256"] = {k: hashes[k] for k in PLATFORMS if k in hashes}
    doc["signature"] = base64.b64encode(sig).decode()
    Path(a.out).write_text(json.dumps(doc, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {a.out}: version {a.version}, files: {', '.join(urls)}")
    print("upload the binaries first, latest.json LAST.")


def cmd_verify(a):
    doc = json.loads(Path(a.manifest).read_text(encoding="utf-8"))
    urls = {k: doc.get("url_" + k, "") for k in PLATFORMS}
    hashes = {k: (doc.get("sha256") or {}).get(k, "") for k in PLATFORMS}
    msg = canonical(doc.get("version", ""), doc.get("notes_ru", ""), urls, hashes)
    ok = verify(base64.b64decode(a.pubkey), msg, base64.b64decode(doc.get("signature", "")))
    print("OK: signature valid" if ok else "BAD: signature does not verify")
    sys.exit(0 if ok else 1)


RFC8032 = [  # (secret, public, message, signature) -- RFC 8032 section 7.1, tests 1-2
    ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
     "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a", "",
     "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
    ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
     "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c", "72",
     "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
]


def cmd_selftest(_):
    bad = 0
    for sk, pk, msg, sig in RFC8032:
        seed, m = bytes.fromhex(sk), bytes.fromhex(msg)
        good = public_key(seed).hex() == pk and sign(seed, m).hex() == sig and verify(bytes.fromhex(pk), m, bytes.fromhex(sig))
        tampered = verify(bytes.fromhex(pk), m + b"x", bytes.fromhex(sig))
        print("rfc8032 vector:", "ok" if good and not tampered else "FAIL")
        bad += 0 if good and not tampered else 1
    c = canonical("0.6.1", "а\nб", {"mac_arm64": "https://x/a.dmg"}, {"mac_arm64": "ab" * 32})
    exp = ("TER-UPDATE-1\nversion:5:0.6.1\nnotes_ru:5:а\nб\nurl_mac_arm64:15:https://x/a.dmg\nurl_mac_x64:0:\nurl_win_x64:0:\n"
           "sha256_mac_arm64:64:" + "ab" * 32 + "\nsha256_mac_x64:0:\nsha256_win_x64:0:\n").encode("utf-8")
    print("canonical message:", "ok" if c == exp else "FAIL")
    bad += 0 if c == exp else 1
    sys.exit(1 if bad else 0)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    k = sub.add_parser("keygen"); k.add_argument("--key", default=str(DEFAULT_KEY)); k.add_argument("--patch-app"); k.set_defaults(f=cmd_keygen)
    s = sub.add_parser("sign"); s.add_argument("--key", default=str(DEFAULT_KEY)); s.add_argument("--version", required=True)
    s.add_argument("--notes-file", required=True); s.add_argument("--url-base", required=True)
    s.add_argument("--mac-arm64"); s.add_argument("--mac-x64"); s.add_argument("--win-x64"); s.add_argument("--out", default="latest.json"); s.set_defaults(f=cmd_sign)
    v = sub.add_parser("verify"); v.add_argument("manifest"); v.add_argument("--pubkey", required=True); v.set_defaults(f=cmd_verify)
    t = sub.add_parser("selftest"); t.set_defaults(f=cmd_selftest)
    a = ap.parse_args()
    a.f(a)


if __name__ == "__main__":
    main()

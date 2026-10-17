import base64
import hashlib
import re
import secrets
import urllib.parse as up

import httpx
import jwt

REDIR = "http://localhost:33418/callback"


def pkce():
    v = secrets.token_urlsafe(48)
    return v, base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b"=").decode()


def register(gw, redirect=REDIR, name="c"):
    return httpx.post(f"{gw.url}/register", json={
        "redirect_uris": [redirect], "client_name": name, "token_endpoint_auth_method": "none",
        "grant_types": ["authorization_code", "refresh_token"], "response_types": ["code"]})


def start(gw, client_id, redirect=REDIR):
    verifier, challenge = pkce()
    c = httpx.Client(follow_redirects=False)
    r = c.get(f"{gw.url}/authorize", params={
        "response_type": "code", "client_id": client_id, "redirect_uri": redirect,
        "code_challenge": challenge, "code_challenge_method": "S256", "state": "st",
        "scope": "Mcp.Access", "resource": f"{gw.url}/mcp"})
    return c, r, verifier


def consent_fields(c, r):
    page = c.get(r.headers["location"])
    txn = re.search(r'name="txn_id" value="([^"]+)"', page.text).group(1)
    csrf = re.search(r'name="csrf_token" value="([^"]+)"', page.text).group(1)
    return txn, csrf


def complete(gw, c, r, client_id, verifier, redirect=REDIR):
    txn, csrf = consent_fields(c, r)
    r2 = c.post(f"{gw.url}/consent", data={"txn_id": txn, "action": "approve", "csrf_token": csrf})
    assert r2.status_code in (302, 303), r2.text
    r3 = c.get(r2.headers["location"])   # mock IdP
    r4 = c.get(r3.headers["location"])   # gateway /auth/callback
    code = up.parse_qs(up.urlparse(r4.headers["location"]).query)["code"][0]
    return httpx.post(f"{gw.url}/token", data={
        "grant_type": "authorization_code", "code": code, "redirect_uri": redirect,
        "client_id": client_id, "code_verifier": verifier})


def get_token(gw, redirect=REDIR):
    cid = register(gw, redirect).json()["client_id"]
    c, r, v = start(gw, cid, redirect)
    tok = complete(gw, c, r, cid, v, redirect)
    assert tok.status_code == 200, tok.text
    return cid, tok.json()


def test_discovery_documents(gw):
    asm = httpx.get(f"{gw.url}/.well-known/oauth-authorization-server").json()
    for k in ("registration_endpoint", "authorization_endpoint", "token_endpoint", "issuer"):
        assert k in asm
    assert asm["code_challenge_methods_supported"] == ["S256"]
    prm = httpx.get(f"{gw.url}/.well-known/oauth-protected-resource/mcp").json()
    assert prm["resource"].endswith("/mcp") and prm["authorization_servers"]
    r = httpx.post(f"{gw.url}/mcp", json={})
    assert r.status_code == 401 and "resource_metadata" in r.headers["www-authenticate"]


def test_dcr_issues_separate_identities_and_no_upstream_secret(gw):
    a, b = register(gw, name="a"), register(gw, name="b")
    ja, jb = a.json(), b.json()
    assert ja["client_id"] != jb["client_id"]
    assert ja["client_id"] != "central-upstream-client"
    assert not ja.get("client_secret")
    assert "CENTRAL-UPSTREAM-SECRET" not in a.text + b.text


def test_authorize_requires_consent_before_upstream(gw, idp):
    idp.last_authorize_params = {}
    cid = register(gw).json()["client_id"]
    c, r, v = start(gw, cid)
    assert r.status_code in (302, 303) and "/consent" in r.headers["location"]
    assert idp.last_authorize_params == {}


def test_consent_forged_csrf_rejected(gw):
    cid = register(gw).json()["client_id"]
    c, r, v = start(gw, cid)
    txn, _ = consent_fields(c, r)
    bad = c.post(f"{gw.url}/consent", data={"txn_id": txn, "action": "approve", "csrf_token": "forged"})
    assert bad.status_code >= 400


def test_consent_from_other_browser_rejected(gw):
    cid = register(gw).json()["client_id"]
    c, r, v = start(gw, cid)
    txn, csrf = consent_fields(c, r)
    attacker = httpx.Client(follow_redirects=False)  # does not hold the victim's cookies
    res = attacker.post(f"{gw.url}/consent", data={"txn_id": txn, "action": "approve", "csrf_token": csrf})
    assert res.status_code >= 400


def test_upstream_uses_central_client_and_fixed_callback(gw, idp):
    cid = register(gw).json()["client_id"]
    c, r, v = start(gw, cid)
    complete(gw, c, r, cid, v)
    q = idp.last_authorize_params
    assert q["client_id"] == "central-upstream-client"
    assert q["redirect_uri"] == f"{gw.url}/auth/callback"
    assert "resource" not in q
    sent_secret = idp.last_token_form.get("client_secret")
    if sent_secret is None:
        basic = base64.b64decode(idp.last_token_auth.split()[1]).decode()
        sent_secret = basic.split(":", 1)[1]
    assert sent_secret == "CENTRAL-UPSTREAM-SECRET"


def test_client_receives_fastmcp_jwt_not_entra_token(gw, idp):
    _, tok = get_token(gw)
    assert tok["access_token"] not in idp.issued
    assert jwt.get_unverified_header(tok["access_token"])["alg"] == "HS256"


def test_redirect_manipulation_rejected(gw):
    cid = register(gw).json()["client_id"]
    r = httpx.get(f"{gw.url}/authorize", params={
        "response_type": "code", "client_id": cid, "redirect_uri": "https://evil.example/cb",
        "code_challenge": "x" * 43, "code_challenge_method": "S256", "state": "s"})
    assert r.status_code == 400
    r = httpx.get(f"{gw.url}/authorize", params={
        "response_type": "code", "client_id": "unregistered", "redirect_uri": REDIR,
        "code_challenge": "x" * 43, "code_challenge_method": "S256"})
    assert r.status_code == 400


def test_pkce_wrong_verifier_rejected(gw):
    cid = register(gw).json()["client_id"]
    c, r, v = start(gw, cid)
    assert complete(gw, c, r, cid, "wrong" + v).status_code >= 400

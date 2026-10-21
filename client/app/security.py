"""Keep the dashboard local by default; authenticate explicitly enabled remote access."""
import base64
import binascii
import hmac
import ipaddress
import os
from urllib.parse import urlsplit

from starlette.responses import JSONResponse


def _loopback(host: str) -> bool:
    if host.lower() == "localhost":
        return True
    try:
        address = ipaddress.ip_address(host)
        return (getattr(address, "ipv4_mapped", None) or address).is_loopback
    except ValueError:
        return False


class DashboardAccess:
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope["type"] not in ("http", "websocket"):
            return await self.app(scope, receive, send)
        headers = {k.decode().lower(): v.decode() for k, v in scope.get("headers", [])}
        host = headers.get("host", "")
        origin = headers.get("origin")
        same_origin = not origin or urlsplit(origin).netloc.lower() == host.lower()
        token = os.environ.get("P2L_CLIENT_TOKEN", "")
        supplied = ""
        scheme, _, value = headers.get("authorization", "").partition(" ")
        if scheme.lower() == "bearer":
            supplied = value
        elif scheme.lower() == "basic":
            try:
                username, _, password = base64.b64decode(value, validate=True).decode().partition(":")
                if username == "p2l":
                    supplied = password
            except (ValueError, UnicodeError, binascii.Error):
                pass
        if token:
            allowed = bool(supplied) and hmac.compare_digest(supplied.encode(), token.encode())
        else:
            peer = (scope.get("client") or ("", 0))[0]
            allowed = _loopback(peer) and _loopback(urlsplit("//" + host).hostname or "")
        if allowed and same_origin:
            return await self.app(scope, receive, send)
        if scope["type"] == "websocket":
            return await send({"type": "websocket.close", "code": 1008})
        status = 401 if token and same_origin else 403
        response = JSONResponse({"detail": "Dashboard authentication required" if token
                                 else "Remote access requires P2L_CLIENT_TOKEN"}, status_code=status,
                                headers={"WWW-Authenticate": 'Basic realm="P2L"'} if status == 401 else {})
        await response(scope, receive, send)

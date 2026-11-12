"""Local UI is loopback-only; reject cross-origin browser and DNS-rebinding access."""
from urllib.parse import urlsplit


class LocalAccessMiddleware:
    def __init__(self, app):
        self.app = app

    async def __call__(self, scope, receive, send):
        if scope['type'] not in ('http', 'websocket'):
            return await self.app(scope, receive, send)
        headers = dict(scope.get('headers', []))
        host = headers.get(b'host', b'').decode('ascii', errors='replace')
        origin = headers.get(b'origin', b'').decode('ascii', errors='replace')
        try:
            parsed = urlsplit('http://' + host)
            allowed = parsed.hostname in ('localhost', '127.0.0.1', '::1')
            if origin:
                source = urlsplit(origin)
                allowed = allowed and source.scheme == scope.get('scheme', 'http').replace('ws', 'http') and source.netloc == host
        except ValueError:
            allowed = False
        if not allowed:
            if scope['type'] == 'websocket':
                await send({'type': 'websocket.close', 'code': 1008})
            else:
                await send({'type': 'http.response.start', 'status': 403, 'headers': [(b'content-type', b'text/plain')]})
                await send({'type': 'http.response.body', 'body': b'Local access only'})
            return
        await self.app(scope, receive, send)

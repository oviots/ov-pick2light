"""HTTPS-only device API; no redirects, URL secrets, payload logs or disk cache."""
import json
import os
import urllib.error
import urllib.parse
import urllib.request


class LicenseError(RuntimeError):
    pass


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def api_url() -> str:
    value = os.environ.get('P2L_LICENSE_API_URL', '').rstrip('/')
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        raise LicenseError('Chưa cấu hình HTTPS P2L_LICENSE_API_URL hợp lệ')
    return value


def post(path: str, body: dict) -> dict:
    request = urllib.request.Request(api_url() + '/api/p2l/v1/' + path,
        data=json.dumps(body).encode(), headers={'Content-Type': 'application/json', 'Accept': 'application/json'}, method='POST')
    try:
        with urllib.request.build_opener(NoRedirect).open(request, timeout=15) as response:
            raw = response.read(65537)
            if len(raw) > 65536: raise LicenseError('Phản hồi license quá lớn')
            result = json.loads(raw)
        if result.get('Status') is not True or not isinstance(result.get('Data'), dict):
            raise LicenseError('Phản hồi license không hợp lệ')
        return result['Data']
    except urllib.error.HTTPError as error:
        messages = {400: 'Yêu cầu license không hợp lệ', 403: 'License không hợp lệ, hết hạn, bị khóa hoặc đã gắn với máy khác',
                    409: 'Máy đã gắn với license khác. Liên hệ quản trị để chuyển license',
                    429: 'Quá nhiều yêu cầu. Vui lòng thử lại sau'}
        raise LicenseError(messages.get(error.code, 'API license chưa sẵn sàng')) from None
    except (urllib.error.URLError, TimeoutError, OSError, ValueError):
        raise LicenseError('Không kết nối được API license qua HTTPS. Vui lòng thử lại') from None

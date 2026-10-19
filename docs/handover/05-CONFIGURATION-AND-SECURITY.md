# 05 — Cấu hình và bảo mật

## 1. Cấu hình bridge .NET

Binary đọc theo thứ tự:

1. `config/p2l_config.json` tương đối với thư mục chứa executable.
2. Biến môi trường prefix `P2L_`, giá trị môi trường override JSON.
3. Default compile-time nếu không có hai nguồn trên.

Nested environment key dùng dấu `__`, ví dụ:

```text
P2L_GATEWAY__HOST
P2L_GATEWAY__PORT
P2L_MQTT__ENABLED
P2L_MQTT__HOST
P2L_MQTT__CLIENTID
P2L_MQTT__TLS__ENABLED
```

Không đưa giá trị thật vào tài liệu, shell history hoặc service unit trong Git.

### 1.1 Nhóm field

| Nhóm | Field chính | Lưu ý vận hành |
|---|---|---|
| Gateway | `Host`, `Port`, `BlockNo` | `BlockNo=00` có blast radius lớn |
| Logging | `LogDirectory`, `MaxLogFileSizeBytes`, `Logging:LogLevel` | Bảo vệ log TX/RX và xoay vòng |
| MQTT | `Enabled`, `Host`, `Port`, `ClientId`, topic | Client ID phải unique cho mỗi instance |
| MQTT auth | `Username`, `Password` | Secret store/env injection, không commit |
| MQTT TLS | CA/client cert/key/password, allow-untrusted | Production phải validate certificate |
| Buffer/labels | `OfflineBufferPath`, `ClientDbPath` | Quyền ghi/đọc tối thiểu, backup có kiểm soát |
| Persistence | `EnableStatePersistence` | Chỉ observed cache; không là session truth |
| Self-heal | restart/window/backoff/watchdog | Tránh restart loop phát lệnh workflow |

Tại baseline, `EnableStatePersistence=false`. Bật option này không giải quyết
recovery session và không thay cho WMS resync.

## 2. Cấu hình web client

Web client dùng file dữ liệu ứng dụng `config/p2l_client_config.json` với các nhóm:

- MQTT connection/topic/TLS;
- `service.binary_path`, `service.args`, `working_dir`, `auto_start`;
- `block_no` và `auto_connect`.

Giá trị an toàn tối thiểu:

```json
{
  "mqtt": {
    "client_id": "p2l-webclient-site-unique",
    "tls": {
      "enabled": true,
      "allow_untrusted_certs": false
    }
  },
  "service": {
    "args": ["serve"],
    "auto_start": false
  },
  "auto_connect": false
}
```

Đây chỉ là minh họa field, không phải config deploy hoàn chỉnh. Không để
`service.args=[]`: binary không tham số vào interactive mode, có thể gửi `Z` lúc
start và `D00` khi shutdown/error.

## 3. Phát hiện bảo mật tại baseline

### P0 — Secret trong Git

`config/p2l_config.json` và `client/p2l_config.json` đang được track và chứa
username/password không rỗng; cấu hình client còn cho phép certificate không tin
cậy. Không chép giá trị secret vào biên bản.

Hành động bắt buộc:

1. Xác định broker/account/certificate bị ảnh hưởng.
2. Rotate/revoke qua secret owner.
3. Thay file tracked bằng template rỗng hoặc example an toàn.
4. Chuyển secret sang secret store/service environment có ACL.
5. Scan Git history và artifact/CI log; xử lý theo security policy.
6. Chạy lại release pipeline từ checkout sạch.

Xóa secret khỏi commit mới không làm secret cũ hết hiệu lực; rotation là bắt buộc.

### P0 — Dashboard không có access control

FastAPI routes có thể điều khiển đèn, terminate-all, sửa config, start/stop/restart
service và xóa log, nhưng source hiện không có auth middleware. Frozen build mặc
định có thể bind `0.0.0.0`.

Cho đến khi fix:

- bind `127.0.0.1`;
- hoặc đặt sau reverse proxy có TLS, SSO/RBAC, CSRF/origin controls và audit;
- firewall deny-by-default;
- tách quyền xem, điều khiển, admin và emergency terminate.

## 4. Network policy tối thiểu

| Luồng | Chiều | Policy |
|---|---|---|
| P2L -> Aioi gateway TCP | Outbound/site LAN | Chỉ IP gateway và port được phê duyệt |
| P2L -> MQTT broker | Outbound | TLS, broker allow-list, client ACL theo topic |
| Browser -> web client | Inbound | Loopback/private proxy, authenticated |
| ICMP/scan | Maintenance | Chỉ bật khi diagnostic được authorize |

MQTT ACL đề xuất:

- bridge chỉ subscribe command topic của đúng site/gateway;
- bridge chỉ publish event/device/status của đúng identity;
- dashboard không có quyền admin broker;
- WMS emergency-off quyền riêng, có audit và approval.

## 5. File và quyền truy cập

| Dữ liệu | Tính chất | Yêu cầu |
|---|---|---|
| Bridge config | Có endpoint/secret | Owner-only read; deploy qua secret mechanism |
| TLS private key | Secret | Không commit; permission tối thiểu |
| Offline MQTT buffer | Có event vận hành | Service-only read/write; retention policy |
| Device SQLite DB | Mapping address/label | Backup/version; không coi là session DB |
| TX/RX logs | Audit, có thể nhạy cảm | Rotation, integrity, access và retention |
| Release artifact | Executable/config template | Immutable, hash/signature, controlled store |

## 6. Backup và restore

Backup:

- config đã tách secret hoặc secret reference;
- CA/cert metadata và quy trình cấp lại key, không nhúng key vào ticket;
- device mapping DB nếu business cần label/order;
- release record + artifact hash;
- log theo retention policy.

Không backup/restore `_activeBatches` hay retained device state như session. Sau
restore, P2L phát sync-required và WMS gửi desired state authoritative.

## 7. Security verification checklist

- [ ] Secret scanning không phát hiện credential thật trong tracked files/artifact.
- [ ] Tất cả credential từng lộ đã rotate/revoke.
- [ ] MQTT TLS verify pass; allow-untrusted = false.
- [ ] Client ID unique; duplicate session takeover alert hoạt động.
- [ ] Dashboard bind/access policy được kiểm tra từ máy ngoài allow-list.
- [ ] RBAC tách read/control/admin/emergency.
- [ ] Config API không trả secret; log không ghi secret.
- [ ] File permissions được kiểm tra bằng service account thực tế.
- [ ] Backup/restore drill kết thúc bằng WMS full-state resync.

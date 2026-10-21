# 08 — Vấn đề và rủi ro đã biết

## 1. Quy ước

- **P0:** có thể lộ secret, điều khiển trái phép hoặc gây sai trạng thái đèn rộng;
  chặn production sign-off.
- **P1:** độ tin cậy/recovery/release nghiêm trọng; phải fix hoặc có risk acceptance
  rõ owner và thời hạn.
- **P2:** maintainability/governance; đưa vào kế hoạch có chủ sở hữu.

Tất cả mục dưới đây đang **Open** tại baseline 2026-09-03. Đường dẫn là evidence
để đội tiếp nhận tái kiểm tra trên commit mới; không thay cho test thực tế.

## 2. Danh sách tổng hợp

| ID | Mức | Tóm tắt | Owner | Trạng thái |
|---|:---:|---|---|---|
| HND-P0-001 | P0 | Credential được track và CI có thể đóng gói | Security/Release TBD | Open |
| HND-P0-002 | P0 | Restart + local registry có thể làm tắt đèn pending | P2L/WMS TBD | Open |
| HND-P0-003 | P0 | `QuickLightAsync` gom module từ nhiều block | P2L TBD | Open |
| HND-P0-004 | P0 | Dashboard control/admin không auth, có thể bind all interfaces | Security/Client TBD | Open |
| HND-P1-005 | P1 | Linux/macOS service chạy CSV thay vì daemon | Release TBD | Open |
| HND-P1-006 | P1 | CSV finish/cancel có thể gửi `D00` | P2L/Product TBD | Open |
| HND-P1-007 | P1 | Duplicate MQTT client/bridge gây session takeover | Operations TBD | Open |
| HND-P1-008 | P1 | Lỗi command bị log rồi nuốt; không có outcome event | P2L/WMS TBD | Open |
| HND-P1-009 | P1 | Decoder interrupt chỉ tạo một event từ packet `t` | Hardware TBD | Open |
| HND-P1-010 | P1 | Version drift và artifact không có provenance đầy đủ | Release TBD | Open |
| HND-P1-011 | P1 | Thiếu unit/contract/restart/multi-block tests | QA/P2L TBD | Open |
| HND-P2-012 | P2 | Tài liệu cũ/runtime/protocol/ownership không nhất quán | P2L TBD | Open |
| HND-P2-013 | P2 | Generated binary/DB/cache/log được track | Repo owner TBD | Open |
| HND-P2-014 | P2 | Chưa có root license/security/contribution policy | Legal/Owner TBD | Open |
| HND-P2-015 | P2 | Python dependencies chưa pin/lock | Release TBD | Open |
| HND-P1-016 | P1 | Package đặt sai tên config so với binary loader | Release/P2L TBD | Open |
| HND-P2-017 | P2 | Dashboard `batch_status.pending` dùng completed modules | Client TBD | Open |

## 3. Chi tiết và tiêu chí đóng

### HND-P0-001 — Credential tracked và release leak

- **Evidence:** `config/p2l_config.json`, `client/p2l_config.json`, các bước copy
  config trong `.github/workflows/build-all.yml`.
- **Tác động:** truy cập broker trái phép; secret đi vào history/artifact/log.
- **Workaround:** dừng release; hạn chế quyền account bị ảnh hưởng.
- **Đóng khi:** rotate/revoke; tracked file thành template an toàn; history/artifact
  scan được xử lý theo policy; CI secret scan và package inspection pass.

### HND-P0-002 — Recovery từ cache làm mất đèn pending

- **Evidence:** `src/P2L.Core/PickingEngine.cs`, `QuickLightAsync` dựng batch từ
  registry status `Operating` rồi gửi P1/P5.
- **Tác động:** sau restart, những module WMS còn pending nhưng registry thiếu có
  thể bị loại khỏi full-block command và tắt.
- **Workaround:** pause delta sau restart; WMS gửi full desired state theo block.
- **Đóng khi:** contract sync-required + `apply_block` authoritative được implement;
  ACC-003 pass trên power-cycle test rig.

### HND-P0-003 — Cross-block aggregation

- **Evidence:** `QuickLightAsync` lấy mọi module `Operating`, nhưng
  `AioiGateway.InstructBatchAsync` dùng block của instruction đầu tiên.
- **Tác động:** command một block có thể chứa address của block khác hoặc ghi đè
  phạm vi sai.
- **Workaround:** không chạy concurrent multi-block qua `light_on` v1.
- **Đóng khi:** API bắt buộc one-block full state; validation reject mixed block;
  ACC-004 pass.

### HND-P0-004 — Dashboard không authentication/authorization

- **Evidence:** `client/app/api/routes.py`, `client/run.py`, service/install config
  dùng `P2L_CLIENT_HOST=0.0.0.0`.
- **Tác động:** người truy cập mạng có thể bật/tắt đèn, terminate all, sửa config,
  start/stop/restart service hoặc xóa log.
- **Workaround:** bind loopback + firewall/reverse proxy có auth.
- **Đóng khi:** threat model, TLS, authn, RBAC, CSRF/origin, audit và negative
  access tests được security approve.

### HND-P1-005 — Service Unix sai entry point

- **Evidence:** `deploy/p2l.service`, `deploy/install-linux.sh`,
  `deploy/install-macos.sh`, service được sinh trong `deploy/package.sh` chạy
  `p2l run tasks.csv`.
- **Tác động:** auto-restart CSV workflow, xóa row, phát lệnh đèn và terminate thay
  vì daemon bridge.
- **Workaround:** không dùng/enable các unit/plist này.
- **Đóng khi:** tất cả dùng `p2l serve`; install/upgrade/stop/restart test pass trên
  OS sạch.

### HND-P1-006 — CSV path chủ động terminate-all

- **Evidence:** `src/P2L.Core/Services/CsvPickTaskRunner.cs` gọi
  `FinishBatchAsync()` mặc định khi complete và cleanup/cancel.
- **Tác động:** mặc định block `00` có thể tắt toàn bộ.
- **Workaround:** disable/deprecate CSV runner trong production.
- **Đóng khi:** workflow chuyển lên WMS hoặc block scope bắt buộc, có auth/audit và
  test không cross-block.

### HND-P1-007 — Duplicate MQTT identity

- **Evidence:** log lịch sử có `SessionTakenOver`; default/static client IDs dễ
  trùng giữa bridge/client/host.
- **Tác động:** reconnect loop, mất command/event hoặc hai owner cạnh tranh.
- **Workaround:** inventory process, một owner/gateway, client ID unique.
- **Đóng khi:** deployment enforce uniqueness/singleton, monitoring alert và
  failover test pass.

### HND-P1-008 — Không có command outcome đáng tin cậy

- **Evidence:** `src/P2L.Mqtt/MqttCommandDispatcher.cs` catch/log exception rồi
  return; event model không có `command_applied`/`command_failed`.
- **Tác động:** producer/API có thể coi publish là thành công dù gateway NACK/timeout.
- **Workaround:** đối chiếu TX/RX log và reconciliation; không tin `status: sent`.
- **Đóng khi:** versioned accepted/applied/failed/unknown outcomes, correlation bắt
  buộc và contract tests pass.

### HND-P1-009 — Packet interrupt lặp chưa được parse đầy đủ

- **Evidence:** `src/P2L.Hardware/Protocol/AioiDecoder.cs::ParseInterrupt` chỉ đọc
  address/status/display ở offset đầu và trả một `ConfirmationEvent`.
- **Tác động:** có thể mất confirmation nếu gateway gộp nhiều record `t` theo
  protocol/firmware.
- **Workaround:** xác minh firmware gửi từng record; audit raw RX.
- **Đóng khi:** format được đối chiếu manual/packet capture; decoder trả mọi record;
  fixtures single/multi/malformed pass.

### HND-P1-010 — Version/provenance drift

- **Evidence:** CLI/MSI `1.0.0`; `global.json` SDK `10.0.0`; package script dùng
  SDK version làm product version; tag `1.0.0` không trỏ baseline.
- **Tác động:** không biết artifact nào tương ứng code/config/test nào.
- **Workaround:** coi artifact hiện tại là inventory only.
- **Đóng khi:** single version source, immutable tag, clean CI build, SBOM/hash và
  signed release record.

### HND-P1-011 — Test coverage không đủ

- **Evidence:** chỉ có `tests/P2L.IntegrationTests`; chưa có unit/contract/recovery
  suite.
- **Tác động:** regression decoder, recovery và block isolation khó phát hiện.
- **Workaround:** manual review + isolated test plan; không ghi skipped là pass.
- **Đóng khi:** các test còn thiếu trong tài liệu 07 được automation hóa và
  acceptance matrix pass.

### HND-P2-012 — Tài liệu không nhất quán

- **Evidence:** AGENTS/tài liệu cũ nói .NET 8/9 trong khi source là .NET 10; một
  số protocol/heartbeat/batch claim không khớp implementation hoặc boundary đích.
- **Tác động:** build/runbook và quyết định ownership sai.
- **Workaround:** ưu tiên source + bộ `docs/handover/`.
- **Đóng khi:** docs inventory, owner, version banner và link source-of-truth được
  thống nhất; tài liệu stale được sửa/archive.

### HND-P2-013 — Repo hygiene

- **Evidence:** tracked `dist/`, `publish/`, Python `__pycache__`, DB/WAL/SHM,
  log/debug artifacts; `.gitignore` chưa bao phủ đầy đủ.
- **Tác động:** repo lớn, diff nhiễu, nguy cơ dữ liệu/secret và artifact stale.
- **Workaround:** không commit thêm generated state; review từng file trước merge.
- **Đóng khi:** retention/migration plan được duyệt, generated files untrack an
  toàn, ignore/CI artifact store được chuẩn hóa, clean checkout test pass.

### HND-P2-014 — Governance/license chưa rõ

- **Evidence:** không có root `LICENSE`, `NOTICE`, `SECURITY`, `CONTRIBUTING`,
  `CHANGELOG`; installer RTF chỉ nêu proprietary/all-rights-reserved.
- **Tác động:** đội nhận không rõ quyền sử dụng/phân phối và kênh security report.
- **Workaround:** không phân phối ngoài phạm vi đã được phê duyệt.
- **Đóng khi:** legal/product owner ban hành bộ policy và dependency license review.

### HND-P2-015 — Python dependency không tái lập

- **Evidence:** `client/requirements.txt` dùng `>=` và không có lock/hash.
- **Tác động:** cùng source có thể tạo client khác nhau hoặc kéo dependency lỗi.
- **Workaround:** lưu build environment và artifact hash.
- **Đóng khi:** lock file/hash pin, vulnerability/license scan và clean rebuild
  reproducibility test pass.

### HND-P1-016 — Package config không được binary đọc

- **Evidence:** `deploy/package.sh` stage `config/appsettings.json`; `Program.cs`
  đọc `config/p2l_config.json` từ executable directory.
- **Tác động:** deployed binary có thể dùng default, ví dụ MQTT disabled hoặc sai
  gateway/topic, dù operator đã sửa file được đóng gói.
- **Workaround:** không dùng package script hiện tại cho rollout; kiểm tra config
  load từ log trên canary.
- **Đóng khi:** tên/path thống nhất, package inspection và ACC-015 pass.

### HND-P2-017 — `batch_status` sai nghĩa pending

- **Evidence:** `client/app/api/routes.py` đưa module `COMPLETED` vào `pending`.
- **Tác động:** dashboard hiển thị session status sai; càng củng cố ownership sai.
- **Workaround:** không dùng endpoint làm business truth.
- **Đóng khi:** session status lấy từ WMS hoặc endpoint legacy bị xóa; UI contract
  test pass.

## 4. Quy trình cập nhật risk register

Mỗi thay đổi trạng thái phải ghi commit/PR, test evidence, approver và ngày. Không
đóng issue chỉ vì code đã merge; exit criteria và retest trên release candidate
phải hoàn tất.

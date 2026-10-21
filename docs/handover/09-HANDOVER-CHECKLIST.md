# 09 — Checklist bàn giao

## 1. Thông tin đợt bàn giao

| Trường | Giá trị |
|---|---|
| Tên đợt bàn giao | TBD |
| Phạm vi site/kho | TBD |
| Ngày bắt đầu/kết thúc | TBD |
| Source commit/tag | TBD |
| Release version/hash | TBD |
| Người bàn giao | TBD |
| Người tiếp nhận | TBD |
| Ticket/biên bản gốc | TBD |

## 2. RACI và kênh liên hệ

- [ ] Product/WMS owner được chỉ định.
- [ ] P2L code owner và on-call owner được chỉ định.
- [ ] Warehouse operator/safety owner được chỉ định.
- [ ] Aioi hardware/vendor contact và escalation SLA được xác nhận.
- [ ] Network/MQTT/platform owner được chỉ định.
- [ ] Security/release approver được chỉ định.
- [ ] Kênh incident, severity matrix và escalation tree được diễn tập.

| Vai trò | Tên/nhóm | Liên hệ | Backup | Đã xác nhận |
|---|---|---|---|:---:|
| WMS owner | TBD | TBD | TBD | [ ] |
| P2L owner | TBD | TBD | TBD | [ ] |
| Operations | TBD | TBD | TBD | [ ] |
| Hardware | TBD | TBD | TBD | [ ] |
| Network/MQTT | TBD | TBD | TBD | [ ] |
| Security | TBD | TBD | TBD | [ ] |
| Release manager | TBD | TBD | TBD | [ ] |

## 3. Quyền truy cập và tài sản

- [ ] Repository, branch protection, CI/CD và artifact store.
- [ ] Issue tracker, change management và incident system.
- [ ] MQTT broker admin/observer account theo least privilege.
- [ ] Secret store; không bàn giao secret qua tài liệu/chat/email.
- [ ] Host/service account và remote access theo policy.
- [ ] Monitoring/log platform và retention policy.
- [ ] Test rig/test block và lịch hardware testing.
- [ ] Vendor manuals/firmware/support entitlement.
- [ ] Credential từng bị track đã rotate/revoke và có evidence.

## 4. Hardware và topology inventory

| Site | Gateway ID | Model/serial | Firmware | IP:port | Blocks | Module range | Owner |
|---|---|---|---|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD | TBD | TBD | TBD |

- [ ] Sơ đồ mạng/VLAN/firewall được cập nhật.
- [ ] Mapping block/address/label được export và kiểm tra tại kho.
- [ ] Gateway static IP, DNS/NTP/timezone được xác nhận.
- [ ] Xác định rõ ý nghĩa block `00` trên firmware đang dùng.
- [ ] Có danh sách spare gateway/module/cable và quy trình thay thế.
- [ ] Có cửa sổ bảo trì và người quan sát physical state.

## 5. Source và kiến trúc

- [ ] Baseline commit/tag/remotes được hai bên đối chiếu.
- [ ] Đội nhận đọc và chấp thuận
  [ADR-0001](adr/0001-p2l-hardware-adapter-boundary.md).
- [ ] WMS được ghi là session/desired-state owner.
- [ ] P2L technical state và prohibited business state được hiểu thống nhất.
- [ ] Consumer/producer inventory của MQTT v1 hoàn tất.
- [ ] Lộ trình v2/migration/deprecation có owner và mốc thời gian.
- [ ] Code ownership/review policy/branch strategy được ghi nhận.

## 6. Build và release

- [ ] .NET/Python/WiX/Docker toolchain được pin và tái tạo.
- [ ] Product version không còn lấy từ SDK version.
- [ ] Python dependencies có lock/hash.
- [ ] CI không copy config chứa secret.
- [ ] Package dùng đúng `config/p2l_config.json`.
- [ ] Windows/Linux/macOS service đều chạy `p2l serve`.
- [ ] Clean CI build tạo SBOM, test reports và SHA-256.
- [ ] Artifact chạy smoke test trên OS/architecture mục tiêu.
- [ ] Rollback artifact/config được lưu và kiểm tra.
- [ ] Mẫu [release record](templates/RELEASE-RECORD.md) được dùng.

## 7. Cấu hình và bảo mật

- [ ] Tách config bridge và client; có template không secret.
- [ ] Secrets inject từ approved secret store/environment.
- [ ] TLS verify bật, CA/certificate rotation procedure có owner.
- [ ] MQTT ACL cô lập theo site/gateway/topic.
- [ ] Client ID unique; singleton/fencing được kiểm tra.
- [ ] Dashboard có auth/RBAC hoặc chỉ bind loopback sau secure proxy.
- [ ] Emergency terminate có quyền/audit riêng.
- [ ] File permissions cho config/key/log/buffer/DB được test bằng service account.
- [ ] Secret/history/artifact scan và security review hoàn tất.

## 8. Operations

- [ ] Runbook start/stop/health/recovery/rollback được đội nhận walkthrough.
- [ ] `p2l serve` là entry point duy nhất của daemon.
- [ ] Monitoring có bridge, gateway, MQTT, restart loop, disk/buffer và takeover.
- [ ] Alert có owner, severity, threshold và playbook link.
- [ ] Log TX/RX có timestamp/rotation/access/retention phù hợp.
- [ ] WMS recovery mode và full-state resync được implement.
- [ ] Backup/restore drill không dùng registry làm session truth.
- [ ] Incident drill mất điện được thực hiện bởi on-call thực tế.

## 9. Test và nghiệm thu

- [ ] Test environment/topology/module scope được ghi rõ.
- [ ] Không hardware test nào chạy trên production block ngoài approval.
- [ ] Unit/contract tests pass, không có required test bị skip.
- [ ] ACC-001 đến ACC-015 trong tài liệu 07 pass.
- [ ] Mất điện sau confirm một phần không làm mất đèn pending.
- [ ] Hai block đồng thời không ảnh hưởng chéo.
- [ ] Duplicate command/replay được dedupe.
- [ ] NACK/timeout trả outcome chính xác.
- [ ] Dashboard negative-access test pass.
- [ ] Test evidence gắn với đúng commit và artifact hash.

## 10. Risk acceptance

- [ ] Mọi P0 trong [risk register](08-KNOWN-ISSUES-AND-RISKS.md) đã đóng.
- [ ] Mọi P1 đã fix/retest hoặc có risk acceptance ký tên và expiry date.
- [ ] P2 có owner/milestone.
- [ ] Không issue nào đóng chỉ dựa trên “đã merge” mà thiếu retest evidence.

| Risk ID | Disposition | Evidence/approval | Expiry/review date |
|---|---|---|---|
| TBD | Fix / Accept / Defer | TBD | TBD |

## 11. Knowledge-transfer sessions

| Buổi | Nội dung | Người trình bày | Người tham dự | Evidence | Hoàn tất |
|---|---|---|---|---|:---:|
| KT-01 | Kiến trúc/ownership/contract | TBD | TBD | TBD | [ ] |
| KT-02 | Build/release/deploy | TBD | TBD | TBD | [ ] |
| KT-03 | Operations/recovery | TBD | TBD | TBD | [ ] |
| KT-04 | Hardware/protocol/safety | TBD | TBD | TBD | [ ] |
| KT-05 | Security/incident response | TBD | TBD | TBD | [ ] |
| KT-06 | Live acceptance drill | TBD | TBD | TBD | [ ] |

## 12. Biên bản ký nhận

Ký nhận source/knowledge không đồng nghĩa production acceptance. Chọn đúng trạng
thái và ghi rõ điều kiện còn lại.

| Bên | Họ tên/vai trò | Trạng thái | Điều kiện/ngoại lệ | Ngày | Chữ ký/link approval |
|---|---|---|---|---|---|
| Bàn giao | TBD | TBD | TBD | TBD | TBD |
| Tiếp nhận source | TBD | Accepted / Rejected | TBD | TBD | TBD |
| WMS/Product | TBD | Accepted / Rejected | TBD | TBD | TBD |
| Operations | TBD | Accepted / Rejected | TBD | TBD | TBD |
| Security | TBD | Accepted / Rejected | TBD | TBD | TBD |
| Production sign-off | TBD | Approved / Blocked | TBD | TBD | TBD |

Production sign-off chỉ được chọn `Approved` khi checklist bắt buộc, P0, acceptance
matrix, release record và rollback drill đều hoàn tất.

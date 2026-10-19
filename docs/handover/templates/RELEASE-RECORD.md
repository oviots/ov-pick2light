# Biên bản phát hành Pick2Light

> Tạo một bản cho mỗi release. Không ghi secret; chỉ ghi secret reference/version.

## 1. Nhận dạng release

| Trường | Giá trị |
|---|---|
| Release version | TBD |
| Release type | Candidate / Production / Hotfix / Rollback |
| Git commit | TBD |
| Annotated tag | TBD |
| Repository/remote | TBD |
| CI run | TBD |
| Build UTC | TBD |
| Release manager | TBD |
| Change ticket | TBD |

## 2. Phạm vi thay đổi

- Tính năng/fix:
- Contract/schema change:
- Config change:
- Database/file-format change:
- Deployment/service change:
- Deprecated/removed behavior:
- Không thay đổi:

Link PR/ADR/risk IDs liên quan. Với change boundary/session, bắt buộc link
ADR-0001 và consumer migration evidence.

## 3. Build environment

| Thành phần | Version/image/digest |
|---|---|
| Runner OS/architecture | TBD |
| .NET SDK | TBD |
| Python | TBD |
| Python lock/hash | TBD |
| PyInstaller | TBD |
| WiX | TBD |
| Docker image digest | TBD |
| Other tools | TBD |

## 4. Artifact manifest

| Platform/RID | File | Byte size | SHA-256 | Signature/SBOM | Smoke test |
|---|---|---:|---|---|---|
| TBD | TBD | TBD | TBD | TBD | Pass/Fail |

Xác nhận package chỉ chứa file allow-list và config template đúng tên
`config/p2l_config.json`.

## 5. Config manifest đã khử secret

| Phạm vi | Config hash/reference | Secret reference/version | Approver |
|---|---|---|---|
| Bridge | TBD | TBD | TBD |
| Web client | TBD | TBD | TBD |
| Service manager | TBD | N/A | TBD |
| MQTT ACL/TLS | TBD | TBD | TBD |

Checklist:

- [ ] Không có secret trong repository/artifact/log.
- [ ] TLS verify bật; allow-untrusted tắt.
- [ ] Client ID unique theo site/gateway.
- [ ] Service arguments là `["serve"]`/`serve`.
- [ ] Dashboard bind/auth/RBAC theo security approval.
- [ ] Block `00` policy được xác nhận.

## 6. Test evidence

| Test suite/case | Environment | Result | Skipped | Evidence link |
|---|---|---|---:|---|
| Unit | TBD | TBD | TBD | TBD |
| Contract | TBD | TBD | TBD | TBD |
| Native/package smoke | TBD | TBD | TBD | TBD |
| Hardware integration | TBD | TBD | TBD | TBD |
| ACC-001..015 | TBD | TBD | TBD | TBD |
| Security | TBD | TBD | TBD | TBD |
| Recovery/rollback drill | TBD | TBD | TBD | TBD |

Unavailable/skipped không được ghi Pass.

## 7. Risk disposition

| Risk/defect ID | Fix commit/evidence | Retest | Acceptance owner/expiry |
|---|---|---|---|
| TBD | TBD | TBD | TBD |

- [ ] Tất cả P0 đã đóng.
- [ ] P1 đã fix hoặc có time-bound written acceptance.
- [ ] Không có regression mở chặn release.

## 8. Rollout plan

| Wave | Site/gateway/block | Window | Operator/on-call | Success criteria | Rollback trigger |
|---|---|---|---|---|---|
| Canary | TBD | TBD | TBD | TBD | TBD |
| Wave 2 | TBD | TBD | TBD | TBD | TBD |

Trình tự bắt buộc:

1. Pause upstream/delta commands.
2. Deploy binary + config đã ghép cặp.
3. Start `p2l serve` đúng một instance.
4. Kiểm tra bridge/gateway/MQTT/security.
5. WMS full-state resync từng block.
6. Mở command traffic theo wave và quan sát.

## 9. Rollback plan

| Thành phần | Phiên bản quay lại | Artifact/config hash | Thao tác | Owner |
|---|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD |

- Rollback trigger:
- Maximum rollback decision time:
- Dữ liệu/contract compatibility:
- WMS resync sau rollback:
- Cách xác minh không cross-block/off ngoài ý muốn:

Không dùng `D00` làm bước rollback mặc định.

## 10. Kết quả rollout

| UTC | Site/wave | Artifact hash | Kết quả | Metric/evidence | Incident |
|---|---|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD | TBD |

## 11. Sign-off

| Vai trò | Họ tên | Quyết định | Điều kiện | Ngày | Approval link/signature |
|---|---|---|---|---|---|
| P2L owner | TBD | Approve/Reject | TBD | TBD | TBD |
| WMS/Product | TBD | Approve/Reject | TBD | TBD | TBD |
| QA | TBD | Approve/Reject | TBD | TBD | TBD |
| Operations | TBD | Approve/Reject | TBD | TBD | TBD |
| Security | TBD | Approve/Reject | TBD | TBD | TBD |
| Release manager | TBD | Release/Hold | TBD | TBD | TBD |

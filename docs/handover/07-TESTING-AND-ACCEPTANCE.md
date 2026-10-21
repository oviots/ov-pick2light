# 07 — Kiểm thử và nghiệm thu

## 1. Chính sách test

Test được chia theo blast radius. Không chạy cả project integration trên gateway
thật chỉ để “xem có pass không”. Fixture và test protocol/hardware có thể gửi
`Z`, `Z1`, P1/P5, `D` hoặc `D00`.

| Tier | Nội dung | Thiết bị thật | Quyền cần có |
|---|---|:---:|---|
| T0 | Format, static analysis, unit/contract thuần | Không | Developer |
| T1 | Build/Native AOT/package inspection | Không | Developer/CI |
| T2 | Network ping/TCP reachability | Có kết nối, không chủ ý đổi state | Network + site approval |
| T3 | Protocol handshake/read/decoder rig | Có thể đổi state | Hardware test approval |
| T4 | Light/confirm/block/power-cycle E2E | Có | Isolated test block + operator |
| T5 | Canary warehouse acceptance | Có, giới hạn | Change approval + rollback owner |

## 2. Bộ test hiện có

Chỉ có project `tests/P2L.IntegrationTests`; chưa có project unit/contract/recovery
riêng.

| Category | Ví dụ | Rủi ro |
|---|---|---|
| `Network` | ICMP, TCP port, idle connection | Tương đối thấp nhưng chạm gateway |
| `Protocol` | Z/ACK, raw frame, malformed command | Có write/reset/terminate path |
| `Hardware` | Bật/tắt, P1/P5, confirm | Thay đổi đèn thật; teardown có terminate |

Chỉ chạy khi đã đọc từng test của đúng commit. Ví dụ T2 sau khi được phép:

```bash
dotnet test tests/P2L.IntegrationTests/P2L.IntegrationTests.csproj \
  --filter "Category=Network"
```

Không chạy `Category=Protocol`, `Category=Hardware` hoặc toàn bộ suite trên block
production. `dotnet test` pass cũng không đóng rủi ro nếu test bị skip do gateway
không sẵn sàng.

## 3. Test còn thiếu trước production sign-off

- Unit test encoder/decoder, đặc biệt interrupt `t` có nhiều record.
- Unit test block isolation của P1/P5.
- MQTT command schema, malformed input, expiry, dedupe và stale revision.
- Producer/consumer contract test với WMS.
- Test command outcome cho ACK, NACK, timeout, disconnect giữa chừng.
- Restart/power-loss resync test.
- Offline-buffer ordering, corruption, capacity và duplicate replay test.
- Security test auth/RBAC/CSRF/origin/network binding.
- Service lifecycle test trên Windows/Linux/macOS.
- Package test chứng minh `p2l_config.json` thật sự được load.

## 4. Acceptance environment bắt buộc

Ghi lại trước test:

| Thuộc tính | Giá trị |
|---|---|
| Test plan/version | TBD |
| Commit/tag | TBD |
| Artifact SHA-256 | TBD |
| OS/architecture | TBD |
| P2L instance/client ID | TBD |
| Gateway model/serial/firmware | TBD |
| Gateway IP/port | TBD |
| Test block(s) | TBD |
| Test module addresses | TBD |
| MQTT broker/version | TBD |
| WMS build/contract version | TBD |
| Operator / safety approver | TBD |
| Start/end UTC | TBD |

Không dùng block `00` cho acceptance nếu test rig có nhiều block/module ngoài
phạm vi.

## 5. Ma trận acceptance trọng yếu

| ID | Kịch bản | Kỳ vọng bắt buộc |
|---|---|---|
| ACC-001 | Apply full state cho một block | Đúng address/display/color; có ACK/outcome; không đèn ngoài block đổi |
| ACC-002 | Confirm từng module | Mỗi press tạo raw event đúng một semantic occurrence sau dedupe; P2L không tự hoàn tất session |
| ACC-003 | Confirm một phần rồi mất điện/restart | P2L yêu cầu sync; WMS gửi full remaining state; đèn pending không bị mất do local cache |
| ACC-004 | Hai block active đồng thời | Update block A không đổi bất kỳ module nào của block B |
| ACC-005 | Duplicate QoS 1 command | Cùng `commandId` không tạo side effect lần hai; outcome nhất quán |
| ACC-006 | Revision cũ đến sau revision mới | P2L từ chối stale revision, giữ desired state mới |
| ACC-007 | MQTT mất rồi phục hồi | Event buffer/replay có thứ tự xác định; consumer dedupe; không mất event trong giới hạn công bố |
| ACC-008 | Gateway NACK/ACK timeout | Có `command_failed` machine-readable; không trả success giả; WMS reconciliation |
| ACC-009 | P2L chết giữa send và ACK | Outcome `unknown`/reconcile; không tự suy luận success |
| ACC-010 | Hai bridge cùng client/gateway | Instance thứ hai bị ngăn hoặc alert; không có takeover loop |
| ACC-011 | Interrupt `t` chứa nhiều record | Tất cả record hợp lệ được parse/phát, không chỉ record đầu |
| ACC-012 | Graceful service stop/start | Không tự `D00`; start lại đi qua sync handshake |
| ACC-013 | Emergency off có quyền | Chỉ scope được authorize tắt; audit đầy đủ |
| ACC-014 | Dashboard từ client không xác thực | Bị từ chối; endpoint control/admin không lộ |
| ACC-015 | Package trên máy sạch | Đọc đúng `p2l_config.json`; binary/client/native SQLite/service entry point hoạt động |

## 6. Kịch bản chi tiết ACC-003 — mất điện sau confirm một phần

Điều kiện: block test có ít nhất ba module A/B/C; WMS lưu desired state và pending
authoritative.

1. WMS gửi full state A/B/C ở revision N.
2. Xác nhận cả ba đèn đúng; confirm A.
3. Xác nhận WMS còn B/C pending.
4. Cắt power/process P2L theo test plan; không gửi cleanup `D00`.
5. Khởi động lại gateway/P2L theo ma trận power-loss.
6. Xác nhận P2L không tự dùng registry/retained cache để relight.
7. WMS nhận sync-required và gửi full desired state B/C ở revision N+1.
8. Xác nhận B/C sáng đúng, A không sáng lại, block khác không đổi.
9. Confirm B/C; WMS tự kết luận session hoàn tất.
10. Lưu TX/RX, MQTT event/outcome, WMS state và video/ảnh theo policy.

Pass chỉ khi không có khoảng nào mà một delta/snapshot stale làm mất đèn B/C.

## 7. Evidence và quy tắc pass/fail

Mỗi test case phải có:

- test ID, revision của test plan và người chạy;
- source commit, artifact hash, config hash đã khử secret;
- exact topology/gateway/block/address;
- log UTC của WMS, broker, P2L TX/RX và quan sát physical;
- expected/actual, pass/fail/blocked;
- link defect cho mọi sai lệch;
- cleanup state và operator xác nhận.

`Skipped`, dependency unavailable, gateway unavailable hoặc chỉ compile pass được
ghi là **Not verified**, không ghi Pass.

## 8. Điều kiện production acceptance

- [ ] Tất cả P0 đóng và retest pass.
- [ ] P1 có fix hoặc risk acceptance bằng văn bản với owner/date.
- [ ] ACC-001 đến ACC-015 pass trên release candidate cuối.
- [ ] Không test nào bị skip trong phạm vi bắt buộc.
- [ ] Security, WMS, P2L, hardware và operations ký nhận.
- [ ] Runbook recovery/rollback được diễn tập bởi người trực thực tế.
- [ ] Release record, test evidence và artifact được lưu ở hệ thống được chỉ định.

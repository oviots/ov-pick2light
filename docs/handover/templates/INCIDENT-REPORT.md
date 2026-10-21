# Biên bản sự cố Pick2Light

> Không chèn password, token, private key hoặc config secret. Dùng secret/incident
> reference ID nếu cần truy vết.

## 1. Tóm tắt

| Trường | Giá trị |
|---|---|
| Incident ID | TBD |
| Tiêu đề | TBD |
| Severity | TBD |
| Site/kho | TBD |
| Bắt đầu UTC/local | TBD |
| Phát hiện UTC/local | TBD |
| Khôi phục UTC/local | TBD |
| Incident commander | TBD |
| Trạng thái | Open / Mitigated / Resolved |

Mô tả một đoạn: điều gì xảy ra, tác động tới picking/đèn nào, hiện đã an toàn hay
chưa.

## 2. Phạm vi kỹ thuật

| Thuộc tính | Giá trị |
|---|---|
| P2L release/commit | TBD |
| Binary SHA-256 | TBD |
| P2L instance/client ID | TBD |
| WMS release/session reference | TBD |
| Gateway ID/model/firmware | TBD |
| Gateway IP:port | TBD |
| Block/module addresses | TBD |
| MQTT broker/version | TBD |
| Power/network scope | TBD |

## 3. Tác động

- Picking/session bị ảnh hưởng:
- Số module/operator/order ước tính:
- Có mis-pick/safety impact không:
- Dữ liệu/event bị mất, lặp hoặc trễ:
- Thời gian gián đoạn:

## 4. Timeline

| UTC | Local time | Actor/system | Sự kiện/evidence | Quyết định |
|---|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD |

Phân biệt thời điểm event xảy ra với thời điểm log/alert được nhận.

## 5. Desired và observed state

| Block | WMS revision | WMS desired state ref | P2L observed ref | Physical observation | Sai lệch |
|---|---:|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD | TBD |

Không copy toàn bộ dữ liệu nhạy cảm nếu có thể dùng immutable evidence link.

## 6. Phân tích TX/RX và lệnh tắt

| UTC | Source command ID | P2L seq | Block/address | Aioi command class | ACK/NACK/timeout | Tác động |
|---|---|---:|---|---|---|---|
| TBD | TBD | TBD | TBD | D / D00 / P1 / P5 / Z / other | TBD | TBD |

Trả lời rõ:

- Có `D{address}` không? Nguồn gọi nào?
- Có terminate block/`D00` không? Ai authorize?
- Có P1/P5 thiếu module so với WMS desired state không?
- Có mixed-block list không?
- Confirm handler chỉ ACK/event hay workflow kế tiếp đã phát off/overwrite?
- Có duplicate bridge, `SessionTakenOver` hoặc event replay không?

## 7. Power/restart recovery

- Component nào mất power/restart:
- Registry/process state trước và sau restart:
- WMS có vào recovery mode không:
- P2L có phát/được coi là sync-required không:
- Full desired state per block được gửi lúc nào:
- Delta command nào đến trước resync không:
- Cross-block state được kiểm tra thế nào:

## 8. Mitigation và recovery

| Hành động | Người approve | Người thực hiện | UTC | Kết quả/evidence |
|---|---|---|---|---|
| TBD | TBD | TBD | TBD | TBD |

Nếu dùng emergency off, ghi rõ scope và lý do không dùng phạm vi nhỏ hơn.

## 9. Nguyên nhân

- Trigger trực tiếp:
- Root cause kỹ thuật:
- Yếu tố góp phần:
- Detection/control nào đã thất bại:
- Vì sao pre-release test không phát hiện:

Không kết luận chỉ từ UI `status: sent`; phải đối chiếu broker, P2L TX/RX,
gateway ACK/NACK và WMS state.

## 10. Corrective actions

| Action ID | Mô tả | Mức | Owner | Due date | Exit criteria | Trạng thái |
|---|---|:---:|---|---|---|---|
| TBD | TBD | P0/P1/P2 | TBD | TBD | TBD | Open |

## 11. Evidence index

- Sanitized logs:
- Broker trace:
- WMS state/revision:
- Config diff đã khử secret:
- Packet capture:
- Ảnh/video physical state:
- Test/reproduction:
- Related PR/release/incident:

Ghi retention, access classification và hash cho evidence quan trọng.

## 12. Phê duyệt đóng sự cố

| Vai trò | Họ tên | Kết luận | Ngày | Approval link/signature |
|---|---|---|---|---|
| Incident commander | TBD | TBD | TBD | TBD |
| WMS owner | TBD | TBD | TBD | TBD |
| P2L owner | TBD | TBD | TBD | TBD |
| Operations | TBD | TBD | TBD | TBD |
| Security/Safety nếu áp dụng | TBD | TBD | TBD | TBD |

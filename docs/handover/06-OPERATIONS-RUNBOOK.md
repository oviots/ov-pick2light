# 06 — Runbook vận hành

## 1. Nguyên tắc an toàn

- Daemon production chỉ chạy `p2l serve`.
- Mỗi gateway chỉ có một bridge active và một MQTT client ID duy nhất.
- Không dùng `p2l`, `p2l run`, `batch_finish` hoặc `terminate_all` như health check.
- Không phát `D00`, `Z`, `Z1`, P1/P5 trên thiết bị thật nếu chưa có phạm vi và
  operator approval.
- Observed registry/retained event không phải session state.
- Sau restart, WMS phải resync full desired state theo từng block.

## 2. Pre-start checklist

- [ ] Có change/incident ticket và operator trực tại khu vực ảnh hưởng.
- [ ] Xác nhận site, gateway IP/port, gateway ID và block được phép thao tác.
- [ ] WMS đã pause delta command hoặc sẵn sàng recovery handshake.
- [ ] Không có process/host khác đang dùng cùng gateway/client ID.
- [ ] Binary hash trùng release record.
- [ ] File đúng tên `config/p2l_config.json` và service account đọc được.
- [ ] Secret/TLS được inject an toàn; certificate validation bật.
- [ ] Dashboard chỉ bind endpoint được phê duyệt.
- [ ] Log/buffer/DB directory có dung lượng và permission đúng.

## 3. Start

Chạy foreground khi commissioning:

```bash
./p2l serve
```

Sau khi unit Linux/macOS đã được sửa và nghiệm thu mới dùng service manager. Unit
tracked tại baseline còn chạy CSV workflow và chưa an toàn để enable.

Windows MSI service dùng `serve`, nhưng vẫn phải đối chiếu binary path, arguments,
account và environment trước start.

## 4. Health check không thay đổi trạng thái đèn

Kiểm tra theo thứ tự:

1. Process/service còn chạy và không restart loop.
2. Log có `P2L service running`.
3. TCP gateway connected; không có chuỗi ACK timeout/NACK liên tục.
4. MQTT status online và `gatewayConnected` đúng.
5. Client ID không bị `SessionTakenOver`.
6. Offline buffer không tăng liên tục và disk còn dung lượng.
7. WMS consumer nhận được status/event test không tác động hardware.

Heartbeat hiện dùng query `Az0000` đọc trạng thái, không phải lệnh `Z`. Không dùng
tài liệu protocol cũ để thay thế xác minh source/version đang chạy.

## 5. Stop có kiểm soát

1. Pause upstream command producer.
2. Ghi nhận command đang in-flight và desired state của WMS.
3. Gửi SIGTERM/Service Stop và chờ process kết thúc trong timeout.
4. Xác nhận MQTT offline/connection close và event buffer được giữ.
5. Không mặc định gửi `D00`; trạng thái physical cần quyết định của operator/WMS.
6. Khi start lại, thực hiện recovery handshake ở mục 7.

`serve` không gửi terminate-all trong normal shutdown. Interactive root command
có gửi terminate mặc định khi shutdown/error, vì vậy không dùng interactive mode
cho service.

## 6. Phân loại lệnh khi điều tra sự cố

| Dấu hiệu TX/log | Ý nghĩa | Blast radius |
|---|---|---|
| `D` + address | Tắt một module | Thấp nhưng vẫn là write |
| terminate block / `D00` | Tắt block/all | Rất cao |
| `P1`/`FP1` | Apply instruction list | Full block; item bị thiếu có thể tắt |
| `P5`/`FP5` | Overwrite instruction list | Full block; item bị thiếu có thể tắt |
| `Z` | Full initialization | Thay đổi gateway state |
| `Z1` | Hardware reset | Cao, cần settling time |
| `Az0000` | Status inquiry | Read-only theo implementation hiện tại |
| `o` | ACK interrupt | Protocol response, không phải off command |

Luôn lưu timestamp, sequence number, gateway, block, source command/correlation ID
và instance/client ID. Không chỉ nhìn event UI.

## 7. Recovery sau mất điện/restart

### 7.1 Mục tiêu

Khôi phục theo ý định mới nhất của WMS mà không tắt nhầm đèn còn pending và không
đụng block khác.

### 7.2 Quy trình

1. Khoanh vùng: power mất ở host P2L, gateway, broker hay toàn site.
2. Giữ upstream ở recovery mode; chưa gửi delta `light_on`.
3. Start đúng một instance bằng `p2l serve`.
4. Xác minh TCP/MQTT và client identity; kiểm tra duplicate takeover.
5. P2L/WMS đánh dấu `syncRequired` cho boot instance mới. Source v1 chưa có event
   này nên phải thực hiện bằng orchestration tạm thời có kiểm soát.
6. WMS đọc session authoritative của chính nó.
7. Với mỗi block active, WMS gửi **toàn bộ** desired light list của riêng block
   đó; không gửi danh sách tổng hợp nhiều block.
8. Chờ gateway outcome/ACK. V1 chưa phát outcome đầy đủ, nên cần đối chiếu log
   TX/RX và quan sát trên test/canary dưới operator control.
9. Chỉ sau khi block đã reconciled mới mở lại delta command.
10. Ghi kết quả vào incident/recovery record.

### 7.3 Không được làm

- Không dựa vào local `Operating` registry để đoán đèn còn lại.
- Không coi retained `p2l/devices/#` là desired state.
- Không gửi một `light_on` đơn lẻ nếu implementation phải tái dựng P1/P5 từ cache.
- Không gửi `terminate_all` để “làm sạch” trừ emergency được authorize.
- Không bật state persistence rồi coi vấn đề đã được giải quyết.

## 8. Duplicate bridge / MQTT `SessionTakenOver`

1. Pause commands.
2. Liệt kê process/host/service cùng client ID; không start thêm instance.
3. Chọn một owner duy nhất của gateway.
4. Stop instance dư bằng lifecycle bình thường.
5. Cấp client ID unique theo site/gateway/instance role.
6. Reconnect, kiểm tra status ổn định, rồi thực hiện WMS resync.

## 9. Gateway ACK timeout/NACK

1. Không retry vô hạn một command có tác động full-block.
2. Ghi command ID, block, sequence, TX/RX và thời điểm.
3. Xác minh network/gateway connection và command scope.
4. Nếu trạng thái apply không chắc chắn, coi là **unknown**, không coi failed hay
   success chỉ từ HTTP `sent`.
5. Đưa WMS vào reconciliation; full-state apply theo revision sau khi gateway ổn.
6. Escalate hardware owner nếu NACK/timeout lặp.

## 10. MQTT outage/offline buffer

- Bridge vẫn có thể nhận interrupt từ gateway nhưng event publish bị buffer.
- Theo dõi file size/disk; channel in-memory có giới hạn và chế độ drop-oldest.
- Khi reconnect, buffer được replay trước live events; consumer phải dedupe.
- Không xóa buffer khi chưa lưu evidence và xác định consumer đã reconcile.
- Event replay không khôi phục session ownership cho P2L.

## 11. Emergency off

Chỉ dùng khi safety/operations policy yêu cầu:

1. Xác nhận người ra lệnh, site, gateway, block và lý do.
2. Ưu tiên address/block cụ thể; `00` là lựa chọn cuối cùng.
3. Ghi audit trước/sau nếu thời gian cho phép.
4. Phát lệnh một lần, xác minh outcome/physical state.
5. WMS đánh dấu session cần reconciliation; không tiếp tục từ state cũ.

## 12. Evidence cần thu thập khi incident

- UTC và local timestamp, site/gateway/block/module.
- Release version, commit, binary SHA-256, instance/client ID.
- Power/network/MQTT timeline.
- Sanitized config diff, không có secret.
- Log TX/RX trước và sau sự kiện; command/event/correlation IDs.
- WMS desired state và revision tương ứng.
- Ảnh/video physical state nếu policy cho phép.
- Tác động, workaround, người approve và tiêu chí khôi phục.

Dùng [mẫu biên bản sự cố](templates/INCIDENT-REPORT.md).

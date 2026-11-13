# Confirm qua MQTT: ACK sau khi lưu signal

1. P2L nhận Confirm có `pickingDetailId`, ghi SQLite (`synchronous=FULL`) rồi mới ACK Controller.
2. Worker đọc SQLite, gửi event MQTT với `eventId` cố định. Event mới đánh thức worker ngay; retry giữ nguyên payload/ID.
3. WMS nhận MQTT và ghi `P2L.Signals`. Vòng PickSession đọc Confirm của các session `InProgress`; `applyReceipts` ghi trạng thái đèn xong thì gửi `confirm_ack`, `outcome: "stored"`. Đủ Confirm thì chuyển session sang `Confirmed` và nhả block, không chờ PUBACK của ACK.
4. P2L chỉ xóa SQLite khi ACK khớp event/block/session/address. PUBACK của broker không xóa SQLite.
5. Worker tồn chỉ xử lý `LightConfirm` thuộc session `Confirmed`. Xử lý hết detail thì chuyển session sang `Completed`. ACK không đợi xử lý tồn; Confirm API dùng phản hồi API, không phát ACK MQTT.

`stored` nghĩa là WMS đã tiếp nhận bền vững, không có nghĩa tồn kho đã cập nhật thành công. Sau ACK, việc xử lý/đối soát thuộc WMS.

## Gửi lại

- Mất MQTT, lỗi ghi DB hoặc mất ACK: P2L giữ SQLite và gửi lại.
- Khi mất ACK, P2L gửi lại event với cùng ID. Nhánh MQTT nhận duplicate key của signal đã lưu sẽ trả ACK lại, kể cả khi session đã hoàn tất. Không thêm truy vấn session/detail hoặc kiểm tra trạng thái tồn.
- ACK được gửi trên topic command với QoS 1, không retained, độc lập với hàng đợi lệnh bật đèn và không bị hủy khi nhả session.
- Không tìm thấy signal thì không ACK, không xóa SQLite. Không tự tạo/khôi phục PickSession.
- ACK replay có cờ retained bị bỏ qua.

Receipt đang chờ không khóa toàn block. Các kiểm tra sở hữu đèn/session và block được cấp phép giữ nguyên. Lệnh bật lại chính item còn receipt bị bỏ qua để tránh relight do retry.

## Kiểm tra và cập nhật

```sh
./p2l confirms
```

SQLite nằm tại `LocalApplicationData/P2L/p2l_confirms.db`, ổn định giữa các thư mục binary. Ctrl+C không xóa receipt chưa nhận ACK.

P2L mới nhận cả `stored` và `applied` (tương thích ACK trước đây). Khi triển khai ACK `stored`, cập nhật P2L r14 trước rồi cập nhật WMS. Không chạy hai binary cùng Controller/database.

API không thay đổi trong lần sửa ACK MQTT này. Cả API và MQTT đều xác nhận sau khi lưu `P2L.Signals`; xem [confirm-api.md](confirm-api.md) cho cấu hình API.

## Kiểm thử local

```sh
# Trong repo WMS:
node --test test/pick2light/confirmAck.test.js test/pick2light/signalRetry.test.js test/pick2light/multiBlockRouting.test.js
# Trong repo P2L:
dotnet run --project tests/P2L.ConfirmationTests -p:PublishAot=false -p:SelfContained=false
```

Test WMS bao gồm: ghi trạng thái Confirm trước ACK; không chờ xử lý tồn hoặc PUBACK để nhả block; gửi lại event đã lưu; lỗi ghi DB; ACK timeout/lỗi publish; retained/legacy events và Confirm API không phát ACK MQTT.

Node tests dùng DB/MQTT giả. Bộ test P2L dùng SQLite thật, Controller giả và broker loopback. Việc deploy/test UAT hoặc Controller thật được xác nhận riêng.

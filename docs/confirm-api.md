# Confirm qua API (tùy chọn)

API gateway: `/Volumes/GTeck/Projects/1shot/workspaces/mml/rocket-wms-api`.
Endpoint: `POST /api/v1/p2l/confirm/events`, Bearer token. Được mount trước middleware OAuth của web; chỉ endpoint này dùng token riêng.

## Cấu hình

Gateway đặt biến môi trường `P2L_CONFIRM_API_TOKEN` thành token bí mật, cùng giá trị quản trị nhập vào authen. Authen không cần thêm biến môi trường; dùng khóa mã hóa P2L hiện có.

Trong màn hình cấu hình license trên authen, bật **Gửi Confirm qua API**, nhập URL HTTPS và token tối thiểu 32 ký tự. JSON tương ứng:

```json
{
  "confirmApi": {
    "enabled": true,
    "url": "https://<gateway>/api/v1/p2l/confirm/events",
    "token": "<cùng giá trị P2L_CONFIRM_API_TOKEN>",
    "pollIntervalMs": 1000,
    "batchSize": 50
  }
}
```

Token được mã hóa trong `P2LConfigurations`, chỉ trả plaintext cho native bridge đã xác thực. Danh sách cấu hình/admin và web client không trả token. Để trống token khi sửa để giữ giá trị cũ. Tắt API để dùng MQTT như trước. Lưu cấu hình rồi khởi động lại P2L để đổi kênh; mỗi lần chỉ có một kênh gửi Confirm.

MQTT vẫn nhận lệnh và gửi trạng thái thông thường. API worker gửi Confirm được cả khi MQTT đang offline. Đây không phải cơ chế nhận công việc mới khi toàn bộ MQTT mất.

## Hợp đồng tiếp nhận

```json
{"events":[{"type":"confirmed","confirmProtocol":1,"eventId":"...","blockNo":"01","correlationId":"...","address":"0001","pickingDetailId":"...","adjustedDisplay":"00002"}]}
```

Gateway giữ các field payload bổ sung trong `Data`, chuẩn hóa các field schema và ghi `P2L.Signals`; không đọc `P2LPickingSessions`/`P2LPickingDetails`, không xử lý tồn trong HTTP request. Field thiếu để `""` hoặc `[]` tùy kiểu. `Key` lấy từ `Key` có sẵn hoặc `eventId`; collection unique index hiện có đảm nhiệm chống trùng. Không thêm collection.

**Metadata nghiệp vụ chưa được bổ sung:** payload P2L hiện chủ yếu có ID/session/address/số lượng. Các field như client/warehouse/store, `addressToItem` để trống khi nguồn không gửi. Phần mapping/enrich này chờ hướng dẫn tiếp; chưa thể coi luồng tồn kho đầy đủ là đã sẵn sàng với payload tối giản. Worker hiện có giữ signal lỗi và không cập nhật tồn khi thiếu item.

Response:

```json
{"storedEventIds":["..."],"errors":[]}
```

- Insert hoàn tất với write concern majority mới xác nhận event đã được nhận.
- Duplicate key (event đã lưu) vẫn trả event ID để P2L xóa receipt SQLite.
- P2L chỉ xóa những ID thuộc batch đang gửi và có trong `storedEventIds`.
- Timeout, HTTP lỗi, response thiếu/sai hoặc event không được xác nhận: giữ SQLite, retry cùng ID. Partial batch chỉ xóa phần đã nhận.
- Response xác nhận **đã lưu signal**, không xác nhận đã commit tồn kho. Sau hand-off, WMS chịu trách nhiệm xử lý và giữ record lỗi.

## Phạm vi WMS

API chỉ tiếp nhận và lưu signal. Worker WMS giữ luồng xử lý hiện có; không bổ sung cơ chế đồng bộ/retry queue picking trong thay đổi API này. Metadata nghiệp vụ và bước xử lý tiếp theo chờ hướng dẫn.

Triển khai gateway + authen trước, giữ API tắt; cập nhật P2L rồi bật API sau khi phần metadata nghiệp vụ được hoàn thiện và test UAT. Không thay đổi `.env` hoặc deploy các dịch vụ trong công việc local này.

## Kiểm thử

- Gateway: `node --test test/p2l/confirmIngest.test.js` (mock Mongo, kiểm tra không có lookup).
- Authen: `node --test server/test/p2lLicense.test.js server/test/p2lCache.test.js` (config/token và Redis loopback).
- WMS: `node --test test/pick2light/confirmAck.test.js test/pick2light/signalRetry.test.js test/pick2light/multiBlockRouting.test.js` (mock Mongo/transactions/runtime).
- P2L: `dotnet run --project tests/P2L.ApiConfirmationTests -p:PublishAot=false -p:SelfContained=false` (SQLite thật + HTTP loopback).

Các test local bao gồm event đã lưu nhưng mất response, partial batch, token sai, response sai ID, API hoạt động khi MQTT offline, khởi động lại P2L và hủy HTTP khi shutdown. Chưa test gateway với MongoDB/UAT hoặc Controller thật.

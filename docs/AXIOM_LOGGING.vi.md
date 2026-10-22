# Gửi log P2L lên Axiom

Axiom phục vụ chẩn đoán từ xa bằng các log ứng dụng và TX/RX như audit local, mặc định **tắt**. File audit xoay vòng tại máy vẫn giữ nguyên. Gửi HTTP chạy trong worker riêng; luồng ghi log không chờ mạng hoặc chờ queue trống.

## Cấu hình

Dataset: `p2l-logs`, endpoint:

```text
https://us-east-1.aws.edge.axiom.co/v1/ingest/p2l-logs
```

Tạo **API token** chỉ có quyền **Ingest** vào dataset `p2l-logs`. Endpoint này không hỗ trợ personal access token (PAT), theo [tài liệu ingest chính thức của Axiom](https://axiom.co/docs/restapi/endpoints/ingestToDataset).

Ví dụ ghép các section sau vào `config/p2l_config.json` cạnh executable; giữ các cấu hình MQTT hiện có:

```json
{
  "Mqtt": {
    "ClientId": "p2l-warehouse-bridge-01"
  },
  "Axiom": {
    "Enabled": false,
    "Endpoint": "https://us-east-1.aws.edge.axiom.co/v1/ingest/p2l-logs",
    "MinimumLevel": "Debug",
    "RequestTimeoutMs": 3000
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "P2L.Hardware": "Debug",
      "P2L.Mqtt": "Debug"
    }
  }
}
```

Đặt biến môi trường cho process/service chạy P2L, rồi khởi động lại process đó:

```bash
export P2L_Axiom__Enabled=true
export P2L_Axiom__MinimumLevel=Debug
export P2L_Axiom__ApiToken='<API_TOKEN_INGEST_P2L_LOGS>'
```

Giá trị token trên chỉ là placeholder; cấp token thật qua cấu hình môi trường của service, không lưu vào repository. P2L đọc `config/p2l_config.json` cạnh executable trước, rồi áp dụng biến môi trường có prefix `P2L_`; dấu `__` biểu diễn cấu hình lồng nhau và biến môi trường ghi đè giá trị JSON.

Các giá trị mặc định của section `Axiom`:

| Tham số | Mặc định | Ý nghĩa |
|---|---:|---|
| `Enabled` | `false` | Bật/tắt gửi log cloud |
| `MinimumLevel` | `Debug` | Ngưỡng bổ sung sau bộ lọc `Logging:LogLevel` dùng chung với local |
| `QueueCapacity` | `1024` | Số log tối đa đang chờ trong RAM |
| `BatchSize` | `100` | Số log tối đa mỗi request |
| `FlushIntervalMs` | `2000` | Thời gian gom batch |
| `RequestTimeoutMs` | `3000` | Giới hạn thời gian một request, gồm đọc ingest receipt |
| `ShutdownTimeoutMs` | `1000` | Thời gian chờ worker xả queue khi dừng, trước khi hủy phần còn lại |

Với cấu hình trên, log chung từ `Information` và log `P2L.Hardware`/`P2L.Mqtt` từ `Debug` được gửi lên cloud. `Axiom:MinimumLevel=Information` vẫn loại toàn bộ `Debug`, kể cả TX/RX. **Cấu hình cũ đã đặt `Information` cần đổi thành `Debug`**; chỉ nâng binary không ghi đè giá trị đã cấu hình. Ví dụ đổi timeout: `P2L_Axiom__RequestTimeoutMs=3000`.

## Tìm đúng Bridge và lần chạy

| Trường cloud | Nguồn và cách dùng |
|---|---|
| `client_id` | `Mqtt:ClientId`, mặc định `p2l-bridge`. Đặt ID ổn định, riêng cho mỗi Bridge. Backup có MQTT client ID riêng nhưng log vẫn dùng identity primary này. |
| `run_id` | GUID mới khi provider khởi tạo, thông thường mỗi lần chạy process. Phân biệt các lần restart của cùng Bridge; reconnect/failover MQTT không đổi giá trị này. |
| `process_id` | PID của process đang ghi log |
| `instance` | Tên máy, hoặc `Axiom:InstanceName` |
| `level`, `category`, `message` | Mức log, component phát log và nội dung |
| `exception`, `properties` | Exception nếu có và các trường log có cấu trúc |

Trong Axiom, mở dataset `p2l-logs`, chọn khoảng thời gian, lọc `client_id` bằng MQTT ClientId primary của Bridge cần kiểm tra. Từ một event, lấy `run_id` rồi thêm bộ lọc bằng giá trị đó để xem cùng một lần chạy; có thể lọc tiếp `category`/`level`. Không dùng một block chung làm identity vì một Bridge có thể phục vụ nhiều block.

Mỗi log còn có `_time`, `event_id`, `event_code`, `service`, `environment`. Các nhãn `Service`/`Environment` mặc định là `p2l-bridge`/`local`. Chỉ **log mới sau khi bật** được gửi; không đọc hoặc backfill file log cũ.

## Khi Axiom chậm hoặc không nhận log

- Payload cloud che các giá trị bí mật đã biết và giới hạn độ dài (`message`:4096 ký tự, `exception`:8192 ký tự; event tối đa 16KiB). File local vẫn giữ cơ chế ghi hiện tại.
- Queue có giới hạn, không ghi spool offline xuống đĩa. Queue đầy thì **bỏ log mới đến**, không chờ và không loại log cũ để nhường chỗ.
- Request lỗi, timeout hoặc ingest receipt không hợp lệ thì **bỏ batch, không retry**. Nếu receipt hợp lệ báo chỉ nhận một phần, đếm phần đã nhận và bỏ phần thất bại, cũng không retry. Batch tiếp theo vẫn được xử lý.
- Log còn trong RAM có thể mất khi process crash hoặc hết thời gian shutdown. Đây là kênh **best effort**, không thay thế audit local hay MQTT business outbox; không bảo đảm cloud có đầy đủ, giống hệt file local khi mạng lỗi, queue đầy hoặc process dừng.
- Provider có bộ đếm `SentCount`/`DroppedCount`; lỗi xuất thông báo tổng quát cùng số đếm ra `stderr`, có giới hạn tần suất. Đây là bộ đếm trong process, chưa có endpoint metrics riêng. Nếu bật nhưng thiếu token/endpoint hợp lệ, provider báo `stderr` và không bật gửi cloud.

## Kết quả kiểm tra ngày 22/10/2026

- P2L Bridge Native AOT macOS ARM64 bản cập nhật build thành công. Không khởi chạy Bridge trên controller thật.

- Unit tests bản cập nhật: **240/240 pass**, gồm **18 case Axiom**. Kiểm tra ClientId primary/default, định danh lần chạy/PID, cùng bộ lọc với file log, exception/structured fields và các ca timeout/drop/shutdown. Sau khi chỉnh cú pháp assertion cho hết cảnh báo analyzer, 18 case Axiom được chạy lại và pass. HTTP trong unit test được giả lập.
- Retest thật `20261022T040009Z`: endpoint chấp nhận **1 log**, `sent=1`, `dropped=0`, kết thúc sau **2938ms**. Dùng token người dùng cấp qua chương trình Native AOT biên dịch cùng source provider/options và đăng ký DI; chỉ tạo log synthetic, không gọi thiết bị.
- Lượt đầu `p2l-axiom-smoke-20261022T033153Z` timeout sau 3004ms, `sent=0`/`dropped=1`, không retry; giữ bằng chứng này riêng. Retest thành công xác nhận ingest tại thời điểm kiểm tra, không bảo đảm mạng hoặc mọi batch luôn thành công.
- Token không được ghi vào source/config. Không gọi production, database, MQTT hoặc controller trong đợt kiểm tra logging này.

- Lượt xác minh identity/Debug thật `p2l-axiom-clientid-20261022T040951Z`: Axiom chấp nhận **2 log** (`Debug` TX/RX giả lập và `Error` có stack trace), `sent=2`, `dropped=0`, **2066ms**, timeout 3000ms/không retry. Cả hai dùng `client_id=p2l-axiom-clientid-test`, chương trình Native AOT cô lập, không gọi thiết bị.

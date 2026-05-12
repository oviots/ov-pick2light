# Hướng dẫn Sử dụng Pick2Light

Tài liệu này hướng dẫn cách cấu hình, chạy và vận hành Pick2Light hằng ngày.

---

## 1. Bạn cần chạy gì

Thông thường P2L gồm 2 service:

- **P2L bridge**: giao tiếp với Aioi gateway và hardware
- **P2L client**: host web dashboard và UI cho operator

Trong nhiều mô hình triển khai, cả hai chạy trên cùng một máy.

---

## 2. Trước khi bắt đầu

Hãy bảo đảm bạn có:

- kết nối mạng đến Aioi gateway
- thông tin broker nếu bật MQTT integration
- bộ binary / package đã build
- quyền ghi vào thư mục config và log

Giá trị gateway mặc định thường dùng trong dự án:

- host: `192.168.1.254`
- port: `5003`

---

## 3. File cấu hình

Các file cấu hình thường gặp:

- bridge config: `appsettings.json`
- client config: `config/p2l_client_config.json`
- bản config bridge local/default: `config/p2l_config.json`

Installer trong repo này đã được thiết kế để tự chép file config mặc định.

Các nhóm setting quan trọng:

- host/port của gateway
- bật/tắt MQTT
- host, port, username, password, TLS của MQTT
- tên các topic commands, events, status, devices
- log directory và self-heal settings
- `StartMinimizedOnWindows`: khi `true`, mở `p2l.exe` không kèm subcommand sẽ tự vào trạng thái minimize trên Windows

---

## 4. Chạy bridge

### Môi trường phát triển

Từ root của repository:

```bash
~/.dotnet/dotnet run --project src/P2L.CLI
```

Bạn cũng có thể dùng các CLI command trong `README.md`.

### Môi trường production

Chạy binary đã đóng gói hoặc cài thành service:

- Linux: `systemd`
- Windows: Windows Service
- macOS: local binary hoặc service wrapper tùy cách triển khai

Nên khởi động bridge trước hoặc cùng lúc với client.

---

## 5. Chạy client

### Môi trường phát triển

Chạy theo startup path chuẩn trong thư mục `client/`.

Nếu đã có packaged client, nên dùng bản đó thay vì dựng môi trường Python thủ công.

### Môi trường production

Chạy service/binary đã được cài từ package, sau đó mở trình duyệt theo host/port được cấu hình.

---

## 6. Trình tự bring-up lần đầu

Thứ tự khuyến nghị:

1. kiểm tra gateway reachable trên mạng
2. khởi động P2L bridge
3. xác nhận bridge đã kết nối MQTT nếu có bật
4. khởi động P2L client
5. mở web UI
6. kiểm tra trạng thái service trong dashboard
7. scan devices hoặc nạp dữ liệu cấu hình đã biết

Nếu tắt MQTT, chỉ dùng luồng bridge/CLI trực tiếp.

---

## 7. Sử dụng web UI

### Dashboard

Dùng để theo dõi:

- trạng thái bridge service
- trạng thái kết nối MQTT
- tình trạng gateway
- các thay đổi trạng thái thiết bị
- batch / picking activity

### Settings -> P2L/Client Settings

Dùng để cấu hình hành vi client và thông số kết nối.

### Settings -> P2L/Bridge Settings

Dùng để:

- chỉnh cấu hình bridge
- start / stop P2L service khi được hỗ trợ
- kiểm tra bridge status

### Settings -> Scan Devices

Tab này xuất hiện khi client đã connect.

Cách dùng:

1. nhập **From address**
2. nhập **To address**
3. bấm **Start Scan**
4. chờ quá trình scan hoàn tất
5. xem danh sách thiết bị ở panel kết quả

Hành vi:

- nút scan bị disable trong lúc scan
- kết quả hiển thị realtime ở panel bên phải
- khi scan xong, nút được enable lại

---

## 8. MQTT cho tích hợp

MQTT là đường tích hợp chính cho web UI và hệ thống bên ngoài.

### Topic mặc định

| Vai trò | Topic mặc định | Retain |
|---|---|---|
| Command vào bridge | `p2l/commands` | No |
| Event nghiệp vụ | `p2l/events` | No |
| Bridge status / LWT | `p2l/status` | Yes |
| State từng device | `p2l/devices/{address}` | Yes |

Lưu ý:

- bridge subscribe vào `p2l/commands`
- event `light_changed` không phát trên `p2l/events`, mà phát theo từng topic `p2l/devices/{address}`
- startup hiện **không xóa retained messages**

### Mẫu command payload

#### Bật một module

Topic: `p2l/commands`

```json
{
  "type": "light_on",
  "correlationId": "demo-001",
  "address": "0001",
  "quantity": 5,
  "color": "Green",
  "ledMode": "On",
  "buzMode": "Off",
  "label": "A-01"
}
```

#### Tắt một module

```json
{
  "type": "light_off",
  "correlationId": "demo-002",
  "address": "0001"
}
```

#### Bắt đầu batch

```json
{
  "type": "batch_start",
  "correlationId": "batch-1001",
  "color": "Green",
  "ledMode": "On",
  "buzMode": "Off",
  "items": [
    {
      "address": "0001",
      "quantity": 3,
      "label": "SKU-A1"
    },
    {
      "label": "SKU-B9",
      "quantity": 2,
      "color": "Red",
      "ledMode": "Flash"
    }
  ]
}
```

Ghi chú:

- mỗi item có thể gửi `address` trực tiếp
- hoặc chỉ gửi `label`, bridge sẽ resolve từ device database của client

#### Kết thúc batch hiện tại

```json
{
  "type": "batch_finish",
  "correlationId": "batch-stop-1"
}
```

#### Tắt toàn bộ đèn

```json
{
  "type": "terminate_all",
  "correlationId": "panic-1"
}
```

#### Yêu cầu snapshot state

```json
{
  "type": "get_snapshot",
  "correlationId": "snapshot-1"
}
```

Bridge sẽ trả về các `light_changed` event trên `p2l/devices/{address}` cho toàn bộ module đang được track. Nếu command có `correlationId`, giá trị này sẽ được echo lại trong `light_changed`, `confirmed` và `batch_completed`.

#### Chạy CSV

```json
{
  "type": "csv_run",
  "correlationId": "csv-1",
  "file": "tasks/picklist.csv",
  "color": "Green"
}
```

#### Điều khiển CSV runner

```json
{
  "type": "csv_control",
  "correlationId": "csv-ctl-1",
  "action": "pause"
}
```

`action` hợp lệ: `pause`, `resume`, `cancel`

#### Scan devices

```json
{
  "type": "scan_devices",
  "correlationId": "scan-1",
  "from": 1,
  "to": 99
}
```

### Mẫu event payload

#### Confirmation event

Topic: `p2l/events`

```json
{
  "type": "confirmed",
  "timestamp": "2026-03-19T07:00:00Z",
  "address": "0001",
  "status": "Normal",
  "adjustedQuantity": 2,
  "adjustedDisplay": "00002",
  "correlationId": "batch-1001",
  "displayData": "00002",
  "label": "SKU-A1"
}
```

#### Batch completed

```json
{
  "type": "batch_completed",
  "timestamp": "2026-03-19T07:05:00Z"
}
```

#### Task event

```json
{
  "type": "task_started",
  "timestamp": "2026-03-19T07:10:00Z",
  "address": "0001",
  "quantity": 4,
  "color": "Green",
  "label": "SKU-A1"
}
```

#### Bridge status

Topic: `p2l/status`

```json
{
  "type": "bridge_status",
  "timestamp": "2026-03-19T07:15:00Z",
  "online": true,
  "gatewayConnected": true
}
```

#### Device state

Topic: `p2l/devices/0001`

```json
{
  "type": "light_changed",
  "timestamp": "2026-03-19T07:16:00Z",
  "address": "0001",
  "moduleStatus": "Operating",
  "color": "Green",
  "ledMode": "On",
  "segMode": "On",
  "buzMode": "Off",
  "displayData": "00005",
  "blockNo": "00",
  "adjustedQuantity": 5,
  "lastInstructionAt": "2026-03-19T07:15:59Z",
  "lastConfirmedAt": null,
  "label": "A-01"
}
```

---

## 9. Các thao tác thường dùng

### Bật một module

Có thể dùng:

- CLI
- MQTT `light_on`
- action tương ứng từ web UI

### Chạy một picking batch

Có thể khởi chạy từ:

- CLI
- CSV workflow
- MQTT command từ hệ thống upstream

### Import / export label hoặc config

Dùng workflow CSV có sẵn để:

- map label
- map address
- chỉnh sửa hàng loạt

---

## 10. Hành vi session của trình duyệt

Client hiện chịu lỗi reload tốt hơn trước.

Hành vi hiện tại:

- reload trang không disconnect backend ngay
- MQTT / client connectivity được giữ qua lần reload
- nếu không còn browser session active, server sẽ disconnect sau 15 phút idle

---

## 11. Log và chẩn đoán

Khi có vấn đề, hãy kiểm tra:

- bridge logs cho lỗi gateway / MQTT
- client logs cho lỗi UI / API / service
- rotating log files trong thư mục log đã cấu hình

Những điểm cần xác minh:

- gateway host và port
- MQTT credentials và TLS files
- topic names
- trạng thái service
- bridge có nhìn thấy gateway là connected hay không

---

## 12. Troubleshooting nhanh

### UI không connect được

Kiểm tra:

- client service có đang chạy không
- host/port có đúng không
- browser có truy cập được FastAPI server không

### MQTT connect rồi nhưng command không chạy

Kiểm tra:

- command topic có khớp không
- bridge đã subscribe thành công chưa
- gateway có connected không
- payload JSON có đúng shape theo command type không

### Scan không thấy thiết bị

Kiểm tra:

- dải địa chỉ đúng chưa
- gateway có connected không
- thiết bị có đang hiện diện trên bus không

### Bridge chạy nhưng hardware không phản hồi

Kiểm tra:

- TCP reachability đến Aioi gateway
- block/controller settings
- dây RS-485 hoặc cấu hình phía gateway

---

## 13. Nên đọc tiếp

- `README.md`
- `docs/ARCHITECTURE.md` và `docs/ARCHITECTURE.vi.md`
- `BUILD.md`
- `WINDOWS_INSTALL.md`

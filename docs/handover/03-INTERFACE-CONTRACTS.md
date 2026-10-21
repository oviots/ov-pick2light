# 03 — Hợp đồng giao tiếp

## 1. Trạng thái tài liệu

Phần **v1 hiện tại** mô tả source tại baseline. Phần **vNext đề xuất** là contract
đích, chưa được implement và chưa được consumer chấp thuận.

JSON dùng `camelCase`; enum được serialize thành chuỗi. Không đưa secret hoặc
thông tin broker thật vào ví dụ.

## 2. MQTT v1 hiện tại

| Luồng | Topic mặc định | QoS | Retain |
|---|---|---:|:---:|
| Command vào P2L | `p2l/commands` | 1 | Consumer không nên publish retained |
| Event nghiệp vụ | `p2l/events` | 1 | Không |
| Observed device state | `p2l/devices/{address}` | 1 | Có |
| Bridge status/LWT | `p2l/status` | 1 | Có |

MQTT QoS 1 là at-least-once. Command có thể được giao lặp; không được giả định
exactly-once.

### 2.1 Command đang hỗ trợ

| `type` | Tác động | Phân loại bàn giao |
|---|---|---|
| `light_on` | Dựng lại danh sách P1/P5 từ registry rồi bật đèn | Hardware intent nhưng unsafe sau restart |
| `light_off` | Gửi lệnh `D` cho một address | Hardware intent |
| `batch_start` | Bật danh sách và tạo batch state | Legacy workflow |
| `batch_finish` | Gửi `D` theo block và xóa batch state | Legacy workflow / chủ động tắt |
| `terminate_all` | Gửi terminate block `00` | Emergency / blast radius lớn |
| `scan_devices` | Scan dải address | Maintenance |
| `get_snapshot` | Phát `light_changed` cho registry hiện có | Observed snapshot, không authoritative |
| `csv_run` | Chạy file CSV | Legacy workflow |
| `csv_control` | Pause/resume/cancel CSV | Legacy workflow |

Ví dụ đã khử thông tin môi trường:

```json
{
  "type": "light_on",
  "correlationId": "cmd-example-001",
  "address": "0001",
  "quantity": 5,
  "blockNo": "01",
  "color": "Green",
  "ledMode": "Flash",
  "buzMode": "Off",
  "label": "BIN-A-01"
}
```

```json
{
  "type": "batch_start",
  "correlationId": "cmd-example-002",
  "blockNo": "01",
  "items": [
    { "address": "0001", "quantity": 5, "color": "Green" },
    { "address": "0002", "quantity": 2, "color": "Red" }
  ]
}
```

```json
{ "type": "light_off", "correlationId": "cmd-example-003", "address": "0001" }
```

```json
{ "type": "batch_finish", "correlationId": "cmd-example-004", "blockNo": "01" }
```

```json
{ "type": "terminate_all", "correlationId": "cmd-example-005" }
```

### 2.2 Event đang phát

| `type` | Topic | Ý nghĩa | Lưu ý |
|---|---|---|---|
| `confirmed` | `p2l/events` | Operator bấm confirm | Raw device event; có thể lặp |
| `light_changed` | `p2l/devices/{address}` | Registry state đổi | Retained, chỉ là observed/cache |
| `batch_completed` | `p2l/events` | P2L thấy pending batch bằng 0 | Vi phạm boundary đích; deprecate |
| `bridge_status` | `p2l/status` | Bridge/gateway online | Retained; LWT báo offline |
| `task_started`, `task_confirmed`, `task_error`, `csv_completed` | `p2l/events` | CSV workflow | Legacy |

Ví dụ:

```json
{
  "type": "confirmed",
  "timestamp": "2026-09-03T01:30:00Z",
  "address": "0001",
  "status": "Normal",
  "adjustedQuantity": 5,
  "adjustedDisplay": "00005",
  "correlationId": "cmd-example-001",
  "displayData": "00005",
  "label": "BIN-A-01"
}
```

### 2.3 Khoảng trống contract v1

- Dispatcher bắt exception và ghi log nhưng không phát `command_failed`.
- API/dashboard trả `status: sent`, không chứng minh gateway đã ACK.
- Không có schema version, `commandId` bắt buộc, `eventId`, expiry hoặc revision.
- `batch_completed` đặt business completion trong P2L.
- `get_snapshot` chỉ phản ánh registry process, không phải hardware truth/session.
- QoS 1 nhưng idempotency contract chưa đầy đủ.

## 3. MQTT vNext đề xuất

Tên topic chỉ là đề xuất; phải được WMS, operations và security phê duyệt trước
khi khóa contract.

### 3.1 Envelope command

```json
{
  "schemaVersion": 2,
  "commandId": "01JEXAMPLE0000000000000000",
  "type": "apply_block",
  "issuedAt": "2026-09-03T01:30:00Z",
  "expiresAt": "2026-09-03T01:30:30Z",
  "source": "wms-site-a",
  "gatewayId": "gateway-a",
  "blockNo": "01",
  "revision": 42,
  "desiredLights": [
    {
      "address": "0001",
      "display": "00005",
      "color": "Green",
      "ledMode": "Flash",
      "buzMode": "Off"
    }
  ]
}
```

Quy tắc:

- `desiredLights` là tập đầy đủ của đúng một block tại `revision` đó.
- P2L từ chối revision cũ và command hết hạn.
- Cùng `commandId` phải cho cùng outcome, không thực thi tác động lần hai.
- Không cho `blockNo="00"` trong `apply_block` thông thường.
- Emergency terminate dùng command riêng và audit/authorization riêng.

### 3.2 Outcome/event

```json
{
  "schemaVersion": 2,
  "eventId": "01JEXAMPLEEVENT00000000000",
  "type": "command_applied",
  "occurredAt": "2026-09-03T01:30:01Z",
  "instanceId": "p2l-site-a-boot-17",
  "commandId": "01JEXAMPLE0000000000000000",
  "gatewayId": "gateway-a",
  "blockNo": "01",
  "revision": 42,
  "result": "acked"
}
```

`command_failed` phải có machine-readable `errorCode` allow-list như
`INVALID_COMMAND`, `STALE_REVISION`, `GATEWAY_OFFLINE`, `NACK`, `ACK_TIMEOUT`,
`BLOCK_SCOPE_VIOLATION`; không đưa credential hoặc raw exception nhạy cảm vào
payload.

### 3.3 Recovery event

```json
{
  "schemaVersion": 2,
  "eventId": "01JEXAMPLEREADY00000000000",
  "type": "bridge_ready",
  "occurredAt": "2026-09-03T01:31:00Z",
  "instanceId": "p2l-site-a-boot-18",
  "gatewayId": "gateway-a",
  "syncRequired": true
}
```

WMS phải phản hồi bằng `apply_block` full state cho từng block đang active.

## 4. HTTP/WebSocket của dashboard hiện tại

Nhóm endpoint chính:

| Nhóm | Endpoint tiêu biểu | Tác động |
|---|---|---|
| UI/stream | `GET /`, `WS /ws` | Xem dashboard/event |
| MQTT | `POST /api/connect`, `/api/disconnect` | Thay đổi connection |
| Service | `/api/service/*`, `/api/p2l-service/*`, `/api/self/restart` | Start/stop/restart process/service |
| Device | `/api/devices`, `/api/scan-devices` | CRUD/scan mapping |
| Light | `/api/devices/{address}/light`, `/off` | Phát lệnh bật/tắt |
| Batch | `/api/batch/start`, `/finish`, `/status` | Legacy workflow |
| Emergency | `POST /api/terminate-all` | Tắt phạm vi lớn |
| Config/log | `/api/config`, `/api/appsettings`, `/api/logs` | Đọc/ghi config, xem/xóa log |

Source hiện không áp authentication/authorization middleware cho các route này.
Không expose trực tiếp trên `0.0.0.0` ra LAN/WAN trước khi có lớp bảo vệ. Status
HTTP `200`/`sent` chỉ nói command đã được publish, không phải hardware đã apply.

## 5. Lệnh Aioi có tác động tắt đèn

| Đường gọi | Lệnh phần cứng | Phạm vi |
|---|---|---|
| `light_off` / `ExtinguishAsync` | `D{address}` | Một module |
| `batch_finish` | terminate block | Một block; `00` có thể là tất cả |
| `terminate_all` | terminate block `00` | Tất cả |
| CSV hoàn tất/hủy | mặc định `FinishBatchAsync()` | Có thể thành `D00` |
| Interactive shutdown/error | terminate mặc định | Có thể thành `D00` |
| P1/P5 full-block | apply/overwrite danh sách | Có thể làm tắt module bị bỏ khỏi danh sách |

Handler confirm tự nó gửi ACK `o`, cập nhật registry và phát event; nó không trực
tiếp gửi `D`. Tuy nhiên tác động kế tiếp từ workflow hoặc P1/P5 có thể làm đèn
khác tắt.

## 6. Quy tắc tương thích

- Thêm field optional là backward-compatible nếu consumer bỏ qua field lạ.
- Đổi semantics, enum hoặc retain behavior phải tăng `schemaVersion`.
- Không tái sử dụng cùng `type` cho nghĩa khác.
- Deprecation phải có owner, consumer inventory, ngày kết thúc và telemetry.
- Mọi contract change phải có fixture JSON, producer/consumer contract test và
  power-loss/multi-block acceptance evidence.

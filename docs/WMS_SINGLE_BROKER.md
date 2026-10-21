# WMS với một broker MQTT

Đặt `Mqtt.RequireApplicationAck=true` cho Bridge dùng cùng RKT hỗ trợ `transport_ping`/`transport_pong` và `event_ack`.

```json
{
  "Mqtt": {
    "Enabled": true,
    "Host": "127.0.0.1",
    "Port": 1883,
    "Tls": { "Enabled": false },
    "ClientId": "p2l-bridge-test",
    "CommandTopic": "test/p2l/commands",
    "EventTopic": "test/p2l/events",
    "DevicesTopic": "test/p2l/devices",
    "StatusTopic": "test/p2l/status",
    "RequireApplicationAck": true,
    "BackupBroker": { "Enabled": false }
  }
}
```

Đây là ví dụ loopback cho môi trường test; dùng endpoint, TLS và topic riêng của môi trường triển khai. Không dùng cấu hình này để thay profile đang chạy.

Trong chế độ này, Bridge giữ business event trong outbox qua reconnect/restart cho đến khi RKT ACK đúng `eventId`. MQTT PUBACK chỉ xác nhận broker đã nhận. Confirm chỉ ACK sau callback bền vững của RKT; `command_result` ACK xác nhận bộ xử lý control đã nhận, không chứng minh accounting hoàn tất.

Mặc định `RequireApplicationAck=false` giữ tương thích với client/SDK manual cũ không phát application ACK. Khi có backup, Bridge vẫn luôn yêu cầu application ACK như trước. Profile WMS bật cờ cần RKT đang hoạt động; không dùng profile này cho standalone/manual-only mà thiếu peer ACK, vì event sẽ tiếp tục chờ trong outbox. Không cần thay client Python/UI cho profile standalone.

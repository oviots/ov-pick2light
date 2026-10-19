# ADR-0001 — P2L là hardware adapter, WMS sở hữu session

- **Trạng thái:** Accepted as handover target; chưa được implement đầy đủ
- **Ngày:** 2026-09-03
- **Decision owners:** Product/WMS owner và P2L owner cần ký xác nhận

## Bối cảnh

P2L cần hoạt động bền vững qua mất điện, restart và mất mạng. Source hiện vừa
điều khiển gateway, vừa giữ active batch/pending item và phát quyết định
`batch_completed`. Local registry có thể bị mất hoặc thiếu state sau restart,
trong khi lệnh P1/P5 mang semantics full-block. Vì vậy dựng lại danh sách từ
local cache có thể tắt các đèn mà WMS vẫn coi là pending.

Hai hệ thống cùng sở hữu session tạo split-brain: WMS và P2L có thể đưa ra hai
kết luận khác nhau sau reconnect, duplicate message hoặc failover.

## Quyết định

1. WMS/upstream là nguồn sự thật duy nhất cho picking session, task, pending,
   completion và desired light state.
2. P2L chỉ nhận hardware intent, điều khiển Aioi gateway và phát raw event,
   observed telemetry cùng command outcome.
3. P2L được giữ technical state có thời hạn nhưng không dùng nó để quyết định
   session hoặc tự relight.
4. Sau mỗi process/gateway restart, P2L phát `syncRequired`; WMS gửi full desired
   state cho từng block đang active.
5. Full-block command luôn cô lập theo block, có revision và idempotency key.
6. `batch_completed` và CSV session orchestration sẽ được deprecate khỏi P2L.

## Hệ quả tích cực

- Một nguồn sự thật cho session và recovery.
- Không cần persistence business state trong hardware adapter.
- Dễ test restart, failover, duplicate command và multi-block.
- P2L có thể thay gateway/protocol mà không thay workflow WMS.

## Đánh đổi

- WMS phải lưu desired state và thực hiện resync.
- Cần versioned contract và command outcome mới.
- Trong giai đoạn migration phải hỗ trợ adapter cho consumer v1.
- Khi WMS unavailable sau restart, P2L không tự relight; warehouse cần runbook
  fail-safe và manual recovery được authorize.

## Phương án bị loại

### Lưu toàn bộ session trong P2L

Không chọn vì nhân đôi business ownership, cần đồng bộ giao dịch với WMS và vẫn
không giải quyết conflict/revision khi cả hai phía phục hồi khác thời điểm.

### Dùng retained `light_changed` làm desired state

Không chọn vì đó là observed process cache, có thể stale, thiếu event hoặc không
phản ánh session cancellation/update mới nhất.

### Tự scan hardware rồi suy luận session

Không chọn vì hardware state không chứa đủ order/task semantics và scan không
thể xác định ý định mới nhất của WMS.

## Kế hoạch migration

1. Inventory mọi producer/consumer của topic v1.
2. Thêm schema v2, `commandId`, `eventId`, outcome và `bridge_ready`.
3. Implement `apply_block` full-state với revision và block isolation.
4. Cho WMS chạy shadow state reconciliation, chưa điều khiển production.
5. Chạy acceptance trên test rig, gồm power cycle và hai block đồng thời.
6. Chuyển WMS thành session owner; theo dõi parity.
7. Tắt producer `batch_*`/`csv_*`, sau đó xóa logic legacy trong major version.

## Tiêu chí hoàn tất ADR

- WMS và P2L owner ký ownership matrix.
- Contract v2 được version hóa và có contract tests.
- Không còn code path P2L quyết định completion/session recovery.
- Power-loss recovery không tắt chéo/thiếu đèn trong acceptance test.
- Runbook và monitoring được operator diễn tập thành công.

# Bộ tài liệu bàn giao Pick2Light

- **Trạng thái:** Bàn giao có điều kiện — chưa đủ điều kiện ký xác nhận vận hành production
- **Ngày chốt baseline:** 2026-09-03
- **Baseline source:** nhánh `main`, commit `9fe503db20ed3ba56f8987b1f772b2a4340d7c63`

Đây là điểm bắt đầu chính thức cho đội tiếp nhận dự án Pick2Light (P2L). Bộ tài
liệu phân biệt rõ:

- **Hiện trạng (as-is):** hành vi đang có trong source tại baseline nêu trên.
- **Kiến trúc đích (to-be):** P2L là hardware adapter, không sở hữu picking
  session; WMS/upstream là nguồn sự thật cho session và desired light state.
- **Điều kiện nghiệm thu:** các blocker phải được đóng và có bằng chứng kiểm thử
  trước khi ký production sign-off.

## Thứ tự đọc khuyến nghị

| Tài liệu | Mục đích |
|---|---|
| [00 — Baseline và trạng thái](00-BASELINE-AND-STATUS.md) | Chốt phiên bản, artifact, mức sẵn sàng |
| [01 — Tổng quan hệ thống](01-SYSTEM-OVERVIEW.md) | Scope, thành phần, thuật ngữ |
| [02 — Kiến trúc và ranh giới](02-ARCHITECTURE-AND-BOUNDARIES.md) | As-is, to-be, ownership và luồng mất điện |
| [03 — Hợp đồng giao tiếp](03-INTERFACE-CONTRACTS.md) | MQTT, HTTP/WebSocket, lệnh phần cứng |
| [04 — Build, release, deploy](04-BUILD-RELEASE-DEPLOY.md) | Quy trình tạo và phát hành artifact |
| [05 — Cấu hình và bảo mật](05-CONFIGURATION-AND-SECURITY.md) | Config, secrets, network và access |
| [06 — Runbook vận hành](06-OPERATIONS-RUNBOOK.md) | Start/stop, health check, sự cố mất điện |
| [07 — Kiểm thử và nghiệm thu](07-TESTING-AND-ACCEPTANCE.md) | Test tiers, acceptance matrix, evidence |
| [08 — Vấn đề và rủi ro](08-KNOWN-ISSUES-AND-RISKS.md) | Danh sách blocker có tiêu chí đóng |
| [09 — Checklist bàn giao](09-HANDOVER-CHECKLIST.md) | Người, quyền truy cập, ký nhận |
| [ADR-0001](adr/0001-p2l-hardware-adapter-boundary.md) | Quyết định ranh giới P2L/WMS |

Mẫu sử dụng lâu dài:

- [Biên bản sự cố](templates/INCIDENT-REPORT.md)
- [Biên bản phát hành](templates/RELEASE-RECORD.md)

## Mức ưu tiên nguồn thông tin

Khi nội dung mâu thuẫn, áp dụng thứ tự sau:

1. Source code và cấu hình của đúng commit/release đang chạy.
2. Biên bản release đã ký, hash artifact và cấu hình đã khử bí mật.
3. Bộ tài liệu `docs/handover/` này.
4. Các tài liệu cũ ở `docs/` và README gốc.

Các tài liệu cũ chỉ có tính tham khảo vì một số mô tả protocol, heartbeat,
runtime và ownership batch không còn khớp source hoặc kiến trúc đích.

## Kết luận bàn giao hiện tại

Đội tiếp nhận có thể nhận source, tri thức và backlog; **chưa nên nhận trách
nhiệm production** cho đến khi ít nhất các hạng mục P0 trong
[danh sách rủi ro](08-KNOWN-ISSUES-AND-RISKS.md) được đóng.

Đặc biệt:

- Không dùng local registry của P2L để khôi phục picking session sau restart.
- Không expose web dashboard ra mạng khi chưa có authentication/authorization.
- Không phát hành từ workflow đang có khả năng đóng gói credential thật.
- Không cài service Linux/macOS bằng script hiện tại cho đến khi entry point
  được đổi sang `p2l serve` và được kiểm thử.

## Chủ sở hữu cần điền khi ký nhận

| Vai trò | Họ tên/nhóm | Kênh liên hệ | Trạng thái |
|---|---|---|---|
| Product owner / WMS owner | TBD | TBD | Chưa chỉ định |
| P2L software owner | TBD | TBD | Chưa chỉ định |
| Warehouse operations owner | TBD | TBD | Chưa chỉ định |
| Network/MQTT owner | TBD | TBD | Chưa chỉ định |
| Hardware/Aioi owner | TBD | TBD | Chưa chỉ định |
| Security approver | TBD | TBD | Chưa chỉ định |

Không đưa password, private key, token hoặc giá trị secret vào bất kỳ tài liệu
bàn giao nào. Chỉ bàn giao secret qua kho bí mật được phê duyệt.

# 00 — Baseline và trạng thái bàn giao

## 1. Baseline được kiểm tra

| Thuộc tính | Giá trị tại 2026-09-03 |
|---|---|
| Repository | Pick2Light (P2L) |
| Nhánh | `main` |
| Commit | `9fe503db20ed3ba56f8987b1f772b2a4340d7c63` |
| Commit subject | `Task# Fix dashboard` |
| GitHub mirror | `github/main` trùng baseline |
| GitLab origin | `origin/main` chậm hơn baseline 2 commit tại lúc kiểm tra |
| Tag gần nhất | `1.0.0`, không trỏ vào baseline |
| Runtime source thực tế | .NET SDK 10, target `net10.0` |
| CLI/MSI version hiển thị | `1.0.0` |
| Trạng thái sign-off | Bàn giao có điều kiện |

Thông tin này là snapshot, không thay cho một release record. Đội tiếp nhận phải
chốt lại commit, tag và remote trước lần phát hành tiếp theo.

## 2. Tình trạng working tree trước khi tạo tài liệu

Workspace đã có thay đổi/artifact không thuộc bộ tài liệu bàn giao:

- file binary/native trong `publish/` đã thay đổi;
- một số file/config/database trong `publish/` chưa được Git theo dõi;
- `AGENTS.md` chưa được Git theo dõi.

Những file này được giữ nguyên. Không coi chúng là artifact được chứng nhận và
không commit chung nếu chưa xác định rõ nguồn gốc.

## 3. Artifact đang có — chỉ dùng để kiểm kê

Các hash dưới đây giúp nhận dạng file đã tồn tại; **không chứng minh** source,
pipeline, test hoặc cấu hình của một bản release hợp lệ.

| File | Kích thước (byte) | SHA-256 |
|---|---:|---|
| `dist/p2l-1.0.0-osx-arm64.tar.gz` | 23,886,433 | `c6b6c8dbf7f2a7697c0bf8060ea94b9f98d6c5f1b8544002e8db137da3591096` |
| `dist/p2l-10.0.0-linux-arm64.tar.gz` | 24,499,849 | `8e11d58b36ca07e5605b1aae4e5eaa8fa64dbcdbcbdceead85feb236a094b075` |
| `dist/p2l-10.0.0-linux-x64.tar.gz` | 25,441,894 | `2ba41dfd4531293e4e94c87b11e3acd36373d6fd43c022d9f8e8dd54b483c3cb` |
| `dist/p2l-1.0.0-osx-x64.tar.gz` | 24,443,601 | `ce95cf2c88ebbcff073308ff5cc2e3aa821a54ab71eb3446d4b50e3fdc5fb243` |

Tên artifact cho thấy version drift: script package đọc `10.0.0` từ
`global.json`, trong khi CLI/MSI đang khai báo `1.0.0`. Không dùng các file trên
để rollout cho đến khi provenance được tái tạo bằng pipeline chuẩn.

## 4. Ma trận nền tảng

| Nền tảng | Có build path | Trạng thái bàn giao |
|---|---|---|
| Windows x64 | Native AOT + WiX MSI | Có đường build; cần release evidence |
| Windows arm64 | WiX workflow có input | Chưa có acceptance evidence trong baseline |
| Linux x64 | Native AOT archive | Script service hiện sai entry point — blocked |
| Linux arm64 | Native AOT archive | Script service hiện sai entry point — blocked |
| macOS arm64 | Native AOT archive | Script service hiện sai entry point — blocked |
| macOS x64 | Có artifact cũ; CI job bị comment | Không nằm trong CI matrix hiện hành |

Native AOT không hỗ trợ cross-publish tùy ý giữa các hệ điều hành. Build release
trên runner đúng OS/architecture hoặc runner đã được chứng minh tương thích.

## 5. Mức sẵn sàng

| Phạm vi | Đánh giá | Ghi chú |
|---|---|---|
| Source handover | Có điều kiện | Source và cấu trúc có thể tiếp nhận |
| Knowledge handover | Sẵn sàng | Bộ tài liệu này là điểm bắt đầu |
| Build reproducibility | Chưa đạt | Version drift, Python dependency chưa pin |
| Security sign-off | Chưa đạt | Credential tracked; web client chưa có auth |
| Recovery sign-off | Chưa đạt | Chưa có contract resync sau mất điện |
| Multi-block sign-off | Chưa đạt | Có nguy cơ gộp trạng thái khác block |
| Production operations | Chưa đạt | Linux/mac service path không an toàn |

Chi tiết và tiêu chí đóng nằm tại
[08 — Vấn đề và rủi ro](08-KNOWN-ISSUES-AND-RISKS.md).

## 6. Phạm vi xác minh của bộ tài liệu

Đã thực hiện review tĩnh source, config, deployment scripts, CI workflows và
artifact inventory. Chưa phát lệnh đến gateway/đèn và chưa chạy integration test
phần cứng, vì các test đó có thể gửi `Z`, `P1`, `P5`, `D` và làm thay đổi trạng
thái thiết bị thật.

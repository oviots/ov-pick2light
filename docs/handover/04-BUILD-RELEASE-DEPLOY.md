# 04 — Build, release và deploy

## 1. Nguyên tắc phát hành

Không phát hành trực tiếp từ các file đang có trong `dist/` hoặc `publish/`.
Một release hợp lệ phải truy ra được:

`tag -> commit -> CI run -> test evidence -> artifact hash -> approved config -> deployment record`

Không đưa credential vào source, artifact, CI log hoặc release record.

## 2. Toolchain tại baseline

| Thành phần | Baseline |
|---|---|
| .NET SDK | `10.0.0`, roll forward latest minor |
| Target framework | `net10.0` |
| Publish | Native AOT, self-contained |
| Python client | Python 3.12 trong CI; PyInstaller build path |
| Windows installer | WiX Toolset 5.0.2 |
| Solution | `light.sln` |

Các hướng dẫn cũ nói .NET 8/9 không phản ánh source hiện tại.

## 3. Build dành cho developer

Các lệnh sau không phát lệnh phần cứng nếu chỉ restore/build:

```bash
dotnet --version
dotnet restore light.sln
dotnet build light.sln --configuration Release --no-restore
```

Publish native cho đúng RID trên host tương thích:

```bash
./deploy/publish.sh osx-arm64
./deploy/publish.sh linux-x64
```

Hoặc package bridge + frozen web client:

```bash
P2L_VERSION=1.0.1 ./deploy/package.sh osx-arm64
```

`P2L_VERSION` phải đến từ release version đã phê duyệt. Không để script suy ra
product version từ SDK version trong `global.json`.

## 4. CI matrix hiện tại

`.github/workflows/build-all.yml` có job Linux x64/arm64, macOS arm64 và Windows
x64; macOS x64 đang bị comment. `.github/workflows/build-msi.yml` hỗ trợ input
Windows x64/arm64.

Không coi một platform được support chỉ vì compile thành công. Mỗi platform cần:

- boot/smoke test artifact sạch trên OS mục tiêu;
- config load test;
- service lifecycle test;
- gateway/MQTT integration test phù hợp;
- hash và release record riêng.

## 5. Release gates bắt buộc

### Gate A — Source và version

- Working tree sạch hoặc mọi thay đổi đã được review.
- Commit review xong, có annotated tag duy nhất.
- Assembly/CLI/MSI/package cùng một SemVer.
- Remote/tag divergence được giải quyết.
- SBOM/license inventory và ownership được phê duyệt.

### Gate B — Security

- Không còn username/password/private key thật trong Git history/release input.
- Credential đã lộ được rotate; secret scanning pass.
- CI chỉ copy template đã khử secret.
- TLS validation bật; không allow untrusted certificate ở production.
- Dashboard có auth hoặc chỉ bind loopback sau reverse proxy được phê duyệt.

### Gate C — Build reproducibility

- Dependency lock/pin, đặc biệt Python client.
- Build trên runner đã định danh, lưu SDK/tool versions.
- Archive được dựng từ checkout sạch.
- Binary/config/native SQLite được smoke test sau giải nén.

### Gate D — Functional/recovery

- Unit, contract và non-hardware test pass.
- Hardware test pass trên test block được cô lập.
- Power-loss, MQTT-loss, duplicate command và multi-block acceptance pass.
- Không có cross-block off; không dùng local cache làm session truth.

### Gate E — Artifact và rollout

- Archive inventory không có file thừa/secret/database/log.
- SHA-256 được tính sau khi đóng gói cuối cùng.
- Release record được phê duyệt.
- Có canary site/device, rollback criteria và người trực.

## 6. Blocker trong build/package hiện tại

1. `deploy/package.sh` mặc định đọc SDK version từ `global.json`, tạo artifact
   `10.0.0` trong khi CLI/MSI hiển thị `1.0.0`.
2. Workflow `build-all.yml` copy `config/p2l_config.json` đang được track; file
   này có credential không rỗng tại baseline.
3. Package script stage `config/appsettings.json`, nhưng binary đọc
   `config/p2l_config.json`; config đóng gói có thể bị bỏ qua.
4. Linux/macOS installer chạy `p2l run tasks.csv`, không chạy `p2l serve`.
5. Python requirements dùng constraint `>=`, chưa có lock/hash nên artifact
   không tái lập chắc chắn.

Không promote artifact cho đến khi các mục này được sửa và pipeline chạy lại.

## 7. Deployment theo nền tảng

### Windows

WiX component hiện đăng ký bridge với argument `serve`, phù hợp entry point daemon.
Trước rollout vẫn phải kiểm tra:

- service account có quyền đọc config/certificate và ghi log/buffer/DB;
- firewall chỉ mở port cần thiết;
- web client không expose không-auth;
- MSI upgrade/rollback được thử trên máy sạch.

### Linux

`deploy/p2l.service`, `deploy/install-linux.sh` và service sinh trong package đang
chạy CSV workflow. **Không cài/enable unit này ở production** trước khi đổi
`ExecStart` thành binary với argument `serve` và chạy acceptance.

### macOS

LaunchAgent/LaunchDaemon sinh bởi script package cũng dùng `run tasks.csv`.
Không load plist này trước khi đổi sang `serve` và xác minh lifecycle.

## 8. Quy trình rollout chuẩn

1. Freeze release candidate và tạo release record từ template.
2. Build/test trên CI sạch; lưu test report, SBOM và hash.
3. Security/operations duyệt config đã khử secret.
4. Backup config/mapping/log metadata hiện tại; không copy secret vào ticket.
5. Pause command producer hoặc đưa WMS vào maintenance mode.
6. Deploy một canary; chỉ start `p2l serve`.
7. Kiểm tra bridge/gateway/MQTT, rồi WMS gửi full desired state per block.
8. Quan sát confirm, command outcome và cross-block isolation.
9. Mở rộng rollout theo batch; dừng khi chạm rollback criteria.
10. Ghi deployed hash, site, thời gian, approver và kết quả vào release record.

## 9. Rollback

- Rollback binary và config như một cặp đã được chứng nhận.
- Không restore observed registry rồi coi đó là session state.
- Sau rollback/restart, WMS phải resync full desired state.
- Nếu contract/database migration không backward-compatible, phải có kế hoạch
  rollback riêng trước deployment.
- Không gửi `D00` như một bước rollback mặc định; chỉ operator được authorize mới
  quyết định emergency terminate.

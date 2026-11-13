# P2L: 3 đèn, 2 block, 1 Controller

Đã chuẩn bị DB riêng trên **UAT `192.168.12.6:37017`**, replica set `rs0`:
`sft_uat_p2l_local_3lights_20261113`.

Production chỉ được đọc 12 phiên gần nhất / 31 detail / 3 demand split / 4 block để đối chiếu schema và liên kết. Tên cửa hàng, SKU, kho, số lượng và ID test đều giả lập. Xem `preparation.json`.

Controller đã được kiểm tra bằng Az: `192.168.1.254:5003`, thấy 0008 / 0014 / 0017, đều PowerOn/Off. License hiện cho phép block 01 + 02 và topic test `p2l/uat/test01/*`.

| Block | Đèn | Cửa hàng lượt A | Cửa hàng lượt B |
|---|---|---|---|
| 01 | 0008 + 0014 | 2 + 3 | 1 + 2 |
| 02 | 0017 | 4 | 1 |

Đã seed `SFT.ScheduleManagements` với `Name=PICK_2_LIGHT_SCHEDULE`, `Status=ENABLED`, `IsDeleted=0`. Hai `P2L.Blocks` đã có MQTTConfig từ license để chạy bằng luồng WMS chuẩn.

Mỗi BIN bắt đầu Qty=100, PendingOutQty=0. Chưa có PickingSession/Signal/Transaction khi chuẩn bị xong. Các demand đều Processing để WMS cấp phiên khi **anh chủ động chạy** bộ WMS test.

## Chạy sau khi setup

Mở terminal 1, start P2L bằng bản đã kiểm tra kết nối:

```sh
cd /Volumes/GTeck/Projects/1shot/workspaces/tools/light/artifacts/uat-integrated-osx-arm64-20261113-r14/bridge
export P2L_LICENSE_API_URL=https://auth-supra.oviots.com
./p2l serve
```

Mở terminal 2, start WMS test:

```sh
cd /Volumes/GTeck/Projects/1shot/workspaces/tools/light
node scripts/uat-3lights/run.cjs run
```

Lệnh thứ hai bắt đầu tạo phiên / reserve tồn và gửi MQTT tới đèn thật. Chỉ chạy sau khi P2L sẵn sàng. Không cần start toàn bộ WMS service hoặc đổi config của service khác.

Bấm CONFIRM trên từng đèn. Hai block nhận phiên độc lập. Block 01 chỉ chuyển lượt B sau khi đủ Confirm của cả 0008 và 0014; block 02 có thể chuyển lượt B riêng, không chờ block 01. Chờ worker tồn hoàn tất rồi kiểm tra:

```sh
node scripts/uat-3lights/run.cjs status
```

Kết quả mong đợi: 4 session Completed, 6 PickingDetails Completed, tổng PickedQty=13; BIN 0008 còn 97, BIN 0014 còn 95, BIN 0017 còn 95; PendingOutQty=0. Tồn chuyển sang STORE, tổng BIN+STORE vẫn 300. Chưa kiểm chứng kết quả này bằng Confirm vật lý trong lượt chuẩn bị.

Ctrl+C ở WMS test dùng shutdown hook WMS (chờ tối đa khoảng 10 giây), dừng cấp phiên và đóng kết nối, giữ dữ liệu đang chờ để chạy tiếp; không xóa/reset database. Để dừng một bài test đang bật đèn, đừng xem Ctrl+C là lệnh tắt đèn vật lý.

## Phạm vi bộ chạy

- Gọi đúng `pick2light/schedule.start()` của WMS local, kiểm bản ghi ScheduleManagement, chờ startup 6 giây; sau đó dùng BlockRegistry, PickingQueue, chọn demand, reserve, nhận MQTT, applyReceipts, ACK `confirm_ack`, worker tồn.
- Hai MQTT client WMS có client ID riêng; chung prefix test và cùng bridge/controller.
- Lấy config client từ authen bằng key đã kích hoạt của máy; không lưu mật khẩu MQTT vào fixture hay in ra terminal.
- `P2L.Blocks` trong DB test có zone và MQTTConfig hợp lệ; runtime đọc trực tiếp từ DB, không ghi đè bằng `P2L_BLOCK_CONFIGS`. MQTT credentials không nằm trong file fixture hoặc terminal output.
- Chỉ giả lập in nhãn: không gọi máy in/API in thật.
- Test này dùng Confirm qua MQTT. Config bridge đã kiểm tra không có confirmApi bật; chưa kiểm thử luồng API bằng bộ này.
- Không có UAT API/web server nào được tự deploy hoặc tự start.
- `check`, `preflight`, `status` chỉ đọc; `seed` tạo DB trống được định danh trong fixture; nếu fixture đã tồn tại chỉ bổ sung config/schedule, không reset tồn/demand. Không drop/delete/reset. `run` chỉ ghi DB test và gửi MQTT test.

Dependency riêng nằm trong thư mục này, không sửa node_modules hay package của WMS:

```sh
cd scripts/uat-3lights
npm ci --ignore-scripts --no-audit --no-fund
```

Nếu chuyển máy: cần Node.js, Python environment `light/.venv` có `cryptography`, key P2L đã kích hoạt trong tài khoản OS và quyền tới UAT Mongo. URI được đọc từ `rocket-wms-service/server/modules/default/conf.json`, không ghi ra tài liệu.

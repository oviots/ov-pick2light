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

## Đợt API r15 đã thêm

Batch/RocketCode: `P2L-API-20261113-R15-01`, cùng DB `sft_uat_p2l_local_3lights_20261113`.
Đã thêm 4 DemandSplits `Processing` và 6 DetailSplits `Pending`, tổng 15 sản phẩm. Giữ SKU/BIN/địa chỉ đèn hiện có; không tạo trực tiếp PickSession, không reset tồn hay sửa kết quả đợt trước.

| StoreCode | Block | 0008 | 0014 | 0017 |
|---|---|---:|---:|---:|
| TEST_R15_01_A | 01 | 3 | 2 | — |
| TEST_R15_01_B | 01 | 1 | 4 | — |
| TEST_R15_02_A | 02 | — | — | 2 |
| TEST_R15_02_B | 02 | — | — | 3 |

WMS chọn thứ tự cửa hàng theo logic ranking hiện có. Khi Confirm đủ số lượng, kỳ vọng thêm 4 session / 6 picking detail hoàn tất; BIN 0008 từ 97 còn 93, 0014 từ 95 còn 89, 0017 từ 95 còn 90, PendingOutQty về 0.

Chạy P2L bằng `artifacts/uat-integrated-osx-arm64-20261113-r15/bridge/p2l serve` với license API đã cấu hình. Để thử hoàn toàn qua API, bật `pickSessionApi` và `confirmApi` trong authen, deploy gateway/WMS đúng DB test. Script thêm dữ liệu không tự start WMS hoặc P2L, không publish lệnh đèn.

```sh
node scripts/uat-3lights/add-round.cjs status
```

`add-round.cjs seed` có marker cố định: chạy lại không tạo trùng đợt này. `plan`/`status` chỉ đọc; `seed` chỉ append dữ liệu vào DB test đã có, không tạo database mới.

## Đợt nhấp nháy tiếp theo

Đã thêm batch `P2L-FLASH-20261113-01` vào cùng DB: 4 demand Processing, 6 dòng, tổng 15 sản phẩm. Cửa hàng `TEST_FLASH_01_A/B` và `TEST_FLASH_02_A/B`, cùng số lượng/địa chỉ của đợt r15; block 01 Green, block 02 Red, tất cả `P2LMode.Led=Flash`, `Buz=Off`.

```sh
node scripts/uat-3lights/add-round.cjs status flash
```

Tồn BIN trước đợt này: 0008=93, 0014=89, 0017=90. Nếu Confirm đủ, kỳ vọng còn 89/83/85 và PendingOutQty=0. Script `seed flash` không tạo trùng batch đã có, không sửa các đợt trước.

## Đợt IsRelightRequested

Batch `P2L-RELIGHT-20261113-01`: 2 demand Processing, 3 dòng, tổng 7 sản phẩm. Block 01: 0008=3 + 0014=2, Green/Flash; block 02: 0017=2, Red/Flash. Tất cả Buz=Off. Giữ các đợt trước và tồn hiện tại.

1. Chờ WMS tạo phiên và bật đèn; xem mã phiên bằng `node scripts/uat-3lights/add-round.cjs status relight`.
2. Trước khi bấm Confirm, tắt riêng block 01 bằng lệnh dưới, thay SESSION_CODE bằng mã phiên InProgress block 01:

```sh
node scripts/uat-3lights/finish-session.cjs 01 SESSION_CODE
```

Script chỉ publish `{"type":"batch_finish","blockNo":"01","correlationId":"SESSION_CODE"}` lên topic test. P2L gửi D01, dọn runtime block đó, không tạo LightConfirm, không sửa WMS DB. Controller vẫn trả ACK `o` cho lệnh D; các event `light_changed` của P2L vẫn có thể xuất hiện. Block 02 không bị tắt.

3. Trong đúng DB UAT, đặt `IsRelightRequested=true` cho cùng phiên:

```js
db.getCollection('P2L.PickingSessions').updateOne(
  {Code:'SESSION_CODE',BlockCode:'01',Status:'InProgress',IsDeleted:0},
  {$set:{IsRelightRequested:true}}
);
```

4. Kỳ vọng: cờ về false, 0008 và 0014 bật lại nhấp nháy đúng 3/2; mã phiên và PendingOutQty giữ nguyên, không có Confirm/tồn OUT-IN chỉ vì tắt hoặc relight. Sau đó bấm Confirm để hoàn tất. MQTT relight và API polling có thể cùng nhận yêu cầu, tùy kênh đang bật.

Không dùng nút Confirm để giả lập tắt đèn: đó là một Confirm thật. `light_off` xóa địa chỉ khỏi pending của runtime; `batch_finish` dọn toàn bộ runtime đúng block/phiên, phù hợp hơn để kiểm thử việc WMS cấp lại payload của cùng phiên. Đây là thao tác kết thúc runtime vật lý để test, không phải API Cancel/Deactive của WMS.

## Đợt HTTP/API

Batch `P2L-HTTP-20261113-01`: 4 demand, 6 dòng, tổng 15 sản phẩm, các store `TEST_HTTP_01_A/B` và `TEST_HTTP_02_A/B`. Tất cả Flash/Buz Off; block 01 Green, block 02 Red. Số lượng cùng ma trận 3/2, 1/4 ở block 01 và 2, 3 ở block 02.

Đợt này đã được thêm và chuyển sang Processing theo yêu cầu khi WMS service đang dừng. WMS vẫn phải chạy schedule để tạo phiên. Để xác nhận đúng kênh API: bật pickSessionApi + confirmApi trong authen, restart P2L, và ngăn MQTT phía WMS gửi lệnh trước. Việc dừng toàn bộ WMS cũng dừng tạo phiên, không thay thế việc tách riêng MQTT.

```sh
node scripts/uat-3lights/add-round.cjs status api
```

Script mặc định `seed api` tạo Paused để chuẩn bị an toàn; `activate api` chuyển riêng batch này sang Processing. Không tạo trùng dữ liệu khi gọi seed lại. Tồn trước đợt: 0008=86, 0014=81, 0017=83; nếu Confirm đủ kỳ vọng còn 82/75/78, PendingOutQty=0. Đây mới là dữ liệu chuẩn bị, chưa xác nhận đã chạy qua API.

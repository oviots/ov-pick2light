# Pick2Light (P2L) — Tóm tắt 1 trang

## P2L là gì

P2L là hệ thống pick-to-light cho kho vận, dùng đèn để hướng dẫn nhân viên lấy hàng đúng vị trí và xác nhận trực tiếp tại thiết bị.

## Giá trị chính

- tăng tốc độ picking
- giảm sai sót vận hành
- theo dõi trạng thái theo thời gian thực
- dễ tích hợp với **SFT Pickwave / Splitting MML**

## Mô hình tổng quan

```text
SFT Pickwave / Splitting MML / Dashboard
        │
        ▼
   MQTT / Web UI
        │
        ▼
   P2L Bridge (.NET)
        │
        ▼
    AIOI Gateway
        │
        ▼
    Light Modules
```

## MQTT là gì

MQTT là chuẩn giao tiếp nhẹ theo mô hình publish/subscribe. Trong P2L, nó giúp hệ thống nghiệp vụ gửi lệnh và nhận trạng thái mà không phải kết nối trực tiếp với phần cứng.

## Thành phần chính

- **Web Client / Dashboard**: giao diện vận hành và giám sát
- **P2L Bridge**: trung tâm điều phối và điều khiển phần cứng
- **MQTT Layer**: kênh tích hợp với hệ thống bên ngoài
- **AIOI Gateway + Light Modules**: lớp thực thi ngoài hiện trường

## Luồng vận hành

1. hệ thống tạo lệnh lấy hàng
2. bridge gửi lệnh xuống gateway
3. đèn sáng ở vị trí cần lấy
4. nhân viên xác nhận trên module
5. trạng thái được cập nhật ngược về dashboard và hệ thống trung tâm

## Điểm mạnh vận hành

- hỗ trợ reconnect
- có heartbeat
- có logging
- phù hợp chạy 24/7
- dễ triển khai dưới dạng binary self-contained

## Kết luận

P2L là nền tảng pick-to-light thực tế cho kho vận, giúp tăng năng suất, giảm lỗi và tạo nền tảng tích hợp lâu dài.

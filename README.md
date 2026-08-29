# Ứng Dụng Phiên Dịch Cuộc Họp Việt - Hàn

Ứng dụng Windows Forms dùng một microphone phòng họp để nhận dạng câu nói, xác định tiếng Việt hoặc tiếng Hàn, dịch sang ngôn ngữ còn lại, tạo giọng nói và phát ra đúng thiết bị âm thanh đã chọn.

- Tiếng Việt -> bản dịch tiếng Hàn -> tai nghe quản lý Hàn Quốc.
- Tiếng Hàn -> bản dịch tiếng Việt -> loa phòng họp.
- Âm thanh gốc từ microphone không được chuyển thẳng ra loa hoặc tai nghe.

## Cài Đặt Môi Trường

1. Cài `.NET 8 SDK` trên Windows.
2. Khôi phục các NuGet package:

```powershell
dotnet restore
```

3. Build ứng dụng:

```powershell
dotnet build
```

Các package chính gồm `NAudio`, `Google.Cloud.Speech.V1`, `Google.Cloud.Translation.V2`, `Google.Cloud.TextToSpeech.V1` và `Google.Apis.Auth`.

## Cấu Hình Google Cloud

1. Tạo một Google Cloud Project.
2. Bật `Speech-to-Text API`.
3. Bật `Cloud Translation API`.
4. Bật `Text-to-Speech API`.
5. Tạo `Service Account` cho ứng dụng.
6. Cấp quyền phù hợp để Service Account dùng được các API trên.
7. Tạo và tải tệp khóa JSON của Service Account.
8. Mở ứng dụng, bấm `Chọn tệp...`, rồi chọn tệp JSON đó.

Không commit tệp JSON xác thực vào source code. Ứng dụng chỉ đọc tệp từ đường dẫn bạn chọn và không hiển thị private key.

## Cấu Hình Thiết Bị Âm Thanh

- `Microphone phòng họp`: microphone chung đặt trước người tham gia.
- `Loa phòng họp`: thiết bị Output 1, dùng để phát bản dịch tiếng Việt.
- `Tai nghe quản lý Hàn Quốc`: thiết bị Output 2, dùng để phát bản dịch tiếng Hàn.

Bấm `Làm mới thiết bị` sau khi cắm lại microphone, loa hoặc tai nghe Bluetooth. Trước khi bắt đầu họp, dùng `Kiểm tra loa phòng họp` và `Kiểm tra tai nghe quản lý` để xác nhận âm thanh phát đúng nơi.

Nếu thiết bị đã chọn bị ngắt kết nối, ứng dụng không tự chuyển sang thiết bị mặc định của Windows để tránh phát nhầm nội dung.

## Cách Chạy Thử

1. Chọn tệp xác thực Google Cloud.
2. Chọn microphone phòng họp.
3. Chọn loa phòng họp cho bản dịch tiếng Việt.
4. Chọn tai nghe quản lý Hàn Quốc cho bản dịch tiếng Hàn.
5. Chọn `Ngôn ngữ nói` và `Ngôn ngữ dịch`. Ứng dụng hỗ trợ `Tiếng Việt`, `Tiếng Anh`, `Tiếng Hàn`.
6. Kiểm tra từng thiết bị đầu ra.
7. Bấm `Bắt đầu`.
8. Nói từng câu, một người nói tại một thời điểm.
9. Bấm `Dừng` khi kết thúc phiên dịch.

Nếu bản dịch là tiếng Việt, âm thanh phát ra loa phòng họp. Nếu bản dịch là tiếng Anh hoặc tiếng Hàn, âm thanh phát ra tai nghe quản lý.

## Bảo Vệ Chống Vòng Lặp Âm Thanh

Khi tiếng Hàn được dịch sang tiếng Việt và phát ra loa phòng họp, ứng dụng tạm ngừng xử lý microphone trong lúc phát và thêm một khoảng ngắn sau đó. Việc này giúp tránh trường hợp microphone nghe lại giọng nói tổng hợp tiếng Việt rồi dịch tiếp thành tiếng Hàn.

## Giới Hạn Của MVP

Phiên bản MVP xử lý một người nói và một câu nói tại một thời điểm. Ứng dụng chưa hỗ trợ hai người nói chồng lên nhau, chưa tách người nói, và chưa dùng streaming Speech-to-Text trực tiếp.

Kiến trúc hiện tại ưu tiên độ ổn định, định tuyến âm thanh chính xác, chống vòng lặp âm thanh và khả năng debug.

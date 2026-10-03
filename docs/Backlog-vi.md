# Backlog

Việc còn treo và việc nên làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit với bản sửa.

### Kiểm chống DPI trên mạng thật

- **Vấn đề:** cờ Anti-DPI (tách tên miền trong CONNECT, tách ClientHello thành nhiều TLS record quanh SNI) mới được kiểm bằng test offline. Một server TLS thật (SslStream/SChannel trên loopback) đã nhận ClientHello bị tách và bắt tay xong. Còn hai câu hỏi chưa ai trả lời: nhà mạng đang chặn có bị lách thật không, và ra dây mỗi record có thành segment riêng không (bắt bằng Wireshark).
- **Vị trí:** `libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/TlsHandshakeChunkingStream.cs`, `StreamExtensions.cs` (`WriteSplitAroundAsync`).
- **Vì sao:** DPI có trạng thái (ráp lại TCP, ráp cả các mảnh handshake) thì cách tách record không qua được; khi đó cần thêm mức gói qua WinDivert (gói giả TTL thấp, checksum sai) như GoodbyeDPI.
- **Ngày ghi:** 2026-10-03.

### Chế độ dự phòng "chỉ tách segment" cho Anti-DPI

- **Vấn đề:** vài server hoặc middlebox cũ (cân bằng tải, TLS nhúng) có thể từ chối ClientHello bị tách thành nhiều record, dù RFC 8446 cho phép. Hiện chưa có chế độ chỉ tách TCP segment mà giữ nguyên record. Ngoài ra, ClientHello gửi lại sau HelloRetryRequest đi nguyên, không bị tách.
- **Vị trí:** `TlsHandshakeChunkingStream.cs` (`SplitAroundServerName`), cờ `Outbound.AntiDpiTls` ở `src/ProxyDivert.Core/Routing/Models/Outbound.cs`.
- **Vì sao:** finding của review ngày 2026-10-03; chỉ đáng làm nếu gặp trang hỏng khi bật cờ.
- **Ngày ghi:** 2026-10-03.

### CLI chưa có cờ Anti-DPI

- **Vấn đề:** `ProxyDivert.Cli` dựng outbound Direct/HTTP/SOCKS mà không có option bật Anti-DPI; chỉ bật được trong app WPF.
- **Vị trí:** `src/ProxyDivert.Cli/Program.cs:82-117`, `src/ProxyDivert.Cli/CliOptions.cs`.
- **Vì sao:** nếu CLI được dùng ngang WPF thì thiếu tính năng; đợt 2026-10-03 chỉ làm WPF.
- **Ngày ghi:** 2026-10-03.

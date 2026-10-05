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

### Dọn source biến thể Anti-DPI không còn policy nào dùng

- **Vấn đề:** `OutboundRegistry` giữ một source riêng cho mỗi cặp chunk size hiệu lực mà policy yêu cầu. Khi user sửa hoặc bỏ cài đặt Anti-DPI của policy, source biến thể cũ không bị dọn tới khi outbound đó bị sửa, bị xoá hoặc app tắt.
- **Vị trí:** `src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs` (`ReconcileAsync`, `InstanceKey`).
- **Vì sao:** với Direct/HTTP/SOCKS thì rẻ, nhưng số source tăng dần theo số lần sửa. Hướng sửa: cho `ReconcileAsync` biết danh sách policy để tính tập key còn dùng. Hướng gọn hơn về lâu dài: thư viện Proxy nhận tuỳ chọn Anti-DPI theo từng lượt connect, registry trở lại một Id một source.
- **Ngày ghi:** 2026-10-05.

### Huỷ thay đổi không đồng bộ lại tunnel VPN

- **Vấn đề:** thêm một outbound VPN rồi bấm Connect khi chưa Áp dụng & Lưu, sau đó bấm Huỷ thay đổi: outbound biến khỏi cấu hình nhưng tunnel vẫn sống (không ai gọi lại `Vpn.SyncAsync`), và tab Outbounds không còn hàng nào để Disconnect, kéo dài tới lần Apply sau. Chiều ngược lại: outbound bị xoá rồi được khôi phục thì mang `KeepConnected` của lần apply, có thể lệch với tunnel thật.
- **Vị trí:** `src/ProxyDivert.Wpf/Services/AppServices.cs` (`DiscardChanges`), `src/ProxyDivert.Core/Hosting/ProxyDivertSession.cs` (`SetVpnConnectedAsync`).
- **Vì sao:** finding của review ngày 2026-10-05; hiếm gặp. Hướng sửa: sau khi discard thì đưa session một lượt đồng bộ VPN theo cấu hình đã khôi phục.
- **Ngày ghi:** 2026-10-05.

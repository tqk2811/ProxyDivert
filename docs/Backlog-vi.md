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

### Secure DNS: chế độ máy chủ DNS cục bộ

- **Vấn đề:** Secure DNS hiện bắt gói UDP/53 bằng WinDivert. Phần mềm có driver lọc mạng riêng (ExitLag chế độ WFP, thấy trên máy thật ngày 2026-10-05; chuyển ExitLag sang NDIS thì hết) lấy gói trước, nên không truy vấn nào tới app và không bắt lại được: thứ tự giữa WinDivert và driver khác do trọng số sublayer WFP quyết định, app không đổi được.
- **Hướng làm:** thêm chế độ thứ hai, app mở DNS server tại `127.0.0.1:53` và `[::1]:53`, đặt DNS của card mạng về đó khi bật engine và khôi phục khi tắt (kèm tự khôi phục lúc khởi động nếu lần trước app chết giữa chừng). Quyết định theo tên miền giữ như `RoutingPolicyResolver.ResolveDns`; câu không khớp chuyển tới DNS gốc. Mất pid người hỏi, nên checkbox "DNS của chính tiến trình" chỉ còn tác dụng ở chế độ bắt gói. Chưa kiểm ExitLag có để yên traffic loopback không.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs`, `src/ProxyDivert.Core/Engine/SecureDnsQueryDecider.cs`, `src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs`.
- **Vì sao:** người dùng game hay chạy ExitLag hoặc phần mềm tương tự song song.
- **Ngày ghi:** 2026-10-05.

### Secure DNS: cảnh báo khi không thấy truy vấn DNS nào

- **Vấn đề:** khi phần mềm khác lấy mất DNS/53 (vd ExitLag), Secure DNS im lặng không làm gì, log không có dòng nào báo.
- **Hướng làm:** có policy bật Secure DNS mà engine chạy khoảng một phút không thấy truy vấn UDP/53 nào thì log Warning một lần, gợi ý kiểm phần mềm can thiệp DNS.
- **Vị trí:** `src/ProxyDivert.Core/Engine/SecureDnsQueryDecider.cs` (đếm truy vấn), `RedirectEngine.cs`.
- **Ngày ghi:** 2026-10-05.

### Secure DNS: TCP/53 và circuit breaker

- **Vấn đề:** (1) trả lời DNS quá dài làm Windows hỏi lại qua TCP/53, mà TCP/53 không bị chặn nên đi DNS thường. (2) DoH lỗi liên tiếp thì mỗi truy vấn vẫn chờ hết timeout; nên có circuit breaker chuyển sang "cho qua" ngay lúc quyết định khi policy cho phép rơi về DNS thường.
- **Vị trí:** `DnsOverHttpsMiddleware.cs` (chỉ xét UDP), `src/ProxyDivert.Core/Engine/DohHealth.cs`.
- **Ngày ghi:** 2026-10-05.

### Pump NETWORK: nhận gói theo lô (RecvEx/SendEx)

- **Vấn đề:** pump nhận và gửi từng gói một. Batch giảm số syscall nhưng làm gói đầu lô chờ cả lô, tăng trễ từng gói; vì vậy chưa làm.
- **Hướng làm:** chỉ làm nếu sau khi thu hẹp filter (2026-10-08) đo vẫn thấy pump bão hoà CPU; giới hạn lô ≤ 8, flush ngay sau mỗi lần recv. Binding `RecvEx` đã có, thiếu `SendEx`.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs`, `Native/WinDivertNative.cs`.
- **Ngày ghi:** 2026-10-08.

### SYN chưa theo dõi của process không liên quan vẫn chờ một lượt quét bảng kernel

- **Vấn đề:** mọi SYN chưa theo dõi của cả máy (kể cả app không bị chuyển hướng) bị giữ tới khi quét bảng kernel xong, trễ thiết lập connection vài ms. Pump không còn bị chặn, nhưng connection của app khác vẫn chậm chút.
- **Hướng làm:** bỏ qua quét khi tracker không có pid target nào; hoặc cache port nguồn đã biết không thuộc target; hoặc chờ SOCKET event vài ms trước khi quét. Đo trễ SYN của app không target trước/sau.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs` (`HoldSynForReconcile`), `Flow/CoalescedSweep.cs`.
- **Ngày ghi:** 2026-10-08.

### Test filter chưa kiểm được cú pháp với driver

- **Vấn đề:** `RedirectFilterTests` chỉ so chuỗi, không chứng minh WinDivert chấp nhận filter.
- **Hướng làm:** thêm binding `WinDivertHelperCompileFilter` (không cần admin) và test biên dịch từng tổ hợp.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Tests/RedirectFilterTests.cs`.
- **Ngày ghi:** 2026-10-08.

### Bảng Connections vẫn dựng lại 500 dòng mỗi 250ms khi đang mở tab

- **Vấn đề:** bộ đếm byte sửa tại chỗ nên không biết rẻ khi nào dữ liệu đổi; hiện chỉ đỡ nhờ dừng timer khi cửa sổ ẩn hoặc không mở tab.
- **Vị trí:** `src/ProxyDivert.Wpf/ViewModels/ConnectionsViewModel.cs` (`Refresh`).
- **Ngày ghi:** 2026-10-08.

### Test RelayTeardownTests chập chờn khi máy bận

- **Vấn đề:** `DisposingTheRelayResetsAConnectionItHasAccepted` rớt một lần (15s) khi chạy cả bộ lúc máy bận, chạy lại thì xanh.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Tests/RelayTeardownTests.cs:64`.
- **Ngày ghi:** 2026-10-08.

# Backlog

Việc còn treo và việc nên làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit với bản sửa.

Các mục mang mã A/B/C/D/E (ở cuối file) đến từ đợt rà soát toàn repo ngày 2026-09-08, trước đây nằm trong `docs/FixList-vi.md` (lịch sử: `git log -p -- docs/FixList-vi.md`). A = lỗi tiềm ẩn ProxyDivert, B = WinDivert, C = cách dùng VpnClient, D = TqkLibrary.Proxy, E = thiết kế (OOP, tái sử dụng, test). Mức: `Cao` = mất dữ liệu / mất mạng / rò tài nguyên tích luỹ trong dùng bình thường; `Vừa` = sai trong tình huống thường gặp nhưng có đường tránh; `Thấp` = hiếm gặp hoặc chỉ tốn tài nguyên; với mục E: `Must` = đã gây lỗi thật hoặc chặn việc mở rộng đã dự định, `Should` = giảm trùng lặp hoặc cho phép test, `Nice` = sạch hơn, không đổi hành vi. Link của các mục này được đối chiếu lại với code ngày 2026-10-09; link không có số dòng (nhóm D7 và phần lớn E) là chưa dò lại dòng, tra theo tên ký hiệu.

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
- **Hướng làm:** chỉ làm nếu sau khi thu hẹp filter (2026-10-08) đo vẫn thấy pump bão hoà CPU; giới hạn lô ≤ 8, flush ngay sau mỗi lần recv. Binding `RecvEx` đã có, thiếu `SendEx`. Đo ngày 2026-10-09 (Release, 32 thread đốt CPU khi đang chơi game): gói chờ tối đa < 0,4ms, mỗi lần recv gần như chỉ có 1 gói, nên chưa cần. Nhận cả lô rồi đẩy sang thread khác xử lý còn tệ hơn: thêm một lần đánh thức thread, và nhiều worker thì đảo thứ tự gói cùng flow.
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

### Lối tắt pump là bản chép tay quyết định của NAT

- **Vấn đề:** `UntrackedEgressBypass` lặp lại điều kiện của `NatRedirectMiddleware` (mask giao thức, cổng relay, FlowKey, kiểm lại). NAT đổi mà quên sửa bypass thì gói bị thả sai; thêm middleware built-in mới vào `AddTrailingMiddlewares` cũng không tự tắt bypass. Nên chuyển quyết định vào NAT (vd `CanPassUntouched`) hoặc thêm test chạy pipeline thật để kiểm hai bên khớp nhau; thiếu cả test cổng relay = 0 và bypass ném exception.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UntrackedEgressBypass.cs`, `ProcessRedirector.cs` (`CreateBypass`).
- **Ngày ghi:** 2026-10-08 (finding review đợt fast path).

### Một nửa cặp pump chết thì cả đường chuyển hướng hỏng mà không ai dừng engine

- **Vấn đề:** sau khi tách handle chiều đi / chiều về, nếu riêng pump `*-reply` dừng bất thường (hoặc một trong hai pump chiều đi `*-tcp` / `*-udp` sau đợt tách theo giao thức, khi đó cả giao thức đó mất chuyển hướng) thì chiều đi vẫn đổi SYN sang relay nhưng không ai đổi gói trả về, mọi kết nối được chuyển hướng treo; log chỉ nói "traffic on this handle is no longer redirected". ProxyDivert.Core cũng không xử lý `PumpStopped`. Nên dừng/báo cả redirector khi một nửa cặp chết. Kèm ràng buộc ngầm: pipeline chiều về chỉ có NAT, stage mới muốn thấy gói trả về sẽ không thấy.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs` (`OnPumpStopped`, `StartRelayReplyPump`).
- **Ngày ghi:** 2026-10-08 (finding review đợt tách handle).

### Mảnh IPv4 thứ hai trở đi bị NAT ghi đè "port"

- **Vấn đề:** parser và NAT không kiểm fragment. Từ mảnh thứ hai trở đi của gói IPv4 bị phân mảnh không có header TCP/UDP, nên hai byte mà code coi là port thực ra là payload; `WritePort` vẫn ghi đè lên đó và làm hỏng gói. TCP hầu như không bị phân mảnh, nhưng UDP lớn (game, VPN) có thể gặp. Checksum cộng dồn đã từ chối fragment, còn đường `SetSource`/`SetDestination` thì chưa.
- **Hướng làm:** parser đánh dấu fragment (flags/offset `& 0x3FFF`); NAT bỏ qua (thả nguyên hoặc cho đi Direct) mảnh không phải mảnh đầu, kèm test.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs:161` (`WritePort`), `Packet/PacketParser.cs`, `TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs`.
- **Ngày ghi:** 2026-10-09 (phát hiện khi phân tích checksum cộng dồn).

### Đường full của pump còn cấp phát 230–600 B mỗi gói

- **Vấn đề:** đường bypass đã không cấp phát (IpAddressKey), nhưng gói đi đường full vẫn tạo `PacketContext` (`PacketPump.cs:184`), `ParsedPacket`, một `byte[]` + `IPAddress` mỗi lần đọc `Source`/`Destination` (`ParsedPacket.cs:26`, header Ipv4/Ipv6), `GetAddressBytes()` trong `WriteIp`, và `NatEntry`/`Slot` mới mỗi gói trong `NatTable.Upsert`. Log đo ngày 2026-10-09: pump `*-reply` khoảng 230–600 B/gói.
- **Vì sao:** chỉ ảnh hưởng traffic đi qua proxy (không ảnh hưởng game Direct), nhưng tải nặng qua proxy sẽ đẩy GC gen0 lên, mà GC dừng cả thread pump.
- **Hướng làm:** NAT dùng `IpAddressKey` và FlowKey dựng từ key; `Upsert` giữ slot cũ khi entry không đổi (đọc kỹ `Slot`/`MarkClosed` vì thay slot đang reset hạn); cân nhắc dùng lại `PacketContext`.
- **Ngày ghi:** 2026-10-09.

### Gợi ý lõi cho pump trên máy nhiều processor group

- **Vấn đề:** máy > 64 logical processor có nhiều group; `PumpCoreHints` có thể gán ideal processor ở group khác với group của thread, `SetThreadIdealProcessorEx` thất bại (chỉ log Debug, pump vẫn chạy). Ranking cũng không lọc theo affinity của process.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Helpers/PumpCoreHints.cs`.
- **Ngày ghi:** 2026-10-09 (finding review).

### A8. Mode nghe socket quét bảng kernel hai lần cho mỗi pid mới (Vừa)

- **Vấn đề:** `AcceptPid` gọi `_shouldTrackProcess(pid)` (→ `ProcessRuleTracker.TryAttach` → raise `ProcessAttached` → ghi `Channel` của `TrackedPidQueue`) trước khi ghi `_pidDecisions[pid]`. Consumer của hàng đợi gọi `AddProcess` lọt vào khe đó thì `PrePopulateForPid` chạy lần hai (quét bảng toàn máy). Luồng pump đã hết phần nặng (không còn `RebuildResolver`, `AddTrackedProcessId` chỉ ghi một `Channel`), lần quét thứ hai đã dời sang luồng consumer và thường tự thoát sớm vì `AddProcess` thấy `already.Accepted`, nhưng đó là may, không phải bảo đảm.
- **Vị trí:** [SocketTracker.cs:260](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L260) (callback), [SocketTracker.cs:268](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L268) (ghi verdict), [SocketTracker.cs:287-298](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L287-L298) (`AddProcess`), [RedirectEngine.cs:495](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L495) (`OnProcessAttached`).
- **Vì sao:** quét bảng ngay trên đường trễ SYN→accept, đúng thứ đã tối ưu ngày 2026-09-07. `TrackedPidQueueTests` chỉ phủ hàng đợi, chưa phủ ca này.
- **Cách sửa:** sửa ở submodule WinDivert: ghi verdict trước khi gọi callback, hoặc `AddProcess` bỏ qua khi `AcceptPid` đang xử lý cùng pid; hoặc `ShouldRedirect` trả verdict thay vì raise sự kiện trong callback.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11).

### A10. Các tab không đồng bộ danh sách với nhau (Cao, còn hở)

- **Vấn đề:** đã sửa bằng cách `MainViewModel` gọi `Reload()` của ba tab khi đổi tab (giữ selection khi nạp lại); phần tham chiếu treo khi xoá policy/outbound do mô hình dữ liệu bịt. Còn hở: chưa chạy thử trên UI thật, không test nào chạm `Reload()`; fallback `Policies[0]` ở `ProcessFilterViewModel` vẫn còn, chỉ an toàn nhờ danh sách đã nạp lại; `Reload()` dựng lại toàn bộ row VM mỗi lần đổi tab nên hàng nào sau này giữ state riêng (không proxy xuống model) sẽ mất state.
- **Vị trí:** [MainViewModel.cs:291-334](../src/ProxyDivert.Wpf/ViewModels/MainViewModel.cs#L291-L334), [ProcessesViewModel.cs:104](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L104), [RulesViewModel.cs:66](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L66), [OutboundsViewModel.cs:64](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L64), [ProcessFilterViewModel.cs:105-106](../src/ProxyDivert.Wpf/ViewModels/ProcessFilterViewModel.cs#L105-L106).
- **Vì sao:** tạo policy rồi gán cho tiến trình là luồng thao tác chính của app.
- **Cách sửa:** chạy thử UI (thêm policy ở Rules thì editor filter tab Processes thấy ngay; xoá policy đang được filter dùng) và thêm một test cho `Reload()` giữ selection.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11).

### A16. Nhóm mức Thấp (ProxyDivert)

- **Vấn đề:** các lỗi nhỏ, mỗi gạch một việc:
  - Regex của user trong luật định tuyến không có trần thời gian ([HostPredicate.cs:87-100](../src/ProxyDivert.Core/Routing/Compiled/HostPredicate.cs#L87-L100)). Đã quyết định KHÔNG đặt `matchTimeout` (timeout làm luật âm thầm ngừng áp dụng khi máy bận, bài học của `RegexBudget`). Rủi ro còn lại: pattern backtracking thảm hoạ treo luồng định tuyến vô hạn. Chọn một trong: `RegexOptions.NonBacktracking`, giới hạn độ dài/độ phức tạp pattern lúc lưu, hoặc cảnh báo ở tab Rules; không thêm timeout.
  - `DomainSuffix` với pattern `.example.com` không bao giờ khớp: nhánh `DomainSuffix` của `NamePredicate` trong [HostPredicate.cs](../src/ProxyDivert.Core/Routing/Compiled/HostPredicate.cs) (và `pattern.Trim()` ở dòng 49) chỉ cắt khoảng trắng. Thêm `TrimStart('.')`.
  - `OnProcessStopped` chỉ detach con trực tiếp, không lan xuống cháu: [ProcessRuleTracker.cs:395-405](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L395-L405), `Detach` ([:380](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L380)) không cascade nên cháu ở lại bảng tracked vĩnh viễn. Lặp tới khi không đổi như `FollowParents`; `ProcessRuleTrackerTests` chưa phủ ca này.
  - `AttachProcessId` có thể ném `KeyNotFoundException` khi `Detach` xen giữa: [ProcessRuleTracker.cs:253](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L253) (`if (!_tracked.TryAdd(...)) return _tracked[processId];`). Nằm trên đường `--pid` của CLI và nút Launch suspended. Dùng `TryGetValue`.
  - Endpoint proxy phân giải bằng `Dns.GetHostAddresses` rồi lấy `addresses[0]`: [OutboundUrl.cs:45](../src/ProxyDivert.Core/Outbounds/Builders/OutboundUrl.cs#L45), gọi lúc dựng instance (registry), không còn trên đường mỗi kết nối. Còn lại: không ưu tiên IPv4 khi `Ipv6Support == Disabled`, không cache, vẫn đồng bộ lúc dựng (cùng gốc với E5.4).
  - Cây tiến trình khi PID tái dùng thành vòng cha–con: [ProcessesViewModel.cs:130](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L130) (`BuildTree`), [:174](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L174) (`FilterName`, đã có `MaxParentSteps = 64`). Không còn treo, nhưng lúc nối chưa có tập-đã-thăm: mọi node trong vòng đều "có cha" nên không node nào là root, cả vòng biến mất khỏi cây dù tiến trình đang redirect. Node có cha nhưng không tới được root thì coi là root.
  - `EtwProcessEventSource.Dispose` chặn `_pump?.Wait(2s)` ([EtwProcessEventSource.cs:192](../src/ProxyDivert.Core/Processes/EtwProcessEventSource.cs#L192)) và nằm trong chuỗi `ProcessInventory.DisposeAsync` → `StopEvents` → `events.Dispose()` ([ProcessInventory.cs:381](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L381)): cầu sync còn sót của chuỗi dispose, chặn luồng tới 2 giây lúc tắt. Cho nguồn sự kiện `IAsyncDisposable`.
  - Cache `Outbound.Address` có khe đua nhỏ ([Outbound.cs:57-77](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L57-L77)): luồng kết nối đọc `_kind/_url` xong, luồng UI sửa ô rồi vứt cache, luồng đọc mới gán `Parsed(<giá trị cũ>)` đè lên nên địa chỉ cũ sống tới lần vứt cache sau; hiếm, Save/Clone dựng bản mới. `AddressProblem` thì parse lại mỗi lần get, không dùng cache.
  - `ConditionGroup.Clone` ([ConditionGroup.cs:14-19](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionGroup.cs#L14-L19)) vẫn ném trên phần tử `null` trong `Children` trong khi `Answer` có guard; `Normalize` chỉ bịt ở cửa đọc file, `AppConfig` dựng trong bộ nhớ không qua `Normalize` vẫn dính NRE. Thêm guard cho khớp `Answer`.
  - Comment ở [VpnProfileReader.cs:204](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L204) còn nói "A secret in the outbound's own box is encrypted at rest", mà DPAPI đã gỡ ngày 2026-09-09.
- **Vị trí:** xem từng gạch.
- **Vì sao:** hiếm gặp hoặc chỉ tốn tài nguyên, nhưng vài gạch (cháu không detach, vòng PID biến mất khỏi cây, NRE `AttachProcessId`) có thể lộ ra lúc dùng thật.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### A21. `OutboundRegistry.DropAsync` bỏ rơi instance đang dựng dở (Cao)

- **Vấn đề:** `ReconcileAsync`/`DiscardAsync`/`DisposeAsync` chen vào lúc một luồng kết nối đang dựng instance (`Lazy.Value` đang chạy `Build`: spawn wireproxy, quay số VPN) hoặc vừa lấy được entry mà chưa gọi `.Instance`. Entry đã rời bảng, `InstanceIfBuilt` là `null` (khi `Lazy` chưa hoặc đang dựng) nên không dispose gì, instance dựng xong không ai sở hữu. Sau khi registry đã `DisposeAsync`, một vòng giám sát còn sót vẫn `GetOrCreate` dựng được instance mới (không có cờ disposed).
- **Vị trí:** [OutboundRegistry.cs:100-106](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L100-L106) (`GetOrCreate`: `GetOrAdd` trả `Entry` rồi mới chạm `.Instance`), [:237-250](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L237-L250) (`DropAsync`: `TryRemove` rồi dispose), [:252-257](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L252-L257) (`DisposeAsync(Entry)`), [:323](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L323) (`InstanceIfBuilt`).
- **Vì sao:** đúng loại rò đã bịt ở chỗ khác: `wireproxy.exe` mồ côi giữ cổng SOCKS, session VPN sống mãi, qua đường Save cấu hình trong lúc kết nối đầu tiên đang dựng (lúc vừa sửa outbound vừa thử).
- **Cách sửa:** `DisposeAsync(Entry)` chờ `entry.Instance` (Lazy block tới khi dựng xong) rồi dispose, như `UdpProxyForwarder.DisposeAsync(Lazy)` đang làm; `GetOrCreate` sau khi `.Instance` xong kiểm entry còn trong `_instances` không, không còn thì dispose và `GetOrCreate` lại; cờ `_disposed` từ chối `GetOrCreate` sau `DisposeAsync`. Test: hai luồng, một `GetOrCreate` với builder chậm, một `ReconcileAsync` đổi chữ ký giữa chừng, khẳng định instance bị dispose đúng một lần.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### A22. `PortTunnel` để lại association SOCKS5 mồ côi khi associate hỏng, bị huỷ hoặc xong muộn (Vừa)

- **Vấn đề:** (1) `AssociateAsync` lấy được `tunnel` từ `GetUdpAssociateSourceAsync` rồi `tunnel.AssociateAsync(ct)` ném (proxy từ chối, hoặc `_cts.Cancel()` từ `DisposeAsync`) thì `catch` chỉ log, không dispose `tunnel`. (2) Associate xong sau mốc 1 giây của `DisposeAsync` thì `_tunnel?.Dispose()` chạy khi `_tunnel` còn `null`, rồi `AssociateAsync` mới gán `_tunnel`: kết nối điều khiển SOCKS5 + socket UDP không ai đóng.
- **Vị trí:** [UdpProxyForwarder.cs:244-270](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L244-L270) (`AssociateAsync`), [:329-340](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L329-L340) (`DisposeAsync`: `_ready.WaitAsync(1s)` rồi `_tunnel?.Dispose()`).
- **Vì sao:** reaper dispose tunnel idle liên tục; proxy ở xa (>1 s để ASSOCIATE) hoặc proxy từ chối UDP là chạm ngay. Finalizer của `BaseProxySourceTunnel` (D5) cũng đi qua đường này.
- **Cách sửa:** bọc phần sau khi có `tunnel` bằng `try/catch` riêng, ném thì `tunnel.Dispose()`; `DisposeAsync` chờ `_ready` không giới hạn (nó đã bị huỷ qua `_cts`) hoặc đọc lại `_tunnel` sau khi chờ; hoặc gán `_tunnel` trước rồi mới `AssociateAsync`.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### A23. Không ai trong app nghe `PumpStopped` (Vừa)

- **Vấn đề:** thư viện đã bắn `IProcessRedirector.PumpStopped`, nhưng `grep` toàn `src/` không có subscriber. Pump gói tin chết (32 lỗi `Recv` liên tiếp, hoặc handle bị đóng) thì engine vẫn báo `IsRunning`, redirect ngừng trong im lặng. Nửa "báo UI" chưa từng được làm. Liên quan mục "Một nửa cặp pump chết..." ở trên và B17.
- **Vị trí:** [ProcessRedirector.cs:81](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L81) (event), [:233-240](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L233-L240) (`OnPumpStopped`); `RedirectEngine.BuildRun` chưa subscribe.
- **Cách sửa:** `RedirectEngine` subscribe trong `BuildRun`, log `Error` và đẩy trạng thái lên `MainViewModel` (thanh gạt về trạng thái lỗi, thông báo), tuỳ chọn tự Stop/Start lại một lần.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### B3. `NatTable.Remove` không có ai gọi: còn nửa UDP (Cao)

- **Vấn đề:** nhánh TCP đã đủ (`MarkClosed` từ `TcpConnectClosed`, `Find` tự loại entry hết hạn, có vòng quét). Entry UDP (proto 17) vào cùng bảng qua `Upsert` nhưng không ai gọi `MarkClosed`/`Remove` cho chúng (`UdpBindRemoved` không được nối vào `_nat`), nên bệnh gốc (cổng nguồn tái dùng trả đích cũ, bảng không nhả) còn nguyên ở nửa UDP; `NatTable.Remove` vẫn không có caller. Chưa có test hết hạn (test chỉ dùng `NatTable` làm phụ kiện). Khi Windows tái dùng cổng nguồn mà SYN mới bị lỡ, `Find` trả entry cũ thì bỏ qua nhánh luồng thoát, NAT giữa dòng và kết nối chết; bộ nhớ bị chặn trên ~262k entry nhưng không bao giờ nhả.
- **Vị trí:** [NatTable.cs:42-97](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatTable.cs#L42-L97), [ProcessRedirector.cs:184](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L184) (chỉ nối `TcpConnectClosed` → `MarkClosed`), [SocketTracker.cs:413](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L413) và [:674](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L674) (nơi bắn `UdpBindRemoved`).
- **Cách sửa:** nối `UdpBindRemoved` → `MarkClosed`, hoặc cho entry UDP TTL riêng trong vòng quét; thêm test hết hạn cho cả hai proto.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### B5. UDP relay khoá upstream theo cổng nguồn, đích đóng băng lần đầu (Vừa)

- **Vấn đề:** `GetOrAdd(UpstreamKey(srcPort, family), _ => new UdpUpstream(entry.OriginalDestination))`: một socket UDP nói với nhiều đích (DNS nhiều server, QUIC, STUN) thì datagram thứ hai trở đi gửi sai địa chỉ. `_upstreams` không bao giờ evict. Trong ProxyDivert nhánh này chỉ chạm khi `HandleUdpDatagram` trả payload ở trường hợp Direct-sau-redirect hiếm ([UdpFlowRouter.cs](../src/ProxyDivert.Core/Engine/UdpFlowRouter.cs)), còn proxy/VPN đi qua `UdpProxyForwarder`; vẫn là lỗi thư viện.
- **Vị trí:** [UdpRelayServer.cs:91](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L91), [:103](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L103) (`UpstreamKey`).
- **Cách sửa:** gửi thẳng `SendAsync(payload, entry.OriginalDestination)` trên một socket theo cổng, thêm idle-eviction.
- **Ngày ghi:** 2026-09-08.

### B14. Nhóm mức Thấp (WinDivert)

- **Vấn đề:** các lỗi nhỏ trong submodule, mỗi gạch một việc:
  - Parser không kiểm đủ byte cho header TCP/UDP, không xét fragment: [PacketParser.cs:20-24](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/PacketParser.cs#L20-L24). Đã có `ihl < 20 || ihl > length`; còn thiếu `ihl + 20/8 <= length` và bỏ qua fragment-offset != 0. `Tcp`/`Udp` view đọc thẳng tại `TransportHeaderOffset` không kiểm biên ([ParsedPacket.cs:20-21](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs#L20-L21)), buffer pump dùng lại nên đọc rác của gói trước. Gắn với mục "Mảnh IPv4 thứ hai trở đi" ở trên.
  - IPv6 extension header không được duyệt, gói TCP đứng sau Hop-by-Hop bị cho qua không redirect: [PacketParser.cs:28-33](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/PacketParser.cs#L28-L33). Duyệt chuỗi extension chuẩn hoặc Drop rõ ràng.
  - `ExpireTick == 0` trùng giá trị thật một lần mỗi 49.7 ngày: [SocketTracker.cs:665](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L665), [TcpFlowState.cs:7](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/Models/TcpFlowState.cs#L7). Dùng `long?` hoặc `bool IsClosing`.
  - `SocketClose` lặp lại cho cùng flow TCP gia hạn `ExpireTick` mãi: [SocketTracker.cs:658-668](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L658-L668). (Phần UDP đã xử lý: `SocketClose` giờ gỡ bind UDP.)
  - Bind UDP trên ANY che phủ mọi tiến trình cùng cổng: [SocketTracker.cs:192-194](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L192-L194).
  - `DnsMessageParser` cấp phát `List(answerCount)` theo số do gói tin quyết định: [DnsMessageParser.cs:55](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsMessageParser.cs#L55). `Math.Min(answerCount, 64)`.
  - `RelayToAsync` không truyền half-close: [RedirectedTcpConnection.cs:84](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L84). `Shutdown(Send)` phía còn lại rồi đợi chiều kia.
  - `TcpRelayServer.Dispose` dispose CTS khi handler còn chạy: [TcpRelayServer.cs:198-211](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L198-L211) (đã có danh sách kết nối đóng trước, cần kiểm lại còn hở không).
  - `ProcessTreeMonitor.Dispose` không gỡ event ([ProcessTreeMonitor.cs:132](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L132)); `SemaphoreSlim` trong [DnsOverHttpsMiddleware.cs:114](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs#L114) không dispose.
  - `SuspendedProcess.Resume()` sau `Dispose()` ném `Win32Exception` khó hiểu: [SuspendedProcess.cs:29-45](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/SuspendedProcess.cs#L29-L45). Set `_resumed = true` trong `Dispose`.
  - Dead code: `WinDivertNative.RecvEx`/`HelperCompileFilter`/`Ntohs`/`Htons` ([WinDivertNative.cs:35-90](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/WinDivertNative.cs#L35-L90)), `IpHlpApi.EnumerateProcessTcpFlows`/`EnumerateProcessUdpBinds` ([IpHlpApi.cs:115-122](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/IpHlpApi.cs#L115-L122)). Lưu ý: `RecvEx` và `HelperCompileFilter` có thể được dùng lại bởi hai mục "Pump NETWORK: nhận gói theo lô" và "Test filter" ở trên; kiểm trước khi xoá.
- **Vị trí:** xem từng gạch.
- **Vì sao:** hiếm gặp hoặc chỉ tốn tài nguyên.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### B16. `SocketTracker.Dispose` `Clear()` bảng handle sau khi chờ, hở khe với `AddProcess` (Thấp)

- **Vấn đề:** `TryAdd` của `AddProcess` lọt vào sau hai vòng shutdown của `Dispose`, nhưng `Clear()` chạy giữa `TryAdd` và `TryRemove` nên re-check thấy `_disposed` mà `TryRemove` trượt: không ai `Shutdown()`, pump per-pid nằm trong `TryRecv` cho tới sự kiện socket kế tiếp của pid đó (có thể không bao giờ), rò 1 handle + 1 thread. Khe rất hẹp.
- **Vị trí:** [SocketTracker.cs:713](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L713) (`_pidHandles.Clear()`), [:342-357](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L342-L357) (`AddProcess`: `TryAdd` rồi re-check `_disposed` + `TryRemove`).
- **Cách sửa:** bỏ `Clear()`, hoặc `Dispose` `TryRemove` từng entry trong chính vòng shutdown.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### B17. Socket pump bỏ cuộc sau 32 lỗi mà entry vẫn ở `_pidHandles` (Vừa)

- **Vấn đề:** pump thoát và đóng handle, nhưng entry còn trong `_pidHandles` nên `IsTrackedProcess(pid)` vẫn trả `true`, không có sự kiện tương đương `PumpStopped` cho lớp SOCKET, và `AddProcess` lần sau thấy đã có entry nên không mở lại: pid ngừng được theo dõi trong im lặng (cùng họ với A23).
- **Vị trí:** [SocketTracker.cs:556-558](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L556-L558) (vòng `TryRecv`: `++failuresInARow >= MaxRecvFailuresInARow` thì `break`).
- **Cách sửa:** khi bỏ cuộc thì `TryRemove` entry và bắn sự kiện (`ProcessPumpStopped(pid)`) để host quyết mở lại hay báo.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### C3. `InTunnelResolver` bind UDP mới mỗi query (Thấp, còn nửa app)

- **Vấn đề:** `VpnUdpClient.Connect` mỗi query + `UnbindUdp` trong `finally`; resolver bind tới 12 socket mỗi tên (2 lần × 3 server × A/AAAA). Nửa thư viện VpnClient đã sửa (cấp phát cổng gập vào dải 49152-65535, bỏ qua cổng còn trong bảng, `TryAdd` thay indexer; test `EphemeralPortAllocationTests`), nên bind-per-query không còn gây hỏng, chỉ còn tốn. Cố ý chưa làm phần "một `UdpConnection` sống lâu, khớp trả lời theo DNS id" vì E6.2 sẽ chuyển resolver này sang lib VpnClient.
- **Vị trí:** [InTunnelResolver.cs:125-142](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L125-L142) (`AskAsync`).
- **Cách sửa:** làm cùng E6.2.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11).

### C8. Nhóm mức Thấp (cách dùng VpnClient)

- **Vấn đề:**
  - `InTunnelResolver` bỏ luôn fallback server khi gặp SERVFAIL: [InTunnelResolver.cs:189](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L189) (`(reply[3] & 0x0F) != 0` trả null) và `return null` ở [:98](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L98) bỏ luôn server còn lại. Chỉ dừng khi NXDOMAIN (rcode 3).
  - Cache DNS khoá theo tên, không theo loại A/AAAA: [InTunnelResolver.cs:66](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L66), [:100](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L100). Khoá `(name, type)`.
  - Dial timeout tới proxy hiện ra như "cancelled": [VpnClientConnectSource.cs:50-53](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L50-L53) và [:68-71](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L68-L71) rethrow OCE vô điều kiện, trong khi timeout đến từ `cts.CancelAfter(ConnectTimeout)` ở [VpnDialer.cs:260](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.Tunnels/VpnDialer.cs#L260). Chỉ rethrow OCE khi token của caller thật sự bị huỷ, còn lại bọc `InitConnectSourceFailedException`.
  - File `.vpn` không stamp config nó trỏ tới: [OutboundSignature.cs:51](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L51), [VpnProfileReader.cs](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs) (`ReadIni`, dòng 298). Stamp cả file được tham chiếu.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### C9. Cửa sổ nhận của `TcpConnection` cứng 65535, không back-pressure (Vừa)

- **Vấn đề:** cửa sổ nhận vẫn quảng cáo 65535 dù không ai đọc `_recvQueue`, nên server cứ đẩy, hàng đợi nhận phình theo tốc độ server thay vì tốc độ tiến trình đọc. (Hai khuyết điểm kia của C1 cũ, `Abort()` → RST và timer FIN-WAIT-2, đã sửa.)
- **Vị trí:** [TcpConnection.cs:37](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.IpStack/Tcp/TcpConnection.cs#L37) (`const ushort ReceiveWindow = 65535`), dùng ở [:463](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.IpStack/Tcp/TcpConnection.cs#L463) và [:952](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.IpStack/Tcp/TcpConnection.cs#L952); `_recvQueue`.
- **Cách sửa:** cửa sổ = `max(0, cap − _recvQueue.Count)` và gửi window update khi tiến trình đọc; commit trong submodule VpnClient, kèm test.
- **Ngày ghi:** 2026-09-11.

### C10. Kết nối qua VPN in-process không bao giờ được half-close (Vừa)

- **Vấn đề:** `StreamTransferHelper` đã biến EOF một chiều thành FIN chiều kia, nhưng với outbound VPN in-process stream là `VpnNetworkStream`: tiến trình đóng socket (EOF phía client) thì tunnel không nhận FIN; pump proxy→client nằm chờ tới khi server xa đóng hoặc engine cancel. `HalfClose` chỉ tự xử lý được `NetworkStream`; `VpnNetworkStream` chỉ có `Abort()` và `Dispose()` → `CloseSend`, không có `CloseSend()` public. Proxy SOCKS/HTTP/wireproxy không bị vì stream là `NetworkStream`.
- **Vị trí:** [TcpConnectionRouter.cs:211](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs#L211) (chỉ truyền `shutdownClientSend`), [ConnectSourceExtensions.cs:37](../src/ProxyDivert.Core/Outbounds/Extensions/ConnectSourceExtensions.cs#L37) (`ShutdownSendWith(shutdownClientSend, null)`), [StreamTransferHelper.cs:126](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L126) và [:144](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L144) (`HalfClose`), [VpnNetworkStream.cs:71-76](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.Sockets/VpnNetworkStream.cs#L71-L76).
- **Cách sửa:** lộ `CloseSend()` public trên `VpnNetworkStream` (lib VpnClient); `VpnClientConnectSource` truyền nó vào tham số thứ hai của `ShutdownSendWith` (qua `ForwardAsync`). Test: `ForwardHalfCloseTests` thêm ca với stream giả không phải `NetworkStream`.
- **Ngày ghi:** 2026-09-11.

### C11. Huỷ giữa lúc dial in-tunnel để lại kết nối nửa mở trong stack (Vừa)

- **Vấn đề:** `WhenAny(Connected, Delay(∞, ct))` rồi `ThrowIfCancellationRequested()` ném OCE mà không `Abort()`/gỡ entry; `connection.Closed` chỉ chạy khi SYN retransmit bỏ cuộc, nên cổng + `TcpConnection` sống tới lúc đó. Mỗi lần Save đổi route (engine cancel các kết nối đang dựng) là một loạt.
- **Vị trí:** [TcpIpStack.cs:109-110](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.IpStack/TcpIpStack.cs#L109-L110).
- **Cách sửa:** `catch (OperationCanceledException) { connection.Abort(); throw; }` (`Closed` sẽ gỡ entry); commit trong submodule VpnClient, kèm test.
- **Ngày ghi:** 2026-09-11.

### D5. Nhóm TqkLibrary.Proxy core, phía client ProxyDivert dùng (Thấp)

- **Vấn đề:**
  - `HttpProxySource` dùng `_proxy.Host` (giữ ngoặc `[::1]`) thay vì `DnsSafeHost` như Socks4/5: [HttpProxySource.ConnectTunnel.cs:39-41](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L39-L41). HTTP proxy cấu hình bằng literal IPv6 resolve thất bại.
  - CONNECT không gửi header `Host` (RFC 7230 bắt buộc): [HttpProxySource.ConnectTunnel.cs:72-80](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L72-L80).
  - `BaseProxySourceTunnel` finalizer gọi `Dispose(false)` nhưng override vẫn chạm object managed: [BaseProxySourceTunnel.cs:16-25](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/BaseProxySourceTunnel.cs#L16-L25), [Socks5ProxySource.BaseTunnel.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs). Đường tới được: A22.
  - Nhánh netstandard2.0 không có timeout connect/handshake, một số chỗ không truyền token: [Socks5ProxySource.BaseTunnel.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs), [Socks4ProxySource.BaseTunnel.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks4ProxySource.BaseTunnel.cs).
  - `LocalProxySource` UDP chỉ IPv4 dù `IsSupportIpv6 = true`: [LocalProxySource.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.cs), [LocalProxySource.UdpTunnel.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.UdpTunnel.cs). Chỉ chạm nếu UDP Direct đi qua relay thay vì passthrough.
  - `LocalProxySource.ConnectTunnel`: `AllowIpv6 = false` mà host chỉ có AAAA thì `ConnectAsync(mảng rỗng)` ném `ArgumentException` khó hiểu: [LocalProxySource.ConnectTunnel.cs:70-84](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.ConnectTunnel.cs#L70-L84).
  - Chỉ 26/223 `await` có `ConfigureAwait(false)` (số đếm 2026-09-11); `async void _MainLoopListen`: [ProxyServer.cs:166](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L166). Nhánh Test ở [OutboundTester.cs:51-52](../src/ProxyDivert.Core/Outbounds/OutboundTester.cs#L51-L52) gọi từ UI, `ConfigureAwait(false)` đã có ở app nhưng continuation nội bên trong lib bị post về Dispatcher.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### D6. Phía server TqkLibrary.Proxy (chỉ `ProxyDivert.Cli --self-host-port`)

- **Vấn đề:** còn 7 mục (6 mục đã sửa trong `fix/wave2`). Test có sẵn ở `src/TestProxy/ServerTest/HttpProxyServerForwardingTest.cs` và `Socks5FailureReplyTest.cs`.
  - **Vừa**, nhánh 1xx: `_ResponseCanHaveBody` ([HttpProxyServer.cs:245-250](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L245-L250)) coi 1xx là response không body và kết thúc `_HttpTransfer`; `source_stream` bị dispose trước khi origin gửi response thật, nên client gửi `Expect: 100-continue` mất response. Gặp 1xx thì đọc tiếp response kế trên cùng upstream.
  - **Vừa**, `GetProxySourceAsync`/`GetConnectSourceAsync` vẫn nằm ngoài `try` ([Socks5ProxyServer.cs:160-161](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L160-L161), [Socks4ProxyServer.cs:149-152](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L149-L152)): outbound không dựng được (wireproxy chưa lên, VPN chưa quay số) vẫn là "connection tự đứt không lời giải thích". Kéo hai lời gọi vào `try`; thêm test cho nhánh SOCKS4.
  - **Vừa**, BIND thiếu reply thứ hai cả hai phía: `Socks5ProxyServer`, `Socks4ProxyServer` và `Socks5ProxySource.BindTunnel`. ProxyDivert không dùng BIND ở đường nào và không có cách kiểm thật ngoài tự viết cả hai đầu nên chưa làm; cần sửa đồng bộ cả ba.
  - **Vừa**, `PreReadAsync` đọc tới khi một lượt không thêm byte ([PreReadStream.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/PreReadStream.cs), `PreReadAsync` dòng 17, `PreReadLineAsync` dòng 53): peer gửi vài byte không CRLF rồi im thì `PreReadLineAsync` chờ vô hạn; `tcpClient.ReceiveTimeout` ([ProxyServer.cs:208](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L208)) không áp cho `ReadAsync`, nên slowloris giữ slot. Cần timeout riêng cho giai đoạn nhận diện giao thức. Nhánh SOCKS không chạm (nhận diện bằng 1 byte).
  - **Thấp**, `HeaderRequestParse`/`HeaderResponseParse` `Split(':')` vứt header có dấu `:` trong value: [HeaderRequestParse.cs:68](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/HeaderRequestParse.cs#L68). `Split(':', 2)`.
  - **Thấp**, `client_isKeepAlive` gán sau `continue` của nhánh 407: [HttpProxyServer.cs:84-92](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L84-L92).
  - **Thấp**, input SOCKS5 dị dạng ném exception thay vì reply (NMETHODS=0, domain length=0, VER không kiểm): [Socks5ProxyServer.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs), [Socks5_DSTADDR.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/Socks5_DSTADDR.cs).
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### D7. Các project TqkLibrary.Proxy mà ProxyDivert không dùng (SshCli, GlobalUnicast, Reverse)

- **Vấn đề:** lỗi thư viện, ProxyDivert không chạm tới (SSH dùng `SshNet`, không phải `SshCli`); ghi lại vì là lỗi thật của thư viện. Chưa dò lại từng dòng ngày 2026-10-09, tra theo ký hiệu. Mọi file nằm dưới `libs/TqkLibrary.Proxy/src/`.
  - **Cao**, SshCli: stdio của tiến trình ControlMaster redirect nhưng không đọc, ssh deadlock khi pipe đầy (`TqkLibrary.Proxy.SshCli/SshProcessRunner.cs`). Dùng `redirect: false` cho master.
  - **Cao**, GlobalUnicast: offset xoá địa chỉ sai (8 thay vì 6) và chọn NIC không lọc khi xoá, nên IPv6 tạm không bao giờ gỡ (`SlaccHelper.cs`).
  - **Cao**, GlobalUnicast: `SemaphoreSlim` static bị dispose theo instance, finalizer `Wait()` trên nó (`GlobalUnicastProxySource.cs`).
  - **Cao**, Reverse: `StreamCopy.PumpAsync` `WhenAny` rồi dispose cả hai, cắt cụt response (`Reverse/Client/StreamCopy.cs`); cùng gốc với E6.7.
  - **Cao**, Reverse: data channel không xác thực (`AuthSignature` khai báo nhưng client không set, server không kiểm), transport mặc định plaintext (`ReverseServer.cs`, `ReverseClient.cs`, `Protocol/Payloads.cs`).
  - **Vừa**, SshCli: password ghi plaintext ra `%TEMP%` suốt đời runner, helper `.cmd` trong thư mục user ghi được (`SshProcessRunner.cs`); mỗi kết nối cõng cứng 500ms (`OpenSshConnectSource.cs`).
  - **Vừa**, Reverse: session đóng nhưng control channel không bao giờ `DisposeAsync` (`ReverseServer.cs`, `ReverseClientSession.cs`); accept không giới hạn và byte định danh kênh không timeout, slowloris (`RawTcpReverseTransportServer.cs`, `HttpListenerWebSocketTransportServer.cs`); AspNetCore dispose `WebSocket` do framework sở hữu, cần `ownsSocket: false` (`AspNetCoreReverseTransportServer.cs`); accept loop HttpListener `catch { break; }` chết vì một exception lẻ; backoff không reset sau khi kết nối thành công, reset khi nhận `HelloAck` (`ReverseClient.cs`); `DialBindAsync` rò listener trên `Any:<random>` khi gửi `BindReady` thất bại.
  - **Vừa**, GlobalUnicast: interface ID sinh bằng `new Random()` (netstandard2.0 seed theo tick), `Task.Delay(3000)` trong semaphore bỏ qua cancellation; chọn prefix sai (bỏ DHCPv6/Manual, nhận nhầm ULA, không loại tentative/deprecated, không refresh khi interface đổi) (`SlaccHelper.cs`).
  - **Thấp**, Reverse: `BindReady.Address` luôn `0.0.0.0`, `BindAccepted.PeerAddress` không điền; `ToEndPoint()` parse địa chỉ rác giết cả session; `_ = SendAsync` nuốt lỗi async; `HandleFrameAsync` không giới hạn đồng thời; `WebSocketStream` coi message rỗng là EOF; `RawTcp.StopAsync` không dispose `_cts`.
  - **Thấp**, GlobalUnicast: `Console.WriteLine` trong thư viện dù có `ILoggerFactory`; `ConnectTunnel` bỏ qua token, socket v6-only nên host chỉ có A record fail.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08.

### E1.3. Sở hữu chỉ nằm trong doc, không trong kiểu, WinDivert (Should)

- **Vấn đề:** `IPacketPumpFactory.Create` ghi "pump takes ownership of handle" chỉ trong doc; `ProcessRedirectorFactory` luôn tạo `IDnsCacheLookup` dù `EnableDnsLookup=false`; 7 dòng `?.Dispose()` tay trong `ProcessRedirector`; `Func<IReverseDnsTable>` tự viết trong `RedirectServiceCollectionExtensions`; `AddTrackedProcessId` ném trước `Start`; `SocketTracker.Dispose` chờ tuần tự 1s/handle.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Interfaces/IPacketPumpFactory.cs`, `.../TqkLibrary.WinDivert.Redirect/ProcessRedirectorFactory.cs`, `ProcessRedirector.cs`, `DependencyInjection/RedirectServiceCollectionExtensions.cs`, `.../Flow/SocketTracker.cs` (`Dispose`).
- **Cách sửa:** `IProcessRedirectorFactory.Create` mở một `IServiceScope` cho session, mọi thứ per-session đăng ký Scoped, `Dispose` = dispose scope (DI đảm bảo thứ tự ngược). Queue pid trước `Start` rồi flush; `Dispose` shutdown tất cả rồi `WaitAll` một lần. Rủi ro thấp.
- **Ngày ghi:** 2026-09-08.

### E2.2. `SocketTracker` ôm 8 mối quan tâm, file churn nhất lib WinDivert (Must)

- **Vấn đề:** [SocketTracker.cs](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs) gồm: handle per-pid, mode machine-wide + `_pidDecisions`, bảng TCP/UDP, decode socket event, reconcile kernel + throttle, cleanup loop, pre-populate. Có 7 điểm rẽ `IsMachineWide`; `FakeTracker` trong test phải implement 13 member chỉ để test NAT (`src/ProxyDivert.Core.Tests/NatRedirectMiddlewareEscapedFlowTests.cs`).
- **Vì sao:** mọi lỗi race SYN/attach/trễ quét/RemoveProcess đều đổ về file này; reconcile gọi `IpHlpApi` static nên không test được.
- **Cách sửa:** `FlowTable` (thuần, 4 dictionary + `Record/Close/Reap(now)` + 4 event, test không cần driver); `SocketEventPump` (decode `WinDivertAddress` → `SocketEvent` record); `IKernelConnectionTable` bọc `IpHlpApi` + `KernelReconciler` (throttle, dùng `IClock`); `IProcessScope` (strategy) với `PerProcessHandleScope`/`MachineWideScope` xoá mọi `if (IsMachineWide)`; `SocketTracker` còn façade ~120 dòng; `ISocketTracker` thu về `IFlowLookup` + `IProcessScope`. Thứ tự: `FlowTable` (viết test trước khi dời, giữ ngữ nghĩa `TryAdd else overwrite` và `wasLive`) → kernel table → scope. Giải quyết luôn B4 (khoá `(pid, startTime)` trong `MachineWideScope`) và A8, B16, B17.
- **Ngày ghi:** 2026-09-08.

### E2.3. `NatRedirectMiddleware` gộp NAT + policy cổng + escaped flow + reset + dedupe log (Should)

- **Vấn đề:** ctor 11 tham số, whitelist cổng, escaped flow, dựng RST cùng nằm trong một class (`libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs`).
- **Cách sửa:** dùng chính pipeline: `TrackedFlowGate` (tracked? reconcile? dstPort filter, gắn `FlowOwner` vào context) → `EscapedFlowMiddleware` (pass/drop/reset) → `NatRewriteMiddleware` (chỉ Upsert + rewrite). Cần `PacketContext.Items` hoặc thuộc tính typed `FlowOwner?`. Test escaped flow sẵn có chạy lại được.
- **Ngày ghi:** 2026-09-08.

### E2.4. `ProcessRedirector` vừa orchestrator, vừa builder pipeline, vừa chính sách IPv6, vừa tuning driver (Should)

- **Vấn đề:** build pipeline v4/v6 lặp gần y hệt; quyết định IPv6 fallback và tham số queue driver nằm trong `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs`.
- **Cách sửa:** `NetworkPipelineFactory.Create(AddressFamily, RelayEndpoints, features)` gọi 2 lần; `Ipv6Policy.Resolve(requested, osSupportsIpv6, relayHasV6)` thuần, test được; `NetworkHandleOptions` truyền vào handle factory. Các hàm `Start*Pump` biến mất.
- **Ngày ghi:** 2026-09-08.

### E3.2. `VpnProtocol`: thêm một giao thức phải sửa 6 chỗ trong 3 file (Should)

- **Vấn đề:** switch dial trong `VpnClientProxySource` (`Required()` kiểm lúc dial thay vì lúc đọc), `VpnProfileReader` (ba chỗ), `VpnProfile` (union-bag tự nhận), `OutboundSignature`.
- **Vị trí:** `src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs` (hoặc file thay thế `VpnClientConnectSource.cs`), `src/ProxyDivert.Core/Vpn/VpnProfileReader.cs`, `Vpn/Models/VpnProfile.cs`, `Outbounds/OutboundSignature.cs`.
- **Cách sửa:** `abstract class VpnProfile { Protocol; CarriesUdp; Signature(); Task<VpnTunnel> DialAsync(VpnTunnelOptions, ct) }` với `WireGuardFileProfile { Engine = WireProxy|InProcess }`, `OpenVpnFileProfile`, `SstpProfile`, `L2tpIpsecProfile`, `Ikev2Profile`, `SoftEtherProfile`; ctor bắt buộc tham số nên validate lúc `Read`. `VpnProfileReader.Read` chỉ chọn subclass; `RunsOnWireProxy(protocol, url)` giữ static. Nhận `Func<CancellationToken, Task<VpnTunnel>>` làm seam test. Cùng lúc giải quyết C6.
- **Ngày ghi:** 2026-09-08.

### E3.4. v4/v6: cặp field nhân đôi và cờ `isIpv6` xuyên 20 file, WinDivert (Should)

- **Vấn đề:** `_listener/_listenerV6`, `Port/PortV6` trong `TcpRelayServer`/`UdpRelayServer`; `RelayPorts` 4 int + `For(bool, bool)`; 4 thuộc tính port trên `IProcessRedirector`; `NatKey.IsIpv6` bool; `IsIpv6 ? Ipv6.X : Ipv4.X` trong `ParsedPacket`; 15 lần `isIpv6` trong `NatRedirectMiddleware`.
- **Vì sao:** lỗi "NAT v4/v6 ghi đè nhau" đã từng xảy ra vì family là cờ rời phải nhớ truyền (`src/ProxyDivert.Core.Tests/Ipv6RoutingTests.cs`).
- **Cách sửa:** value object `RelayEndpoint(IpProtocol, AddressFamily, IPAddress Loopback, int Port)` + `RelayEndpointSet.TryGet(protocol, family)`; `TcpRelayServer` = composite của N `TcpRelayListener(loopbackAddress, nat, handler)`, `UdpRelayServer` tương tự; `NatKey(IpProtocol, AddressFamily, ushort)`; `IIpHeaderView { Family; Source; Destination; HeaderLength; Protocol }` do hai view implement, `ParsedPacket.Ip` trả về nó. Thứ tự: NatKey/INatTable (cơ học, có test) → relay listener → header view → vòng lặp trong ProcessRedirector. Hai family hai socket loopback riêng là bắt buộc, composite giữ đúng điều đó.
- **Ngày ghi:** 2026-09-08.

### E3.5. tcp/udp là `byte` magic 6/17 và `isTcp ? :` rải rác, WinDivert (Should)

- **Vấn đề:** `FlowKey.Protocol` là `byte` dù có `IpProtocol` enum; literal 6/17 trong `SocketTracker`, `NatRedirectMiddleware`, `Ipv6BlockMiddleware`, `UdpRelayServer`; ba cặp `isTcp ? tracker.IsTrackedTcp : IsTrackedUdp` trong `NatRedirectMiddleware`.
- **Cách sửa:** `FlowKey`/`NatKey`/`NatEntry`/`INatTable` dùng `IpProtocol`; `ITransportFlowPolicy { IsTracked; TryGetOwner; MayRedirectNow }` với `TcpFlowPolicy`/`UdpFlowPolicy` chọn một lần theo `p.Protocol`; "UDP hỏi `ShouldRedirectUdp` còn TCP không" thuộc `UdpFlowPolicy`.
- **Ngày ghi:** 2026-09-08.

### E4.3. `RedirectOptions` là túi delegate thay cho interface host, WinDivert (Should)

- **Vấn đề:** 4 delegate trong `RedirectOptions`; consumer implement cả 4 bằng method private trên một class (`src/ProxyDivert.Core/Engine/RedirectEngine.cs`); Demo phải tạo `redirectorRef` gán sau `Create` để handler gọi ngược; mode chọn bằng null-ness của `Func` (`ISocketTrackerFactory`); sentinel `ProcessId = 0` làm `NatEntry.ProcessId = 0` bị stamp lặng lẽ khi tracker miss (`NatRedirectMiddleware`).
- **Cách sửa:** `IRedirectHost { HandleTcpAsync(session, conn, ct); UdpVerdict HandleUdp(dg); bool ShouldRedirectUdp(target); }` + `IProcessSelector` + `ProcessScopeKind` enum tường minh; `RedirectOptions` chỉ còn dữ liệu; `InitialProcessIds` thay sentinel 0, bỏ fallback root pid. Giữ overload extension nhận lambda cho Demo.
- **Ngày ghi:** 2026-09-08.

### E4.4. Packet model: `WinDivertAddress` rò lên middleware, chữ ký async mời làm sai, `MarkModified` tách rời (Should)

- **Vấn đề:** `PacketContext.Address` là struct native public, `IPacketInjector.Inject(..., in WinDivertAddress)`, magic `IfIdx = 1`, dựng address inbound trùng ở `NatRedirectMiddleware` và `DnsOverHttpsMiddleware`. `IPacketMiddleware.InvokeAsync` trả `Task` nhưng pump `GetAwaiter().GetResult()` và cấm await I/O: consumer NuGet viết `await http.GetAsync()` sẽ treo mạng cả máy. `SetSource/SetDestination` ghi buffer nhưng phải nhớ gọi `MarkModified` riêng; setter trên header view struct không có tác dụng (`TcpHeader.cs`).
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Models/PacketContext.cs`, `Pipeline/PacketPump.cs`, `Packet/Models/ParsedPacket.cs`, `Packet/Models/TcpHeader.cs`.
- **Cách sửa:** value type `PacketRoute { Direction; Loopback; InterfaceRef; Family }` với factory `LoopbackOutbound(family)`/`InboundOn(iface)`; `PacketPump` là nơi duy nhất map sang `WinDivertAddress`; struct native `internal`. `void Invoke(PacketContext, PacketDelegate next)` đồng bộ (middleware built-in đều đã đồng bộ nên đổi cơ học), `IPacketInjector` là đường duy nhất cho việc chậm. Bỏ setter trên view; ghi qua `ctx.Rewrite(...)` tự đặt `Modified`.
- **Ngày ghi:** 2026-09-08.

### E4.5. Redirect phụ thuộc cứng SecureDns/Inspection, không thêm được feature thứ 6 (Should)

- **Vấn đề:** `IReverseDnsTable` nằm trên interface session; `PeekableStream` (kiểu Inspection) nằm trên model `RedirectedTcpConnection`; `ProcessRedirector` new thẳng hai middleware DNS; `CapturesUdp` hard-code nhu cầu từng feature nên middleware user cần UDP khi `Protocols = Tcp` không bao giờ thấy gói UDP; `ConfigureNetworkPipeline` chỉ chèn sau NAT; doc NAT "must run first" mâu thuẫn thứ tự đăng ký thực. `DnsCacheLookup` (spawn `ipconfig`) nằm ở core `Flow/` với interface khác tên `IReverseDnsTable` dù cùng mục đích IP→tên.
- **Cách sửa:** `IRedirectFeature { NeedsUdpCapture; Families; Contribute(builder, PipelineStage, ctx) }` với `DnsSniffFeature`, `DohFeature`, `BlockTargetUdpFeature`; `ProcessRedirector` lặp `options.Features` và ghép filter driver từ `NeedsUdpCapture`; `PipelineStage` enum (PreNat/Nat/PostNat) để ràng buộc thứ tự thành kiểu; `ReverseDns` rời khỏi interface session; `IPeekableStream` ở core. `IAddressNameLookup` chung cho `ReverseDnsTable` và `DnsCacheLookup` (dời sang SecureDns), `CompositeNameLookup`. `Ipv6BlockMiddleware`, `BlockTargetUdpMiddleware` chỉ phụ thuộc `ISocketTracker`, dời về core làm "per-process firewall". Mẫu để nhân bản: `IHostNameParser` strategy list của Inspection.
- **Ngày ghi:** 2026-09-08.

### E4.6. Exception là API nhưng không nhất quán, lib Proxy (Should)

- **Vấn đề:** server chỉ bắt `InitConnectSourceFailedException`; Socks5 auth fail ném `Exception` trần; Http ném không message, mất status; `WireGuardException : Exception` ngoài cây `ProxySourceException`; `InitConnectSourceFailedException` không có ctor `(message, inner)` nên app bọc mất inner (`VpnClientConnectSource.cs`). Hệ quả: `catch (Exception) when (isIpv6 ...)` trong [TcpConnectionRouter.cs:186-192](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs#L186-L192) đánh dấu outbound "IPv4-only" kể cả khi lỗi là 407/auth/proxy chết.
- **Cách sửa:** cây `ProxySourceException` → `UpstreamUnreachableException`, `ProxyHandshakeException { Status }`, `ProxyAuthenticationException`, `DestinationUnreachableException`; mọi lớp có `(message, inner)`; `WireGuardException` vào cây. App: `NoteIpv6Failure` chỉ với `DestinationUnreachableException`/timeout; C8 (dial timeout thành "cancelled") sửa cùng.
- **Ngày ghi:** 2026-09-08.

### E4.7. Nhóm Nice (abstraction)

- **Vấn đề:**
  - `IProcessRedirector` phơi `Nat`, `TcpConnectionOpened/Closed`, `TrackedProcessIds` không consumer nào dùng. Tách `IRedirectDiagnostics`.
  - Marker interface rỗng và dán nhãn sai trong Proxy: `IHttpProxy/ISocks4Proxy/ISocks5Proxy/ISsh/IVpn/IAuthentication`; `LocalProxySource : IHttpProxy`, `WireGuardProxySource : ISocks5Proxy` lộ chi tiết cài đặt. Xoá hoặc cho một member thật.
  - `LiveTcpConnectionRegistry.CloseWhereRouteChanged` nhận kiểu bê tông `RoutingPolicyResolver`; chỉ cần `Func<RouteTarget, RouteDecision>`.
  - Reverse: `event Func<…,Task>` chỉ await delegate cuối (`RawTcpReverseTransportServer.cs`); AspNetCore phải poll `socket.State` mỗi giây vì `IControlChannel` thiếu `Task Closed`.
  - `Singleton` limit toàn process trong Proxy (`Singleton.cs`); `LogCritical` cho mọi tunnel fail và header ở `Information` (`ProxyServer.cs`, `HttpProxyServer.cs`) là noise với hàng trăm tunnel/phút.
  - Naming trong WinDivert: 4 "tracker" (`SocketTracker`/`ProcessRuleTracker`/`ConnectionTracker`/`IProcessTreeMonitor`), `AddTrackedProcessId` vs `AddProcess`, file `Ipv4Header.cs` chứa `Ipv4HeaderView`, consumer phải `using` 7 namespace con `.Interfaces/.Enums/.Models` (`RedirectEngine.cs`). NuGet nên phẳng namespace.
- **Vị trí:** xem từng gạch; `IProcessRedirector.cs` ở `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/`, `LiveTcpConnectionRegistry.cs` ở `src/ProxyDivert.Core/Engine/`.
- **Ngày ghi:** 2026-09-08. Xen kẽ khi chạm file liên quan, không lên lịch riêng.

### E5.4. Static giấu I/O và thời gian (Should)

- **Vấn đề:**
  - DNS đồng bộ trong parser và factory trên luồng relay: `src/ProxyDivert.Core/Vpn/WireGuardConfigParser.cs` (resolve endpoint) và [OutboundUrl.cs:45](../src/ProxyDivert.Core/Outbounds/Builders/OutboundUrl.cs#L45) (chạy lúc dựng instance; xem thêm A16); lib đã có ctor `Socks5ProxySource(Uri)` resolve lười. Parser trả `DnsEndPoint`, app dùng ctor `Uri`.
  - `OutboundSignature.Of` ([OutboundSignature.cs:27](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L27)) stat file mỗi outbound mỗi `Sync`/`ApplyOutbounds`; đã thu hẹp (chỉ stat khi địa chỉ là file, `StampFile` dòng 73-80), còn lại là bỏ hẳn I/O khỏi `Of`.
  - `Environment.TickCount`/`UtcNow` rải rác trong WinDivert (`SocketTracker.cs`, `SecureDns/ReverseDnsTable.cs`, `Redirect/Models/ConnectionStatistics.cs`) và cùng mẫu `TickCount + Volatile + CompareExchange` ở app ([ProcessInventory.cs:195](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L195)): grace 30s / retention 30 phút không test được. `IClock` (net8 `TimeProvider`) + `RateGate(IClock, TimeSpan)` dùng chung.
  - `VpnConnectionKeeper.ConnectRoutedVpns` gán `outbound.KeepConnected = true` lên object của caller ([VpnConnectionKeeper.cs:191](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L191)); người gọi duy nhất nay là `ProxyDivertSession` (cho cả WPF lẫn CLI) và việc gán lên config của người gọi là cố ý (snapshot phải mang cờ, tab Outbounds phải thấy tunnel đã bật). Còn lại chỉ là chỗ gán: keeper trả danh sách, session tự gán.
  - `AppConfig` trộn tuỳ chọn UI (`Language`, `Theme`) với cấu hình engine: hướng `UiPreferences` do Wpf định nghĩa, Core lưu mờ.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### E6.1. WireGuard `.conf`: ba parser, hai model trùng tên (Should)

- **Vấn đề:** `src/ProxyDivert.Core/Vpn/WireGuardConfigParser.cs` cho model `WireProxyCli.WireGuardConfig` (lib chỉ có writer `internal`); VpnClient `WireGuardConfFile.cs` sang model khác (byte[] key); `VpnProfileReader.ReadIni` lần ba. Hành vi lệch: app strip comment inline, VpnClient chỉ bỏ dòng đầu `#`; app nhiều peer, VpnClient chỉ giữ peer cuối. `WireGuardOptions` là túi 2 hình dạng loại trừ nhau kiểm lúc chạy.
- **Cách sửa:** chuyển `WireGuardConfigParser` (+ 9 test) vào WireProxyCli thành `WireGuardConfigReader` public, model round-trip `Parse/ToString`; `WireGuardOptions.FromInline(config)` / `FromFile(path, endpoint)`. Hai repo khác nhau nên chấp nhận 2 reader (Proxy + VpnClient), không 3; `VpnProfileReader.ReadIni` dùng cùng tokenizer.
- **Ngày ghi:** 2026-09-08.

### E6.2. `InTunnelResolver` là DNS client thứ hai trong hệ (Should)

- **Vấn đề:** [InTunnelResolver.cs](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs) tự dựng `BuildQuery/ReadAnswer/SkipName`; `TqkLibrary.WinDivert.SecureDns.DnsMessageParser.ParseAddressAnswers` ([DnsMessageParser.cs:25](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsMessageParser.cs#L25)) đã có (CNAME, chống vòng pointer); VpnClient không có client in-tunnel; `InTunnelResolver(VpnTunnel)` không test được nếu không có VPN thật.
- **Cách sửa:** chuyển sang `TqkLibrary.VpnClient.Sockets` thành `VpnDnsClient(TcpIpStack, IPAddress? dns)` implement `IHostResolver` (chỉ phụ thuộc `VpnUdpClient`), test in-memory ở repo VpnClient; một `UdpConnection` sống lâu (giải quyết C3). `DnsQueryBuilder` đặt cạnh parser trong SecureDns cho DoH dùng chung.
- **Ngày ghi:** 2026-09-08.

### E6.3. `UdpProxyForwarder` và glue TCP→proxy có hai bản, Demo và app (Should)

- **Vấn đề:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/UdpProxyForwarder.cs` vs [UdpProxyForwarder.cs](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs) (cùng `PortTunnel`, cùng `GetAwaiter().GetResult()` khi inject); `Demo/Running/ProxyRedirectorRunner.cs` vs [TcpConnectionRouter.cs](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs); `RelayDirectAsync` có I/O nằm trong model `RedirectedTcpConnection`.
- **Cách sửa:** package thứ 6 `TqkLibrary.WinDivert.Proxy` (Redirect + TqkLibrary.Proxy) chứa `SocksUdpAssociateForwarder` (bản app, có idle timeout) và `ProxyTcpConnectionHandler`; `DirectTcpConnectionHandler : IRedirectHost` mặc định thay `RelayDirectAsync`. Demo và app cùng dùng.
- **Ngày ghi:** 2026-09-08.

### E6.4. ProcessControl của WinDivert bị app viết lại tốt hơn (Should)

- **Vấn đề:** app chỉ còn dùng `ISuspendedProcessLauncher`; lib `ProcessTreeMonitor` poll `Process.GetProcesses()` + `OpenProcess` từng pid mỗi 500ms, `ProcessFinder` dùng `MainModule`; app [NativeProcessLister.cs](../src/ProxyDivert.Core/Processes/NativeProcessLister.cs) một syscall có parent cho mọi tiến trình; P/Invoke trùng giữa `ProcessNativeMethods.cs` của lib và của app; `ManagedProcessLister` ≈ `ProcessFinder.ListAll`.
- **Cách sửa:** hạ `IProcessLister`/`NativeProcessLister`/`ManagedProcessLister`/`IProcessDetailsReader`/`NativeProcessDetailsReader`/`ProcessSnapshot`/`DebugPrivilege` (+ test) xuống `TqkLibrary.WinDivert.ProcessControl`; xoá `ProcessFinder`/`ProcessTreeMonitor` hoặc viết lại trên `IProcessLister`. `ProcessInventory` + event source ETW/WMI (kéo `TraceEvent`) ở lại app hoặc `.ProcessControl.Etw`.
- **Ngày ghi:** 2026-09-08.

### E6.5. Tunnel client trong Proxy lặp 7 lần, base class rỗng mang finalizer (Should)

- **Vấn đề:** [BaseProxySourceTunnel.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/BaseProxySourceTunnel.cs) rỗng; mỗi source (`Socks5ProxySource.BaseTunnel.cs`, `HttpProxySource.ConnectTunnel.cs`, `LocalProxySource.ConnectTunnel.cs`...) tự khai `TcpClient` + `Stream` + Dispose; guard `GetStreamAsync` "Mustbe run X first" giống hệt ở 7 chỗ; fallback "BND.ADDR = 0.0.0.0" chép 3 lần. ProxyDivert tạo 1 tunnel/1 kết nối TCP nên mỗi kết nối trả phí finalization queue vô ích.
- **Cách sửa:** `UpstreamTcpTunnel<TSource>` chứa `_tcpClient/_stream`, `ConnectUpstreamAsync`, `GetStreamAsync`, `Dispose(bool)` đúng cờ, bỏ finalizer; `Socks5ClientHandshake`/`Socks4ClientHandshake` static nhận `Stream` (test codec in-memory được). Bump minor vì `BaseTunnel` là public API.
- **Ngày ghi:** 2026-09-08.

### E6.6. WireProxyCli và SshCli copy-paste 5 tiện ích process (Should)

- **Vấn đề:** locate binary (`WireProxyProcessRunner.cs` ↔ `SshProcessRunner.cs`), `BuildPsi`/`BuildArgumentString`, chmod file tạm, stderr buffer, idiom Kill+Dispose ×5. Hình dạng khác nhau hợp lý (daemon + socket vs child-per-connection) nên cái dùng chung là helper, không phải base source.
- **Cách sửa:** `TqkLibrary.Proxy.Process` (hoặc internal + `InternalsVisibleTo`): `ExecutableLocator`, `ChildProcess` (Start, stderr tail giới hạn N dòng cuối, `Exited` không bắn khi Dispose, `KillAndDispose`, Job Object), `TempSecretFile` (tạo đúng quyền, xoá), `TcpListenerProbe`. `IChildProcess` là seam để test `WireGuardProxySource` không cần `wireproxy.exe`.
- **Ngày ghi:** 2026-09-08.

### E6.7. Relay hai chiều có ba bản với ngữ nghĩa half-close khác nhau (Should)

- **Vấn đề:** [StreamTransferHelper.cs](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs) (`WhenAll`, không đóng chiều kia), `Reverse/Client/StreamCopy.cs` (`WhenAny`, dispose cả hai), [ConnectSourceExtensions.cs](../src/ProxyDivert.Core/Outbounds/Extensions/ConnectSourceExtensions.cs) (app bọc thêm với chú thích "lib để boilerplate cho caller"). Liên quan C10.
- **Cách sửa:** một `StreamRelay` trong core Proxy với policy half-close rõ, extension `IConnectSource.RelayAsync(Stream, ...)` để app bỏ file này; Reverse dùng chung.
- **Ngày ghi:** 2026-09-08.

### E6.8. Nhóm nhỏ trùng lặp (Nice)

- **Vấn đề:**
  - Bỏ ngoặc IPv6 `Trim('[', ']')` ở 7 chỗ và `host:port` qua `LastIndexOf(':')` ở 2 chỗ (`LocalProxySource.ConnectTunnel.cs`, `Socks5_DSTADDR.cs`, `OutboundUrl.cs`, `VpnProfileReader.cs`, `InTunnelResolver.cs`, `WireGuardConfigParser.cs`). Làm `EndPointParsing` trong core Proxy.
  - Credential từ `Uri.UserInfo` ba cách, độ đúng khác nhau (Http `Split(':')` không unescape): `HttpProxySource.cs`, `Socks5ProxySource.cs`, `Socks4ProxySource.cs`. Làm `ProxyUri.ParseCredential(Uri)`.
  - Ghi header IPv4 tay ở 3 nơi: `TcpResetPacketBuilder.cs`, `DnsOverHttpsMiddleware.cs`, VpnClient `Ipv4.Build`. Trong WinDivert: `Ipv4PacketWriter`/`Ipv6PacketWriter` ở core `Packet/`; không tham chiếu VpnClient.
  - `IsElevated` chép 2 bản (`src/ProxyDivert.Cli/Program.cs`, `MainViewModel.CheckElevated`). Gom vào `Core/Hosting/Elevation`.
  - Wpf: `Reload()` chép list config → ObservableCollection và "thêm vào cả hai danh sách" ở 3 VM; mẹo "gỡ ra cắm lại để grid vẽ lại" ở `ProcessesViewModel.cs` và `RulesViewModel.cs`.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08.

### E7.2. Sự kiện `ISocketTracker` bắn từ 3 ngữ cảnh thread mà interface không nói (Should)

- **Vấn đề:** sự kiện bắn từ socket pump, từ thread gọi `RemoveProcess` (UI), và từ NETWORK pump qua reconcile; `ISocketTracker` không có ghi chú thread trong khi `ITcpRelayServer` có; `ProcessRedirector` forward event ra host nên host chậm sẽ chặn pump.
- **Vị trí:** `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs`, `Flow/Interfaces/ISocketTracker.cs`.
- **Cách sửa:** sau E2.2, mọi mutation đi qua một `Channel<FlowMutation>` do thread sở hữu `FlowTable`; event bắn từ đúng một thread; doc một dòng. Lợi ích chính là hợp đồng rõ, không phải hiệu năng.
- **Ngày ghi:** 2026-09-08.

### E8.2. Nhóm Nice (MVVM)

- **Vấn đề:**
  - 6 VM nhận `AppServices` bê tông, 41 chỗ gọi thẳng `Engine/Config/Vpn`; `CanToggleVpn` chạy `OutboundUsage.RoutedOutboundIds` mỗi lần WPF hỏi CanExecute ([OutboundsViewModel.cs:100](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L100)). Hướng: VM nhận `IProxyDivertSession` + `IConfigEditor`, cache theo config version. `ProxyDivertSession` hiện là class, chưa có interface; `AppServices` vẫn là mặt tiền VM gọi. Rút interface khi có test VM cần thay nó.
  - VM tạo Window ([ProcessesViewModel.cs:318](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L318), `new ProcessFilterWindow(viewModel)`). Làm `IDialogService.EditFilter(vm) : bool` để test `AddRule/EditRule`.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-10-09).

### E9.1. Test của WinDivert nằm trong repo app, submodule 0 test, thiếu seam bảng kernel (Must)

- **Vấn đề:** 7 file test của lib (`NatRedirectMiddlewareEscapedFlowTests`, `EscapedFlowBlocklistTests`, `Ipv6RoutingTests`, `DnsMessageParserTests`, `PeekableStreamTests`, `TcpResetPacketBuilderTests`, `TlsClientHelloParserTests`) nằm ở `src/ProxyDivert.Core.Tests`; `IpHlpApi` static gọi thẳng từ `SocketTracker` (`PrePopulateForPid`, reconcile). Submodule đã có project test đầu tiên `TqkLibrary.WinDivert.Tests` (không cần admin) nhưng 7 file trên chưa dời và seam bảng kernel chưa có.
- **Vì sao:** không publish/validate NuGet độc lập được; reconcile/pre-populate (nguồn mọi bug race) không test được dù `IWinDivertHandleFactory` đã fake được.
- **Cách sửa:** dời 7 file vào `libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Tests` (+ `.Redirect.Tests`), `RawTcpPackets.cs` thành helper chung; `IKernelConnectionTable` + `IClock` inject qua `ISocketTrackerFactory`. Test được ngay không cần driver: `PacketPump` (fake handle), relay servers (loopback thật + `NatTable` seed), `DnsOverHttpsMiddleware` (fake resolver + recording injector), `ProcessRedirector` (5 factory fake).
- **Ngày ghi:** 2026-09-08.

### E9.2. Seam test còn thiếu ở app và Proxy (Should)

- **Vấn đề:**
  - `VpnConnectionKeeperTests` đã chạy nhánh Connected qua registry + `FakeOutboundSourceBuilder`/`FakeManagedProxySource`; còn thiếu test nhánh down → `DiscardAsync` → dựng lại (`DownPollsBeforeRebuild`). `KeptVpnTunnel` gọi `_registry.GetOrCreate` ([KeptVpnTunnel.cs](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs)).
  - `VpnClientProxySource.DialAsync` gọi static `VpnDialer`, `VpnTunnel` ctor internal: E3.2 `VpnProfile.DialAsync` virtual; VpnClient nên phơi `IVpnTunnel` hoặc factory test.
  - Proxy lib: `TestProxy` = 6 test đánh internet thật, 0 test in-memory cho codec Socks4/5/HTTP; `TestProxy.Local` cần SSH/WG thật với mật khẩu hard-code (`OpenSshProxySourceTest.cs`; nên lấy từ env như `TESTPROXY_HTTPBIN`); Reverse 0 test dù chạy trên `Stream` (một duplex pipe + `StreamControlChannel` là đủ). E6.5 handshake static trên `Stream` mở đường test codec.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11).

### E9.3. Test Wpf đặt sai project (Nice)

- **Vấn đề:** `DataGridColumnBindingTests`, `WpfResourceSmokeTests`, `PolicyRenameTests`, `TextBoxPlaceholderTests`, `StartupRegistrationTests` test Wpf nhưng nằm trong `ProxyDivert.Core.Tests` (csproj tham chiếu cả Wpf).
- **Cách sửa:** tách `ProxyDivert.Wpf.Tests`.
- **Ngày ghi:** 2026-09-08.

### OpenVPN và WireGuard native chưa thử với máy chủ thật

- **Vấn đề:** SSTP, L2TP/IPsec, SoftEther đã dựng được đường hầm tới VPN Gate thật (2026-09-10). OpenVPN và WireGuard chạy native qua TqkLibrary.VpnClient mới có unit test; đường WireGuard qua wireproxy cũng chưa chạy thật vì máy chưa có `wireproxy.exe`.
- **Vị trí:** `src/ProxyDivert.Core/Vpn/Client/`, `src/ProxyDivert.Core/Vpn/VpnProfileReader.cs`, `libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.Tunnels/`.
- **Vì sao:** SoftEther cũng qua hết test offline nhưng chạy thật mới lộ lỗi login PACK; hai giao thức này có thể giấu lỗi cùng loại.
- **Ngày ghi:** 2026-10-09 (chuyển từ docs/Plan-vi.md đã xoá).

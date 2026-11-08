# Backlog

Việc còn treo và việc nên làm. Xử lý xong mục nào thì xoá mục đó trong cùng commit với bản sửa.

Các mục mang mã A/B/C/D/E (ở cuối file) đến từ đợt rà soát toàn repo ngày 2026-09-08, trước đây nằm trong `docs/FixList-vi.md` (lịch sử: `git log -p -- docs/FixList-vi.md`). A = lỗi tiềm ẩn ProxyDivert, B = WinDivert, C = cách dùng VpnClient, D = TqkLibrary.Proxy, E = thiết kế (OOP, tái sử dụng, test). Mục có chỗ sửa chính nằm trong một thư viện đã chuyển sang backlog của submodule đó: [TqkLibrary.WinDivert](../libs/TqkLibrary.WinDivert/docs/Backlog-vi.md), [TqkLibrary.Proxy](../libs/TqkLibrary.Proxy/docs/Backlog-vi.md), [TqkLibrary.VpnClient](../libs/TqkLibrary.VpnClient/.docs/Backlog-vi.md); file này giữ mục sửa ở `src/ProxyDivert.*` và mục trải đều app lẫn thư viện. Mức: `Cao` = mất dữ liệu / mất mạng / rò tài nguyên tích luỹ trong dùng bình thường; `Vừa` = sai trong tình huống thường gặp nhưng có đường tránh; `Thấp` = hiếm gặp hoặc chỉ tốn tài nguyên; với mục E: `Must` = đã gây lỗi thật hoặc chặn việc mở rộng đã dự định, `Should` = giảm trùng lặp hoặc cho phép test, `Nice` = sạch hơn, không đổi hành vi. Link của các mục này được đối chiếu lại với code ngày 2026-10-09; link không có số dòng (nhóm D7 và phần lớn E) là chưa dò lại dòng, tra theo tên ký hiệu.

### A10. `Reload()` dựng lại toàn bộ row VM mỗi lần đổi tab (Thấp)

- **Vấn đề:** phần đồng bộ danh sách giữa các tab đã xong (chạy thử UI 2026-11-08, có `TabReloadTests`). Còn lại: `Reload()` của ba tab tạo mới mọi row VM mỗi lần đổi tab, nên hàng nào sau này giữ state riêng (không chuyển tiếp xuống model) sẽ mất state đó; `OutboundsViewModel` đã phải tự áp lại trạng thái VPN.
- **Vị trí:** [ProcessesViewModel.cs:104](../src/ProxyDivert/ViewModels/ProcessesViewModel.cs#L104), [RulesViewModel.cs:66](../src/ProxyDivert/ViewModels/RulesViewModel.cs#L66), [OutboundsViewModel.cs:64](../src/ProxyDivert/ViewModels/OutboundsViewModel.cs#L64).
- **Vì sao:** chưa gây lỗi, nhưng là bẫy cho lần thêm state vào row.
- **Cách sửa:** khi có row giữ state riêng thì đổi sang đối chiếu theo Id (giữ row cũ, chỉ thêm/bớt).
- **Ngày ghi:** 2026-09-08 (thu hẹp 2026-11-08).

### A21. `OutboundRegistry.DropAsync` bỏ rơi instance đang dựng dở (Cao)

- **Vấn đề:** `ReconcileAsync`/`DiscardAsync`/`DisposeAsync` chen vào lúc một luồng kết nối đang dựng instance (`Lazy.Value` đang chạy `Build`: spawn wireproxy, quay số VPN) hoặc vừa lấy được entry mà chưa gọi `.Instance`. Entry đã rời bảng, `InstanceIfBuilt` là `null` (khi `Lazy` chưa hoặc đang dựng) nên không dispose gì, instance dựng xong không ai sở hữu. Sau khi registry đã `DisposeAsync`, một vòng giám sát còn sót vẫn `GetOrCreate` dựng được instance mới (không có cờ disposed).
- **Vị trí:** [OutboundRegistry.cs:100-106](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L100-L106) (`GetOrCreate`: `GetOrAdd` trả `Entry` rồi mới chạm `.Instance`), [:237-250](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L237-L250) (`DropAsync`: `TryRemove` rồi dispose), [:252-257](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L252-L257) (`DisposeAsync(Entry)`), [:323](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L323) (`InstanceIfBuilt`).
- **Vì sao:** đúng loại rò đã bịt ở chỗ khác: `wireproxy.exe` mồ côi giữ cổng SOCKS, session VPN sống mãi, qua đường Save cấu hình trong lúc kết nối đầu tiên đang dựng (lúc vừa sửa outbound vừa thử).
- **Cách sửa:** `DisposeAsync(Entry)` chờ `entry.Instance` (Lazy block tới khi dựng xong) rồi dispose, như `UdpProxyForwarder.DisposeAsync(Lazy)` đang làm; `GetOrCreate` sau khi `.Instance` xong kiểm entry còn trong `_instances` không, không còn thì dispose và `GetOrCreate` lại; cờ `_disposed` từ chối `GetOrCreate` sau `DisposeAsync`. Test: hai luồng, một `GetOrCreate` với builder chậm, một `ReconcileAsync` đổi chữ ký giữa chừng, khẳng định instance bị dispose đúng một lần.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### [Thấp] API cục bộ: trả 500 khi engine áp cấu hình lỗi dù thay đổi đã lưu

- **Vấn đề:** `EditAsync` thay danh sách trong `AppServices.Config` và ghi file trước khi chờ `Session.ApplyAsync`. Nếu bước áp vào engine ném lỗi, request trả 500 nhưng thay đổi đã nằm trong file và trên cửa sổ. Tool gọi lại thì tạo trùng (POST không kèm id).
- **Vị trí:** `src/ProxyDivert/Api/WindowApiBackend.cs` (`EditAsync`), `src/ProxyDivert/ViewModels/MainViewModel.cs` (`SaveFromApiAsync`).
- **Vì sao:** chốt một ngữ nghĩa rõ ràng: hoặc trả 200 kèm cảnh báo "đã lưu, engine áp lỗi", hoặc gom "thay list + RememberApplied + Reload" vào một hàm `AppServices.ApplyConfig` có test.
- **Ngày ghi:** 2026-10-12.

### [Thấp] API cục bộ: thêm/sửa outbound

- **Vấn đề:** API mới chỉ liệt kê outbound (không URL, không mật khẩu); chưa thêm, sửa hay xoá được proxy/VPN qua API. User đã chốt để đợt sau.
- **Vị trí:** `src/ProxyDivert/Api/LocalApiService.cs`, `src/ProxyDivert/Api/WindowApiBackend.cs` (`EditAsync` hiện chỉ chép `Policies`/`ProcessRules`).
- **Vì sao:** tool bên ngoài muốn chuyển sang proxy mới thì hiện phải tạo outbound bằng tay trên cửa sổ trước.
- **Ngày ghi:** 2026-10-12.

### [Thấp] API/MCP không kiểm Origin/Host khi tắt token — rủi ro DNS rebinding

- **Vấn đề:** khi "Require token" tắt, mọi request tới `/api` và `/mcp` đều được nhận mà không kiểm header `Origin` hay `Host`. Một trang web trong trình duyệt của user có thể lái API qua `localhost` (DNS rebinding, hoặc request cross-origin đơn giản), ví dụ bật/tắt engine hay sửa policy.
- **Vị trí:** `src/ProxyDivert/Api/LocalApiHost.cs` (`IsGuarded`, middleware `CheckTokenAsync`).
- **Vì sao:** spec MCP khuyến nghị server cục bộ phải kiểm `Origin` (và chỉ bind loopback) để chặn kiểu tấn công này. Nên từ chối request có `Origin` lạ hoặc `Host` không phải loopback/địa chỉ đã cấu hình, kể cả khi token tắt.
- **Ngày ghi:** 2026-10-13.

### Dọn source biến thể Anti-DPI không còn policy nào dùng

- **Vấn đề:** `OutboundRegistry` giữ một source riêng cho mỗi cặp chunk size hiệu lực mà policy yêu cầu. Khi user sửa hoặc bỏ cài đặt Anti-DPI của policy, source biến thể cũ không bị dọn tới khi outbound đó bị sửa, bị xoá hoặc app tắt.
- **Vị trí:** `src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs` (`ReconcileAsync`, `InstanceKey`).
- **Vì sao:** với Direct/HTTP/SOCKS thì rẻ, nhưng số source tăng dần theo số lần sửa. Hướng sửa: cho `ReconcileAsync` biết danh sách policy để tính tập key còn dùng. Hướng gọn hơn về lâu dài: thư viện Proxy nhận tuỳ chọn Anti-DPI theo từng lượt connect, registry trở lại một Id một source.
- **Ngày ghi:** 2026-10-05.

### Huỷ thay đổi không đồng bộ lại tunnel VPN

- **Vấn đề:** thêm một outbound VPN rồi bấm Connect khi chưa Áp dụng & Lưu, sau đó bấm Huỷ thay đổi: outbound biến khỏi cấu hình nhưng tunnel vẫn sống (không ai gọi lại `Vpn.SyncAsync`), và tab Outbounds không còn hàng nào để Disconnect, kéo dài tới lần Apply sau. Chiều ngược lại: outbound bị xoá rồi được khôi phục thì mang `KeepConnected` của lần apply, có thể lệch với tunnel thật.
- **Vị trí:** `src/ProxyDivert/Services/AppServices.cs` (`DiscardChanges`), `src/ProxyDivert.Core/Hosting/ProxyDivertSession.cs` (`SetVpnConnectedAsync`).
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

### Bảng Connections vẫn dựng lại 500 dòng mỗi 250ms khi đang mở tab

- **Vấn đề:** bộ đếm byte sửa tại chỗ nên không biết rẻ khi nào dữ liệu đổi; hiện chỉ đỡ nhờ dừng timer khi cửa sổ ẩn hoặc không mở tab.
- **Vị trí:** `src/ProxyDivert/ViewModels/ConnectionsViewModel.cs` (`Refresh`).
- **Ngày ghi:** 2026-10-08.

### A16. Nhóm mức Thấp (ProxyDivert)

- **Vấn đề:** các lỗi nhỏ, mỗi gạch một việc:
  - Regex của user trong luật định tuyến không có trần thời gian ([HostPredicate.cs:87-100](../src/ProxyDivert.Core/Routing/Compiled/HostPredicate.cs#L87-L100)). Đã quyết định KHÔNG đặt `matchTimeout` (timeout làm luật âm thầm ngừng áp dụng khi máy bận, bài học của `RegexBudget`). Rủi ro còn lại: pattern backtracking thảm hoạ treo luồng định tuyến vô hạn. Chọn một trong: `RegexOptions.NonBacktracking`, giới hạn độ dài/độ phức tạp pattern lúc lưu, hoặc cảnh báo ở tab Rules; không thêm timeout.
  - `DomainSuffix` với pattern `.example.com` không bao giờ khớp: nhánh `DomainSuffix` của `NamePredicate` trong [HostPredicate.cs](../src/ProxyDivert.Core/Routing/Compiled/HostPredicate.cs) (và `pattern.Trim()` ở dòng 49) chỉ cắt khoảng trắng. Thêm `TrimStart('.')`.
  - `OnProcessStopped` chỉ detach con trực tiếp, không lan xuống cháu: [ProcessRuleTracker.cs:395-405](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L395-L405), `Detach` ([:380](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L380)) không cascade nên cháu ở lại bảng tracked vĩnh viễn. Lặp tới khi không đổi như `FollowParents`; `ProcessRuleTrackerTests` chưa phủ ca này.
  - `AttachProcessId` có thể ném `KeyNotFoundException` khi `Detach` xen giữa: [ProcessRuleTracker.cs:253](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L253) (`if (!_tracked.TryAdd(...)) return _tracked[processId];`). Nằm trên đường nút Launch suspended. Dùng `TryGetValue`.
  - Endpoint proxy phân giải bằng `Dns.GetHostAddresses` rồi lấy `addresses[0]`: [OutboundUrl.cs:45](../src/ProxyDivert.Core/Outbounds/Builders/OutboundUrl.cs#L45), gọi lúc dựng instance (registry), không còn trên đường mỗi kết nối. Còn lại: không ưu tiên IPv4 khi `Ipv6Support == Disabled`, không cache, vẫn đồng bộ lúc dựng (cùng gốc với E5.4).
  - Cây tiến trình khi PID tái dùng thành vòng cha–con: [ProcessesViewModel.cs:130](../src/ProxyDivert/ViewModels/ProcessesViewModel.cs#L130) (`BuildTree`), [:174](../src/ProxyDivert/ViewModels/ProcessesViewModel.cs#L174) (`FilterName`, đã có `MaxParentSteps = 64`). Không còn treo, nhưng lúc nối chưa có tập-đã-thăm: mọi node trong vòng đều "có cha" nên không node nào là root, cả vòng biến mất khỏi cây dù tiến trình đang redirect. Node có cha nhưng không tới được root thì coi là root.
  - `EtwProcessEventSource.Dispose` chặn `_pump?.Wait(2s)` ([EtwProcessEventSource.cs:192](../src/ProxyDivert.Core/Processes/EtwProcessEventSource.cs#L192)) và nằm trong chuỗi `ProcessInventory.DisposeAsync` → `StopEvents` → `events.Dispose()` ([ProcessInventory.cs:381](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L381)): cầu sync còn sót của chuỗi dispose, chặn luồng tới 2 giây lúc tắt. Cho nguồn sự kiện `IAsyncDisposable`.
  - Cache `Outbound.Address` có khe đua nhỏ ([Outbound.cs:57-77](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L57-L77)): luồng kết nối đọc `_kind/_url` xong, luồng UI sửa ô rồi vứt cache, luồng đọc mới gán `Parsed(<giá trị cũ>)` đè lên nên địa chỉ cũ sống tới lần vứt cache sau; hiếm, Save/Clone dựng bản mới. `AddressProblem` thì parse lại mỗi lần get, không dùng cache.
  - `ConditionGroup.Clone` ([ConditionGroup.cs:14-19](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionGroup.cs#L14-L19)) vẫn ném trên phần tử `null` trong `Children` trong khi `Answer` có guard; `Normalize` chỉ bịt ở cửa đọc file, `AppConfig` dựng trong bộ nhớ không qua `Normalize` vẫn dính NRE. Thêm guard cho khớp `Answer`.
  - Comment ở [VpnProfileReader.cs:204](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L204) còn nói "A secret in the outbound's own box is encrypted at rest", mà DPAPI đã gỡ ngày 2026-09-09.
- **Vị trí:** xem từng gạch.
- **Vì sao:** hiếm gặp hoặc chỉ tốn tài nguyên, nhưng vài gạch (cháu không detach, vòng PID biến mất khỏi cây, NRE `AttachProcessId`) có thể lộ ra lúc dùng thật.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11 và 2026-10-09).

### A22. `PortTunnel` để lại association SOCKS5 mồ côi khi associate hỏng, bị huỷ hoặc xong muộn (Vừa)

- **Vấn đề:** (1) `AssociateAsync` lấy được `tunnel` từ `GetUdpAssociateSourceAsync` rồi `tunnel.AssociateAsync(ct)` ném (proxy từ chối, hoặc `_cts.Cancel()` từ `DisposeAsync`) thì `catch` chỉ log, không dispose `tunnel`. (2) Associate xong sau mốc 1 giây của `DisposeAsync` thì `_tunnel?.Dispose()` chạy khi `_tunnel` còn `null`, rồi `AssociateAsync` mới gán `_tunnel`: kết nối điều khiển SOCKS5 + socket UDP không ai đóng.
- **Vị trí:** [UdpProxyForwarder.cs:244-270](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L244-L270) (`AssociateAsync`), [:329-340](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L329-L340) (`DisposeAsync`: `_ready.WaitAsync(1s)` rồi `_tunnel?.Dispose()`).
- **Vì sao:** reaper dispose tunnel idle liên tục; proxy ở xa (>1 s để ASSOCIATE) hoặc proxy từ chối UDP là chạm ngay. Finalizer của `BaseProxySourceTunnel` (D5, ở [backlog TqkLibrary.Proxy](../libs/TqkLibrary.Proxy/docs/Backlog-vi.md)) cũng đi qua đường này.
- **Cách sửa:** bọc phần sau khi có `tunnel` bằng `try/catch` riêng, ném thì `tunnel.Dispose()`; `DisposeAsync` chờ `_ready` không giới hạn (nó đã bị huỷ qua `_cts`) hoặc đọc lại `_tunnel` sau khi chờ; hoặc gán `_tunnel` trước rồi mới `AssociateAsync`.
- **Ngày ghi:** 2026-09-11 (kiểm lại 2026-10-09).

### A23. Không ai trong app nghe `PumpStopped` (Vừa)

- **Vấn đề:** thư viện đã bắn `IProcessRedirector.PumpStopped`, nhưng `grep` toàn `src/` không có subscriber. Pump gói tin chết (32 lỗi `Recv` liên tiếp, hoặc handle bị đóng) thì engine vẫn báo `IsRunning`, redirect ngừng trong im lặng. Nửa "báo UI" chưa từng được làm. Liên quan mục "Một nửa cặp pump chết..." và B17 ở [backlog TqkLibrary.WinDivert](../libs/TqkLibrary.WinDivert/docs/Backlog-vi.md).
- **Vị trí:** [ProcessRedirector.cs:81](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L81) (event), [:233-240](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L233-L240) (`OnPumpStopped`); `RedirectEngine.BuildRun` chưa subscribe.
- **Cách sửa:** `RedirectEngine` subscribe trong `BuildRun`, log `Error` và đẩy trạng thái lên `MainViewModel` (thanh gạt về trạng thái lỗi, thông báo), tuỳ chọn tự Stop/Start lại một lần.
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

### C10. Kết nối qua VPN in-process không bao giờ được half-close (Vừa)

- **Vấn đề:** `StreamTransferHelper` đã biến EOF một chiều thành FIN chiều kia, nhưng với outbound VPN in-process stream là `VpnNetworkStream`: tiến trình đóng socket (EOF phía client) thì tunnel không nhận FIN; pump proxy→client nằm chờ tới khi server xa đóng hoặc engine cancel. `HalfClose` chỉ tự xử lý được `NetworkStream`; `VpnNetworkStream` chỉ có `Abort()` và `Dispose()` → `CloseSend`, không có `CloseSend()` public. Proxy SOCKS/HTTP/wireproxy không bị vì stream là `NetworkStream`.
- **Vị trí:** [TcpConnectionRouter.cs:211](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs#L211) (chỉ truyền `shutdownClientSend`), [ConnectSourceExtensions.cs:37](../src/ProxyDivert.Core/Outbounds/Extensions/ConnectSourceExtensions.cs#L37) (`ShutdownSendWith(shutdownClientSend, null)`), [StreamTransferHelper.cs:126](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L126) và [:144](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L144) (`HalfClose`), [VpnNetworkStream.cs:71-76](../libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.Sockets/VpnNetworkStream.cs#L71-L76).
- **Cách sửa:** lộ `CloseSend()` public trên `VpnNetworkStream` (lib VpnClient); `VpnClientConnectSource` truyền nó vào tham số thứ hai của `ShutdownSendWith` (qua `ForwardAsync`). Test: `ForwardHalfCloseTests` thêm ca với stream giả không phải `NetworkStream`.
- **Ngày ghi:** 2026-09-11.

### E3.2. `VpnProtocol`: thêm một giao thức phải sửa 6 chỗ trong 3 file (Should)

- **Vấn đề:** switch dial trong `VpnClientProxySource` (`Required()` kiểm lúc dial thay vì lúc đọc), `VpnProfileReader` (ba chỗ), `VpnProfile` (union-bag tự nhận), `OutboundSignature`.
- **Vị trí:** `src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs` (hoặc file thay thế `VpnClientConnectSource.cs`), `src/ProxyDivert.Core/Vpn/VpnProfileReader.cs`, `Vpn/Models/VpnProfile.cs`, `Outbounds/OutboundSignature.cs`.
- **Cách sửa:** `abstract class VpnProfile { Protocol; CarriesUdp; Signature(); Task<VpnTunnel> DialAsync(VpnTunnelOptions, ct) }` với `WireGuardFileProfile { Engine = WireProxy|InProcess }`, `OpenVpnFileProfile`, `SstpProfile`, `L2tpIpsecProfile`, `Ikev2Profile`, `SoftEtherProfile`; ctor bắt buộc tham số nên validate lúc `Read`. `VpnProfileReader.Read` chỉ chọn subclass; `RunsOnWireProxy(protocol, url)` giữ static. Nhận `Func<CancellationToken, Task<VpnTunnel>>` làm seam test. Cùng lúc giải quyết C6.
- **Ngày ghi:** 2026-09-08.

### E4.7. Nhóm Nice (abstraction)

- **Phạm vi:** chỉ còn các gạch sửa ở app; các gạch sửa trong thư viện đã chuyển sang backlog [TqkLibrary.WinDivert](../libs/TqkLibrary.WinDivert/docs/Backlog-vi.md), [TqkLibrary.Proxy](../libs/TqkLibrary.Proxy/docs/Backlog-vi.md) (cùng tiêu đề).
- **Vấn đề:**
  - `LiveTcpConnectionRegistry.CloseWhereRouteChanged` nhận kiểu bê tông `RoutingPolicyResolver`; chỉ cần `Func<RouteTarget, RouteDecision>`.
- **Vị trí:** `LiveTcpConnectionRegistry.cs` ở `src/ProxyDivert.Core/Engine/`.
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

### E6.8. Nhóm nhỏ trùng lặp (Nice)

- **Phạm vi:** chỉ còn các gạch sửa ở app; các gạch sửa trong thư viện đã chuyển sang backlog [TqkLibrary.WinDivert](../libs/TqkLibrary.WinDivert/docs/Backlog-vi.md), [TqkLibrary.Proxy](../libs/TqkLibrary.Proxy/docs/Backlog-vi.md) (cùng tiêu đề).
- **Vấn đề:**
  - `IsElevated` chép 2 bản (`src/ProxyDivert.Cli/Program.cs`, `MainViewModel.CheckElevated`). Gom vào `Core/Hosting/Elevation`.
  - Wpf: `Reload()` chép list config → ObservableCollection và "thêm vào cả hai danh sách" ở 3 VM; mẹo "gỡ ra cắm lại để grid vẽ lại" ở `ProcessesViewModel.cs` và `RulesViewModel.cs`.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08.

### E8.2. Nhóm Nice (MVVM)

- **Vấn đề:**
  - 6 VM nhận `AppServices` bê tông, 41 chỗ gọi thẳng `Engine/Config/Vpn`; `CanToggleVpn` chạy `OutboundUsage.RoutedOutboundIds` mỗi lần WPF hỏi CanExecute ([OutboundsViewModel.cs:100](../src/ProxyDivert/ViewModels/OutboundsViewModel.cs#L100)). Hướng: VM nhận `IProxyDivertSession` + `IConfigEditor`, cache theo config version. `ProxyDivertSession` hiện là class, chưa có interface; `AppServices` vẫn là mặt tiền VM gọi. Rút interface khi có test VM cần thay nó.
  - VM tạo Window ([ProcessesViewModel.cs:318](../src/ProxyDivert/ViewModels/ProcessesViewModel.cs#L318), `new ProcessFilterWindow(viewModel)`). Làm `IDialogService.EditFilter(vm) : bool` để test `AddRule/EditRule`.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-10-09).

### E9.2. Seam test còn thiếu ở app và Proxy (Should)

- **Phạm vi:** chỉ còn các gạch sửa ở app; các gạch sửa trong thư viện đã chuyển sang backlog [TqkLibrary.Proxy](../libs/TqkLibrary.Proxy/docs/Backlog-vi.md) (cùng tiêu đề).
- **Vấn đề:**
  - `VpnConnectionKeeperTests` đã chạy nhánh Connected qua registry + `FakeOutboundSourceBuilder`/`FakeManagedProxySource`; còn thiếu test nhánh down → `DiscardAsync` → dựng lại (`DownPollsBeforeRebuild`). `KeptVpnTunnel` gọi `_registry.GetOrCreate` ([KeptVpnTunnel.cs](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs)).
  - `VpnClientProxySource.DialAsync` gọi static `VpnDialer`, `VpnTunnel` ctor internal: E3.2 `VpnProfile.DialAsync` virtual; VpnClient nên phơi `IVpnTunnel` hoặc factory test.
- **Vị trí:** xem từng gạch.
- **Ngày ghi:** 2026-09-08 (kiểm lại 2026-09-11).

### E9.3. Test Wpf đặt sai project (Nice)

- **Vấn đề:** `DataGridColumnBindingTests`, `WpfResourceSmokeTests`, `PolicyRenameTests`, `TextBoxPlaceholderTests`, `StartupRegistrationTests` test Wpf nhưng nằm trong `ProxyDivert.Core.Tests` (csproj tham chiếu cả Wpf).
- **Cách sửa:** tách `ProxyDivert.Tests`.
- **Ngày ghi:** 2026-09-08.

### OpenVPN và WireGuard native chưa thử với máy chủ thật

- **Vấn đề:** SSTP, L2TP/IPsec, SoftEther đã dựng được đường hầm tới VPN Gate thật (2026-09-10). OpenVPN và WireGuard chạy native qua TqkLibrary.VpnClient mới có unit test; đường WireGuard qua wireproxy cũng chưa chạy thật vì máy chưa có `wireproxy.exe`.
- **Vị trí:** `src/ProxyDivert.Core/Vpn/Client/`, `src/ProxyDivert.Core/Vpn/VpnProfileReader.cs`, `libs/TqkLibrary.VpnClient/src/TqkLibrary.VpnClient.Tunnels/`.
- **Vì sao:** SoftEther cũng qua hết test offline nhưng chạy thật mới lộ lỗi login PACK; hai giao thức này có thể giấu lỗi cùng loại.
- **Ngày ghi:** 2026-10-09 (chuyển từ docs/Plan-vi.md đã xoá).

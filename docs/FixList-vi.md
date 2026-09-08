# Danh sách cần sửa

Kết quả đợt rà soát toàn bộ mã nguồn ngày 2026-09-08: ProxyDivert (Core / Wpf / Cli), ba submodule TqkLibrary.WinDivert, TqkLibrary.Proxy, và cách ProxyDivert dùng TqkLibrary.VpnClient (không rà nội bộ VpnClient). Rà theo hai trục: **lỗi tiềm ẩn** (phần A–D) và **thiết kế** (phần E, ưu tiên hướng đối tượng rồi tới tái sử dụng).

Thuật ngữ: [Glossary-vi.md](Glossary-vi.md). Mọi vị trí code là số dòng tại commit `ee7b799` trên `master` (các file được link không đổi so với lúc rà, `d7a60c0`); sửa xong mục nào thì đánh dấu `[x]` và ghi commit.

Cách đọc mỗi mục:
- **Mức**: `Cao` = mất dữ liệu / mất mạng / rò tài nguyên tích luỹ trong dùng bình thường; `Vừa` = sai trong tình huống thường gặp nhưng có đường tránh; `Thấp` = hiếm gặp hoặc chỉ tốn tài nguyên.
- **Vị trí** → **Vấn đề** → **Vì sao cần sửa** → **Cách sửa** → (**Ghi chú** nếu cần đổi API thư viện hay có ràng buộc).

Các file đang được sửa cho tính năng tray icon / auto start (`AppConfig`, `MainViewModel`, `SettingsViewModel`, `App.xaml.cs`, `AppArguments`, `StartupRegistration`) **không** nằm trong đợt rà này.

---

## A. Lỗi tiềm ẩn — ProxyDivert

### A1. Nút Test outbound VPN để lại `wireproxy.exe` chạy mãi — Cao

- [x] **Vị trí**: [RedirectEngine.cs:621-637](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L621-L637), [OutboundSourceFactory.cs:142-152](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L142-L152)
- **Vấn đề**: `factory.Create(outbound)` cố ý không ghi vào `_cache`, nên `using var factory` khi dispose duyệt một cache rỗng. Chỉ `tunnel` (IConnectSource) được dispose; `IProxySource` không. Với outbound VPN, [wireproxy](Glossary-vi.md#L93) chỉ bị `Kill()` trong `WireProxyProcessRunner.Dispose`, mà không ai gọi.
- **Vì sao**: mỗi lần bấm Test là thêm một tiến trình con mồ côi giữ cổng SOCKS và khoá UDP; comment ở dòng 619-620 nói ngược với điều đang xảy ra.
- **Cách sửa**: đưa `source` ra biến ngoài `try`, thêm `(source as IDisposable)?.Dispose()` trong `finally` (sau khi dispose `tunnel`).
- **Đã sửa**: ProxyDivert — commit "Put down the tunnel the Test button started"

### A2. `UdpProxyForwarder._tunnels` tăng vô hạn — Cao

- [x] **Vị trí**: [UdpProxyForwarder.cs:26](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L26), [UdpProxyForwarder.cs:39-46](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L39-L46), [RoutingPolicyResolver.cs:139-140](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L139-L140)
- **Vấn đề**: khoá theo `(outboundId, clientPort, isIpv6)` nên mỗi cổng nguồn của tiến trình là một `PortTunnel` giữ một kết nối TCP điều khiển [SOCKS5](Glossary-vi.md#L21) + một socket UDP + hai Task. Chỉ bị xoá khi user sửa đúng outbound đó hoặc stop engine. Nhánh `UdpMode.ThroughOutbound` không đọc `BlockQuic`.
- **Vì sao**: trình duyệt mở cổng nguồn mới cho mỗi kết nối [QUIC](Glossary-vi.md#L29) và mỗi query DNS, vài giờ là hàng nghìn tunnel, mỗi cái một socket và một kết nối tới proxy.
- **Cách sửa**: thêm `LastUsedTicks` vào `PortTunnel`, một timer quét đóng tunnel im lặng quá 60 giây, cộng cap cứng số tunnel trên mỗi outbound. Cân nhắc áp `BlockQuic` cho cả nhánh `ThroughOutbound`.
- **Đã sửa**: ProxyDivert — commit "Let idle UDP tunnels go, and put a ceiling on them". KHÔNG áp `BlockQuic` cho nhánh `ThroughOutbound`: ở đó datagram đi trong tunnel nên không rò IP thật, chặn chỉ làm trình duyệt mất vài giây thử QUIC rồi mới lùi về TCP — đúng cái đã sửa ở đợt 2026-09-07.

### A3. `ProcessInventory.Reconcile` retire nhầm tiến trình vừa start — Vừa

- [x] **Vị trí**: [ProcessInventory.cs:207-253](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L207-L253), [ProcessInventory.cs:376-391](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L376-L391)
- **Vấn đề**: `Reconcile` chụp danh sách tiến trình ở thời điểm T rồi cuối cùng retire mọi pid không có trong đó. `OnProcessStarted` chạy trên thread ETW/WMI **không** lấy `_reconcileLock`, nên tiến trình start ở T+ε được `Admit` → attach → rồi bị `Retire(P, "exited")` ngay → `SocketTracker.RemoveProcess` xoá sạch flow của nó. Nhận lại ở lần reconcile sau (5 giây).
- **Vì sao**: trình duyệt spawn hàng chục tiến trình con trong vài trăm ms, cửa sổ này trúng thật; kết quả là vài giây đầu của tiến trình đi thẳng không qua proxy.
- **Cách sửa**: ghi tick lúc bắt đầu `ListAll()`, thêm `AdmittedTicks` vào entry và bỏ qua retire cho entry được admit sau tick đó; hoặc cho `OnProcessStarted` lấy `_reconcileLock`.
- **Đã sửa**: commit "fix(processes): stop retiring processes that started during the listing". Không cần tick: chụp `HashSet` các pid có trong bảng **trước** `ListAll()` và chỉ retire trong tập đó — entry do event thread admit giữa chừng không nằm trong tập nên không bị đụng, đồng thời tránh được dictionary tick phụ (phải dọn key mồ côi và vẫn còn race giữa ghi tick với `TryAdd`). Kèm test `A_process_that_starts_while_the_machine_is_being_listed_is_not_retired_as_exited` (đã xác nhận fail khi bỏ fix) và `A_process_that_exits_is_still_retired_on_the_next_pass` để chặn hướng "không retire gì nữa".

### A4. `RebuildResolver` ghi `_resolver` ngoài `_stateLock` — Vừa

- [ ] **Vị trí**: [RedirectEngine.cs:332-337](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L332-L337), gọi từ [RedirectEngine.cs:304-330](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L304-L330) và [RedirectEngine.cs:184-194](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L184-L194)
- **Vấn đề**: thread sự kiện tiến trình đọc `_config` cũ, UI thread `ApplyConfig` gán `_config` mới và dựng resolver mới, rồi thread sự kiện gán đè `_resolver` bằng bản dựng từ config cũ. Field cũng không `Volatile`.
- **Vì sao**: user vừa Save, log báo "configuration applied", nhưng mọi kết nối mới đi theo policy cũ cho tới lần attach/detach kế tiếp; rất khó tái hiện.
- **Cách sửa**: chụp `_config` vào biến local dưới `_stateLock` rồi `Volatile.Write(ref _resolver, ...)`, hoặc bọc thân `RebuildResolver` trong lock.

### A5. `ConfigStore.Save` không lock, tên temp cố định — Vừa

- [ ] **Vị trí**: [ConfigStore.cs:64-81](../src/ProxyDivert.Core/Configuration/ConfigStore.cs#L64-L81), [AppServices.cs:150](../src/ProxyDivert.Wpf/Services/AppServices.cs#L150), [AppServices.cs:259-268](../src/ProxyDivert.Wpf/Services/AppServices.cs#L259-L268)
- **Vấn đề**: `AppServices.Save()` chạy trên UI thread, còn `SaveAndApply`/`StartEngineAsync`/`SetVpnConnectedAsync` gọi `ConfigStore.Save` trên worker queue. Hai bên chồng nhau trên cùng `FilePath + ".tmp"`.
- **Vì sao**: hoặc `IOException` ném thẳng lên UI thread, hoặc A ghi xong `.tmp`, B truncate `.tmp`, A `File.Replace` bằng file cụt: mất toàn bộ cấu hình, mà project không có backup ngoài `.bak` lúc load hỏng.
- **Cách sửa**: `lock` tĩnh quanh thân `Save`; tên temp duy nhất (`FilePath + "." + Guid.NewGuid():N + ".tmp"`); dài hạn thì đưa mọi Save qua worker queue (xem E).

### A6. `GetOrAdd` với factory tốn tài nguyên không dispose bản thua — Vừa

- [x] **Vị trí**: [OutboundSourceFactory.cs:51-57](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L51-L57), [UdpProxyForwarder.cs:44](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L44)
- **Vấn đề**: `ConcurrentDictionary.GetOrAdd` có thể chạy factory hai lần khi hai kết nối tới cùng outbound chưa cache. Bản không thắng bị vứt mà không `Dispose()`. Với `PortTunnel`, bản thua đã kịp `Task.Run(AssociateAsync)` nên mở hẳn một UDP ASSOCIATE không ai đóng.
- **Cách sửa**: `GetOrAdd(key, k => new Lazy<T>(() => ..., ExecutionAndPublication)).Value`, hoặc so sánh reference sau `GetOrAdd` và dispose bản không được giữ.
- **Đã sửa**: ProxyDivert — commit "Build a shared instance once, even when two threads ask together"

### A7. `ApplyConfig` giữ `_stateLock` khi dispose tuần tự PortTunnel — Vừa

- [ ] **Vị trí**: [RedirectEngine.cs:184-214](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L184-L214), [RedirectEngine.cs:288-300](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L288-L300), [UdpProxyForwarder.cs:179-186](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L179-L186)
- **Vấn đề**: mỗi `PortTunnel.Dispose` chờ tới 2 giây, tuần tự, dưới lock. Cộng với A2, sửa một outbound có thể khoá `_stateLock` hàng chục giây, chặn `Start`/`Stop`/`ApplyConfig` kế tiếp. `CloseWhereRouteChanged` cũng gọi `Cancel()` dưới lock nên callback huỷ chạy inline.
- **Cách sửa**: thu thập danh sách cần bỏ dưới lock, dispose ngoài lock (hoặc trên thread pool).

### A8. Mode nghe socket quét bảng kernel hai lần cho mỗi pid mới — Vừa

- [ ] **Vị trí**: [RedirectEngine.cs:283](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L283), [RedirectEngine.cs:304-316](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L304-L316), [SocketTracker.cs:204-228](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L204-L228)
- **Vấn đề**: `ShouldTrackProcess` → `ProcessRuleTracker.ShouldRedirect` → `TryAttach` raise `ProcessAttached` **đồng bộ** → `OnProcessAttached` gọi `AddTrackedProcessId` → `SocketTracker.AddProcess` chạy `PrePopulateForPid`. Stack quay về `AcceptPid` lại chạy `PrePopulateForPid` lần hai. `RebuildResolver()` cũng chạy trên chính luồng pump.
- **Vì sao**: quét bảng toàn máy hai lần ngay trên đường trễ SYN→accept, đúng thứ đã tối ưu ở đợt 2026-09-07.
- **Cách sửa**: `ShouldRedirect` trả verdict thay vì raise `ProcessAttached` trong callback; hoặc `OnProcessAttached` bỏ qua `AddTrackedProcessId` khi đang ở trong callback (cờ `[ThreadStatic]`). Gắn với E (tách trách nhiệm engine).

### A9. `UdpProxyForwarder`: chặn async trên vòng nhận, fire-and-forget nuốt lỗi — Vừa

- [ ] **Vị trí**: [UdpProxyForwarder.cs:62](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L62), [UdpProxyForwarder.cs:147](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L147)
- **Vấn đề**: `InjectUdpReplyToProcessAsync(...).GetAwaiter().GetResult()` chặn cả `ReceiveLoopAsync`; `_ = tunnel.SendAsync(...)` trong `try/catch` chỉ bắt lỗi đồng bộ, lỗi async thành unobserved và hàm vẫn trả `true`.
- **Cách sửa**: `await` trong `ReceiveLoopAsync`; với `SendAsync` gắn `.ContinueWith(log, OnlyOnFaulted)` hoặc await có try/catch.

### A10. Các tab không đồng bộ danh sách với nhau — Cao

- [x] **Vị trí**: [ProcessesViewModel.cs:54-61](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L54-L61), [RulesViewModel.cs:127-172](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L127-L172), [OutboundsViewModel.cs:146-179](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L146-L179), [ProcessFilterViewModel.cs:101-106](../src/ProxyDivert.Wpf/ViewModels/ProcessFilterViewModel.cs#L101-L106)
- **Vấn đề**: mỗi ViewModel nạp `Policies`/`Outbounds` một lần trong constructor. `MainViewModel.ReloadAll` tồn tại nhưng không ai gọi. Thêm policy ở tab Rules → editor filter ở tab Processes không thấy; xoá policy → fallback `Policies[0]` ghi Guid mồ côi vào `rule.PolicyIds`; thêm outbound → combo "Default outbound" tab Rules không thấy.
- **Vì sao**: user tạo policy rồi gán cho tiến trình là luồng thao tác chính của app.
- **Cách sửa**: ngắn hạn gọi `ReloadAll()` khi TabControl đổi tab. Đúng hơn (xem E): một `ObservableCollection` duy nhất cho policy và outbound sống trong `AppServices`, các ViewModel bind chung.
- **Đã sửa**: ProxyDivert — commit "fix(ui): refresh the shared lists when the tab changes". Mới build xanh, CHƯA chạy thử trên UI thật.

### A11. `LaunchSuspended` resume trước khi luật mới tới engine — Cao

- [x] **Vị trí**: [ProcessesViewModel.cs:268-274](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L268-L274), [AppServices.cs:173-195](../src/ProxyDivert.Wpf/Services/AppServices.cs#L173-L195)
- **Vấn đề**: `Add(rule)` gọi `SaveAndApply()` là hàm **enqueue** lên worker rồi trả Task không ai await. Ngay dòng sau `ForceProcessScan()` khớp với config cũ rồi `suspended.Resume()`.
- **Vì sao**: đúng [rò rỉ SYN](Glossary-vi.md#L37) mà tính năng launch-suspended sinh ra để bịt; tiến trình chạy mà không được redirect tới lần scan sau.
- **Cách sửa**: đổi command thành `async Task`, `await _services.SaveAndApply()` (hoặc `WhenIdleAsync()`) trước `ForceProcessScan()` + `Resume()`.
- **Đã sửa**: ProxyDivert — commit "fix(processes): wait for the new filter before resuming a suspended launch"

### A12. Cột Duration của connection đã đóng đếm mãi — Vừa

- [ ] **Vị trí**: [ByteSizeConverter.cs:36-51](../src/ProxyDivert.Wpf/Converters/ByteSizeConverter.cs#L36-L51), [ConnectionsView.xaml:33-34](../src/ProxyDivert.Wpf/Views/ConnectionsView.xaml#L33-L34)
- **Vấn đề**: `DurationConverter` chỉ nhận `StartedUtc`, luôn tính tới `UtcNow`; `ConnectionInfo` có sẵn `EndedUtc`.
- **Cách sửa**: [MultiBinding](Glossary-vi.md#L133) `StartedUtc` + `EndedUtc`, tính `(EndedUtc ?? UtcNow) - StartedUtc`.

### A13. Tick checkbox chọn hàng làm filter "dirty" — Vừa

- [ ] **Vị trí**: [ConditionNodeViewModel.cs:20](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L20), [ConditionNodeViewModel.cs:35-37](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L35-L37), [ProcessFilterViewModel.cs:193-197](../src/ProxyDivert.Wpf/ViewModels/ProcessFilterViewModel.cs#L193-L197)
- **Vấn đề**: constructor đăng ký `PropertyChanged → RaiseChanged` cho mọi property, kể cả `IsSelected` mà doc comment nói "Never saved". Mở filter chỉ để xem, tick rồi bỏ tick, đóng → bị hỏi "chưa lưu".
- **Cách sửa**: lọc `e.PropertyName` trong handler, bỏ `IsSelected` (và `IsNew` ở leaf).

### A14. `Ungroup` âm thầm đổi ngữ nghĩa bộ lọc — Vừa

- [ ] **Vị trí**: [ConditionGroupViewModel.cs:111-114](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionGroupViewModel.cs#L111-L114), [ConditionGroupViewModel.cs:173-184](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionGroupViewModel.cs#L173-L184)
- **Vấn đề**: `CanUngroup` chỉ chặn khi `Negate`. Với `x AND (a OR b)`, Ungroup đổ `a`, `b` vào cha và vứt toán tử con → `x AND a AND b`.
- **Cách sửa**: chặn (hoặc hỏi xác nhận) khi `group.Operator != Parent.Operator && Children.Count > 1`.

### A15. CLI: exception khi `--launch` trỏ file không tồn tại thoát ra ngoài — Vừa

- [ ] **Vị trí**: [Program.cs:229-262](../src/ProxyDivert.Cli/Program.cs#L229-L262)
- **Vấn đề**: khối chỉ có `finally`, không `catch`; stack trace và exit code lạ thay vì một dòng lỗi.
- **Cách sửa**: thêm `catch (Exception ex)` ghi `Console.Error` và `return 1`.

### A16. Nhóm mức Thấp (ProxyDivert)

- [ ] Id trùng trong json làm `RoutingPolicyResolver` ném trong ctor: [RoutingPolicyResolver.cs:34-37](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L34-L37). Dùng vòng gán `dict[x.Id] = x` như `OutboundUsage`.
- [ ] Regex của user trong `HostMatcher` không có timeout: [HostMatcher.cs:86-99](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L86-L99). Truyền `TimeSpan.FromMilliseconds(50)` như `ProcessRuleMatcher` và coi `RegexMatchTimeoutException` là không khớp.
- [ ] `DomainSuffix` với pattern `.example.com` không bao giờ khớp: [HostMatcher.cs:58-64](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L58-L64). `TrimStart('.')`.
- [ ] `OnProcessStopped` chỉ detach con trực tiếp, không lan xuống cháu: [ProcessRuleTracker.cs:369-379](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L369-L379). Lặp tới khi không đổi như `FollowParents`.
- [ ] `AttachProcessId` có thể ném `KeyNotFoundException` khi `Detach` xen giữa: [ProcessRuleTracker.cs:229](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L229). `TryGetValue`.
- [ ] `Dns.GetHostAddresses` đồng bộ trên đường kết nối và luôn lấy `addresses[0]`: [OutboundSourceFactory.cs:280-295](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L280-L295). Ưu tiên IPv4 khi `Ipv6Support == Disabled`, cache endpoint.
- [ ] Đổi tên policy làm mất `SelectedRule`: [RulesViewModel.cs:111-121](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L111-L121). Nhớ và gán lại sau `Insert`.
- [ ] Timer refresh Connections/Log chạy cả khi tab ẩn, xoá selection mỗi 250ms: [ConnectionsViewModel.cs:38-41](../src/ProxyDivert.Wpf/ViewModels/ConnectionsViewModel.cs#L38-L41), [LogViewModel.cs:34-37](../src/ProxyDivert.Wpf/ViewModels/LogViewModel.cs#L34-L37). Start/Stop theo `IsVisible`.
- [ ] Cây tiến trình có thể lặp vô hạn khi PID bị tái dùng thành vòng cha–con: [ProcessesViewModel.cs:77-98](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L77-L98). Tập đã thăm khi nối.

---

## B. Lỗi tiềm ẩn — TqkLibrary.WinDivert

### B1. Đóng handle khi thread khác còn đang `Recv` — Cao

- [x] **Vị trí**: [WinDivertHandle.cs:41-93](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/WinDivertHandle.cs#L41-L93), [PacketPump.cs:113-121](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L113-L121), [SocketTracker.cs:322-324](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L322-L324), [SocketTracker.cs:559-572](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L559-L572)
- **Vấn đề**: mọi P/Invoke nhận `IntPtr` lấy từ `DangerousGetHandle()` nên [SafeHandle](Glossary-vi.md#L378) không đếm tham chiếu được. `PacketPump.Dispose` chờ 1 giây rồi `_handle.Dispose()` vô điều kiện dù pump chưa thoát; `Inject` cũng gọi hai hàm native sau một lần đọc `_disposed` không có gì giữ handle sống.
- **Vì sao**: pump kẹt trong `Recv` quá 1 giây (driver bận, máy chậm) → `DeviceIoControl` trên số handle đã đóng, kernel có thể đã cấp số đó cho object khác. Đây là lỗi native, không catch được.
- **Cách sửa**: khai báo các P/Invoke nhận `WinDivertSafeHandle` để marshaller tự `AddRef/Release`; trong `Dispose` bỏ qua `_handle.Dispose()` khi `Wait` timeout (để finalizer lo).
- **Đã sửa**: TqkLibrary.WinDivert `4656753`

### B2. Một lỗi `Recv` giết pump vĩnh viễn, chỉ log Debug — Cao

- [x] **Vị trí**: [PacketPump.cs:58-62](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L58-L62), [PacketPump.cs:91](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L91), [WinDivertHandle.cs:41-47](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/WinDivertHandle.cs#L41-L47), [SocketTracker.cs:441-460](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L441-L460)
- **Vấn đề**: `if (!TryRecv(...)) break;` coi mọi lỗi là shutdown; `TryRecv` vứt `GetLastWin32Error`. Buffer `new byte[65535]` nhỏ hơn `WINDIVERT_MTU_MAX` (65575) nên gói LSO/GSO lớn trả `ERROR_INSUFFICIENT_BUFFER`.
- **Vì sao**: một gói là đủ dừng toàn bộ chuyển hướng; mọi packet sau đó đi thẳng (lộ IP thật) mà `ProcessRedirector`/UI không có tín hiệu nào.
- **Cách sửa**: `TryRecv` trả mã Win32; `break` chỉ khi `ERROR_NO_DATA`/`ERROR_OPERATION_ABORTED`, `continue` khi `ERROR_INSUFFICIENT_BUFFER`, log Error mã khác. Buffer 65576. Thêm sự kiện `PumpStopped` để host biết và báo UI.
- **Đã sửa**: TqkLibrary.WinDivert `db58a43`

### B3. `NatTable.Remove` không có ai gọi — Cao

- [x] **Vị trí**: [NatTable.cs:16-41](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatTable.cs#L16-L41), [NatRedirectMiddleware.cs:188](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L188), [ProcessRedirector.cs:131](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L131)
- **Vấn đề**: entry chỉ bị ghi đè, không hết hạn. Khi Windows tái dùng cổng nguồn mà SYN mới bị lỡ, `Find` trả entry cũ → bỏ qua nhánh [luồng thoát](Glossary-vi.md#L296) → NAT giữa dòng → kết nối chết. `ResetEscapedFlows` cũng bỏ sót đúng những flow cần reset. Bộ nhớ bị chặn trên ~262k entry nhưng không bao giờ nhả.
- **Cách sửa**: gọi `Remove` từ `TcpConnectClosed` (tracker đã bắn `FlowKey` đủ proto/port/family), hoặc thêm mốc thời gian và quét như `SocketTracker.CleanupLoop`.
- **Đã sửa**: TqkLibrary.WinDivert `37b9048`

### B4. Cache verdict theo pid trần, sai khi tái dùng PID — Vừa

- [ ] **Vị trí**: [SocketTracker.cs:204-231](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L204-L231), [SocketTracker.cs:315](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L315); [ProcessTreeMonitor.cs:26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L26), [ProcessTreeMonitor.cs:92-97](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L92-L97)
- **Vấn đề**: `_pidDecisions[pid] = false` chỉ bị xoá bởi `RemoveProcess`, mà `RemoveProcess` chỉ được gọi cho pid đã attach. Pid được phán `false` (chrome) thoát, Windows cấp lại số đó cho game cần redirect → `AcceptPid` trả cache `false` mãi mãi, không log. Dictionary cũng phình theo mọi tiến trình trên máy. `ProcessTreeMonitor` so `seen` với `_knownDescendants` tích luỹ toàn thời gian thay vì snapshot trước, nên con mới trúng pid cũ không bao giờ bắn `ChildSpawned`.
- **Vì sao**: xem [tái dùng PID](Glossary-vi.md#L374); mode nghe socket dựa hoàn toàn vào cache này.
- **Cách sửa**: khoá theo `(pid, startTime)` hoặc TTL cho verdict `false`; `ProcessTreeMonitor` gán `_knownDescendants = seen` cuối mỗi `Scan`.

### B5. UDP relay khoá upstream theo cổng nguồn, đích đóng băng lần đầu — Vừa

- [ ] **Vị trí**: [UdpRelayServer.cs:91](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L91), [UdpRelayServer.cs:132-150](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L132-L150)
- **Vấn đề**: `GetOrAdd(UpstreamKey(srcPort, family), _ => new UdpUpstream(entry.OriginalDestination))`: một socket UDP nói với nhiều đích (DNS nhiều server, QUIC, STUN) thì datagram thứ hai trở đi gửi sai địa chỉ. `_upstreams` không bao giờ evict.
- **Ghi chú**: trong ProxyDivert nhánh này chỉ chạm khi `HandleUdpDatagram` trả payload ở trường hợp Direct-sau-redirect hiếm ([RedirectEngine.cs:558-570](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L558-L570)), còn proxy/VPN đi qua `UdpProxyForwarder`. Vẫn là lỗi thư viện.
- **Cách sửa**: gửi thẳng `SendAsync(payload, entry.OriginalDestination)` trên một socket theo cổng, thêm idle-eviction.

### B6. `TcpRelayServer.HandleAsync`: phần trước `try` ném thì rò `TcpClient` — Vừa

- [ ] **Vị trí**: [TcpRelayServer.cs:101](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L101), [TcpRelayServer.cs:105-129](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L105-L129)
- **Vấn đề**: `_ = Task.Run(() => HandleAsync(...))`; `RemoteEndPoint` và `new RedirectedTcpConnection` → `GetStream()` nằm ngoài `catch`. Client connect rồi RST ngay (QUIC fallback, racing connections) → task faulted không log, `client` không `Close()`.
- **Cách sửa**: bọc toàn bộ thân trong `try/catch/finally` có `client.Close()`, `.ContinueWith` log lỗi.

### B7. `ProcessRedirector.Start` không rollback — Vừa

- [ ] **Vị trí**: [ProcessRedirector.cs:138-176](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L138-L176)
- **Vấn đề**: `tracker.Start()` và `StartRelays()` chạy trước `OpenNetworkHandle`. Handle mở thất bại (không elevated) → `Start()` ném ra, tracker + hai relay listener + pump task còn sống, caller thường không `Dispose` vì "Start đã fail".
- **Cách sửa**: `try/catch` quanh thân, gọi `Dispose()` rồi rethrow.

### B8. `AddProcess` đua với `Dispose`, handler ném làm `RemoveProcess` dở dang — Vừa

- [ ] **Vị trí**: [SocketTracker.cs:239-299](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L239-L299), [SocketTracker.cs:328-345](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L328-L345), [SocketTracker.cs:551-577](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L551-L577)
- **Vấn đề**: `AddProcess` từ thread watcher, `Dispose` từ UI: qua được check `_cts.IsCancellationRequested` rồi `Dispose` clear xong trước `TryAdd` → handle không bao giờ đóng; task body đọc `_cts.Token` sau `_cts.Dispose()` → `ObjectDisposedException` unobserved. `RemoveProcess` gọi `TcpConnectClosed?.Invoke`/`UdpBindRemoved?.Invoke` không try/catch, một subscriber ném là handle SOCKET đã đóng nhưng flow còn nguyên.
- **Cách sửa**: lock hoặc cờ `_disposed` kiểm lại sau `TryAdd`; chụp `_cts.Token` trước `Task.Run`; bọc từng `Invoke` như `PumpLoop`.

### B9. `ConcurrentDictionary.Count` trên hot path mỗi sự kiện socket — Vừa

- [ ] **Vị trí**: [SocketTracker.cs:504](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L504), [SocketTracker.cs:516](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L516), [SocketTracker.cs:531](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L531)
- **Vấn đề**: `_tcpFlows.Count` lấy **mọi** lock của dictionary và là tham số `LogTrace` nên chạy dù không bật Trace, trong khi pump NETWORK đọc `_tcpFlows` mỗi packet. Mode máy-toàn-bộ là mọi socket event trên máy.
- **Cách sửa**: bọc `if (_logger.IsEnabled(LogLevel.Trace))` như `NatRedirectMiddleware.cs:169` đã làm.

### B10. `DnsCacheLookup.Refresh` redirect stderr không đọc, không Kill khi timeout — Vừa

- [ ] **Vị trí**: [DnsCacheLookup.cs:39](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L39), [DnsCacheLookup.cs:67-79](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L67-L79)
- **Vấn đề**: `RedirectStandardError = true` nhưng chỉ `ReadToEnd()` stdout; `ipconfig` ghi đủ 4KB stderr là pipe đầy, `ReadToEnd` treo vô hạn, vòng refresh chết im lặng. `WaitForExit(10s)` không `Kill()`. `Start()` check-then-assign không atomic.
- **Cách sửa**: bỏ redirect stderr (hoặc `BeginErrorReadLine`), `Kill()` khi timeout, `Interlocked.CompareExchange` trong `Start`.

### B11. `IDnsCacheLookup` transient bị root ServiceProvider giữ mãi — Vừa

- [ ] **Vị trí**: [RedirectServiceCollectionExtensions.cs:44](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/DependencyInjection/RedirectServiceCollectionExtensions.cs#L44), [DnsCacheLookup.cs:26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L26)
- **Vấn đề**: `sp.GetRequiredService<IDnsCacheLookup>` với `sp` là root; transient `IDisposable` được root container thêm vào danh sách disposables và giữ tham chiếu mạnh tới khi app thoát. Mỗi Start/Stop là một bản rò kèm `_map` chỉ tăng.
- **Cách sửa**: resolve từ `IServiceScope` do session sở hữu, hoặc đăng ký factory trả instance không bị container theo dõi.

### B12. `ReverseDnsTable.Trim` sort toàn bảng trên mỗi Add sau khi đầy — Vừa

- [ ] **Vị trí**: [ReverseDnsTable.cs:43](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/ReverseDnsTable.cs#L43), [ReverseDnsTable.cs:74-90](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/ReverseDnsTable.cs#L74-L90)
- **Vấn đề**: trim đúng phần thừa nên `Count == _capacity` sau trim, Add kế tiếp lại copy list 20k + Sort O(n log n), trên luồng pump mỗi câu trả lời DNS.
- **Cách sửa**: trim theo lô 10-20% capacity.

### B13. Peek 2048 byte không đủ cho ClientHello hậu lượng tử → mất SNI — Vừa

- [ ] **Vị trí**: [TlsClientHelloParser.cs:21](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/TlsClientHelloParser.cs#L21), [TlsClientHelloParser.cs:67-72](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/TlsClientHelloParser.cs#L67-L72), [HostNameInspector.cs:54](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/HostNameInspector.cs#L54)
- **Vấn đề**: Chrome/Edge với X25519MLKEM768 gửi ClientHello ~2.0-2.3KB và xáo thứ tự extension; `key_share` ~1.2KB đứng trước `server_name` và vắt qua mốc 2048 là parser trả false, `HostNameInspector` trả null. Routing theo domain âm thầm tụt xuống reverse-DNS/IP.
- **Cách sửa**: nâng `RecommendedPeekSize` lên 8192 (hoặc 16644 = record TLS tối đa).

### B14. Nhóm mức Thấp (WinDivert)

- [ ] Parser không kiểm đủ byte cho header TCP/UDP, không xét fragment: [PacketParser.cs:12-34](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/PacketParser.cs#L12-L34). Buffer pump dùng lại nên đọc rác của gói trước thay vì ném. Yêu cầu `ihl + 20/8 <= length`, bỏ qua fragment-offset != 0.
- [ ] IPv6 extension header không được duyệt, gói TCP đứng sau Hop-by-Hop bị cho qua không redirect: [PacketParser.cs:30-31](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/PacketParser.cs#L30-L31). Duyệt chuỗi extension chuẩn hoặc Drop rõ ràng.
- [ ] `ExpireTick == 0` trùng giá trị thật một lần mỗi 49.7 ngày: [SocketTracker.cs:527](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L527), [TcpFlowState.cs:7](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/Models/TcpFlowState.cs#L7). Dùng `long?` hoặc `bool IsClosing`.
- [ ] `_udpBinds` không có reaper; `SocketClose` lặp lại gia hạn `ExpireTick` mãi: [SocketTracker.cs:472-481](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L472-L481), [SocketTracker.cs:528-530](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L528-L530).
- [ ] Bind UDP trên ANY che phủ mọi tiến trình cùng cổng: [SocketTracker.cs:144-155](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L144-L155).
- [ ] `PacketPump`: `CalcChecksums`/`TrySend` nằm ngoài `try`, middleware set `ctx.Length` quá buffer là pump chết không log: [PacketPump.cs:71-89](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L71-L89). `_cts.Dispose()` khi middleware async còn cầm token: [PacketPump.cs:120](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L120).
- [ ] `DnsMessageParser` cấp phát `List(answerCount)` theo số do gói tin quyết định: [DnsMessageParser.cs:55](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsMessageParser.cs#L55). `Math.Min(answerCount, 64)`.
- [ ] `RelayToAsync` không truyền half-close: [RedirectedTcpConnection.cs:83-89](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L83-L89). `Shutdown(Send)` phía còn lại rồi đợi chiều kia.
- [ ] `TcpRelayServer.Dispose` dispose CTS khi handler còn chạy: [TcpRelayServer.cs:161-162](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L161-L162).
- [ ] `Dispose` không xoá event/collection: [ProcessTreeMonitor.cs:125-130](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L125-L130), [SocketTracker.cs:551-577](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L551-L577); `SemaphoreSlim` trong [DnsOverHttpsMiddleware.cs:52](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs#L52) không dispose.
- [ ] `SuspendedProcess.Resume()` sau `Dispose()` ném `Win32Exception` khó hiểu: [SuspendedProcess.cs:29-45](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/SuspendedProcess.cs#L29-L45). Set `_resumed = true` trong `Dispose`.
- [ ] Dead code: `WinDivertNative.RecvEx`/`HelperCompileFilter`/`Ntohs`/`Htons` ([WinDivertNative.cs:25-34](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/WinDivertNative.cs#L25-L34), [WinDivertNative.cs:63-76](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/WinDivertNative.cs#L63-L76)), `IpHlpApi.EnumerateProcessTcpFlows`/`EnumerateProcessUdpBinds` ([IpHlpApi.cs:114-126](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Native/IpHlpApi.cs#L114-L126)).

---

## C. Cách dùng TqkLibrary.VpnClient

### C1. Kết nối proxy rò vĩnh viễn trong stack (FIN-WAIT-2) — Cao

- [x] **Vị trí**: [VpnClientConnectSource.cs:86-92](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L86-L92); thư viện `TqkLibrary.VpnClient.Sockets/VpnNetworkStream.cs:61-65`, `TqkLibrary.VpnClient.IpStack/Tcp/TcpConnection.cs:37,520,565`
- **Vấn đề**: `Dispose` chỉ dispose stream; `VpnNetworkStream.Dispose` chỉ `CloseSend()` (gửi FIN). `TcpConnection` chỉ arm timer ở TIME-WAIT, không có timeout [FIN-WAIT-2](Glossary-vi.md#L382), và cửa sổ nhận luôn báo 65535 dù không ai đọc `_recvQueue`.
- **Vì sao**: trình duyệt huỷ request là chuyện thường; server không có lý do gửi FIN; mỗi lần là một cổng + hàng đợi nhận sống mãi trong tunnel dài ngày.
- **Cách sửa**: thư viện lộ `Abort()` (gửi RST) trên `VpnTcpClient`/`VpnNetworkStream` và `VpnClientConnectSource.Dispose` gọi nó; đồng thời thêm timer FIN-WAIT-2 trong `TcpConnection`.
- **Ghi chú**: cần commit trong submodule VpnClient trước.
- **Đã sửa**: VpnClient `77a494e` (Abort + timer FIN-WAIT-2, kèm 5 test); ProxyDivert — commit "Reset a finished tunnel connection instead of half-closing it"

### C2. Keeper không bao giờ dựng lại tunnel in-process — Cao

- [x] **Vị trí**: [VpnClientProxySource.cs:121-136](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L121-L136), [KeptVpnTunnel.cs:81-175](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L81-L175); thư viện `VpnReconnectOptions.cs:20` (`MaxAttempts = 0` = vô hạn), `ReconnectingVpnConnection.cs:205`, `VpnTunnelOptions.cs:8-39` (không có nút reconnect)
- **Vấn đề**: `WaitUntilDownAsync` chỉ hoàn thành khi `DriverState.Disconnected`, mà driver mặc định retry vô hạn với cap 30 giây. Toàn bộ backoff, `StableFor`, `RetryCount`, và việc `Invalidate` để nhận config user vừa sửa chỉ chạy được với wireproxy.
- **Vì sao**: server chết hoặc key bị thu hồi → UI kẹt "Reconnecting" với lý do rỗng, user sửa config cũng không được nhận vì source không bao giờ được dựng lại.
- **Cách sửa**: trong `WatchAsync`, `IsRunning == false` liên tiếp N chu kỳ poll thì coi là down và trả reason (vòng ngoài parse lại từ đầu); lâu dài thêm `ReconnectPolicy` vào `VpnTunnelOptions` để set `MaxAttempts`.
- **Đã sửa**: ProxyDivert — commit "Give up on a tunnel that keeps saying it is re-establishing"

### C3. `InTunnelResolver` bind UDP mới mỗi query, làm bộ đếm cổng wrap — Vừa

- [ ] **Vị trí**: [InTunnelResolver.cs:113-131](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L113-L131); thư viện `TcpIpStack.cs:35,77,80-81,92`
- **Vấn đề**: `_nextPort` dùng chung cho `ConnectAsync` và `BindUdp`, `(ushort)Interlocked.Increment` wrap sau 16k lần rồi quay vòng cả dải; `_connections[localPort] = connection` ghi đè kết nối sống, `Closed` của nạn nhân xoá nhầm entry mới. Resolver bind tới 12 socket mỗi tên (2 lần × 3 server × A/AAAA).
- **Cách sửa**: một `UdpConnection` sống lâu trong `InTunnelResolver`, khớp trả lời theo DNS id; thư viện cấp phát cổng bỏ qua cổng đang có trong bảng.

### C4. WireGuard in-process không có PersistentKeepalive — Vừa

- [ ] **Vị trí**: [VpnConnectionKeeper.cs:29](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L29), [VpnClientProxySource.cs:183-184](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L183-L184); thư viện `WireGuardConfFile.cs:36,132`, `WireGuardTimers.cs:80`; wireproxy có mặc định 25 ở `WireGuardOptions.cs:84`
- **Vấn đề**: comment của keeper nói "config writer thêm [PersistentKeepalive](Glossary-vi.md#L105) khi file không có", nhưng chỉ đúng với wireproxy. `VpnDialer.ConnectWireGuardAsync` parse file thô, keepalive = 0 = tắt.
- **Vì sao**: file của provider không có dòng này → sau 30-120 giây im lặng NAT phía peer hết hạn, tunnel im lặng chết mà WireGuard không báo link-loss.
- **Cách sửa**: thêm `DefaultPersistentKeepalive` vào `VpnTunnelOptions` (hoặc overload nhận `WireGuardConfig`), ép 25 giây khi file không có. Cần sửa trong submodule.

### C5. `KeptVpnTunnel.Dispose` dispose CTS khi loop còn chạy — Vừa

- [x] **Vị trí**: [KeptVpnTunnel.cs:192-207](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L192-L207), [KeptVpnTunnel.cs:126](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L126), [KeptVpnTunnel.cs:159](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L159)
- **Vấn đề**: `Cancel` → `Wait(2s)` có thể timeout (dial mặc định 90 giây) → `_cts.Dispose()`. Loop tới `Task.Delay(delay, ct)` ném `ObjectDisposedException`, không nhánh catch nào bắt; `SetStatus(Stopped)` không chạy, fault unobserved.
- **Cách sửa**: bỏ `_cts.Dispose()` hoặc chuyển vào `_loop.ContinueWith`.
- **Đã sửa**: ProxyDivert — commit "Stop disposing the supervision token out from under its own loop"

### C6. `Outbound.SupportsUdp` và source thực tế có thể lệch nhau — Vừa

- [ ] **Vị trí**: [Outbound.cs:72](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L72), [VpnProfileReader.cs:83-94](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L83-L94), [VpnProfileReader.cs:270-271](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L270-L271), [OutboundSourceFactory.cs:211-235](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L211-L235)
- **Vấn đề**: routing hỏi `RunsOnWireProxy` từ URL (`Auto` + không `.conf` → mang được UDP), factory lại `Sniff` nội dung file và trả `WireGuardWireProxy` cho bất kỳ `[Interface]` → wireproxy TCP-only. File `wg0.txt` làm UDP được route tới rồi fail ở `GetUdpAssociateSourceAsync` thay vì hạ xuống Block.
- **Cách sửa**: `Sniff` trả `Auto` cho file WireGuard không có đuôi `.conf` (ép user chọn rõ), để hai bên cùng trả lời từ URL.

### C7. Chặn async trong chuỗi dispose tới 7 giây mỗi outbound VPN — Vừa

- [ ] **Vị trí**: [VpnClientProxySource.cs:260](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L260), [KeptVpnTunnel.cs:198](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L198), [VpnConnectionKeeper.cs:129-134](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L129-L134), [AppServices.cs:206](../src/ProxyDivert.Wpf/Services/AppServices.cs#L206)
- **Vấn đề**: `DropAsync().Wait(5s)` sau `_loop.Wait(2s)`, chạy trong vòng lặp. Thường trên worker queue (mọi Save/Start/Stop sau đó xếp hàng), nhưng `ConnectRoutedVpns` gọi `Sync` trên **UI thread** nên đổi signature rồi Start là đơ cửa sổ.
- **Cách sửa**: chuỗi teardown `IAsyncDisposable` từ đầu tới cuối (xem E), hoặc giao drop cho pool và chỉ await lúc shutdown.

### C8. Nhóm mức Thấp (VpnClient usage)

- [ ] `InTunnelResolver` bỏ luôn fallback server khi gặp SERVFAIL: [InTunnelResolver.cs:93-94](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L93-L94). Chỉ dừng khi NXDOMAIN (rcode 3).
- [ ] Cache DNS khoá theo tên, không theo loại A/AAAA: [InTunnelResolver.cs:65](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L65), [InTunnelResolver.cs:96](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L96). Khoá `(name, type)`.
- [ ] Dial timeout tới proxy dưới dạng "cancelled": [VpnClientProxySource.cs:157-168](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L157-L168), [VpnClientConnectSource.cs:68-71](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L68-L71). Chỉ rethrow OCE khi token của caller thật sự bị huỷ, còn lại bọc `InitConnectSourceFailedException`.
- [ ] File `.vpn` không stamp config nó trỏ tới: [OutboundSignature.cs:48](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L48), [VpnProfileReader.cs:207-218](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L207-L218). Stamp cả file được tham chiếu.

---

## D. Lỗi tiềm ẩn — TqkLibrary.Proxy

ProxyDivert chỉ tham chiếu `TqkLibrary.Proxy` (phía client: `LocalProxySource`, `HttpProxySource`, `Socks4/5ProxySource`, `StreamTransferHelper`) và `TqkLibrary.Proxy.Vpn.WireProxyCli`. Phía server (`HttpProxyServer`, `Socks4/5ProxyServer`) chỉ chạy khi `ProxyDivert.Cli --self-host-port`. `SshCli`, `SshNet`, `GlobalUnicast`, `Reverse.*` không được ProxyDivert dùng; vẫn liệt kê vì là lỗi thư viện, gom ở D4 và D5.

### D1. `StreamTransferHelper`: một chiều kết thúc không kéo sập chiều còn lại — Cao (ProxyDivert dùng)

- [x] **Vị trí**: [StreamTransferHelper.cs:40-49](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L40-L49), [StreamTransferHelper.cs:53-96](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L53-L96); gọi từ [ConnectSourceExtensions.cs:27-31](../src/ProxyDivert.Core/Outbounds/Extensions/ConnectSourceExtensions.cs#L27-L31) và [RedirectEngine.cs:483-490](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L483-L490)
- **Vấn đề**: pump gặp EOF (`byte_read == 0`) hoặc exception chỉ `return`, không `Shutdown(Send)` cũng không dispose stream đối diện; `WaitUntilDisconnect` lại `Task.WhenAll` cả hai. Tiến trình đóng socket → pump client→proxy thoát, pump proxy→client vẫn treo ở `ReadAsync` trên upstream cho tới khi server phía xa tự đóng (keep-alive 60 giây tới vô hạn). `ForwardAsync` không return nên `finally { tunnel?.Dispose(); }` không chạy.
- **Vì sao**: mỗi kết nối tiến trình đóng là một tunnel SOCKS5 / VPN rò tới khi server idle-out; với VPN in-process còn cộng dồn với C1. Half-close (client FIN) cũng không được truyền sang server nên giao thức dựa vào half-close bị cắt. Nhánh cancel (Save đổi route) trên net8 vẫn cắt được vì `NetworkStream.ReadAsync` huỷ được, nhưng thư viện còn target netstandard2.0 nơi token không cắt read đang treo.
- **Cách sửa**: pump thoát vì EOF → `(peer as NetworkStream)?.Socket.Shutdown(SocketShutdown.Send)`; thoát vì lỗi/cancel → dispose cả hai stream; thêm `cancellationToken.Register(() => { first.Dispose(); second.Dispose(); })` cho TFM cũ. Thứ tự: sửa trong submodule, commit, rồi cập nhật app.
- **Đã sửa**: TqkLibrary.Proxy `65b53d0`

### D2. wireproxy "sẵn sàng" giả và zombie sau timeout — Cao (ProxyDivert dùng)

- [x] **Vị trí**: [WireProxyProcessRunner.cs:66-73](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L66-L73), [WireProxyProcessRunner.cs:138-166](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L138-L166)
- **Vấn đề**: fast-path dòng 68 ngoài lock: `_process != null && !HasExited` là `return` luôn, không chờ `WaitForListenerAsync`. (1) Caller A vừa `Start()` và đang chờ listener, caller B (kết nối thứ 2 của cùng burst) return ngay → `Socks5ProxySource` connect vào cổng chưa listen → refused. (2) `WaitForListenerAsync` timeout (10 giây, handshake WireGuard chậm) ném ra nhưng `_process` còn sống và **không bị kill**; mọi `EnsureStartedAsync` sau đó return ở dòng 68 coi như sẵn sàng, listener không bao giờ lên, không có đường dọn trừ dispose cả source.
- **Vì sao**: trình duyệt mở 6 socket song song vào outbound VPN vừa được chọn là trúng (1); mạng chậm lúc khởi động là trúng (2) và kẹt tới khi user Disconnect/Connect lại.
- **Cách sửa**: cache một `Task _startTask` chung cho mọi caller cùng await; trong `catch` của `WaitForListenerAsync` kill + null hoá `_process` trước khi ném.
- **Đã sửa**: TqkLibrary.Proxy `53a0702`

### D3. Không có Job Object → `wireproxy.exe` mồ côi khi app chết — Cao (ProxyDivert dùng)

- [x] **Vị trí**: [WireProxyProcessRunner.cs:218-235](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L218-L235), [WireProxyProcessRunner.cs:306-323](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L306-L323)
- **Vấn đề**: kill chỉ xảy ra trong `Dispose()`. End Task từ Task Manager, crash vì unhandled exception, hoặc A1 → wireproxy tiếp tục chạy, giữ tunnel WireGuard và cổng SOCKS5, tích luỹ mỗi lần chạy lại. File `%TEMP%\tqk-wg-*.conf` chứa PrivateKey nằm lại.
- **Cách sửa**: tạo Job Object với `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `AssignProcessToJobObject` ngay sau `Start()`; dọn `tqk-wg-*.conf` mồ côi lúc khởi tạo runner.
- **Đã sửa**: TqkLibrary.Proxy `3474023`

### D4. Nhóm WireProxyCli mức Vừa (ProxyDivert dùng)

- [x] `Dispose()` không lấy `_lock` → có thể sinh process sau khi đã dispose: [WireProxyProcessRunner.cs:306-323](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L306-L323) vs [WireProxyProcessRunner.cs:70-136](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L70-L136). User bấm Disconnect đúng lúc kết nối đầu đang dựng tunnel → `_process = process` gán sau khi `Dispose` đã chạy → mồ côi. Sửa: `Dispose` vào `_lock`, sau gán re-check `_disposed`.
- [x] Cổng SOCKS5 chốt một lần trong ctor, restart tái dùng cổng cũ; probe chỉ TCP connect trần: [WireProxyProcessRunner.cs:57](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L57), [WireProxyProcessRunner.cs:168-189](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L168-L189). Cổng bị chương trình khác chiếm trong lúc wireproxy chết → probe "thành công" vào tiến trình lạ. Sửa: chọn lại cổng mỗi restart, probe bằng greeting SOCKS5 (`05 01 00`).
- [x] Buffer stderr giữ 8KB **đầu** nên lý do chết bị mất: [WireProxyProcessRunner.cs:208-216](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L208-L216); [WireProxyKeptTunnel.cs:60-66](../src/ProxyDivert.Core/Vpn/WireProxyKeptTunnel.cs#L60-L66) lấy `lines[^1]` nên UI hiện một dòng log khởi động vô nghĩa. Sửa: ring buffer N dòng cuối.
- [x] File `.conf` tạm chứa PrivateKey không siết ACL trên Windows (`chmod` chỉ chạy khi `!_isWindows`): [WireProxyProcessRunner.cs:104-116](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L104-L116). Sửa: tạo file bằng `FileStream` + `FileSecurity` đúng quyền từ đầu.
- [x] `WireGuardConfigWriter` ghi giá trị nguyên xi, không chặn `\r\n`: [WireGuardConfigWriter.cs:33-66](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardConfigWriter.cs#L33-L66). Giá trị từ file `.conf` của user chứa xuống dòng sẽ inject thêm dòng cấu hình. Sửa: reject giá trị chứa `\r`/`\n`.
- [x] `process.Start()` ném thì `Process` không dispose; nhánh `isRestart && !AutoRestart` ném mãi không dọn: [WireProxyProcessRunner.cs:76-83](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L76-L83), [WireProxyProcessRunner.cs:127-135](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L127-L135). Thấp.
- [x] `ResolveBinary` chạy `where` đồng bộ trong ctor: [WireProxyProcessRunner.cs:284-299](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L284-L299). Thấp.
- **Đã sửa**: TqkLibrary.Proxy `23516ac` (Dispose vào lock), `d13d82d` (cổng + probe SOCKS5), `f5fb255` (ring buffer stderr), `992eba4` (ACL), `770ba56` (chặn xuống dòng), `53a0702` (Start ném thì dispose Process), `0c54d5f` (memo hoá `where`)

### D5. Nhóm TqkLibrary.Proxy core (client, ProxyDivert dùng) — Thấp

- [ ] `HttpProxySource` dùng `_proxy.Host` (giữ ngoặc `[::1]`) thay vì `DnsSafeHost` như Socks4/5: [HttpProxySource.ConnectTunnel.cs:38-40](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L38-L40). HTTP proxy cấu hình bằng literal IPv6 resolve thất bại.
- [ ] CONNECT không gửi header `Host` (RFC 7230 bắt buộc): [HttpProxySource.ConnectTunnel.cs:65-71](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L65-L71).
- [ ] `IsSupportIpv6` trên `Socks5ProxySource`/`HttpProxySource` là property chết, không chỗ nào đọc: [Socks5ProxySource.cs:52](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.cs#L52), [HttpProxySource.cs:35](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.cs#L35). `ApplyIpv6Support` ở [OutboundSourceFactory.cs:113-126](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L113-L126) vì thế là no-op với outbound Socks5/Http; chỉ chốt chặn của `RedirectEngine` mới có tác dụng. Liên quan E.
- [ ] `BaseProxySourceTunnel` finalizer gọi `Dispose(false)` nhưng override vẫn chạm object managed: [BaseProxySourceTunnel.cs:16-19](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/BaseProxySourceTunnel.cs#L16-L19), [Socks5ProxySource.BaseTunnel.cs:26-31](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs#L26-L31). Đường tới được: [UdpProxyForwarder.cs:127-129](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L127-L129) rò source khi `AssociateAsync` ném.
- [ ] Nhánh netstandard2.0 không có timeout connect/handshake, một số chỗ không truyền token: [Socks5ProxySource.BaseTunnel.cs:50-82](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs#L50-L82), [Socks4ProxySource.BaseTunnel.cs:28](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks4ProxySource.BaseTunnel.cs#L28).
- [ ] `LocalProxySource` UDP chỉ IPv4 dù `IsSupportIpv6 = true`: [LocalProxySource.cs:47-59](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.cs#L47-L59), [LocalProxySource.UdpTunnel.cs:32-37](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.UdpTunnel.cs#L32-L37). Chỉ chạm nếu UDP Direct đi qua relay thay vì passthrough.
- [ ] `LocalProxySource.ConnectTunnel`: `IsSupportIpv6=false` mà host chỉ có AAAA thì `ConnectAsync(mảng rỗng)` ném `ArgumentException` khó hiểu: [LocalProxySource.ConnectTunnel.cs:67-77](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.ConnectTunnel.cs#L67-L77).
- [ ] Chỉ 22/210 `await` có `ConfigureAwait(false)`; `async void _MainLoopListen`: [ProxyServer.cs:166](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L166). Nhánh Test ở [RedirectEngine.cs:626-627](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L626-L627) gọi từ UI nên continuation nội bộ bị post về Dispatcher.

### D6. Phía server TqkLibrary.Proxy (chỉ `ProxyDivert.Cli --self-host-port`)

- [ ] **Cao** `TransferAsync` quay vòng 100% CPU khi nguồn đóng sớm (không kiểm `byte_read == 0`): [StreamExtensions.cs:23-29](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamExtensions.cs#L23-L29). Ném `EndOfStreamException`.
- [ ] **Cao** `HttpProxyServer` dispose stream client sau mỗi request nên keep-alive chết từ request thứ 2: [HttpProxyServer.cs:134](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L134), [HttpProxyServer.cs:165](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L165), [BaseProxyServerHandler.cs:73-76](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Handlers/BaseProxyServerHandler.cs#L73-L76). Chỉ `using` khi handler trả instance khác.
- [ ] **Cao** Request line mất query string (`AbsolutePath` thay vì `PathAndQuery`): [HttpProxyServer.cs:149](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L149).
- [ ] **Cao** Response chunked / close-delimited không bao giờ được forward body: [HttpProxyServer.cs:168-184](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L168-L184), [HttpUtilities.cs:7-20](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/HttpUtilities.cs#L7-L20).
- [ ] **Vừa** `Socks5_Request.Uri` tạo URI sai cho đích IPv6 (thiếu ngoặc): [Socks5_Request.cs:53-67](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/Socks5_Request.cs#L53-L67). Mọi CONNECT ATYP=0x04 ném tại [Socks5ProxyServer.cs:113-114](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L113-L114).
- [ ] **Vừa** Server SOCKS4/5 không trả reply lỗi khi connect upstream thất bại: [Socks5ProxyServer.cs:158-165](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L158-L165), [Socks4ProxyServer.cs:145-150](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L145-L150).
- [ ] **Vừa** BIND thiếu reply thứ hai cả hai phía: [Socks5ProxyServer.cs:399-419](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L399-L419), [Socks5ProxySource.BindTunnel.cs:52-59](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BindTunnel.cs#L52-L59), tương tự Socks4.
- [ ] **Vừa** SOCKS4 server resolve DNS đồng bộ và cục bộ ([rò rỉ DNS](Glossary-vi.md#L113)): [Socks4ProxyServer.cs:114](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L114), [Socks4ProxyServer.cs:143](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L143).
- [ ] **Vừa** `PreReadAsync` chỉ `ReadAsync` một lần, read ngắn bị coi là hết stream: [PreReadStream.cs:17-39](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/PreReadStream.cs#L17-L39), [PreReadStream.cs:63-64](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/PreReadStream.cs#L63-L64).
- [ ] **Vừa** Body response ghi thẳng `_clientStream` bỏ qua stream handler đã bọc (throttling/đếm byte mất body): [HttpProxyServer.cs:178-184](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L178-L184).
- [ ] **Thấp** `HeaderRequestParse`/`HeaderResponseParse` `Split(':')` vứt header có dấu `:` trong value: [HeaderRequestParse.cs:66-72](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/HeaderRequestParse.cs#L66-L72). `Split(':', 2)`.
- [ ] **Thấp** `client_isKeepAlive` gán sau `continue` của nhánh 407: [HttpProxyServer.cs:84-92](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L84-L92).
- [ ] **Thấp** Input SOCKS5 dị dạng ném exception thay vì reply (NMETHODS=0, domain length=0, VER không kiểm): [Socks5ProxyServer.cs:79-80](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L79-L80), [Socks5_DSTADDR.cs:87-93](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/Socks5_DSTADDR.cs#L87-L93).

### D7. Các project ProxyDivert không dùng (SshCli, SshNet, GlobalUnicast, Reverse)

- [ ] **Cao** SshNet không kiểm host key khi allow-list rỗng → MITM im lặng: [SshNetProxySource.cs:121-133](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshNet/SshNetProxySource.cs#L121-L133). Luôn đăng ký handler, TOFU có lưu hoặc bắt buộc fingerprint.
- [ ] **Cao** SshCli: stdio của tiến trình ControlMaster redirect nhưng không đọc → ssh deadlock khi pipe đầy: [SshProcessRunner.cs:55-68](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshCli/SshProcessRunner.cs#L55-L68), [SshProcessRunner.cs:172-189](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshCli/SshProcessRunner.cs#L172-L189). `redirect: false` cho master.
- [ ] **Cao** GlobalUnicast: offset xoá địa chỉ sai (8 thay vì 6) và chọn NIC không lọc khi xoá → IPv6 tạm không bao giờ gỡ: [SlaccHelper.cs:96](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L96), [SlaccHelper.cs:136-137](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L136-L137), [SlaccHelper.cs:159](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L159).
- [ ] **Cao** GlobalUnicast: `SemaphoreSlim` static bị dispose theo instance, finalizer `Wait()` trên nó: [GlobalUnicastProxySource.cs:14](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/GlobalUnicastProxySource.cs#L14), [GlobalUnicastProxySource.cs:33-49](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/GlobalUnicastProxySource.cs#L33-L49), [GlobalUnicastProxySource.cs:89-106](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/GlobalUnicastProxySource.cs#L89-L106).
- [ ] **Cao** Reverse: `StreamCopy.PumpAsync` `WhenAny` rồi dispose cả hai, cắt cụt response: [StreamCopy.cs:5-19](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/StreamCopy.cs#L5-L19). Cùng lỗi gốc với D1.
- [ ] **Cao** Reverse: data channel không xác thực, `AuthSignature` khai báo nhưng client không set, server không kiểm; transport mặc định plaintext: [ReverseServer.cs:103-125](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseServer.cs#L103-L125), [ReverseClient.cs:219-236](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/ReverseClient.cs#L219-L236), [Payloads.cs:78-84](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Protocol/Payloads.cs#L78-L84).
- [ ] **Vừa** SshCli password ghi plaintext ra `%TEMP%` suốt đời runner, helper `.cmd` trong thư mục user ghi được: [SshProcessRunner.cs:253-278](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshCli/SshProcessRunner.cs#L253-L278).
- [ ] **Vừa** SshCli mỗi kết nối cõng cứng 500ms: [OpenSshConnectSource.cs:36-50](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshCli/OpenSshConnectSource.cs#L36-L50).
- [ ] **Vừa** SshNet mỗi kết nối mở cổng loopback ai cũng nối được, có race giành kết nối: [SshNetConnectSource.cs:36-79](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshNet/SshNetConnectSource.cs#L36-L79). Dùng `ChannelDirectTcpip`.
- [ ] **Vừa** Reverse: session đóng nhưng control channel không bao giờ `DisposeAsync`: [ReverseServer.cs:127-132](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseServer.cs#L127-L132), [ReverseClientSession.cs:136-154](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseClientSession.cs#L136-L154).
- [ ] **Vừa** Reverse: accept không giới hạn, byte định danh kênh không timeout (slowloris): [RawTcpReverseTransportServer.cs:73-111](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.RawTcp/RawTcpReverseTransportServer.cs#L73-L111), [HttpListenerWebSocketTransportServer.cs:61-86](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.WebSocket/HttpListenerWebSocketTransportServer.cs#L61-L86).
- [ ] **Vừa** Reverse/AspNetCore dispose `WebSocket` do framework sở hữu: [AspNetCoreReverseTransportServer.cs:24-35](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.AspNetCore/AspNetCoreReverseTransportServer.cs#L24-L35). `ownsSocket: false`.
- [ ] **Vừa** Reverse: accept loop HttpListener `catch { break; }` chết vì một exception lẻ: [HttpListenerWebSocketTransportServer.cs:58-59](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.WebSocket/HttpListenerWebSocketTransportServer.cs#L58-L59).
- [ ] **Vừa** Reverse: [backoff](Glossary-vi.md#L109) không reset sau khi kết nối thành công: [ReverseClient.cs:30-52](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/ReverseClient.cs#L30-L52). Reset khi nhận `HelloAck`.
- [ ] **Vừa** Reverse: `DialBindAsync` rò listener trên `Any:<random>` khi gửi `BindReady` thất bại: [ReverseClient.cs:179-203](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/ReverseClient.cs#L179-L203).
- [ ] **Vừa** GlobalUnicast: interface ID sinh bằng `new Random()` (netstandard2.0 seed theo tick), `Task.Delay(3000)` trong semaphore bỏ qua cancellation: [SlaccHelper.cs:61](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L61), [SlaccHelper.cs:122](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L122).
- [ ] **Vừa** GlobalUnicast chọn prefix sai: bỏ DHCPv6/Manual, nhận nhầm ULA, không loại tentative/deprecated, không refresh khi interface đổi: [SlaccHelper.cs:30-53](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L30-L53).
- [ ] **Thấp** Reverse: `BindReady.Address` luôn `0.0.0.0`, `BindAccepted.PeerAddress` không điền; `ToEndPoint()` parse địa chỉ rác giết cả session; `_ = SendAsync` nuốt lỗi async; `HandleFrameAsync` không giới hạn đồng thời; `WebSocketStream` coi message rỗng là EOF; `RawTcp.StopAsync` không dispose `_cts`. Vị trí: [ReverseClient.cs:113](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/ReverseClient.cs#L113), [ReverseClient.cs:184-196](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/ReverseClient.cs#L184-L196), [ReverseClientSession.cs:167-172](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseClientSession.cs#L167-L172), [ReverseSourceBase.cs:30](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseSourceBase.cs#L30), [WebSocketStream.cs:44-48](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.WebSocket/WebSocketStream.cs#L44-L48), [RawTcpReverseTransportServer.cs:43-53](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.RawTcp/RawTcpReverseTransportServer.cs#L43-L53).
- [ ] **Thấp** GlobalUnicast: `Console.WriteLine` trong thư viện dù có `ILoggerFactory`; `ConnectTunnel` bỏ qua token, socket v6-only nên host chỉ có A record fail: [SlaccHelper.cs:121](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/SlaccHelper.cs#L121), [GlobalUnicastProxySource.ConnectTunnel.cs:25-34](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.GlobalUnicast/GlobalUnicastProxySource.ConnectTunnel.cs#L25-L34).

---

## E. Thiết kế (OOP, tái sử dụng)

Đánh giá chung: kiến trúc tầng đúng như `Plan-vi.md` (Core không tham chiếu WPF; kiểu của VpnClient chỉ xuất hiện trong `Vpn/Client`; 5 project WinDivert không có chu trình; [ba tầng cấu hình](Glossary-vi.md#L300)). OOP mới dừng ở "interface + factory": phần đa hình cho các trục mở rộng đã dự định (loại điều kiện, giao thức VPN, mode phát hiện, v4/v6, tcp/udp) vẫn là `switch` rải nhiều file; sở hữu vòng đời không nằm trong kiểu; bốn [God class](Glossary-vi.md#L420) ôm phần khó nhất và cũng là phần không có test. Mức: `Must` = đã gây lỗi thật hoặc chặn việc mở rộng đã ghi trong plan; `Should` = giảm trùng lặp / cho phép test; `Nice` = sạch hơn, không đổi hành vi.

### E1. Vòng đời và sở hữu

#### E1.1 Một instance `IProxySource`, ba chủ — Must (app)

- [ ] **Vị trí**: [OutboundSourceFactory.cs:76-98](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L76-L98) (cache + dispose theo chữ ký), [KeptVpnTunnel.cs:116](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L116) và [KeptVpnTunnel.cs:204](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L204) (`Invalidate` khi thất bại và khi dispose), [RedirectEngine.cs:288-300](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L288-L300) (invalidate lần ba), [KeptVpnTunnel.cs:137-147](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L137-L147) (keeper ép kiểu `WireGuardProxySource` để tìm lại tunnel).
- **Vấn đề**: factory cache, keeper và engine đều có quyền giết instance của nhau qua `Invalidate(Guid)`; `Sync` và `ApplyOutbounds` cùng chạy trên cùng danh sách mỗi Save và cùng tính `OutboundSignature.Of`. Đây là bẫy đã ghi ở [vòng đời VPN tách khỏi engine](Glossary-vi.md#L344).
- **Cách sửa**: factory chỉ **tạo**: `IOutboundInstance Create(Outbound, ctx)` trả `{ IProxySource Source; IManagedProxySource? Tunnel; OutboundCapabilities Caps; string Signature }`. `OutboundRegistry` (singleton, thay cache trong factory) là chủ duy nhất: `Reconcile(outbounds)` trả `(added, removed)`; keeper chỉ đăng ký/huỷ tunnel từ `added/removed`, không `Invalidate`; engine lấy `Source` từ registry. Thứ tự: thêm `IOutboundInstance` + registry bọc factory → keeper đổi sang sự kiện registry → bỏ 3 chỗ `Invalidate`. Rủi ro vừa: giữ quy tắc "đổi tên không rớt tunnel" trong `Signature`.

#### E1.2 Chuỗi dispose đồng bộ ép `Wait` ở 5 tầng — Must (lib Proxy + app)

- [ ] **Vị trí**: `IProxySource` không kế thừa `IDisposable` ([IProxySource.cs:3](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IProxySource.cs#L3)); các source có state tự thêm `IDisposable` ([WireGuardProxySource.cs:15](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L15)) hoặc `IAsyncDisposable` ([ReverseClientSession.cs:13](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseClientSession.cs#L13)); app dọn bằng `(source as IDisposable)?.Dispose()` ([OutboundSourceFactory.cs:297-300](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L297-L300)); chuỗi `Wait`: [VpnClientProxySource.cs:260](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L260), [KeptVpnTunnel.cs:198](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L198), [UdpProxyForwarder.cs:179-186](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L179-L186), [ProcessInventory.cs:430](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L430), [AppServices.cs:275](../src/ProxyDivert.Wpf/Services/AppServices.cs#L275).
- **Vì sao**: xem [IAsyncDisposable và chuỗi dispose đồng bộ](Glossary-vi.md#L436); một source chỉ có `IAsyncDisposable` sẽ rò mà không có cảnh báo compile; đây là lớp lỗi "Save đơ" có tiền sử (C7).
- **Cách sửa**: lib thêm `IAsyncDisposable` vào `IProxySource` (hoặc helper `ProxySourceDisposal.DisposeAsync` ưu tiên async, fallback sync) và mọi source lib implement. App: `OutboundRegistry.ReconcileAsync`, `KeptVpnTunnel : IAsyncDisposable` (await `_loop`), `VpnConnectionKeeper.SyncAsync`, `RedirectEngine.StopAsync`, `AppServices.DisposeAsync` gọi từ `OnExit`; chấp nhận `Wait` ở đúng một chỗ ngoài cùng. Thứ tự: lib → factory/registry → keeper → engine → UI.

#### E1.3 Sở hữu chỉ nằm trong doc, không trong kiểu (WinDivert) — Should

- [ ] **Vị trí**: `IPacketPumpFactory.Create` "pump takes ownership of handle" ([IPacketPumpFactory.cs:10-12](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Interfaces/IPacketPumpFactory.cs#L10-L12)); `ProcessRedirectorFactory` luôn tạo `IDnsCacheLookup` dù `EnableDnsLookup=false` ([ProcessRedirectorFactory.cs:55](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirectorFactory.cs#L55)); 7 dòng `?.Dispose()` tay ([ProcessRedirector.cs:366-372](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L366-L372)); `Func<IReverseDnsTable>` tự viết ([RedirectServiceCollectionExtensions.cs:43-44](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/DependencyInjection/RedirectServiceCollectionExtensions.cs#L43-L44)); `AddTrackedProcessId` ném trước `Start` ([ProcessRedirector.cs:104-108](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L104-L108)); `SocketTracker.Dispose` chờ tuần tự 1s/handle ([SocketTracker.cs:568-572](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L568-L572)).
- **Cách sửa**: `IProcessRedirectorFactory.Create` mở một `IServiceScope` cho session, mọi thứ per-session đăng ký Scoped, `Dispose` = dispose scope (DI đảm bảo thứ tự ngược). Đồng thời giải quyết B11. Queue pid trước `Start` rồi flush; `Dispose` shutdown tất cả rồi `WaitAll` một lần. Rủi ro thấp.

### E2. Trách nhiệm và kích thước (God class)

#### E2.1 `RedirectEngine` 641 dòng, 7 mối quan tâm, không test được — Must (app)

- [ ] **Vị trí**: [RedirectEngine.cs:43](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L43). Vòng đời + lock [:117-279](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L117-L279); reconcile outbound + học IPv6 [:288-300](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L288-L300); cầu tracker↔redirector + rebuild resolver [:304-341](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L304-L341); routing + tunnel TCP + đo thời gian [:348-492](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L348-L492); UDP tầng gói + forward [:525-604](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L525-L604); test outbound tĩnh [:612-638](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L612-L638). Năm field nullable cùng bật/tắt theo run [:63-67](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L63-L67); tracker/forwarder/hostNames được `new` trong `Start` [:157-162](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L157-L162).
- **Vì sao**: `HandleTcpAsync`/`HandleUdpDatagram` chạy trên luồng relay đọc field nullable không qua lock trong khi `Stop` gán null dưới lock; mỗi thành phần theo run thêm một `?.`. Không có unit test nào chạm engine.
- **Cách sửa**: `EngineRun` (sealed, bất biến sau ctor) giữ redirector/tracker/hostNames/udpForwarder/cts/resolver; `RedirectEngine` chỉ còn `EngineRun? _run` + `Start/Stop/ApplyConfig`, handler capture `run` một lần ở đầu hàm. `TcpConnectionRouter` (HandleTcp + TunnelAsync + NoteIpv6Failure) và `UdpFlowRouter` (ShouldRedirectUdpFlow + HandleUdpDatagram) nhận resolver qua `IResolverSource`. `OutboundTester` thay static `TestOutboundAsync` (cùng lúc sửa A1). Nhận `IProcessRuleTrackerFactory`/`IUdpForwarderFactory` qua ctor để test. Thứ tự: rút `EngineRun` (chỉ dời field) → tách hai router → factory. Rủi ro: giữ nguyên thứ tự `Connections.Open/Close` và `_liveConnections.Register`.

#### E2.2 `SocketTracker` 578 dòng, 8 mối quan tâm, file churn nhất lib — Must (WinDivert)

- [ ] **Vị trí**: [SocketTracker.cs](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs): handle per-pid [:53-75](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L53-L75), [:237-300](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L237-L300); mode machine-wide + `_pidDecisions` [:177-231](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L177-L231); bảng TCP/UDP [:37-38](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L37-L38); decode socket event [:487-549](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L487-L549); reconcile kernel + throttle [:398-434](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L398-L434); cleanup loop [:463-485](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L463-L485); pre-populate [:353-366](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L353-L366). 7 điểm rẽ `IsMachineWide`; `FakeTracker` trong test phải implement 13 member chỉ để test NAT ([NatRedirectMiddlewareEscapedFlowTests.cs:148-190](../src/ProxyDivert.Core.Tests/NatRedirectMiddlewareEscapedFlowTests.cs#L148-L190)).
- **Vì sao**: mọi lỗi race SYN/attach/trễ quét/RemoveProcess trong memory đều đổ về file này; reconcile (nguồn của các bug race) gọi `IpHlpApi` static nên không test được.
- **Cách sửa**: `FlowTable` (thuần, 4 dictionary + `Record/Close/Reap(now)` + 4 event, test 100% không driver); `SocketEventPump` (decode `WinDivertAddress` → `SocketEvent` record); `IKernelConnectionTable` bọc `IpHlpApi` + `KernelReconciler` (throttle, dùng `IClock`); `IProcessScope` [strategy](Glossary-vi.md#L424) với `PerProcessHandleScope`/`MachineWideScope` xoá mọi `if (IsMachineWide)`; `SocketTracker` còn façade ~120 dòng; `ISocketTracker` thu về `IFlowLookup` + `IProcessScope`. Thứ tự: `FlowTable` (viết test trước khi dời, giữ ngữ nghĩa `TryAdd else overwrite` và `wasLive`) → kernel table → scope. Giải quyết luôn B4 (khoá `(pid, startTime)` trong `MachineWideScope`).

#### E2.3 `NatRedirectMiddleware`: NAT + policy cổng + escaped flow + reset + dedupe log — Should (WinDivert)

- [ ] **Vị trí**: ctor 11 tham số [NatRedirectMiddleware.cs:68-79](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L68-L79); whitelist cổng [:177-181](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L177-L181); escaped flow [:312-344](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L312-L344); dựng RST [:349-378](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L349-L378).
- **Cách sửa**: dùng chính [pipeline](Glossary-vi.md#L69): `TrackedFlowGate` (tracked? reconcile? dstPort filter → gắn `FlowOwner` vào context) → `EscapedFlowMiddleware` (pass/drop/reset) → `NatRewriteMiddleware` (chỉ Upsert + rewrite). Cần `PacketContext.Items` hoặc thuộc tính typed `FlowOwner?`. Test escaped flow sẵn có chạy lại được.

#### E2.4 `ProcessRedirector`: orchestrator kiêm builder pipeline kiêm chính sách IPv6 kiêm tuning driver — Should (WinDivert)

- [ ] **Vị trí**: build pipeline v4/v6 lặp gần y hệt [ProcessRedirector.cs:231-264](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L231-L264) vs [:268-288](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L268-L288); quyết định IPv6 fallback [:155-163](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L155-L163), [:179-188](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L179-L188); tham số queue driver [:333-353](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L333-L353).
- **Cách sửa**: `NetworkPipelineFactory.Create(AddressFamily, RelayEndpoints, features)` gọi 2 lần; `Ipv6Policy.Resolve(requested, osSupportsIpv6, relayHasV6)` thuần, test được; `NetworkHandleOptions` truyền vào handle factory. Ba hàm `Start*Pump` biến mất.

#### E2.5 `OutboundSourceFactory` gộp 5 việc — Should (app)

- [ ] **Vị trí**: cache + signature [OutboundSourceFactory.cs:51-98](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L51-L98); switch IPv6 theo kiểu bê tông [:113-126](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L113-L126); switch theo kind [:154-200](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L154-L200); VPN hai engine [:209-258](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L209-L258); URL parse + DNS đồng bộ [:263-295](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L263-L295). Lớp `sealed`, không interface, `KeptVpnTunnel` nhận thẳng ([KeptVpnTunnel.cs:50](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L50)) nên `VpnConnectionKeeperTests` chỉ test được nhánh thất bại.
- **Cách sửa**: `IOutboundSourceBuilder { OutboundKind Kind; string Signature(Outbound, BuildContext); IOutboundInstance Build(Outbound, BuildContext); }` với `Direct/Http/Socks4/Socks5/VpnSourceBuilder`; `OutboundSourceRegistry` (DI `IEnumerable<IOutboundSourceBuilder>`) chọn builder; cache tách riêng (E1.1). Thứ tự: rút builder sau façade (test `OutboundSourceFactoryTests` vẫn xanh) → tách cache → đổi DI ở [ProxyDivertServiceCollectionExtensions.cs:68](../src/ProxyDivert.Core/DependencyInjection/ProxyDivertServiceCollectionExtensions.cs#L68). Thêm kind SshNet (lib đã có) khi đó chỉ là thêm một builder.

#### E2.6 `AppServices` chứa chính sách miền, CLI phải chép lại — Should (app)

- [ ] **Vị trí**: [AppServices.cs:29](../src/ProxyDivert.Wpf/Services/AppServices.cs#L29): container, load config, chính sách file log [:92-105](../src/ProxyDivert.Wpf/Services/AppServices.cs#L92-L105), hàng đợi [:259-268](../src/ProxyDivert.Wpf/Services/AppServices.cs#L259-L268), bật engine = `ConnectRoutedVpns` + save + `Start` [:204-213](../src/ProxyDivert.Wpf/Services/AppServices.cs#L204-L213), map detection mode [:133-134](../src/ProxyDivert.Wpf/Services/AppServices.cs#L133-L134). CLI chép [Program.cs:189-192](../src/ProxyDivert.Cli/Program.cs#L189-L192), [Program.cs:210](../src/ProxyDivert.Cli/Program.cs#L210); WPF `Vpn.Sync` lúc khởi động còn CLI không.
- **Cách sửa**: `ProxyDivert.Core/Hosting/ProxyDivertSession : IAsyncDisposable` gom `ProcessInventory` start + chọn event source theo config, `VpnConnectionKeeper.Sync`, `StartAsync/StopAsync/ApplyAsync(config)` với hàng đợi tuần tự bên trong. `AppServices` còn `ConfigStore`, `Config` đang sửa, log path. `Program.cs` dùng cùng session. Cùng lúc chuyển `LaunchSuspended` ([ProcessesViewModel.cs:229-285](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L229-L285)) thành `SuspendedLaunchService` trong Core (CLI hiện làm cùng việc bằng `AttachProcessId`).

### E3. Đa hình thay cho `switch`

Bảng phán quyết (giữ nguyên: `UdpMode`, `HostMatcherType`, `ProcessMatcherType`, `ArgumentMatcherType`, `ProcessEventSourceKind`, `DnsMode`, `Ipv6Support` vì mỗi enum chỉ switch một chỗ, đầy đủ nhánh, có `default: throw`).

#### E3.1 Loại điều kiện tiến trình: seam đã hứa "một class một dòng" nhưng cần sửa 6 chỗ — Must (app)

- [ ] **Vị trí**: lời hứa [ProcessCondition.cs:9-10](../src/ProxyDivert.Core/Routing/Models/Conditions/ProcessCondition.cs#L9-L10); thực tế switch ở [ProcessRuleMatcher.cs:83-90](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L83-L90), [ProcessRuleMatcher.cs:43-55](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L43-L55), [ConditionNodeViewModel.cs:54-61](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L54-L61), [ConditionLeafViewModel.cs:82-94](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionLeafViewModel.cs#L82-L94), [ConditionTextBuilder.cs:69-76](../src/ProxyDivert.Wpf/Helpers/ConditionTextBuilder.cs#L69-L76), enum [ConditionSubject.cs:6-8](../src/ProxyDivert.Core/Routing/Enums/ConditionSubject.cs#L6-L8).
- **Cách sửa**: các class đã có `Clone()` ảo nên model đã mang hành vi; thêm `abstract ConditionResult Evaluate(in ConditionSubject)` vào `ProcessCondition` (giữ [logic ba trạng thái](Glossary-vi.md#L195) trong `ConditionGroup`), `RegexBudgetMs` chung nằm ở `ConditionSubject`. Cho UI: `abstract LeafCondition.Descriptor` (`Subject`, `MatcherValues`, `DefaultMatcher`, `Create`) thay enum `ConditionSubject` + `Matchers`; `ConditionLeafViewModel`/`ConditionTextBuilder` chỉ đọc descriptor. Thứ tự: `Evaluate` trước (29 fact `ProcessRuleMatcherTests` chuyển gần nguyên), descriptor UI sau.

#### E3.2 `VpnProtocol`: thêm một giao thức = sửa 6 chỗ trong 3 file — Should (app)

- [ ] **Vị trí**: [VpnClientProxySource.cs:181-221](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L181-L221) (switch dial, `Required()` kiểm lúc dial thay vì lúc đọc), [VpnProfileReader.cs:83-94](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L83-L94), [VpnProfileReader.cs:262-279](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L262-L279), [VpnProfileReader.cs:310-332](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L310-L332), [VpnProfile.cs:11-14](../src/ProxyDivert.Core/Vpn/Models/VpnProfile.cs#L11-L14) (union-bag tự nhận), [OutboundSignature.cs:43-49](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L43-L49).
- **Cách sửa**: `abstract class VpnProfile { Protocol; CarriesUdp; Signature(); Task<VpnTunnel> DialAsync(VpnTunnelOptions, ct) }` với `WireGuardFileProfile { Engine = WireProxy|InProcess }`, `OpenVpnFileProfile`, `SstpProfile`, `L2tpIpsecProfile`, `Ikev2Profile`, `SoftEtherProfile`; ctor bắt buộc tham số nên validate lúc `Read`. `VpnProfileReader.Read` chỉ chọn subclass; `RunsOnWireProxy(protocol, url)` giữ static (không đụng đĩa). `VpnClientProxySource` nhận `Func<CancellationToken, Task<VpnTunnel>>` = seam test. Cùng lúc giải quyết C6.

#### E3.3 `ProcessDetectionMode`: một mode = 3 nút vặn ở 3 nơi — Should (app)

- [ ] **Vị trí**: [RedirectEngine.cs:150-152](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L150-L152), [RedirectEngine.cs:167](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L167), [AppServices.cs:133-134](../src/ProxyDivert.Wpf/Services/AppServices.cs#L133-L134), [Program.cs:190-191](../src/ProxyDivert.Cli/Program.cs#L190-L191), `SettingsViewModel.ApplyEventSource`.
- **Cách sửa**: `IProcessDetectionStrategy` với `ProcessEventDetection`/`SocketSniffDetection`: `ConfigureInventory(inventory, config)`, `ConfigureRedirect(RedirectOptions, tracker)`, `AttachFromEvents`. Xem [hai cách phát hiện](Glossary-vi.md#L357).

#### E3.4 v4/v6: cặp field nhân đôi và cờ `isIpv6` xuyên 20 file — Should (WinDivert)

- [ ] **Vị trí**: `_listener/_listenerV6`, `Port/PortV6` [TcpRelayServer.cs:28-37](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L28-L37), [TcpRelayServer.cs:59-84](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L59-L84), [UdpRelayServer.cs:21-31](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L21-L31); `RelayPorts` 4 int + `For(bool, bool)` [RelayPorts.cs:14-29](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RelayPorts.cs#L14-L29); 4 thuộc tính port trên interface [IProcessRedirector.cs:19-24](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L19-L24); `NatKey.IsIpv6` bool [NatKey.cs:13](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/NatKey.cs#L13); `IsIpv6 ? Ipv6.X : Ipv4.X` [ParsedPacket.cs:25-26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs#L25-L26), [ParsedPacket.cs:71-87](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs#L71-L87); 15 lần `isIpv6` trong NatRedirectMiddleware.
- **Vì sao**: lỗi "NAT v4/v6 ghi đè nhau" đã từng xảy ra vì family là cờ rời phải nhớ truyền ([Ipv6RoutingTests.cs:28-30](../src/ProxyDivert.Core.Tests/Ipv6RoutingTests.cs#L28-L30)); xem [dual-stack](Glossary-vi.md#L85).
- **Cách sửa**: [value object](Glossary-vi.md#L432) `RelayEndpoint(IpProtocol, AddressFamily, IPAddress Loopback, int Port)` + `RelayEndpointSet.TryGet(protocol, family)`; `TcpRelayServer` = composite của N `TcpRelayListener(loopbackAddress, nat, handler)` (family suy từ địa chỉ), `UdpRelayServer` tương tự; `NatKey(IpProtocol, AddressFamily, ushort)`; `IIpHeaderView { Family; Source; Destination; HeaderLength; Protocol }` do hai view implement, `ParsedPacket.Ip` trả về nó. Thứ tự: NatKey/INatTable (cơ học, có test) → relay listener → header view → vòng lặp trong ProcessRedirector. Hai family hai socket loopback riêng là bắt buộc, composite giữ đúng điều đó.

#### E3.5 tcp/udp là `byte` magic 6/17 và `isTcp ? :` — Should (WinDivert)

- [ ] **Vị trí**: `FlowKey.Protocol` là `byte` [FlowKey.cs:8](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/Models/FlowKey.cs#L8) dù có `IpProtocol` enum; literal tại [SocketTracker.cs:509-542](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L509-L542), [NatRedirectMiddleware.cs:317](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L317), [Ipv6BlockMiddleware.cs:85-86](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Ipv6BlockMiddleware.cs#L85-L86), [UdpRelayServer.cs:75](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L75); ba cặp `isTcp ? tracker.IsTrackedTcp : IsTrackedUdp` [NatRedirectMiddleware.cs:139-142](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L139-L142), [:162-164](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L162-L164), [:194-196](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L194-L196).
- **Cách sửa**: `FlowKey`/`NatKey`/`NatEntry`/`INatTable` dùng `IpProtocol`; `ITransportFlowPolicy { IsTracked; TryGetOwner; MayRedirectNow }` với `TcpFlowPolicy`/`UdpFlowPolicy` chọn một lần theo `p.Protocol`; "UDP hỏi `ShouldRedirectUdp` còn TCP không" thuộc `UdpFlowPolicy`.

### E4. Abstraction và hướng phụ thuộc

#### E4.1 `IKeptTunnel` đặt nhầm nhà; giám sát hai tầng chồng nhau — Should (lib Proxy + app)

- [ ] **Vị trí**: [KeptVpnTunnel.cs:133-147](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L133-L147) pattern-match `WireGuardProxySource` để bọc `WireProxyKeptTunnel` ([WireProxyKeptTunnel.cs:8-13](../src/ProxyDivert.Core/Vpn/WireProxyKeptTunnel.cs#L8-L13)), trong khi lib đã cố ý phơi `IsRunning/StartAsync/Exited/Socks5Endpoint` ([WireGuardProxySource.cs:46-83](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L46-L83)); `OpenSshProxySource` (ControlMaster) và `SshNetProxySource` (`EnsureConnectedAsync`) cũng là tunnel giữ được nhưng keeper lọc `Kind != Vpn` ([VpnConnectionKeeper.cs:85](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L85)). Bốn bộ [backoff](Glossary-vi.md#L109) chồng nhau: keeper, driver VpnClient (vô hạn), `WireGuardOptions.AutoRestart`, Reverse.
- **Cách sửa**: lib core thêm `IManagedProxySource : IProxySource { bool IsRunning; string Endpoint; Task StartAsync(ct); Task<string> WaitUntilDownAsync(ct); }` với hợp đồng "source tự reconnect, host chỉ retry lần dial đầu + hiển thị trạng thái" (giữ ranh giới đúng của `IKeptTunnel` hiện tại, xem [driver tự kết nối lại](Glossary-vi.md#L125)); `WireGuardProxySource`, `OpenSshProxySource`, `SshNetProxySource` implement. App xoá `WireProxyKeptTunnel`, `Resolve()` = `source as IManagedProxySource ?? throw`, keeper lọc theo interface thay vì `Kind`. Đi cùng C2 (thoát khỏi retry vô hạn).

#### E4.2 Cờ `IsSupportUdp/Ipv6/Bind` thay cho interface; hai nguồn sự thật cho khả năng — Should (lib Proxy + app)

- [ ] **Vị trí**: [IProxySource.cs:5-33](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IProxySource.cs#L5-L33); server chỉ đọc 2/3 cờ; `IsSupportIpv6` chỉ có tác dụng ở `LocalProxySource` (D5); app switch trên kiểu bê tông [OutboundSourceFactory.cs:113-126](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L113-L126) nên SshNet/OpenSsh/Reverse rơi vào no-op; model đoán lại khả năng từ `Kind`+URL ở [Outbound.cs:69-79](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L69-L79) và gọi ngược `VpnProfileReader` (model routing phụ thuộc Vpn); Reverse khai `IsSupportUdp` từ Hello nhưng `ReverseUdpAssociateSource` ném `NotImplementedException` ([ReverseUdpAssociateSource.cs:31-38](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseUdpAssociateSource.cs#L31-L38)).
- **Cách sửa**: lib tách `IBindCapable`, `IUdpCapable` (server kiểm `is`), `IAddressFamilyPolicy { bool AllowIpv6 {get;set;} }` chỉ ở source thật sự lọc (Local, VpnClient); cờ cũ `[Obsolete]` một phiên bản. App: `OutboundCapabilities` do builder trả về cùng instance (E1.1), resolver hỏi `registry.Find(id)?.Caps ?? outbound.SupportsUdp`. Đồng thời gọn 6 phép `Kind ==` trong engine thành `RouteDecision.IsDirect/IsBlocked/UsesTunnel`.

#### E4.3 `RedirectOptions` là túi delegate thay cho interface host — Should (WinDivert)

- [ ] **Vị trí**: 4 delegate [RedirectOptions.cs:33](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L33), [:36](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L36), [:78](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L78), [:121](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L121); consumer implement cả 4 bằng method private trên một class ([RedirectEngine.cs:144-152](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L144-L152)); Demo phải tạo `redirectorRef` gán sau `Create` để handler gọi ngược ([ProxyRedirectorRunner.cs:50-70](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/ProxyRedirectorRunner.cs#L50-L70)); mode chọn bằng null-ness của `Func` ([ISocketTrackerFactory.cs:25-26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/Interfaces/ISocketTrackerFactory.cs#L25-L26)); sentinel `ProcessId = 0` ([RedirectOptions.cs:23](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L23)) làm `NatEntry.ProcessId = 0` bị stamp lặng lẽ khi tracker miss ([NatRedirectMiddleware.cs:194-196](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L194-L196)).
- **Cách sửa**: `IRedirectHost { HandleTcpAsync(session, conn, ct); UdpVerdict HandleUdp(dg); bool ShouldRedirectUdp(target); }` + `IProcessSelector` + `ProcessScopeKind` enum tường minh; `RedirectOptions` chỉ còn dữ liệu; `InitialProcessIds` thay sentinel 0, bỏ fallback root pid. Giữ overload extension nhận lambda cho Demo.

#### E4.4 Packet model: `WinDivertAddress` rò lên middleware; chữ ký async mời làm sai; `MarkModified` tách rời — Should (WinDivert)

- [ ] **Vị trí**: `PacketContext.Address` là struct native public ([PacketContext.cs:37](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Models/PacketContext.cs#L37)), `IPacketInjector.Inject(..., in WinDivertAddress)`, magic `IfIdx = 1` ([NatRedirectMiddleware.cs:236-238](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L236-L238)), dựng address inbound trùng ở [NatRedirectMiddleware.cs:362-368](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L362-L368) và [DnsOverHttpsMiddleware.cs:172-182](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs#L172-L182). `IPacketMiddleware.InvokeAsync` trả `Task` nhưng pump `GetAwaiter().GetResult()` ([PacketPump.cs:73](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L73)) và cấm await I/O: consumer NuGet viết `await http.GetAsync()` sẽ treo mạng cả máy. `SetSource/SetDestination` ghi buffer nhưng phải nhớ gọi `MarkModified` riêng ([ParsedPacket.cs:59-69](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs#L59-L69)); setter trên header view struct không có tác dụng ([TcpHeader.cs:14-18](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/TcpHeader.cs#L14-L18)).
- **Cách sửa**: value type `PacketRoute { Direction; Loopback; InterfaceRef; Family }` với factory `LoopbackOutbound(family)`/`InboundOn(iface)`; `PacketPump` là nơi duy nhất map sang `WinDivertAddress`; struct native `internal`. `void Invoke(PacketContext, PacketDelegate next)` đồng bộ (6 middleware built-in đều đã đồng bộ nên đổi cơ học), `IPacketInjector` là đường duy nhất cho việc chậm. Bỏ setter trên view; ghi qua `ctx.Rewrite(...)` tự đặt `Modified`.

#### E4.5 Redirect phụ thuộc cứng SecureDns/Inspection; không thêm được feature thứ 6 — Should (WinDivert)

- [ ] **Vị trí**: `IReverseDnsTable` trên interface session ([IProcessRedirector.cs:31](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L31)); `PeekableStream` (kiểu Inspection) trên model ([RedirectedTcpConnection.cs:33](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L33)); `ProcessRedirector` new thẳng hai middleware DNS ([ProcessRedirector.cs:253-256](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L253-L256)); `CapturesUdp` hard-code nhu cầu từng feature ([ProcessRedirector.cs:228-229](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L228-L229)) nên middleware user cần UDP khi `Protocols = Tcp` không bao giờ thấy gói UDP; `ConfigureNetworkPipeline` chỉ chèn sau NAT; doc NAT "must run first" mâu thuẫn thứ tự đăng ký thực ([ProcessRedirector.cs:243-259](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L243-L259)). `DnsCacheLookup` (spawn `ipconfig`) nằm ở core `Flow/` với interface khác tên `IReverseDnsTable` dù cùng mục đích IP→tên.
- **Cách sửa**: `IRedirectFeature { NeedsUdpCapture; Families; Contribute(builder, PipelineStage, ctx) }` với `DnsSniffFeature`, `DohFeature`, `BlockTargetUdpFeature`; `ProcessRedirector` lặp `options.Features` và ghép filter driver từ `NeedsUdpCapture`; `PipelineStage` enum (PreNat/Nat/PostNat) làm ràng buộc thứ tự thành kiểu; `ReverseDns` rời khỏi interface session; `IPeekableStream` ở core. `IAddressNameLookup` chung cho `ReverseDnsTable` và `DnsCacheLookup` (dời sang SecureDns), `CompositeNameLookup`. Hai middleware chỉ phụ thuộc `ISocketTracker` (`Ipv6BlockMiddleware`, `BlockTargetUdpMiddleware`) dời về core làm "per-process firewall". Mẫu để nhân bản: `IHostNameParser` strategy list của Inspection.

#### E4.6 Exception là API nhưng không nhất quán — Should (lib Proxy)

- [ ] **Vị trí**: server chỉ bắt `InitConnectSourceFailedException` ([HttpProxyServer.cs:111](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L111)); Socks5 auth fail ném `Exception` trần ([Socks5ProxySource.BaseTunnel.cs:117](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs#L117)); Http ném không message, mất status ([HttpProxySource.ConnectTunnel.cs:44-47](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L44-L47)); `WireGuardException : Exception` ngoài cây `ProxySourceException`; `InitConnectSourceFailedException` không có ctor `(message, inner)` nên app bọc mất inner ([VpnClientConnectSource.cs:54-58](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L54-L58)). Hệ quả: `catch (Exception) when (isIpv6 ...)` ([RedirectEngine.cs:460-469](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L460-L469)) đánh dấu outbound "IPv4-only" kể cả khi lỗi là 407/auth/proxy chết.
- **Cách sửa**: cây `ProxySourceException` → `UpstreamUnreachableException`, `ProxyHandshakeException { Status }`, `ProxyAuthenticationException`, `DestinationUnreachableException`; mọi lớp có `(message, inner)`; `WireGuardException` vào cây. App: `NoteIpv6Failure` chỉ với `DestinationUnreachableException`/timeout; C8 (dial timeout thành "cancelled") sửa cùng.

#### E4.7 Nhóm Nice (abstraction)

- [ ] `IProcessRedirector` phơi `Nat`, `TcpConnectionOpened/Closed`, `TrackedProcessIds` không consumer nào dùng ([IProcessRedirector.cs:16](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L16), [:48-49](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L48-L49)). Tách `IRedirectDiagnostics`.
- [ ] Marker interface rỗng và dán nhãn sai trong Proxy: `IHttpProxy/ISocks4Proxy/ISocks5Proxy/ISsh/IVpn/IAuthentication`; `LocalProxySource : IHttpProxy`, `WireGuardProxySource : ISocks5Proxy` lộ chi tiết cài đặt ([LocalProxySource.cs:8](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.cs#L8), [WireGuardProxySource.cs:15](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L15)). Xoá hoặc cho một member thật.
- [ ] `LiveTcpConnectionRegistry.CloseWhereRouteChanged` nhận kiểu bê tông `RoutingPolicyResolver` ([LiveTcpConnectionRegistry.cs:52](../src/ProxyDivert.Core/Engine/LiveTcpConnectionRegistry.cs#L52)); chỉ cần `Func<RouteTarget, RouteDecision>`.
- [ ] `RedirectEngine.TestOutboundAsync` static tự dựng factory, bỏ qua DI ([RedirectEngine.cs:612-638](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L612-L638)). Thành `OutboundTester(registry)`.
- [ ] Reverse: `event Func<…,Task>` chỉ await delegate cuối ([RawTcpReverseTransportServer.cs:116-118](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.RawTcp/RawTcpReverseTransportServer.cs#L116-L118)); AspNetCore phải poll `socket.State` mỗi giây vì `IControlChannel` thiếu `Task Closed` ([AspNetCoreReverseTransportServer.cs:42-50](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.AspNetCore/AspNetCoreReverseTransportServer.cs#L42-L50)).
- [ ] `Singleton` limit toàn process trong Proxy ([Singleton.cs:3-7](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Singleton.cs#L3-L7)); `LogCritical` cho mọi tunnel fail và header ở `Information` ([ProxyServer.cs:248-251](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L248-L251), [HttpProxyServer.cs:63](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L63)) là noise với hàng trăm tunnel/phút.
- [ ] Naming trong WinDivert: 4 "tracker" (`SocketTracker`/`ProcessRuleTracker`/`ConnectionTracker`/`IProcessTreeMonitor`), `AddTrackedProcessId` vs `AddProcess`, file `Ipv4Header.cs` chứa `Ipv4HeaderView`, consumer phải `using` 7 namespace con `.Interfaces/.Enums/.Models` ([RedirectEngine.cs:23-29](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L23-L29)). NuGet nên phẳng namespace.

### E5. Đóng gói và mô hình cấu hình

#### E5.1 Toàn vẹn tham chiếu của cấu hình vá ở 4 nơi — Must (app)

- [ ] **Vị trí**: xoá policy sửa `ProcessRule.PolicyIds` tại [RulesViewModel.cs:157-168](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L157-L168); xoá outbound trỏ về Block tại [OutboundsViewModel.cs:174-175](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L174-L175); built-in bị sửa bậy vá lúc load tại [ConfigStore.cs:94-108](../src/ProxyDivert.Core/Configuration/ConfigStore.cs#L94-L108); policy đã xoá nhưng filter còn trỏ thì resolver bỏ qua tại [RoutingPolicyResolver.cs:60-66](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L60-L66). Grid sửa thẳng `Kind/Url/Username` qua TwoWay ([OutboundsView.xaml:149-166](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml#L149-L166)), chặn built-in ở code-behind ([OutboundsView.xaml.cs:17-20](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml.cs#L17-L20)). CLI dựng `AppConfig` tay ([Program.cs:143-150](../src/ProxyDivert.Cli/Program.cs#L143-L150)) không qua phép vá nào.
- **Cách sửa**: `AppConfig` thành [aggregate](Glossary-vi.md#L428): `RemovePolicy(id)`, `RemoveOutbound(id)`, `Normalize()` (gọi từ `Load` và `Clone`); `Outbound.IsBuiltIn` bảo vệ setter hoặc `BuiltInOutbound : Outbound` sealed. VM chỉ gọi aggregate. Cùng lúc giải quyết A10 bằng một `ObservableCollection` chung. Thứ tự: thêm method → chuyển 2 VM → `RestoreBuiltInOutbounds` thành `Normalize` → đổi target 8 fact `ConfigStoreTests`.

#### E5.2 `Outbound.Url` là chuỗi 4 nghĩa, parse ở 4 nơi — Should (app)

- [ ] **Vị trí**: [Outbound.cs:22-27](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L22-L27); parse tại [OutboundSourceFactory.cs:263-275](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L263-L275), [VpnProfileReader.cs:39-62](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L39-L62), [OutboundSignature.cs:54-70](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L54-L70) (`Expand` viết lại), [Program.cs:291-301](../src/ProxyDivert.Cli/Program.cs#L291-L301) (`KindFromUrl`); `ProxyUriParser` vẫn `internal` trong Demo dù plan ghi "nâng lên thư viện" ([ProxyUriParser.cs:10](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Parsing/ProxyUriParser.cs#L10)).
- **Cách sửa**: [value object](Glossary-vi.md#L432) `OutboundAddress` (`ProxyEndpoint | VpnFile | VpnEndpoint | VpnIniFile`) với `TryParse(kind, raw, out address, out error)`; model giữ string để JSON, thêm `[JsonIgnore] Address` lazy; VM gọi `TryParse` lúc rời ô; factory/signature/CLI/Demo dùng chung.

#### E5.3 `RoutingRule.Pattern` parse lại mỗi kết nối; resolver trộn phần tĩnh với phần động — Should (app)

- [ ] **Vị trí**: [HostMatcher.cs:102-108](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L102-L108) (`Split('/')` + `IPAddress.TryParse` mỗi lần), [HostMatcher.cs:138-150](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L138-L150), `_regexCache` tĩnh không xoá [HostMatcher.cs:25](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L25); `Resolve` chạy `Where().OrderBy()` mỗi kết nối [RoutingPolicyResolver.cs:92](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L92); resolver dựng lại mỗi attach/detach pid ([RedirectEngine.cs:309](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L309), [RedirectEngine.cs:323](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L323)) vì ôm cả `policiesByProcessId`: 60 tab Chrome = 60 lần copy dictionary.
- **Cách sửa**: `CompiledRuleSet` (bất biến, dựng một lần mỗi config: rule đã parse thành `IHostPredicate`, sắp theo Order) + `ProcessPolicyMap` (ConcurrentDictionary do tracker sở hữu); `RoutingPolicyResolver(ruleSet, policyMap)` không cần rebuild khi pid đổi (giải quyết A4 và A8). Pattern sai phát hiện lúc compile, báo ở tab Rules. Giữ thứ tự "policy đầu tiên quyết UDP". 11 test `HostMatcherTests` giữ qua adapter.

#### E5.4 Static giấu I/O và thời gian — Should

- [ ] DNS đồng bộ trong parser và factory trên luồng relay: [WireGuardConfigParser.cs:216](../src/ProxyDivert.Core/Vpn/WireGuardConfigParser.cs#L216), [OutboundSourceFactory.cs:291](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L291); lib đã có ctor `Socks5ProxySource(Uri)` resolve lười ([Socks5ProxySource.cs:27-49](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.cs#L27-L49)). Parser trả `DnsEndPoint`, app dùng ctor `Uri`.
- [ ] `OutboundSignature.Of` stat file mỗi outbound mỗi `Sync`/`ApplyOutbounds` ([OutboundSignature.cs:54-70](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L54-L70)).
- [ ] `Environment.TickCount`/`UtcNow` rải rác trong WinDivert ([SocketTracker.cs:402-413](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L402-L413), [ReverseDnsTable.cs:37](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/ReverseDnsTable.cs#L37), [ConnectionStatistics.cs:14-31](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/ConnectionStatistics.cs#L14-L31)) và cùng mẫu `TickCount + Volatile + CompareExchange` ở app ([ProcessInventory.cs:195-198](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L195-L198)): grace 30s / retention 30 phút không test được. `IClock` (net8 `TimeProvider`) + `RateGate(IClock, TimeSpan)` dùng chung.
- [ ] `VpnConnectionKeeper.ConnectRoutedVpns` gán `outbound.KeepConnected = true` lên object của caller ([VpnConnectionKeeper.cs:163](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L163)) trong khi WPF truyền `Config` sống; đã trả danh sách id nên bỏ dòng gán, `AppServices` flip trước khi clone.
- [ ] `AppConfig` trộn tuỳ chọn UI (`Language`, `Theme`) với cấu hình engine (file đang sửa, chỉ ghi nhận): hướng `UiPreferences` do Wpf định nghĩa, Core lưu mờ.

### E6. Tái dùng và trùng lặp

#### E6.1 WireGuard `.conf`: ba parser, hai model trùng tên — Should (lib Proxy + app)

- [ ] **Vị trí**: app [WireGuardConfigParser.cs:103-126](../src/ProxyDivert.Core/Vpn/WireGuardConfigParser.cs#L103-L126) cho model `WireProxyCli.WireGuardConfig` (lib chỉ có writer `internal` [WireGuardConfigWriter.cs:12](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardConfigWriter.cs#L12)); VpnClient `WireGuardConfFile.cs:26-90` sang model khác (byte[] key); [VpnProfileReader.cs:281-297](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L281-L297) `ReadIni` lần ba. Hành vi lệch: app strip comment inline, VpnClient chỉ bỏ dòng đầu `#`; app nhiều peer, VpnClient chỉ giữ peer cuối. `WireGuardOptions` là túi 2 hình dạng loại trừ nhau kiểm lúc chạy ([WireGuardOptions.cs:16-45](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardOptions.cs#L16-L45)).
- **Cách sửa**: chuyển `WireGuardConfigParser` (+ 9 test) vào WireProxyCli thành `WireGuardConfigReader` public, model round-trip `Parse/ToString`; `WireGuardOptions.FromInline(config)` / `FromFile(path, endpoint)`. Hai repo khác nhau nên chấp nhận 2 reader (Proxy + VpnClient), không 3; `VpnProfileReader.ReadIni` dùng cùng tokenizer.

#### E6.2 `InTunnelResolver` là DNS client thứ hai trong hệ — Should (app → lib VpnClient)

- [ ] **Vị trí**: [InTunnelResolver.cs:141-233](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L141-L233) tự dựng `BuildQuery/ReadAnswer/SkipName`; `TqkLibrary.WinDivert.SecureDns.DnsMessageParser.ParseAddressAnswers` đã có (CNAME, chống vòng pointer) ([DnsMessageParser.cs:25](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsMessageParser.cs#L25)); VpnClient không có client in-tunnel; `InTunnelResolver(VpnTunnel)` không test được không có VPN thật.
- **Cách sửa**: chuyển sang `TqkLibrary.VpnClient.Sockets` thành `VpnDnsClient(TcpIpStack, IPAddress? dns)` implement `IHostResolver` (chỉ phụ thuộc `VpnUdpClient`), test in-memory ở repo VpnClient; một `UdpConnection` sống lâu (giải quyết C3). `DnsQueryBuilder` đặt cạnh parser trong SecureDns cho DoH dùng chung.

#### E6.3 `UdpProxyForwarder` và glue TCP→proxy có hai bản (Demo và app) — Should (WinDivert + app)

- [ ] **Vị trí**: [Demo/UdpProxyForwarder.cs:19-171](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/UdpProxyForwarder.cs#L19-L171) vs [UdpProxyForwarder.cs:21-188](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L21-L188) (cùng `PortTunnel`, cùng `GetAwaiter().GetResult()` khi inject); [ProxyRedirectorRunner.cs:186-198](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/ProxyRedirectorRunner.cs#L186-L198) vs [RedirectEngine.cs:446-491](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L446-L491); `RelayDirectAsync` I/O nằm trong model ([RedirectedTcpConnection.cs:71-108](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L71-L108)).
- **Cách sửa**: package thứ 6 `TqkLibrary.WinDivert.Proxy` (Redirect + TqkLibrary.Proxy) chứa `SocksUdpAssociateForwarder` (bản app, có idle timeout theo A2) và `ProxyTcpConnectionHandler`; `DirectTcpConnectionHandler : IRedirectHost` mặc định thay `RelayDirectAsync`. Demo và app cùng dùng.

#### E6.4 ProcessControl bị app viết lại tốt hơn — Should (WinDivert + app)

- [ ] **Vị trí**: app chỉ còn dùng `ISuspendedProcessLauncher`; lib [ProcessTreeMonitor.cs:59-103](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L59-L103) poll `Process.GetProcesses()` + `OpenProcess` từng pid mỗi 500ms, [ProcessFinder.cs:65-69](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessFinder.cs#L65-L69) dùng `MainModule`; app [NativeProcessLister.cs:47-81](../src/ProxyDivert.Core/Processes/NativeProcessLister.cs#L47-L81) một syscall có parent cho mọi tiến trình; P/Invoke trùng [ProcessNativeMethods.cs:75-92](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/Native/ProcessNativeMethods.cs#L75-L92) vs [ProcessNativeMethods.cs:114-124](../src/ProxyDivert.Core/Processes/Native/ProcessNativeMethods.cs#L114-L124); `ManagedProcessLister` ≈ `ProcessFinder.ListAll`.
- **Cách sửa**: hạ `IProcessLister`/`NativeProcessLister`/`ManagedProcessLister`/`IProcessDetailsReader`/`NativeProcessDetailsReader`/`ProcessSnapshot`/`DebugPrivilege` (+ test) xuống `TqkLibrary.WinDivert.ProcessControl`; xoá `ProcessFinder`/`ProcessTreeMonitor` hoặc viết lại trên `IProcessLister`. `ProcessInventory` + event source ETW/WMI (kéo `TraceEvent`) ở lại app hoặc `.ProcessControl.Etw`. Xem [tách bảng process](Glossary-vi.md#L254).

#### E6.5 Tunnel client trong Proxy lặp 7 lần; base class rỗng mang finalizer — Should (lib Proxy)

- [ ] **Vị trí**: [BaseProxySourceTunnel.cs:5-35](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/BaseProxySourceTunnel.cs#L5-L35); mỗi source tự khai `TcpClient` + `Stream` + Dispose: [Socks5ProxySource.BaseTunnel.cs:18-31](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs#L18-L31), [HttpProxySource.ConnectTunnel.cs:18-42](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L18-L42), [LocalProxySource.ConnectTunnel.cs:11-23](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.ConnectTunnel.cs#L11-L23); guard `GetStreamAsync` "Mustbe run X first" giống hệt ở 7 chỗ; fallback "BND.ADDR = 0.0.0.0" chép 3 lần. ProxyDivert tạo 1 tunnel/1 kết nối TCP nên mỗi kết nối trả phí finalization queue vô ích.
- **Cách sửa**: `UpstreamTcpTunnel<TSource>` chứa `_tcpClient/_stream`, `ConnectUpstreamAsync`, `GetStreamAsync`, `Dispose(bool)` đúng cờ, **bỏ finalizer**; `Socks5ClientHandshake`/`Socks4ClientHandshake` static nhận `Stream` (test codec in-memory được). Bump minor vì `BaseTunnel` là public API.

#### E6.6 WireProxyCli và SshCli copy-paste 5 tiện ích process — Should (lib Proxy)

- [ ] **Vị trí**: locate binary [WireProxyProcessRunner.cs:267-304](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireProxyProcessRunner.cs#L267-L304) ↔ [SshProcessRunner.cs:213-251](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.SshCli/SshProcessRunner.cs#L213-L251); `BuildPsi`/`BuildArgumentString`; chmod file tạm; stderr buffer; idiom Kill+Dispose ×5. Hình dạng khác nhau hợp lý (daemon + socket vs child-per-connection) nên cái dùng chung là helper, không phải base source.
- **Cách sửa**: `TqkLibrary.Proxy.Process` (hoặc internal + `InternalsVisibleTo`): `ExecutableLocator`, `ChildProcess` (Start, stderr tail giới hạn N dòng cuối, `Exited` không bắn khi Dispose, `KillAndDispose`, Job Object theo D3), `TempSecretFile` (tạo đúng quyền, xoá), `TcpListenerProbe`. `IChildProcess` là seam để test `WireGuardProxySource` không cần `wireproxy.exe`. Giải quyết luôn D4 (stderr, ACL).

#### E6.7 Relay hai chiều có ba bản với ngữ nghĩa half-close khác nhau — Should (lib Proxy + app)

- [ ] **Vị trí**: [StreamTransferHelper.cs:53-96](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamTransferHelper.cs#L53-L96) (`WhenAll`, không đóng chiều kia), [StreamCopy.cs:5-19](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Client/StreamCopy.cs#L5-L19) (`WhenAny`, dispose cả hai), [ConnectSourceExtensions.cs:15-32](../src/ProxyDivert.Core/Outbounds/Extensions/ConnectSourceExtensions.cs#L15-L32) (app bọc thêm với chú thích "lib để boilerplate cho caller").
- **Cách sửa**: một `StreamRelay` trong core Proxy với policy half-close rõ (D1), extension `IConnectSource.RelayAsync(Stream, ...)` để app bỏ file này; Reverse dùng chung.

#### E6.8 Nhóm nhỏ

- [ ] Bỏ ngoặc IPv6 `Trim('[', ']')` ở 7 chỗ và `host:port` qua `LastIndexOf(':')` ở 2 chỗ: [LocalProxySource.ConnectTunnel.cs:101](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.ConnectTunnel.cs#L101), [Socks5_DSTADDR.cs:32](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/Socks5_DSTADDR.cs#L32), [OutboundSourceFactory.cs:288](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L288), [VpnProfileReader.cs:114](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L114), [InTunnelResolver.cs:62](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L62), [WireGuardConfigParser.cs:199-220](../src/ProxyDivert.Core/Vpn/WireGuardConfigParser.cs#L199-L220). `EndPointParsing` trong core Proxy.
- [ ] Credential từ `Uri.UserInfo` ba cách, độ đúng khác nhau (Http `Split(':')` không unescape): [HttpProxySource.cs:25-32](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.cs#L25-L32), [Socks5ProxySource.cs:36-46](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.cs#L36-L46), [Socks4ProxySource.cs:32-37](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks4ProxySource.cs#L32-L37). `ProxyUri.ParseCredential(Uri)`.
- [ ] Ghi header IPv4 tay ở 3 nơi: [TcpResetPacketBuilder.cs:70-80](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/TcpResetPacketBuilder.cs#L70-L80), [DnsOverHttpsMiddleware.cs:136-146](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs#L136-L146), VpnClient `Ipv4.Build`. Trong WinDivert: `Ipv4PacketWriter`/`Ipv6PacketWriter` ở core `Packet/`; không tham chiếu VpnClient.
- [ ] `IsElevated` chép 2 bản ([Program.cs:303-314](../src/ProxyDivert.Cli/Program.cs#L303-L314), `MainViewModel.CheckElevated`). `Core/Hosting/Elevation`.
- [ ] Wpf: `Reload()` chép list config → ObservableCollection và "thêm vào cả hai danh sách" ở 3 VM; mẹo "gỡ ra cắm lại để grid vẽ lại" ở [ProcessesViewModel.cs:169-174](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L169-L174) và [RulesViewModel.cs:111-116](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L111-L116). Xem E8.1.

### E7. Mô hình luồng như một thiết kế

Ba phong cách cùng tồn tại và đều được ghi ở đầu class (điểm tốt): lock (`RedirectEngine._stateLock`, `VpnConnectionKeeper._lock`, `ProcessInventory._reconcileLock`), hàng đợi (`AppServices.Enqueue`, `AppLoggerProvider`), lock-free (`ConcurrentDictionary`, swap `_resolver`).

#### E7.1 Công việc nặng chạy trên luồng ETW/WMI qua chuỗi event đồng bộ — Should (app)

- [ ] **Vị trí**: [ProcessInventory.cs:413-424](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L413-L424) `Raise` trên luồng pump → [ProcessRuleTracker.cs:361-367](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L361-L367) → [RedirectEngine.cs:304-316](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L304-L316) mở handle driver + `RebuildResolver`; `IProcessEventSource` yêu cầu "handlers must be short" ([IProcessEventSource.cs:38-39](../src/ProxyDivert.Core/Processes/Interfaces/IProcessEventSource.cs#L38-L39)) nhưng chuỗi này vi phạm; `ApplyConfig` giữ `_stateLock` khi `_tracker.ApplyRules` raise event.
- **Cách sửa**: tracker đẩy `(pid, attach/detach)` vào `Channel<T>`, một consumer làm việc với redirector ([single-writer](Glossary-vi.md#L444)); E5.3 xoá `RebuildResolver`; `EngineRun` bất biến (E2.1) cho phép thu hẹp lock xuống chỉ phép swap `_run`. Giải quyết A3, A4, A8.

#### E7.2 Sự kiện `ISocketTracker` bắn từ 3 ngữ cảnh thread mà interface không nói — Should (WinDivert)

- [ ] **Vị trí**: từ socket pump [SocketTracker.cs:517](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L517), từ thread gọi `RemoveProcess` (UI) [SocketTracker.cs:334](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L334), từ NETWORK pump qua reconcile [SocketTracker.cs:374](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L374); `ISocketTracker` không có ghi chú thread trong khi `ITcpRelayServer` có; `ProcessRedirector` forward event ra host nên host chậm sẽ chặn pump.
- **Cách sửa**: sau E2.2, mọi mutation đi qua một `Channel<FlowMutation>` do thread sở hữu `FlowTable`; event bắn từ đúng một thread; doc một dòng. Lợi ích chính là hợp đồng rõ, không phải hiệu năng.

### E8. Tầng MVVM

#### E8.1 Model POCO bind thẳng vào DataGrid, VM giữ hai bản danh sách — Should (app)

- [ ] **Vị trí**: model không có `INotifyPropertyChanged` → mẹo re-insert, `OnPropertyChanged(nameof(IsRunning))` thủ công; grid ghi trực tiếp vào model mà engine sẽ clone ([OutboundsView.xaml:149-166](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml#L149-L166)) nên không có chỗ validate (E5.2) hay chặn built-in (E5.1); 3 `MultiBinding` tra `VpnTunnels` theo `Id` ([VpnConverters.cs:21-59](../src/ProxyDivert.Wpf/Converters/VpnConverters.cs#L21-L59)) là dấu hiệu thiếu row VM.
- **Cách sửa**: `OutboundRowViewModel`/`RuleRowViewModel`/`FilterRowViewModel` (ObservableObject bọc model, `Commit()` ghi về aggregate, `Validate()`); `Rows.Sync(config.X, m => new RowVm(m))` helper chung; `OutboundRowViewModel.Tunnel` observable thay converter. [BindingProxy](Glossary-vi.md#L141) là giới hạn WPF thật, giữ.

#### E8.2 Nhóm Nice (MVVM)

- [ ] 6 VM nhận `AppServices` bê tông, 41 chỗ gọi thẳng `Engine/Config/Vpn`; `CanToggleVpn` chạy `OutboundUsage.RoutedOutboundIds` mỗi lần WPF hỏi CanExecute ([OutboundsViewModel.cs:99-111](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L99-L111)). Sau E2.6: VM nhận `IProxyDivertSession` + `IConfigEditor`, cache theo config version.
- [ ] VM tạo Window ([ProcessesViewModel.cs:195-207](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L195-L207)). `IDialogService.EditFilter(vm) : bool` để test `AddRule/EditRule`.

### E9. Khả năng test

#### E9.1 Test của WinDivert nằm trong repo app; submodule 0 test; thiếu seam bảng kernel — Must (WinDivert)

- [ ] **Vị trí**: `libs/TqkLibrary.WinDivert/tests` không tồn tại; 7 file test lib nằm ở [src/ProxyDivert.Core.Tests](../src/ProxyDivert.Core.Tests) (`NatRedirectMiddlewareEscapedFlowTests`, `EscapedFlowBlocklistTests`, `Ipv6RoutingTests`, `DnsMessageParserTests`, `PeekableStreamTests`, `TcpResetPacketBuilderTests`, `TlsClientHelloParserTests`); `IpHlpApi` static gọi thẳng từ [SocketTracker.cs:358-359](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L358-L359), [SocketTracker.cs:421-422](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L421-L422).
- **Vì sao**: không thể publish/validate NuGet độc lập; reconcile/pre-populate (nguồn mọi bug race trong memory) không test được dù `IWinDivertHandleFactory` đã fake được.
- **Cách sửa**: tạo `libs/TqkLibrary.WinDivert/tests/TqkLibrary.WinDivert.Tests` (+ `.Redirect.Tests`), dời 7 file, `RawTcpPackets.cs` thành helper chung; `IKernelConnectionTable` + `IClock` inject qua `ISocketTrackerFactory`. Test được ngay không cần driver: `PacketPump` (fake handle), relay servers (loopback thật + `NatTable` seed), `DnsOverHttpsMiddleware` (fake resolver + recording injector), `ProcessRedirector` (5 factory fake).

#### E9.2 Seam còn thiếu ở app và Proxy — Should

- [ ] `KeptVpnTunnel` gọi thẳng `_factory.GetOrCreate` ([KeptVpnTunnel.cs:139](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L139)) → sau E2.5 đăng ký `FakeManagedSource` để test đường Connected/Reconnecting (hiện `VpnConnectionKeeperTests` chỉ test được nhánh fail bằng file không tồn tại, [VpnConnectionKeeperTests.cs:28-45](../src/ProxyDivert.Core.Tests/VpnConnectionKeeperTests.cs#L28-L45)).
- [ ] `VpnClientProxySource.DialAsync` gọi static `VpnDialer`, `VpnTunnel` ctor internal → E3.2 `VpnProfile.DialAsync` virtual; VpnClient nên phơi `IVpnTunnel` hoặc factory test.
- [ ] `RedirectEngine` `new` tracker/forwarder trong `Start` → E2.1 factory.
- [ ] Proxy lib: `TestProxy` = 6 test đánh internet thật, 0 test in-memory cho codec Socks4/5/HTTP; `TestProxy.Local` cần SSH/WG thật với mật khẩu hard-code ([OpenSshProxySourceTest.cs:14-20](../libs/TqkLibrary.Proxy/src/TestProxy.Local/OpenSshProxySourceTest.cs#L14-L20), nên lấy từ env như `TESTPROXY_HTTPBIN`); Reverse 0 test dù chạy trên `Stream` (một duplex pipe + `StreamControlChannel` là đủ). E6.5 handshake static trên `Stream` mở đường test codec.

#### E9.3 Test đặt sai project — Nice

- [ ] `DataGridColumnBindingTests` (639 dòng), `WpfResourceSmokeTests`, `PolicyRenameTests`, `TextBoxPlaceholderTests`, `StartupRegistrationTests` test Wpf nhưng nằm trong `ProxyDivert.Core.Tests` (csproj tham chiếu cả Wpf). Tách `ProxyDivert.Wpf.Tests`.

### E10. Thiết kế tốt, giữ làm mẫu

- `ProcessInventory` với 3 interface nhỏ + `FakeProcessMachine`; `ProcessSnapshot` record bất biến với `with`; `TrackedProcess.WithRule/WithPolicies`. Hình mẫu cho E2.1 và E1.1.
- Resolver bất biến thay nguyên khối + `ConfigStore.Clone` ba tầng; reconcile theo chữ ký thay `InvalidateAll` (ý tưởng đúng, chỉ cần một chủ).
- Hợp đồng `IKeptTunnel` (`IsRunning` tức thời vs `WaitUntilDownAsync` hết cứu) đúng, chỉ sai chỗ đặt; `VpnStatus` bất biến; `LiveTcpConnection` chỉ sở hữu CTS của nó (sở hữu ghi trong kiểu).
- `ConditionResult` bốn trạng thái + `ProcessCondition` đa hình + `JsonPolymorphic`: nền đúng, chỉ thiếu `Evaluate` ảo.
- `AppLoggerProvider` hàng đợi bị chặn + đếm dòng rớt; `AppServices.Enqueue`; `CoalescedDispatcherAction`; `LocalizationScope.Version` + `LocalizedBinding`.
- WinDivert: seam driver `IWinDivertHandle`/factory với hợp đồng thread rõ; `PacketContext` chỉ mang gói tin (đã cắt phụ thuộc ngược NatTable); `PacketPipelineBuilder` kiểu ASP.NET; `NatTable.Upsert` trả `isNew`; `IpHlpApi` predicate-over-pids; Inspection với `IHostNameParser` strategy list + `PeekableStream`; fail-safe IPv6; `TryAdd` trong DI.
- Proxy: `PreReadStream` + `DefaultProxyServerFactory` sniff byte đầu cho 3 giao thức một listener; hook hai tầng mang `tunnelId`; `IUdpAssociateSource` là API datagram; Reverse `IControlChannel` + `FrameCodec` một wire format; `WireProxyProcessRunner.Exited` không bắn khi Dispose; `DefaultPersistentKeepalive` có lý do ghi rõ.
- Chú thích "vì sao" ở đầu class và tại mỗi quyết định khó là tài sản; refactor phải giữ nguyên các đoạn này.

---

## F. Thứ tự sửa đề xuất

Nguyên tắc: bug Cao trước vì sửa nhanh và độc lập; refactor thiết kế đi theo cụm phụ thuộc, mỗi bước bọc code cũ trước rồi mới xoá, test xanh mới sang bước sau. Bug trong submodule commit trong submodule trước, rồi cập nhật app.

**Đợt 1 — bug Cao, mỗi cái một commit (1-2 ngày)**
1. A1 Test outbound rò wireproxy; D3 Job Object (cùng file runner); D2 fast-path + kill khi timeout; D4 nhóm WireProxyCli.
2. A2 `_tunnels` idle timeout + cap; A6 `Lazy<T>` trong `GetOrAdd`.
3. B2 pump không chết im lặng + buffer 65576 + sự kiện `PumpStopped`; B1 SafeHandle P/Invoke.
4. B3 `NatTable.Remove` từ `TcpConnectClosed`; D1 `StreamTransferHelper` đóng chiều kia.
5. A10 đồng bộ tab (tạm `ReloadAll` khi đổi tab); A11 await `SaveAndApply` trong LaunchSuspended.
6. C2 keeper coi `IsRunning == false` N chu kỳ là down; C5 bỏ `_cts.Dispose`; C1 thêm `Abort()` vào VpnClient + timer FIN-WAIT-2.

**Đợt 2 — bug Vừa theo cụm file**: A3/A4/A5 (Core), B4 + D7 PID reuse (`MachineWideScope` sẽ thay nên chỉ sửa tối thiểu: khoá theo start time), B6-B13, C3/C4/C6, A12-A15, D6 nếu còn dùng self-host.

**Đợt 3 — refactor thiết kế, theo cụm phụ thuộc**

| Thứ tự | Việc | Vì sao đứng đây |
|---|---|---|
| 1 | E1.2 `IAsyncDisposable` xuyên chuỗi (lib Proxy → app) | Mọi refactor vòng đời sau đó đều dựa vào nó; sửa lớp lỗi "Save đơ" |
| 2 | E1.1 + E2.5 `IOutboundInstance` + `OutboundRegistry` + builder per kind | Một chủ cho `IProxySource`; seam để test keeper |
| 3 | E4.1 `IManagedProxySource` vào lib, xoá `WireProxyKeptTunnel`; E4.2 capability interface | Keeper và engine hết đoán kiểu bê tông; cùng lúc chốt C2 |
| 4 | E2.1 `EngineRun` + hai router + `OutboundTester` | Chạm cùng vùng với 2-3, làm rời sẽ conflict |
| 5 | E5.3 `CompiledRuleSet` + `ProcessPolicyMap`; E7.1 `Channel` attach/detach | Xoá `RebuildResolver`, hết A4/A8, thu hẹp lock |
| 6 | E5.1 aggregate `AppConfig`; E8.1 row ViewModel; E5.2 `OutboundAddress` | Bịt gốc A10 và toàn vẹn cấu hình; validate tại ô nhập |
| 7 | E3.1 `ProcessCondition.Evaluate` + descriptor | Seam đã hứa, độc lập với 1-6 |
| 8 | E2.6 `ProxyDivertSession` dùng chung WPF/CLI; E3.3 detection strategy | Sau khi engine gọn mới đáng gom host |
| 9 | E3.2 `VpnProfile` subclass; E6.1 WireGuard reader về WireProxyCli; E6.2 DNS client về VpnClient | Cụm VPN, mỗi việc một commit trong submodule tương ứng |
| 10 | E4.6 cây exception Proxy; E6.5 `UpstreamTcpTunnel`; E6.6 `ChildProcess`; E6.7 `StreamRelay` | Lib Proxy nội bộ, không đụng app ngoài đổi catch |

**Đợt 4 — WinDivert (submodule riêng, có thể song song với đợt 3 từ bước 5)**

| Thứ tự | Việc |
|---|---|
| 1 | E9.1 tạo `tests/` trong submodule, dời 7 file test, thêm `IKernelConnectionTable` + `IClock` |
| 2 | E2.2 tách `SocketTracker` (`FlowTable` có test trước) → `IProcessScope` (bịt B4 hẳn) |
| 3 | E3.4 + E3.5 family-agnostic và `IpProtocol` (NatKey → relay listener → header view) |
| 4 | E2.3 tách NAT thành 3 middleware; E2.4 `NetworkPipelineFactory` + `Ipv6Policy` |
| 5 | E4.3 `IRedirectHost`; E4.5 `IRedirectFeature` + `PipelineStage`; E4.4 `PacketRoute` + middleware đồng bộ |
| 6 | E6.3 package `TqkLibrary.WinDivert.Proxy`; E6.4 hạ `IProcessLister` xuống ProcessControl; E7.2 single-writer `FlowTable` |

Việc Nice (E4.7, E8.2, E9.3, E6.8) xen kẽ khi chạm file liên quan, không lên lịch riêng.

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

- [x] **Vị trí**: [RedirectEngine.cs:332-337](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L332-L337), gọi từ [RedirectEngine.cs:304-330](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L304-L330) và [RedirectEngine.cs:184-194](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L184-L194)
- **Vấn đề**: thread sự kiện tiến trình đọc `_config` cũ, UI thread `ApplyConfig` gán `_config` mới và dựng resolver mới, rồi thread sự kiện gán đè `_resolver` bằng bản dựng từ config cũ. Field cũng không `Volatile`.
- **Vì sao**: user vừa Save, log báo "configuration applied", nhưng mọi kết nối mới đi theo policy cũ cho tới lần attach/detach kế tiếp; rất khó tái hiện.
- **Cách sửa**: chụp `_config` vào biến local dưới `_stateLock` rồi `Volatile.Write(ref _resolver, ...)`, hoặc bọc thân `RebuildResolver` trong lock.
- **Đã sửa**: commit "fix(engine): rebuild the route resolver under the state lock". Bọc cả thân trong lock (kể cả `BuildPolicyMap` — để ngoài thì hai attach song song, cái xong sau ghi đè bằng map thiếu tiến trình của cái kia), field đổi thành `volatile`. Không deadlock: `ProcessRuleTracker` chỉ giữ `_rulesLock` trong một dòng gán/đọc, không raise event khi đang giữ.

### A5. `ConfigStore.Save` không lock, tên temp cố định — Vừa

- [x] **Vị trí**: [ConfigStore.cs:64-81](../src/ProxyDivert.Core/Configuration/ConfigStore.cs#L64-L81), [AppServices.cs:150](../src/ProxyDivert.Wpf/Services/AppServices.cs#L150), [AppServices.cs:259-268](../src/ProxyDivert.Wpf/Services/AppServices.cs#L259-L268)
- **Vấn đề**: `AppServices.Save()` chạy trên UI thread, còn `SaveAndApply`/`StartEngineAsync`/`SetVpnConnectedAsync` gọi `ConfigStore.Save` trên worker queue. Hai bên chồng nhau trên cùng `FilePath + ".tmp"`.
- **Vì sao**: hoặc `IOException` ném thẳng lên UI thread, hoặc A ghi xong `.tmp`, B truncate `.tmp`, A `File.Replace` bằng file cụt: mất toàn bộ cấu hình, mà project không có backup ngoài `.bak` lúc load hỏng.
- **Cách sửa**: `lock` tĩnh quanh thân `Save`; tên temp duy nhất (`FilePath + "." + Guid.NewGuid():N + ".tmp"`); dài hạn thì đưa mọi Save qua worker queue (xem E).
- **Đã sửa**: commit "fix(config): serialise saves and give each one its own temp file". Làm cả `lock` tĩnh lẫn tên temp duy nhất, thêm xoá temp khi ghi/swap ném. Test `Saving_from_two_threads_at_once_leaves_one_readable_config_and_no_litter` (40 luồng) — đã xác nhận fail 3/3 lần khi bỏ fix. Phần "mọi Save qua worker queue" vẫn để cho Đợt 3 (E1.2).

### A6. `GetOrAdd` với factory tốn tài nguyên không dispose bản thua — Vừa

- [x] **Vị trí**: [OutboundSourceFactory.cs:51-57](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L51-L57), [UdpProxyForwarder.cs:44](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L44)
- **Vấn đề**: `ConcurrentDictionary.GetOrAdd` có thể chạy factory hai lần khi hai kết nối tới cùng outbound chưa cache. Bản không thắng bị vứt mà không `Dispose()`. Với `PortTunnel`, bản thua đã kịp `Task.Run(AssociateAsync)` nên mở hẳn một UDP ASSOCIATE không ai đóng.
- **Cách sửa**: `GetOrAdd(key, k => new Lazy<T>(() => ..., ExecutionAndPublication)).Value`, hoặc so sánh reference sau `GetOrAdd` và dispose bản không được giữ.
- **Đã sửa**: ProxyDivert — commit "Build a shared instance once, even when two threads ask together"

### A7. `ApplyConfig` giữ `_stateLock` khi dispose tuần tự PortTunnel — Vừa

- [x] **Vị trí**: [RedirectEngine.cs:184-214](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L184-L214), [RedirectEngine.cs:288-300](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L288-L300), [UdpProxyForwarder.cs:179-186](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L179-L186)
- **Vấn đề**: mỗi `PortTunnel.Dispose` chờ tới 2 giây, tuần tự, dưới lock. Cộng với A2, sửa một outbound có thể khoá `_stateLock` hàng chục giây, chặn `Start`/`Stop`/`ApplyConfig` kế tiếp. `CloseWhereRouteChanged` cũng gọi `Cancel()` dưới lock nên callback huỷ chạy inline.
- **Cách sửa**: thu thập danh sách cần bỏ dưới lock, dispose ngoài lock (hoặc trên thread pool).
- **Đã sửa**: commit "refactor(udp): close a tunnel without holding up the next save". `InvalidateOutbound` vẫn gỡ khỏi bảng dưới lock (đó mới là thứ chặn lưu lượng) nhưng đẩy phần đóng sang thread pool, nên `ApplyConfig` không còn chờ. `PortTunnel` chuyển sang `IAsyncDisposable`. **Phần còn lại đã xong** ở commit "refactor(engine): stop doing the slow part of a save under the state lock": `CloseWhereRouteChanged` và `ReconcileOutbounds` đều ra ngoài `_stateLock`, nên callback huỷ không còn chạy dưới lock.

### A8. Mode nghe socket quét bảng kernel hai lần cho mỗi pid mới — Vừa

- [ ] **Vị trí**: [RedirectEngine.cs:371](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L371), [RedirectEngine.cs:401-413](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L401-L413), [SocketTracker.cs:204-228](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L204-L228)
- **Vấn đề**: `ShouldTrackProcess` → `ProcessRuleTracker.ShouldRedirect` → `TryAttach` raise `ProcessAttached` **đồng bộ** → `OnProcessAttached` gọi `AddTrackedProcessId` → `SocketTracker.AddProcess` chạy `PrePopulateForPid`. Stack quay về `AcceptPid` lại chạy `PrePopulateForPid` lần hai. `RebuildResolver()` cũng chạy trên chính luồng pump.
- **Vì sao**: quét bảng toàn máy hai lần ngay trên đường trễ SYN→accept, đúng thứ đã tối ưu ở đợt 2026-09-07.
- **Cách sửa**: `ShouldRedirect` trả verdict thay vì raise `ProcessAttached` trong callback; hoặc `OnProcessAttached` bỏ qua `AddTrackedProcessId` khi đang ở trong callback (cờ `[ThreadStatic]`). Gắn với E (tách trách nhiệm engine).
- **Xong một nửa** (2026-09-09, E5.3 + E7.1): luồng pump **hết** cả hai thứ nặng — `RebuildResolver` xoá hẳn, còn `AddTrackedProcessId` giờ chỉ là một lần ghi vào `Channel` ([TrackedPidQueue](../src/ProxyDivert.Core/Engine/TrackedPidQueue.cs)). Đường trễ SYN→accept vì thế chỉ còn **một** `PrePopulateForPid`, của chính `AcceptPid`.
- **Nửa còn lại**: lần `PrePopulateForPid` thứ hai **vẫn còn**, chỉ là đã dời sang luồng consumer. Trên thực tế nó thường tự thoát sớm — `AcceptPid` kịp ghi `_pidDecisions[pid]` trước khi consumer chạy tới, và `AddProcess` thấy `already.Accepted` thì `return` — nhưng đó là **may**, không phải bảo đảm. Muốn chắc thì phải sửa trong submodule WinDivert (`ShouldRedirect` trả verdict, hoặc `AcceptPid` ghi verdict trước khi hỏi host), tức thuộc đợt 4.

### A9. `UdpProxyForwarder`: chặn async trên vòng nhận, fire-and-forget nuốt lỗi — Vừa

- [x] **Vị trí**: [UdpProxyForwarder.cs:62](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L62), [UdpProxyForwarder.cs:147](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L147)
- **Vấn đề**: `InjectUdpReplyToProcessAsync(...).GetAwaiter().GetResult()` chặn cả `ReceiveLoopAsync`; `_ = tunnel.SendAsync(...)` trong `try/catch` chỉ bắt lỗi đồng bộ, lỗi async thành unobserved và hàm vẫn trả `true`.
- **Cách sửa**: `await` trong `ReceiveLoopAsync`; với `SendAsync` gắn `.ContinueWith(log, OnlyOnFaulted)` hoặc await có try/catch.
- **Đã sửa**: cùng commit với A7. `ReceiveLoopAsync` `await` `OnReplyAsync`; `Send` giữ nguyên không await (await ở đó sẽ làm nghẽn vòng nhận của relay) nhưng gắn continuation `OnlyOnFaulted` để lỗi async được ghi log thay vì biến mất.

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

- [x] **Vị trí**: [ByteSizeConverter.cs:36-51](../src/ProxyDivert.Wpf/Converters/ByteSizeConverter.cs#L36-L51), [ConnectionsView.xaml:33-34](../src/ProxyDivert.Wpf/Views/ConnectionsView.xaml#L33-L34)
- **Vấn đề**: `DurationConverter` chỉ nhận `StartedUtc`, luôn tính tới `UtcNow`; `ConnectionInfo` có sẵn `EndedUtc`.
- **Cách sửa**: [MultiBinding](Glossary-vi.md#L133) `StartedUtc` + `EndedUtc`, tính `(EndedUtc ?? UtcNow) - StartedUtc`.
- **Đã sửa**: commit "fix(ui): measure a finished connection to when it ended". `DurationConverter` implement cả `IValueConverter` lẫn `IMultiValueConverter` (giữ được một resource key duy nhất), cột đổi sang `MultiBinding`. Test: `The_duration_column_reads_both_ends_of_a_connection` (chạy view thật — lỗi gốc nằm trong XAML nên phải kiểm ở đó) + `DurationConverterTests` cho phần tính. Bẫy: `DurationConverter` trùng tên `System.Windows.DurationConverter`, trong test phải dùng using alias.

### A13. Tick checkbox chọn hàng làm filter "dirty" — Vừa

- [x] **Vị trí**: [ConditionNodeViewModel.cs:20](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L20), [ConditionNodeViewModel.cs:35-37](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L35-L37), [ProcessFilterViewModel.cs:193-197](../src/ProxyDivert.Wpf/ViewModels/ProcessFilterViewModel.cs#L193-L197)
- **Vấn đề**: constructor đăng ký `PropertyChanged → RaiseChanged` cho mọi property, kể cả `IsSelected` mà doc comment nói "Never saved". Mở filter chỉ để xem, tick rồi bỏ tick, đóng → bị hỏi "chưa lưu".
- **Cách sửa**: lọc `e.PropertyName` trong handler, bỏ `IsSelected` (và `IsNew` ở leaf).
- **Đã sửa**: commit "fix(ui): stop the row tick boxes marking a filter as edited". Lọc qua `protected virtual bool IsPartOfTheFilter(string?)` thay vì hardcode danh sách tên trong lớp cha — leaf override để loại thêm `IsNew`. Test: `Ticking_a_condition_row_to_group_it_is_not_an_edit`, `The_caret_moving_to_a_new_row_is_not_an_edit`.

### A14. `Ungroup` âm thầm đổi ngữ nghĩa bộ lọc — Vừa

- [x] **Vị trí**: [ConditionGroupViewModel.cs:111-114](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionGroupViewModel.cs#L111-L114), [ConditionGroupViewModel.cs:173-184](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionGroupViewModel.cs#L173-L184)
- **Vấn đề**: `CanUngroup` chỉ chặn khi `Negate`. Với `x AND (a OR b)`, Ungroup đổ `a`, `b` vào cha và vứt toán tử con → `x AND a AND b`.
- **Cách sửa**: chặn (hoặc hỏi xác nhận) khi `group.Operator != Parent.Operator && Children.Count > 1`.
- **Đã sửa**: commit "fix(ui): refuse to dissolve a bracket that means something". Chọn chặn (không hỏi xác nhận — người dùng vẫn tháo được từng dòng). Ba điểm phải làm thêm mới đủ: (1) kiểm lại trong thân `Ungroup` vì `RelayCommand.Execute` không tự hỏi `CanExecute`; (2) `OnOperatorChanged` phải notify các group con — đổi toán tử ở cha làm câu trả lời của con khác đi; (3) **phát sinh từ A13**: nút "Group selected" trước đó dựa vào `Changed` để bật, mà tick giờ không còn raise `Changed` nữa → phải nghe `PropertyChanged` của con (commit "fix(ui): re-ask for Group selected from the tick itself"). Test: `ConditionEditorTests` (5 test).

### A15. CLI: exception khi `--launch` trỏ file không tồn tại thoát ra ngoài — Vừa

- [x] **Vị trí**: [Program.cs:229-262](../src/ProxyDivert.Cli/Program.cs#L229-L262)
- **Vấn đề**: khối chỉ có `finally`, không `catch`; stack trace và exit code lạ thay vì một dòng lỗi.
- **Cách sửa**: thêm `catch (Exception ex)` ghi `Console.Error` và `return 1`.
- **Đã sửa**: commit "fix(cli): report a failed launch instead of throwing out of main". Chỉ build-verified — chạy thử thật cần quyền admin và sẽ nạp driver WinDivert lên máy, nên không chạy.

### A16. Nhóm mức Thấp (ProxyDivert)

- [x] Id trùng trong json làm `RoutingPolicyResolver` ném trong ctor: [RoutingPolicyResolver.cs:34-37](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L34-L37). Dùng vòng gán `dict[x.Id] = x` như `OutboundUsage`. **Đã sửa** cùng E5.1 (commit `c89edc0`): vòng gán ở [RoutingPolicyResolver.cs:41](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L41), và `AppConfig.Normalize` bỏ trùng ngay từ file. Nó ném ra khỏi `StartAsync` ⇒ một dòng lặp trong file sửa tay là cả máy không có redirect nào. Test `TwoOutboundsSharingAnId_DoNotStopTheResolverFromBeingBuilt`.
- [ ] Regex của user trong `HostMatcher` không có timeout: [HostMatcher.cs:86-99](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L86-L99). Truyền timeout **hằng số** và coi `RegexMatchTimeoutException` là không khớp — KHÔNG chép cách tính hạn của `ProcessRuleMatcher` trước A17, xem A17.
- [ ] `DomainSuffix` với pattern `.example.com` không bao giờ khớp: [HostMatcher.cs:58-64](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L58-L64). `TrimStart('.')`.
- [ ] `OnProcessStopped` chỉ detach con trực tiếp, không lan xuống cháu: [ProcessRuleTracker.cs:369-379](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L369-L379). Lặp tới khi không đổi như `FollowParents`.
- [ ] `AttachProcessId` có thể ném `KeyNotFoundException` khi `Detach` xen giữa: [ProcessRuleTracker.cs:229](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L229). `TryGetValue`.
- [ ] `Dns.GetHostAddresses` đồng bộ trên đường kết nối và luôn lấy `addresses[0]`: [OutboundSourceFactory.cs:280-295](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L280-L295). Ưu tiên IPv4 khi `Ipv6Support == Disabled`, cache endpoint.
- [x] Đổi tên policy làm mất `SelectedRule`: [RulesViewModel.cs:111-121](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L111-L121). Nhớ và gán lại sau `Insert`. **Đã sửa** cùng E8.1 (commit `64be057`) theo hướng khác: không còn `Insert` nào để gán lại sau. `PolicyRowViewModel` tự raise, hàng không rời collection, nên ListBox không bỏ chọn và bảng luật bên dưới không bị xoá. Test `Renaming_a_policy_leaves_the_rule_the_user_had_picked_alone` (dựng ListBox thật, đã kiểm ngược).
- [ ] Timer refresh Connections/Log chạy cả khi tab ẩn, xoá selection mỗi 250ms: [ConnectionsViewModel.cs:38-41](../src/ProxyDivert.Wpf/ViewModels/ConnectionsViewModel.cs#L38-L41), [LogViewModel.cs:34-37](../src/ProxyDivert.Wpf/ViewModels/LogViewModel.cs#L34-L37). Start/Stop theo `IsVisible`.
- [ ] Cây tiến trình có thể lặp vô hạn khi PID bị tái dùng thành vòng cha–con: [ProcessesViewModel.cs:77-98](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L77-L98). Tập đã thăm khi nối.

### A17. Ngân sách regex của bộ lọc tiến trình tính theo đồng hồ tường — Cao

- [x] **Vị trí**: [ProcessRuleMatcher.cs:23-34](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L23-L34), [ProcessRuleMatcher.cs:268-302](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L268-L302). Từ E3.1 ngân sách và vòng chạy regex nằm ở [ConditionContext.cs](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionContext.cs#L77) — hai link trên là vị trí lúc phát hiện.
- **Vấn đề**: `Evaluate` đóng dấu hạn `TickCount64 + 100ms` cho cả bộ lọc, `RunRegex` bỏ cuộc trả `Unknown` khi hạn hết. Nhưng khoảng thời gian đó tính cả lúc luồng **không được lịch chạy**. Đo trên máy này: 32 luồng quay ở ưu tiên cao trên 32 core làm 47/100 ms biến mất trước khi pattern kịp chạy, chưa làm việc regex nào. Máy bận hơn thì mất trọn — một regex rẻ như `^chr.*` trả `Unknown`, nghĩa là "không kết luận được", nên **bộ lọc của user âm thầm không áp dụng và không có gì báo**.
- **Lỗi thứ hai cùng chỗ**: timeout truyền vào `Regex.IsMatch` là phần hạn còn lại nên đổi mỗi lần gọi. Khoá cache regex của .NET gồm cả timeout ⇒ mọi lần gọi đều trượt cache và **dựng lại pattern từ đầu**, trong khi hàm này chạy trên mọi tiến trình của máy ở mỗi lần quét.
- **Phát hiện thế nào**: `ProcessRuleMatcherTests.The_plain_comparisons_still_work_with_no_readable_path(Regex, "^chr.*", "chrome.exe", true)` rớt 1 lần ở đợt 2 rồi không dựng lại được, và rớt lại ở đợt 3 đúng lượt chạy ngay sau build. Chạy riêng hoặc `--no-build` thì 5/5 xanh. Probe dựng tải CPU đo được con số 47 ms ở trên.
- **Đã sửa**: commit "fix(core): stop charging a filter for time it did not spend matching". Tách `RegexBudget` (internal) đếm thời gian **thực sự nằm trong `Regex.IsMatch`** bằng `Stopwatch`, thay cho hạn đồng hồ tường; timeout truyền vào là hằng số nên pattern nằm yên trong cache. Đánh đổi ghi rõ trong code: pattern cuối có thể vượt ngân sách tối đa một lượt timeout, đổi lấy việc không dựng lại regex.
- **Test**: `RegexBudgetTests` (4 test). Đã kiểm ngược: dựng lại ngữ nghĩa hạn-đồng-hồ cũ thì 3/4 đỏ. Một test "end to end" viết ban đầu đã bỏ vì nó xanh cả với code cũ — hạn cũ đóng dấu *bên trong* `Evaluate` nên nghỉ *trước* khi gọi không tái hiện được gì.
- **Sửa tiếp (hiệu năng)**: commit "perf(core): keep the expressions a filter is built from". Chỉ đổi timeout thành hằng số **chưa đủ** — `Regex.CacheSize` mặc định là **15**, mà `EitherSubject` hỏi mỗi luật hai pattern (tên + đường dẫn đã chuẩn hoá), nên chỉ 8 điều kiện là vượt ngưỡng và lại dựng lại pattern trên mọi tiến trình mỗi lần quét. Thêm `RegexCache` giữ hẳn `Regex` đã `Compiled`. Đo end-to-end qua chính `ProcessRuleMatcher`, một lượt quét 301 tiến trình:

  | Số điều kiện | `Regex.IsMatch` tĩnh | Giữ instance, thông dịch | Giữ instance, `Compiled` |
  |---|---|---|---|
  | 8 | 2,04 ms | 1,94 ms | **1,04 ms** |
  | 20 | 24,46 ms | 5,32 ms | **2,61 ms** |
  | 40 | 37,58 ms | 8,89 ms | **5,24 ms** |

  Chọn cột cuối. Giữ instance là thứ xoá được vực thẳm ở mốc 15; `Compiled` giảm tiếp khoảng một nửa ở mọi cỡ, và **dưới** mốc 15 thì nó là thứ duy nhất có tác dụng — 8 điều kiện, giữ instance mà thông dịch thì không nhanh hơn cache sẵn của framework. Giá phải trả: ~2 ms dựng mỗi pattern, một lần, và một dynamic method giữ theo biểu thức.

- **Bẫy đã mắc rồi sửa**: bản `RegexCache` đầu gọi `entries.Count >= Capacity` ở **mọi** lần tra. `ConcurrentDictionary.Count` khoá toàn bộ bucket (đúng lỗi B9 đã sửa ở đợt 2), làm bản có cache **chậm hơn** bản cũ (3,07 ms so với 2,59 ms). Chỉ phát hiện được vì đo trước/sau chứ không tin vào microbenchmark. Đã chuyển sang `TryGetValue` trên đường trúng, chỉ kiểm `Count` khi trượt.
- **Bẫy đo đạc**: bảng số ở commit `792185c` là **đo một lượt mỗi cấu hình** nên dính sai số nguội tới **2×** (lượt đầu 4,48 ms, các lượt sau 1,95 ms cho cùng một thứ). Bảng trên đã đo lại: mỗi ô là **best-of-3 sau khi làm nóng**, cùng một probe, cùng một phiên. Kết luận không đổi nhưng các con số thì đổi — và lần đầu tôi đo nhầm cả mốc nền vì `git stash` chỉ trả về `792185c` (đã có cache) chứ không phải `53742d1`.
- **Đã cân nhắc và loại**: nâng `Regex.CacheSize` (là thiết lập toàn tiến trình, áp lên cả thư viện khác, mà vẫn còn chi phí băm khoá); `RegexOptions.NonBacktracking` (bỏ được deadline nhưng từ chối lookaround/backreference ngay lúc dựng, và chậm hơn `Compiled` với pattern đơn giản).

---

### A18. `ResolveUdp` hỏi bảng pid hai lần cho cùng một datagram — Vừa

- [x] **Vị trí**: `RoutingPolicyResolver.ResolveUdp` — gọi `GetPolicy(target.ProcessId)` lấy thiết lập UDP, rồi gọi `Resolve(target)` vốn tự đọc bảng pid **lần nữa**.
- **Vấn đề**: hai lần đọc có thể rơi vào hai phía của một lần sửa bộ lọc. Datagram khi đó được trả lời bằng `UdpMode`/`BlockQuic` của policy này ghép với **rule của policy kia** — một tổ hợp user chưa bao giờ cấu hình.
- **Vì sao mới thành lỗi**: trước E5.3 resolver ôm một **bản chụp đông cứng** của bảng pid, nên hai lần đọc luôn cho cùng kết quả và đây chỉ là thừa. Từ khi bảng pid đọc **sống** (tracker trả lời trực tiếp) thì nó thành lỗi thật. Đây là loại lỗi sinh ra bởi chính thay đổi thiết kế, không phải lỗi có sẵn.
- **Hậu quả cụ thể**: `ReconcileTrackedWithRules` sửa mô tả tiến trình **tại chỗ** trên mọi lần Save, nên cửa sổ này mở đúng vào lúc user bấm Save trong khi trình duyệt đang chạy QUIC. Kết quả sai lệch về phía nguy hiểm: `BlockQuic=false` của policy mới ghép với rule tunnel của policy cũ ⇒ datagram đi thẳng mang địa chỉ thật.
- **Đã sửa**: cùng đợt E5.3, commit `1eb59bf`. Đọc một lần vào biến local rồi dùng chung cho cả thiết lập lẫn rule ([RoutingPolicyResolver.cs:149](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L149)).
- **Test**: `ResolveUdp_asks_which_policies_a_process_has_exactly_once` (đếm số lần đọc) và `ResolveUdp_answers_from_one_reading_and_not_a_mix_of_two` (nguồn giả trả danh sách khác nhau mỗi lần hỏi). **Đã kiểm ngược**: dựng lại dạng đọc hai lần thì cả hai đỏ.

### A19. Hai policy trùng `Id` làm engine không khởi động được — Vừa

- [x] **Vị trí**: `RoutingPolicyResolver` ctor cũ — `policies.ToDictionary(p => p.Id)`.
- **Vấn đề**: `ToDictionary` **ném `ArgumentException`** khi có khoá trùng. Lời gọi nằm trong `BuildRun` nên exception thoát ra từ `StartAsync`.
- **Vì sao cần sửa**: file cấu hình json sửa tay (chính cách dự án này chấp nhận cho việc sửa dữ liệu debug — xem [ba tầng cấu hình](Glossary-vi.md#L300), không có migration) hoàn toàn có thể copy-paste ra hai policy cùng `Id`. Khi đó máy **không có redirect nào cả**, và thông báo là một `ArgumentException` về "duplicate key" không nói gì về policy nào. Bỏ redirect toàn máy là cái giá đắt hơn nhiều so với việc chọn một trong hai.
- **Đã sửa**: cùng đợt E5.3, commit `1eb59bf`. `CompiledRuleSet.Compile` gán bằng indexer (`byId[policy.Id] = ...`) nên bản sau cùng thắng, engine vẫn chạy ([CompiledRuleSet.cs:52](../src/ProxyDivert.Core/Routing/Compiled/CompiledRuleSet.cs#L52)).
- **Còn hở**: chưa có gì **báo** cho user biết cấu hình có id trùng — mới chỉ là không sập nữa. Chỗ đúng để báo là `AppConfig.Normalize()` của E5.1, nơi đã nhận việc dọn cấu hình lúc `Load`.
- **Test**: `TwoPoliciesSharingAnId_DoNotStopTheEngineFromStarting`.

### A20. Lỗ `null` trong cây điều kiện sửa tay làm editor không mở được — Thấp

- [x] **Vị trí**: [`ConditionGroup.Clone`](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionGroup.cs#L14) (`Children.Select(child => child.Clone())`), gọi từ `ProcessFilterViewModel.RootGroupOf` ngay trong constructor của editor.
- **Vấn đề**: `"Children": [null, ...]` là JSON hợp lệ mà editor không bao giờ ghi ra, nhưng file sửa tay thì có thể. Evaluator vốn coi `null` là một dòng rỗng nên engine vẫn chạy — còn editor sao chép cây trước khi hiện, bản sao ném `NullReferenceException` ⇒ **đúng cái cửa sổ duy nhất sửa được file lại là cái không mở được**. Biến thể `"Children": null` thì ném ngay trong lượt quét tiến trình của engine (vòng `foreach` của nhóm).
- **Phát hiện thế nào**: lúc dời evaluator vào model (E3.1), guard `null` của evaluator cũ phải chép theo — và `Clone` bên cạnh thì không có. Đã dựng lại bằng test: bỏ bản sửa thì stack trace đúng là `ConditionGroup.Clone` ← `RootGroupOf` ← ctor `ProcessFilterViewModel`.
- **Đã sửa**: commit `8d30d96`. [`AppConfig.Normalize`](../src/ProxyDivert.Core/Configuration/Models/AppConfig.cs#L189) — chỗ đã nhận việc sửa file tay từ E5.1 — gọi [`DropHolesInConditions`](../src/ProxyDivert.Core/Configuration/Models/AppConfig.cs#L207): bỏ `null` khỏi mọi danh sách con và cho nhóm không có danh sách một danh sách rỗng. Đi **hết** cây chứ không dừng ở `ProcessCondition.MaxDepth` như evaluator, vì một lỗ nằm dưới ngưỡng đó vẫn chặn được bản sao; đệ quy vẫn có trần nhờ giới hạn lồng nhau của chính serializer.
- **Test**: `AppConfigIntegrityTests.ANullInAConditionTree_IsDroppedWhenTheFileIsRead` (ghi file, chèn `null` vào **cả hai** tầng nhóm, nạp lại, rồi mở editor trên nó) và `AGroupWithNoListOfChildren_GetsAnEmptyOne`. **Đã kiểm ngược**: bỏ lời gọi trong `Normalize` thì cả hai đỏ.

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

- [x] **Vị trí**: [SocketTracker.cs:204-231](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L204-L231), [SocketTracker.cs:315](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L315); [ProcessTreeMonitor.cs:26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L26), [ProcessTreeMonitor.cs:92-97](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.ProcessControl/ProcessTreeMonitor.cs#L92-L97)
- **Vấn đề**: `_pidDecisions[pid] = false` chỉ bị xoá bởi `RemoveProcess`, mà `RemoveProcess` chỉ được gọi cho pid đã attach. Pid được phán `false` (chrome) thoát, Windows cấp lại số đó cho game cần redirect → `AcceptPid` trả cache `false` mãi mãi, không log. Dictionary cũng phình theo mọi tiến trình trên máy. `ProcessTreeMonitor` so `seen` với `_knownDescendants` tích luỹ toàn thời gian thay vì snapshot trước, nên con mới trúng pid cũ không bao giờ bắn `ChildSpawned`.
- **Vì sao**: xem [tái dùng PID](Glossary-vi.md#L374); mode nghe socket dựa hoàn toàn vào cache này.
- **Cách sửa**: khoá theo `(pid, startTime)` hoặc TTL cho verdict `false`; `ProcessTreeMonitor` gán `_knownDescendants = seen` cuối mỗi `Scan`.
- **Đã sửa**: submodule WinDivert (branch `fix/wave2`), 2 commit — "fix(flow): expire a refused pid so a reused number is asked about again" (verdict `false` mang mốc tick, hết hạn 30s; vòng cleanup dọn entry hết hạn nên bảng không phình theo mọi tiến trình trên máy; verdict `true` giữ nguyên vì `RemoveProcess` đã dọn) và "fix(processcontrol): announce a child whose pid has been reused" (`_knownDescendants` thay bằng snapshot mỗi lần quét). Chọn TTL chứ không khoá `(pid, startTime)`, đúng ý doc "sửa tối thiểu": lấy start time phải mở handle cho **mỗi** sự kiện socket, mà `MachineWideScope` ở Đợt 4 sẽ thay hẳn chỗ này.

### B5. UDP relay khoá upstream theo cổng nguồn, đích đóng băng lần đầu — Vừa

- [ ] **Vị trí**: [UdpRelayServer.cs:91](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L91), [UdpRelayServer.cs:132-150](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/UdpRelayServer.cs#L132-L150)
- **Vấn đề**: `GetOrAdd(UpstreamKey(srcPort, family), _ => new UdpUpstream(entry.OriginalDestination))`: một socket UDP nói với nhiều đích (DNS nhiều server, QUIC, STUN) thì datagram thứ hai trở đi gửi sai địa chỉ. `_upstreams` không bao giờ evict.
- **Ghi chú**: trong ProxyDivert nhánh này chỉ chạm khi `HandleUdpDatagram` trả payload ở trường hợp Direct-sau-redirect hiếm ([UdpFlowRouter.cs:98-111](../src/ProxyDivert.Core/Engine/UdpFlowRouter.cs#L98-L111)), còn proxy/VPN đi qua `UdpProxyForwarder`. Vẫn là lỗi thư viện.
- **Cách sửa**: gửi thẳng `SendAsync(payload, entry.OriginalDestination)` trên một socket theo cổng, thêm idle-eviction.

### B6. `TcpRelayServer.HandleAsync`: phần trước `try` ném thì rò `TcpClient` — Vừa

- [x] **Vị trí**: [TcpRelayServer.cs:101](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L101), [TcpRelayServer.cs:105-129](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/TcpRelayServer.cs#L105-L129)
- **Vấn đề**: `_ = Task.Run(() => HandleAsync(...))`; `RemoteEndPoint` và `new RedirectedTcpConnection` → `GetStream()` nằm ngoài `catch`. Client connect rồi RST ngay (QUIC fallback, racing connections) → task faulted không log, `client` không `Close()`.
- **Cách sửa**: bọc toàn bộ thân trong `try/catch/finally` có `client.Close()`, `.ContinueWith` log lỗi.
- **Đã sửa**: submodule WinDivert, commit "fix(redirect): close an accepted socket whose setup throws". Tách `HandleCoreAsync`; `HandleAsync` chỉ còn try/catch/finally đóng socket (không cần `.ContinueWith` nữa vì không còn gì thoát ra ngoài task).

### B7. `ProcessRedirector.Start` không rollback — Vừa

- [x] **Vị trí**: [ProcessRedirector.cs:138-176](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L138-L176)
- **Vấn đề**: `tracker.Start()` và `StartRelays()` chạy trước `OpenNetworkHandle`. Handle mở thất bại (không elevated) → `Start()` ném ra, tracker + hai relay listener + pump task còn sống, caller thường không `Dispose` vì "Start đã fail".
- **Cách sửa**: `try/catch` quanh thân, gọi `Dispose()` rồi rethrow.
- **Đã sửa**: submodule WinDivert, commit "fix(redirect): unwind a redirect whose start fails". Tách `StartCore`; `Start` bọc try, gọi `Dispose()` (nuốt lỗi dọn dẹp, chỉ log Debug) rồi rethrow lỗi gốc.

### B8. `AddProcess` đua với `Dispose`, handler ném làm `RemoveProcess` dở dang — Vừa

- [x] **Vị trí**: [SocketTracker.cs:239-299](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L239-L299), [SocketTracker.cs:328-345](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L328-L345), [SocketTracker.cs:551-577](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L551-L577)
- **Vấn đề**: `AddProcess` từ thread watcher, `Dispose` từ UI: qua được check `_cts.IsCancellationRequested` rồi `Dispose` clear xong trước `TryAdd` → handle không bao giờ đóng; task body đọc `_cts.Token` sau `_cts.Dispose()` → `ObjectDisposedException` unobserved. `RemoveProcess` gọi `TcpConnectClosed?.Invoke`/`UdpBindRemoved?.Invoke` không try/catch, một subscriber ném là handle SOCKET đã đóng nhưng flow còn nguyên.
- **Cách sửa**: lock hoặc cờ `_disposed` kiểm lại sau `TryAdd`; chụp `_cts.Token` trước `Task.Run`; bọc từng `Invoke` như `PumpLoop`.
- **Đã sửa**: submodule WinDivert, commit "fix(flow): stop AddProcess leaving a handle behind when Dispose races it". Đủ cả 3 phần. Điểm mấu chốt của thứ tự: entry phải vào `_pidHandles` TRƯỚC rồi mới đọc lại `_disposed` — như vậy hoặc `Dispose` thấy entry, hoặc `AddProcess` thấy cờ, không có cửa nào lọt.

### B9. `ConcurrentDictionary.Count` trên hot path mỗi sự kiện socket — Vừa

- [x] **Vị trí**: [SocketTracker.cs:504](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L504), [SocketTracker.cs:516](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L516), [SocketTracker.cs:531](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L531)
- **Vấn đề**: `_tcpFlows.Count` lấy **mọi** lock của dictionary và là tham số `LogTrace` nên chạy dù không bật Trace, trong khi pump NETWORK đọc `_tcpFlows` mỗi packet. Mode máy-toàn-bộ là mọi socket event trên máy.
- **Cách sửa**: bọc `if (_logger.IsEnabled(LogLevel.Trace))` như `NatRedirectMiddleware.cs:169` đã làm.
- **Đã sửa**: submodule WinDivert, commit "perf(flow): stop counting the flow table on every socket event". Hỏi `IsEnabled` một lần vào biến `trace` dùng cho cả 3 dòng trong `HandleEvent`.

### B10. `DnsCacheLookup.Refresh` redirect stderr không đọc, không Kill khi timeout — Vừa

- [x] **Vị trí**: [DnsCacheLookup.cs:39](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L39), [DnsCacheLookup.cs:67-79](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L67-L79)
- **Vấn đề**: `RedirectStandardError = true` nhưng chỉ `ReadToEnd()` stdout; `ipconfig` ghi đủ 4KB stderr là pipe đầy, `ReadToEnd` treo vô hạn, vòng refresh chết im lặng. `WaitForExit(10s)` không `Kill()`. `Start()` check-then-assign không atomic.
- **Cách sửa**: bỏ redirect stderr (hoặc `BeginErrorReadLine`), `Kill()` khi timeout, `Interlocked.CompareExchange` trong `Start`.
- **Đã sửa**: submodule WinDivert, commit "fix(flow): stop the DNS cache refresh hanging on an unread stderr pipe". Bỏ hẳn redirect stderr (không dùng `BeginErrorReadLine`: không ai cần nội dung đó). `Start` dùng `Interlocked.Exchange` trên một cờ `int` riêng chứ không CAS trên `_loopTask` — `Dispose` chờ trên `_loopTask`, nên đặt placeholder vào đó sẽ làm `Dispose` chờ nhầm một task không bao giờ xong.

### B11. `IDnsCacheLookup` transient bị root ServiceProvider giữ mãi — Vừa

- [x] **Vị trí**: [RedirectServiceCollectionExtensions.cs:44](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/DependencyInjection/RedirectServiceCollectionExtensions.cs#L44), [DnsCacheLookup.cs:26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/DnsCacheLookup.cs#L26)
- **Vấn đề**: `sp.GetRequiredService<IDnsCacheLookup>` với `sp` là root; transient `IDisposable` được root container thêm vào danh sách disposables và giữ tham chiếu mạnh tới khi app thoát. Mỗi Start/Stop là một bản rò kèm `_map` chỉ tăng.
- **Cách sửa**: resolve từ `IServiceScope` do session sở hữu, hoặc đăng ký factory trả instance không bị container theo dõi.
- **Đã sửa**: submodule WinDivert, commit "fix(di): stop the root container holding every DNS cache lookup". Chọn hướng factory (`TryAddSingleton<Func<IDnsCacheLookup>>`) chứ không dùng scope: session không có scope riêng và dựng scope chỉ để giữ một object sẽ phải kèm wrapper dispose scope theo. **Breaking nhỏ**: ai resolve thẳng `IDnsCacheLookup` phải chuyển sang `Func<>` — trong repo này không có chỗ nào. Test `DnsCacheLookupLifetimeTests` dùng `WeakReference` để chứng minh container không còn giữ.

### B12. `ReverseDnsTable.Trim` sort toàn bảng trên mỗi Add sau khi đầy — Vừa

- [x] **Vị trí**: [ReverseDnsTable.cs:43](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/ReverseDnsTable.cs#L43), [ReverseDnsTable.cs:74-90](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/ReverseDnsTable.cs#L74-L90)
- **Vấn đề**: trim đúng phần thừa nên `Count == _capacity` sau trim, Add kế tiếp lại copy list 20k + Sort O(n log n), trên luồng pump mỗi câu trả lời DNS.
- **Cách sửa**: trim theo lô 10-20% capacity.
- **Đã sửa**: submodule WinDivert, commit "perf(securedns): trim the reverse-DNS table in batches". Trim xuống dưới capacity 1/8. Test `ReverseDnsTableTests` (3 test). Lưu ý khi đọc test: bảng trim theo **hạn dùng gần nhất**, không theo thứ tự thêm — bản ghi mới với TTL ngắn vẫn có thể bị loại trước bản ghi cũ TTL dài.

### B13. Peek 2048 byte không đủ cho ClientHello hậu lượng tử → mất SNI — Vừa

- [x] **Vị trí**: [TlsClientHelloParser.cs:21](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/TlsClientHelloParser.cs#L21), [TlsClientHelloParser.cs:67-72](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/TlsClientHelloParser.cs#L67-L72), [HostNameInspector.cs:54](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Inspection/HostNameInspector.cs#L54)
- **Vấn đề**: Chrome/Edge với X25519MLKEM768 gửi ClientHello ~2.0-2.3KB và xáo thứ tự extension; `key_share` ~1.2KB đứng trước `server_name` và vắt qua mốc 2048 là parser trả false, `HostNameInspector` trả null. Routing theo domain âm thầm tụt xuống reverse-DNS/IP.
- **Cách sửa**: nâng `RecommendedPeekSize` lên 8192 (hoặc 16644 = record TLS tối đa).
- **Đã sửa**: submodule WinDivert (branch `fix/wave2`), commit "fix(inspection): read the SNI of a post-quantum ClientHello". Chọn 8192.
- **Phát hiện thêm khi sửa**: chỉ nâng peek size là **tạo hồi quy 3 giây**. `HostNameInspector` peek trong vòng lặp cho tới khi số byte không tăng nữa, nên nó không phân biệt được "client chưa nói xong" với "client nói xong và không có tên" — gói first-flight hoàn chỉnh mà không có SNI/Host phải đợi hết `HostPeekTimeout` (3s) mới được route. Lỗi này ĐÃ có sẵn với 2048, nâng lên 8192 chỉ làm nó thành phổ biến. Thêm `IHostNameParser.WantsMoreData` (TLS xét độ dài record đã khai báo, HTTP xét dòng trống kết thúc header). Test: 4 test parser + 2 test inspector đo thời gian — đã xác nhận test fail (đúng 3s) khi bỏ nhánh dừng sớm.

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

### B15. `ObjectDisposedException` trên luồng pump — nửa còn lại của B1 — Cao

- [x] **Vị trí** (số dòng sau khi sửa): bốn chỗ hủy ở `SocketTracker` — [330-338](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L330-L338) (thua đua `TryAdd`), [345-350](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L345-L350) (orphan), [373-379](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L373-L379) (`RemoveProcess`), [663-691](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L663-L691) (`Dispose`); chỗ nhận quyền đóng: [SocketTracker.cs:513-517](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L513-L517). Bên NETWORK: [PacketPump.cs:91-95](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L91-L95) và [PacketPump.cs:181-199](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L181-L199).
- **Triệu chứng user báo**: `System.ObjectDisposedException: 'Cannot access a disposed object. Object name: WinDivertSafeHandle'` ném từ `SafeHandle.DangerousAddRef` → `WinDivertNative.Recv` → `WinDivertHandle.TryRecv` → `SocketTracker.PumpLoop`, chạy trên thread pool.
- **Vấn đề**: [SafeHandle](Glossary-vi.md#L378) chỉ che được lời gọi **đang bay** lúc thread khác dispose — việc đóng thật bị hoãn tới khi lời gọi đó trả về. Nó **không** che lời gọi **kế tiếp**: handle đã dispose thì `DangerousAddRef` ném ngay. Mọi chỗ hủy ở đây đóng handle bất kể pump đã ra hay chưa, nên lượt gọi sau của pump nổ trên luồng không ai canh.
- **Vì sao B1 chưa đóng hết**: B1 kê hai việc — (1) khai báo P/Invoke nhận `WinDivertSafeHandle`, (2) *"trong `Dispose` bỏ qua `_handle.Dispose()` khi `Wait` timeout"*. Commit `4656753` chỉ làm việc (1). Việc (2) không những không làm mà còn bị viết comment biện minh ngược lại ngay tại `PacketPump.Dispose`: *"disposing anyway is still safe: every call into the driver holds a reference on the handle"* — đúng vế đầu, thiếu vế sau.
- **Chỗ nặng nhất**: nhánh "discard ours" của `AddProcess` khi thua đua `TryAdd`. Ở đó CTS **không** bị cancel và **không** có `Wait` nào, pump vừa được `Task.Run` ba dòng trước đã cầm handle bị đóng ngay — khớp đúng stack trace user gửi (không có frame `Wait`, tức không ai observe task đó).
- **Đã sửa**: submodule WinDivert, branch `fix/handle-close-race`, commit `9321fd1` + `65213ad`. Bất biến mới: **chỉ luồng đang đọc handle mới được đóng nó**. `PumpLoop` bọc `try/finally { handle.Dispose(); }`; các chỗ hủy chỉ còn `Shutdown()` (đây mới là thứ làm `recv` trả về) và `Wait(1s)` cho gọn. `Wait` hết giờ giờ chỉ tốn thêm chút thời gian handle còn mở, thay vì một cú ném. Ngoại lệ duy nhất: `PacketPump` chưa `Start()` thì không có pump để giao, `Dispose` tự đóng — nên `Start` set cờ `_started` **trước** `Task.Run`.
- **Test**: project test đầu tiên của thư viện này (`TqkLibrary.WinDivert.Tests`, commit `d47f3b9`), 5 test, **không cần quyền admin và không nạp driver** — `SocketTracker` nhận handle factory qua DI, `PacketPump` nhận thẳng handle. Handle giả ném khi bị gọi sau lúc đóng *và đếm lại*, vì nếu chỉ ném thì exception rơi trên luồng pump không ai chờ rồi biến mất — đúng cách lỗi này lọt lưới bấy lâu. Đã kiểm ngược: 4/5 đỏ trên `master` cũ.
- **Bẫy đã mắc rồi sửa**: hai test đầu **xanh cả trên code cũ**. Chúng đợi `handle` bị đóng rồi mới assert — nhưng đóng chính là việc code hỏng làm **sớm** (mốc 1 giây), còn lời gọi vi phạm của pump mãi giây thứ 2 mới tới. Đổi sang đợi đúng lời gọi đó (`WaitForSend`) thì mới đỏ. Bài học chung: mốc chờ của test phải là **hành vi sai**, không phải điều kiện dẫn tới nó.

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

- [x] **Vị trí**: [InTunnelResolver.cs:113-131](../src/ProxyDivert.Core/Vpn/Client/InTunnelResolver.cs#L113-L131); thư viện `TcpIpStack.cs:35,77,80-81,92`
- **Vấn đề**: `_nextPort` dùng chung cho `ConnectAsync` và `BindUdp`, `(ushort)Interlocked.Increment` wrap sau 16k lần rồi quay vòng cả dải; `_connections[localPort] = connection` ghi đè kết nối sống, `Closed` của nạn nhân xoá nhầm entry mới. Resolver bind tới 12 socket mỗi tên (2 lần × 3 server × A/AAAA).
- **Cách sửa**: một `UdpConnection` sống lâu trong `InTunnelResolver`, khớp trả lời theo DNS id; thư viện cấp phát cổng bỏ qua cổng đang có trong bảng.
- **Đã sửa**: **chỉ nửa thư viện** — submodule VpnClient, commit "fix(ipstack): keep ephemeral ports inside their range and out of live flows". Cấp phát gập counter vào đúng dải 49152-65535, bỏ qua cổng còn trong bảng, và lấy cổng bằng `TryAdd` thay cho indexer (chính indexer mới là chỗ ghi đè kết nối sống). Test `EphemeralPortAllocationTests` (3 test, chạy 20k lần cấp phát).
- **Cố ý CHƯA làm**: phần "một `UdpConnection` sống lâu trong `InTunnelResolver`". Sau khi cấp phát cổng đã đúng thì bind-per-query không còn gây hỏng, chỉ còn tốn — mà E6.2 sẽ chuyển hẳn resolver này sang lib VpnClient, nên dựng cơ chế ghép kênh theo DNS id ở đây rồi bỏ đi là phí. Để lại cho E6.2.

### C4. WireGuard in-process không có PersistentKeepalive — Vừa

- [x] **Vị trí**: [VpnConnectionKeeper.cs:29](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L29), [VpnClientProxySource.cs:183-184](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L183-L184); thư viện `WireGuardConfFile.cs:36,132`, `WireGuardTimers.cs:80`; wireproxy có mặc định 25 ở `WireGuardOptions.cs:84`
- **Vấn đề**: comment của keeper nói "config writer thêm [PersistentKeepalive](Glossary-vi.md#L105) khi file không có", nhưng chỉ đúng với wireproxy. `VpnDialer.ConnectWireGuardAsync` parse file thô, keepalive = 0 = tắt.
- **Vì sao**: file của provider không có dòng này → sau 30-120 giây im lặng NAT phía peer hết hạn, tunnel im lặng chết mà WireGuard không báo link-loss.
- **Cách sửa**: thêm `DefaultPersistentKeepalive` vào `VpnTunnelOptions` (hoặc overload nhận `WireGuardConfig`), ép 25 giây khi file không có. Cần sửa trong submodule.
- **Đã sửa**: submodule VpnClient (branch `fix/wave2`), commit "feat(tunnels): keep an in-process WireGuard tunnel alive behind NAT" — `VpnTunnelOptions.WireGuardKeepaliveSeconds` mặc định 25, truyền xuống `WireGuardConfFile.Load/Parse` làm giá trị dùng khi file không có dòng nào. **Parser vẫn mặc định 0** (đọc đúng như file viết) — cấp fallback là quyết định của caller, không phải của parser. `WireGuardConfig` là class có `init` chứ không phải record nên không dùng được `with`; vì vậy giá trị phải đi vào lúc parse. Kèm test project mới `TqkLibrary.VpnClient.Tunnels.Tests` (Tunnels trước đó không có test nào), 4 test. Repo cha: sửa lại comment sai ở `VpnConnectionKeeper`.

### C5. `KeptVpnTunnel.Dispose` dispose CTS khi loop còn chạy — Vừa

- [x] **Vị trí**: [KeptVpnTunnel.cs:192-207](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L192-L207), [KeptVpnTunnel.cs:126](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L126), [KeptVpnTunnel.cs:159](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L159)
- **Vấn đề**: `Cancel` → `Wait(2s)` có thể timeout (dial mặc định 90 giây) → `_cts.Dispose()`. Loop tới `Task.Delay(delay, ct)` ném `ObjectDisposedException`, không nhánh catch nào bắt; `SetStatus(Stopped)` không chạy, fault unobserved.
- **Cách sửa**: bỏ `_cts.Dispose()` hoặc chuyển vào `_loop.ContinueWith`.
- **Đã sửa**: ProxyDivert — commit "Stop disposing the supervision token out from under its own loop"

### C6. `Outbound.SupportsUdp` và source thực tế có thể lệch nhau — Vừa

- [x] **Vị trí**: [Outbound.cs:72](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L72), [VpnProfileReader.cs:83-94](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L83-L94), [VpnProfileReader.cs:270-271](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L270-L271), [OutboundSourceFactory.cs:211-235](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L211-L235)
- **Vấn đề**: routing hỏi `RunsOnWireProxy` từ URL (`Auto` + không `.conf` → mang được UDP), factory lại `Sniff` nội dung file và trả `WireGuardWireProxy` cho bất kỳ `[Interface]` → wireproxy TCP-only. File `wg0.txt` làm UDP được route tới rồi fail ở `GetUdpAssociateSourceAsync` thay vì hạ xuống Block.
- **Cách sửa**: `Sniff` trả `Auto` cho file WireGuard không có đuôi `.conf` (ép user chọn rõ), để hai bên cùng trả lời từ URL.
- **Đã sửa**: commit "fix(vpn): stop guessing the engine for a WireGuard file that is not .conf". Thông điệp lỗi sẵn có của `FromFile` đã hướng dẫn đúng ("Give it a .ovpn or .conf extension, or pick the protocol by hand") nên không thêm message mới. Test: `A_wireguard_file_without_the_conf_extension_asks_the_user_which_engine_it_meant` (kiểm cả `RunsOnWireProxy` để chốt hai bên cùng câu trả lời), `The_same_file_is_read_once_the_user_names_the_engine`.

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
- [ ] Chỉ 22/210 `await` có `ConfigureAwait(false)`; `async void _MainLoopListen`: [ProxyServer.cs:166](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L166). Nhánh Test ở [OutboundTester.cs:51-52](../src/ProxyDivert.Core/Outbounds/OutboundTester.cs#L51-L52) gọi từ UI nên continuation nội bộ bị post về Dispatcher.

### D6. Phía server TqkLibrary.Proxy (chỉ `ProxyDivert.Cli --self-host-port`)

**Đã sửa 8/13** trong submodule TqkLibrary.Proxy (branch `fix/wave2`), 4 commit. Test mới:
`src/TestProxy/ServerTest/HttpProxyServerForwardingTest.cs` (4 test, dựng origin server giả trên
loopback — không phụ thuộc internet như bộ test cũ) và `Socks5FailureReplyTest.cs` (2 test). Đã xác
nhận cả 4 test HTTP fail khi hoàn nguyên fix, và test IPv6 fail khi bỏ dấu ngoặc.

- **Lưu ý về commit**: 4 mục **Cao** nằm chung một commit "fix(proxyserver): stop a source that
  ends early spinning the transfer loop" — lẽ ra mỗi mục một commit. Thân commit đó cũng mất một
  từ (viết `` `size` `` trong `-m` của bash → backtick bị shell nuốt). Không amend theo quy tắc
  `~/.claude/git.md`; nội dung đầy đủ ghi ở đây. Bài học đã lưu vào `~/.claude/experience-git.md`.
- **Chưa làm — BIND reply thứ hai**: cần sửa đồng bộ cả `Socks5ProxyServer`, `Socks4ProxyServer`
  và `Socks5ProxySource.BindTunnel`, mà ProxyDivert không dùng BIND ở bất kỳ đường nào và không có
  cách kiểm thật ngoài tự viết cả hai đầu. Rủi ro cao hơn giá trị trong đợt này.
- **Chưa làm — 3 mục Thấp**: ngoài phạm vi Đợt 2.
- Mục **SOCKS4 rò rỉ DNS**: đã chuyển `Dns.GetHostAddresses` sang `GetHostAddressesAsync` (bỏ chặn
  luồng). Phần "rò rỉ" thì **không bịt được**: reply SOCKS4 bắt buộc mang địa chỉ IPv4, nên tên
  buộc phải phân giải tại máy này. Đó là giới hạn của giao thức, không phải của hàm.

- [x] **Cao** `TransferAsync` quay vòng 100% CPU khi nguồn đóng sớm (không kiểm `byte_read == 0`): [StreamExtensions.cs:23-29](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/StreamExtensions.cs#L23-L29). Ném `EndOfStreamException`.
- [x] **Cao** `HttpProxyServer` dispose stream client sau mỗi request nên keep-alive chết từ request thứ 2: [HttpProxyServer.cs:134](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L134), [HttpProxyServer.cs:165](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L165), [BaseProxyServerHandler.cs:73-76](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Handlers/BaseProxyServerHandler.cs#L73-L76). Chỉ `using` khi handler trả instance khác.
- [x] **Cao** Request line mất query string (`AbsolutePath` thay vì `PathAndQuery`): [HttpProxyServer.cs:149](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L149).
- [x] **Cao** Response chunked / close-delimited không bao giờ được forward body: [HttpProxyServer.cs:168-184](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L168-L184), [HttpUtilities.cs:7-20](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/HttpUtilities.cs#L7-L20).
- [x] **Vừa** `Socks5_Request.Uri` tạo URI sai cho đích IPv6 (thiếu ngoặc): [Socks5_Request.cs:53-67](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Helpers/Socks5_Request.cs#L53-L67). Mọi CONNECT ATYP=0x04 ném tại [Socks5ProxyServer.cs:113-114](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L113-L114).
- [x] **Vừa** Server SOCKS4/5 không trả reply lỗi khi connect upstream thất bại: [Socks5ProxyServer.cs:158-165](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L158-L165), [Socks4ProxyServer.cs:145-150](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L145-L150).
- [ ] **Vừa** BIND thiếu reply thứ hai cả hai phía: [Socks5ProxyServer.cs:399-419](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks5ProxyServer.cs#L399-L419), [Socks5ProxySource.BindTunnel.cs:52-59](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BindTunnel.cs#L52-L59), tương tự Socks4.
- [x] **Vừa** SOCKS4 server resolve DNS đồng bộ và cục bộ ([rò rỉ DNS](Glossary-vi.md#L113)): [Socks4ProxyServer.cs:114](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L114), [Socks4ProxyServer.cs:143](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/Socks4ProxyServer.cs#L143).
- [x] **Vừa** `PreReadAsync` chỉ `ReadAsync` một lần, read ngắn bị coi là hết stream: [PreReadStream.cs:17-39](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/PreReadStream.cs#L17-L39), [PreReadStream.cs:63-64](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/StreamHelpers/PreReadStream.cs#L63-L64).
- [x] **Vừa** Body response ghi thẳng `_clientStream` bỏ qua stream handler đã bọc (throttling/đếm byte mất body): [HttpProxyServer.cs:178-184](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L178-L184).
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

- [x] **Vị trí**: [OutboundSourceFactory.cs:76-98](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L76-L98) (cache + dispose theo chữ ký), [KeptVpnTunnel.cs:116](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L116) và [KeptVpnTunnel.cs:204](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L204) (`Invalidate` khi thất bại và khi dispose), [RedirectEngine.cs:288-300](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L288-L300) (invalidate lần ba), [KeptVpnTunnel.cs:137-147](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L137-L147) (keeper ép kiểu `WireGuardProxySource` để tìm lại tunnel).
- **Vấn đề**: factory cache, keeper và engine đều có quyền giết instance của nhau qua `Invalidate(Guid)`; `Sync` và `ApplyOutbounds` cùng chạy trên cùng danh sách mỗi Save và cùng tính `OutboundSignature.Of`. Đây là bẫy đã ghi ở [vòng đời VPN tách khỏi engine](Glossary-vi.md#L344).
- **Cách sửa**: factory chỉ **tạo**: `IOutboundInstance Create(Outbound, ctx)` trả `{ IProxySource Source; IManagedProxySource? Tunnel; OutboundCapabilities Caps; string Signature }`. `OutboundRegistry` (singleton, thay cache trong factory) là chủ duy nhất: `Reconcile(outbounds)` trả `(added, removed)`; keeper chỉ đăng ký/huỷ tunnel từ `added/removed`, không `Invalidate`; engine lấy `Source` từ registry. Thứ tự: thêm `IOutboundInstance` + registry bọc factory → keeper đổi sang sự kiện registry → bỏ 3 chỗ `Invalidate`. Rủi ro vừa: giữ quy tắc "đổi tên không rớt tunnel" trong `Signature`.
- **Đã sửa**: 2 commit, làm cùng E2.5 (xem [builder theo kind và registry sở hữu instance](Glossary-vi.md#L448)).
  1. `refactor(outbounds): let each kind build its own way out` — [IOutboundInstance.cs:20](../src/ProxyDivert.Core/Outbounds/IOutboundInstance.cs#L19) mang `Source` + [`Tunnel`](../src/ProxyDivert.Core/Outbounds/IOutboundInstance.cs#L43) + `Signature` + [`SetIpv6Support`](../src/ProxyDivert.Core/Outbounds/IOutboundInstance.cs#L64).
  2. `refactor(outbounds): give an outbound instance a single owner` — [OutboundRegistry.cs:34](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L34) là chủ duy nhất. Hai đường bỏ instance, **cố ý đọc khác nhau**: [`ReconcileAsync`](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L119) = cấu hình đổi; [`DiscardAsync`](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L154) = người canh một outbound nói instance nó canh đã hỏng. Cả hai bắn [`InstanceDropped`](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L62).
  - `KeptVpnTunnel` hết ép kiểu: [Resolve:151](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L151) chỉ hỏi `instance.Tunnel`, còn cầu nối wireproxy dời vào builder — cạnh chỗ chọn engine. (E4.1 sau đó xoá hẳn cầu nối đó: source tự nói.)
- **Hai bug thật lòi ra khi làm** (không phải chỉ dọn hình dáng):
  1. **Drop của keeper không tới `UdpProxyForwarder`.** Engine chỉ reset IPv6 đã học + đóng tunnel UDP cho các outbound mà **reconcile** bỏ; keeper `Invalidate` thẳng nên VPN chết rồi dựng lại thì TCP chạy tiếp còn UDP vẫn trỏ vào source đã dispose. Nay đi qua [OnOutboundInstanceDropped:388](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L388), phủ cả hai đường bỏ.
  2. **`WireProxyPath` không tới lượt dial đầu tiên.** Đường duy nhất đưa path vào factory là `ApplyOutboundsAsync`, mà keeper dial lúc khởi động (`AppServices.ConnectKeptVpnsAsync`) trước khi engine chạy lần nào ⇒ tunnel đầu phiên dựng như thể ô cấu hình rỗng, chỉ đúng lại sau khi user bấm Start. Nay keeper đưa path cho registry **trước** khi so hay dựng ([VpnConnectionKeeper.cs:87](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L93)), và hỏi chữ ký của registry ([:108](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L108)) thay vì tự tính.
- **Khác dự kiến** (4 điểm, đều có lý do):
  - **Không** tách `Signature` xuống builder. `OutboundSignature` vốn đã là một class một việc và đang có test riêng; rải quy tắc "đổi tên không rớt tunnel" ra 6 builder là cách chắc chắn nhất để hai bên trả lời lệch nhau. Thay vào đó [`SignatureOf`](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L84) là cửa duy nhất, keeper không tự tính nữa.
  - **Không** thêm `OutboundCapabilities`. Chưa có ai đọc: `SupportsUdp` được hỏi trên đường routing **trước khi** có instance, và đó là cố ý (không được chạm đĩa trên hot path) — C6 đã làm hai câu trả lời khớp nhau. Để lại cho E4.2, lúc đó mới có người dùng.
  - `Tunnel` tạm là `IKeptTunnel` của app chứ chưa phải `IManagedProxySource`: đưa interface đó xuống lib là E4.1, bước 3.
  - Keeper **vẫn tự quyết** tập VPN cần giữ (`SyncAsync`) chứ không nghe `added/removed`. Registry chỉ giữ instance mà **ai đó đã cần**, còn tunnel phải dựng chủ động; nghe registry thì sẽ không có tunnel nào được dựng cả.
- **Test**: `OutboundRegistryTests` (4 test cũ đổi tên theo `ReconcileAsync`, thêm 3: dispose đúng một lần + báo `InstanceDropped`, `Discard` dựng lại instance mới, hai builder cùng kind bị từ chối lúc wiring), `OutboundInstanceTests` (VPN có tunnel, SOCKS5 không, Block nói vì sao), `VpnConnectionKeeperTests` thêm 2 — **đường thành công của vòng giám sát lần đầu tiên chạy được**, vì trước đây mọi outbound VPN đều là dial thật nên chỉ dựng được tunnel hỏng.

#### E1.2 Chuỗi dispose đồng bộ ép `Wait` ở 5 tầng — Must (lib Proxy + app)

- [x] **Vị trí**: `IProxySource` không kế thừa `IDisposable` ([IProxySource.cs:3](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IProxySource.cs#L3)); các source có state tự thêm `IDisposable` ([WireGuardProxySource.cs:15](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L15)) hoặc `IAsyncDisposable` ([ReverseClientSession.cs:13](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseClientSession.cs#L13)); app dọn bằng `(source as IDisposable)?.Dispose()` ([OutboundSourceFactory.cs:297-300](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L297-L300)); chuỗi `Wait`: [VpnClientProxySource.cs:260](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L260), [KeptVpnTunnel.cs:198](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L198), [UdpProxyForwarder.cs:179-186](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L179-L186), [ProcessInventory.cs:430](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L430), [AppServices.cs:275](../src/ProxyDivert.Wpf/Services/AppServices.cs#L275).
- **Vì sao**: xem [IAsyncDisposable và chuỗi dispose đồng bộ](Glossary-vi.md#L436); một source chỉ có `IAsyncDisposable` sẽ rò mà không có cảnh báo compile; đây là lớp lỗi "Save đơ" có tiền sử (C7).
- **Cách sửa**: lib thêm `IAsyncDisposable` vào `IProxySource` (hoặc helper `ProxySourceDisposal.DisposeAsync` ưu tiên async, fallback sync) và mọi source lib implement. App: `OutboundRegistry.ReconcileAsync`, `KeptVpnTunnel : IAsyncDisposable` (await `_loop`), `VpnConnectionKeeper.SyncAsync`, `RedirectEngine.StopAsync`, `AppServices.DisposeAsync` gọi từ `OnExit`; chấp nhận `Wait` ở đúng một chỗ ngoài cùng. Thứ tự: lib → factory/registry → keeper → engine → UI.
- **Đã sửa**: 5 commit, mỗi chặng build + test xanh mới sang chặng sau.
  1. `refactor(proxy): make releasing a way out an async contract` — chọn phương án **đầu** của "Cách sửa" (thêm `IAsyncDisposable` vào `IProxySource`) chứ không phải helper, vì cái giá trị nhất là **được compiler kiểm**: một source chỉ có dạng async trước đây bị bỏ qua lặng lẽ. netstandard2.0 phải thêm `Microsoft.Bcl.AsyncInterfaces` (đã có sẵn trong `Directory.Packages.props`, project Reverse đang dùng).
  2. `refactor(core): release a way out without blocking a thread` — factory chuyển sang `InvalidateAsync`/`InvalidateAllAsync`/`ApplyOutboundsAsync`/`DisposeSourceAsync`; `VpnClientProxySource` **bỏ hẳn** `IDisposable` nên không còn đường đồng bộ nào để làm sai.
  3. `refactor(vpn): supervise and stop a tunnel without blocking a thread` — `KeptVpnTunnel` + `VpnConnectionKeeper`.
  4. `refactor(udp): close a tunnel without holding up the next save` (kèm A7 phần UDP + A9) và `refactor(engine): stop doing the slow part of a save under the state lock` (kèm A7 phần `CloseWhereRouteChanged`, và `ProcessInventory.DisposeAsync`).
  5. `refactor(host): wait for the teardown at the edge, not in five places` — `AppServices.DisposeAsync`, hàng đợi việc nhận `Func<Task>`, CLI `await using`, và **xoá toàn bộ cầu nối đồng bộ** của các thao tác.
- **Khác dự kiến**: `RedirectEngine` phải thêm `SemaphoreSlim _lifecycle`. `_stateLock` trước đây bao trọn `Start`/`Stop`/`ApplyConfig` nên chúng loại trừ nhau *tình cờ*; monitor không giữ được qua `await`, nên tính chất đó phải nói ra thành lệnh. Không có nó thì `Start` có thể chen vào lúc `Stop` còn đang dispose redirector.
- **Còn giữ đồng bộ, có chủ ý**: `Dispose()` trên 4 singleton DI (`RedirectEngine`, `ProcessInventory`, `VpnConnectionKeeper`, `OutboundSourceFactory`) — `ServiceProvider.Dispose()` **ném** nếu singleton chỉ có `IAsyncDisposable`. Ứng dụng đi đường `DisposeAsync`; cái này là lối thoát cho container. Và đúng **một** chỗ chặn: `App.OnExit` (không thể async), ghi rõ lý do tại chỗ.

#### E1.3 Sở hữu chỉ nằm trong doc, không trong kiểu (WinDivert) — Should

- [ ] **Vị trí**: `IPacketPumpFactory.Create` "pump takes ownership of handle" ([IPacketPumpFactory.cs:10-12](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Interfaces/IPacketPumpFactory.cs#L10-L12)); `ProcessRedirectorFactory` luôn tạo `IDnsCacheLookup` dù `EnableDnsLookup=false` ([ProcessRedirectorFactory.cs:55](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirectorFactory.cs#L55)); 7 dòng `?.Dispose()` tay ([ProcessRedirector.cs:366-372](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L366-L372)); `Func<IReverseDnsTable>` tự viết ([RedirectServiceCollectionExtensions.cs:43-44](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/DependencyInjection/RedirectServiceCollectionExtensions.cs#L43-L44)); `AddTrackedProcessId` ném trước `Start` ([ProcessRedirector.cs:104-108](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L104-L108)); `SocketTracker.Dispose` chờ tuần tự 1s/handle ([SocketTracker.cs:568-572](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L568-L572)).
- **Cách sửa**: `IProcessRedirectorFactory.Create` mở một `IServiceScope` cho session, mọi thứ per-session đăng ký Scoped, `Dispose` = dispose scope (DI đảm bảo thứ tự ngược). Đồng thời giải quyết B11. Queue pid trước `Start` rồi flush; `Dispose` shutdown tất cả rồi `WaitAll` một lần. Rủi ro thấp.

### E2. Trách nhiệm và kích thước (God class)

#### E2.1 `RedirectEngine` 641 dòng, 7 mối quan tâm, không test được — Must (app)

- [x] **Vị trí**: `RedirectEngine.cs` trước khi sửa: vòng đời + lock `:117-279`; reconcile outbound + học IPv6 `:288-300`; cầu tracker↔redirector + rebuild resolver `:304-341`; routing + tunnel TCP + đo thời gian `:348-492`; UDP tầng gói + forward `:525-604`; test outbound tĩnh `:612-638`. Năm field nullable cùng bật/tắt theo run `:63-67`; tracker/forwarder/hostNames được `new` trong `Start` `:157-162`. (Số dòng giữ nguyên như lúc rà soát — file đã đổi hẳn, xem **Đã sửa**.)
- **Vì sao**: `HandleTcpAsync`/`HandleUdpDatagram` chạy trên luồng relay đọc field nullable không qua lock trong khi `Stop` gán null dưới lock; mỗi thành phần theo run thêm một `?.`. Không có unit test nào chạm engine.
- **Cách sửa**: `EngineRun` (sealed, bất biến sau ctor) giữ redirector/tracker/hostNames/udpForwarder/cts/resolver; `RedirectEngine` chỉ còn `EngineRun? _run` + `Start/Stop/ApplyConfig`, handler capture `run` một lần ở đầu hàm. `TcpConnectionRouter` (HandleTcp + TunnelAsync + NoteIpv6Failure) và `UdpFlowRouter` (ShouldRedirectUdpFlow + HandleUdpDatagram) nhận resolver qua `IResolverSource`. `OutboundTester` thay static `TestOutboundAsync` (cùng lúc sửa A1). Nhận `IProcessRuleTrackerFactory`/`IUdpForwarderFactory` qua ctor để test. Thứ tự: rút `EngineRun` (chỉ dời field) → tách hai router → factory. Rủi ro: giữ nguyên thứ tự `Connections.Open/Close` và `_liveConnections.Register`.
- **Đã sửa** (3 commit, nhánh `refactor/engine-run`): `RedirectEngine` 755 → 496 dòng, còn đúng vòng đời + cầu tracker↔redirector + reconcile.
  - [EngineRun.cs](../src/ProxyDivert.Core/Engine/EngineRun.cs) gom redirector/tracker/hostNames/udpForwarder/cts + hai router. Engine còn **một** `volatile EngineRun? _run` ([RedirectEngine.cs:67](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L67)), và `IsRunning` thành `_run is not null` ([:75](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L75)) — không còn cờ thứ hai để lệch với thực tế. Thứ tự dispose trong `Stop` chuyển nguyên vào [EngineRun.DisposeAsync](../src/ProxyDivert.Core/Engine/EngineRun.cs#L92).
  - Ba handler mà `RedirectOptions` gọi tên ([:202-204](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L202-L204)) nay chỉ là một cú chuyển tiếp: lấy run một lần rồi giao việc cho router ([:465-479](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L465-L479)).
  - [TcpConnectionRouter.cs](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs) và [UdpFlowRouter.cs](../src/ProxyDivert.Core/Engine/UdpFlowRouter.cs) đọc bảng định tuyến qua [IResolverSource](../src/ProxyDivert.Core/Engine/Interfaces/IResolverSource.cs) **mỗi** kết nối/gói, nên "lưu xong có hiệu lực từ kết nối kế tiếp" là đúng nghĩa đen chứ không xấp xỉ.
  - [OutboundTester.cs](../src/ProxyDivert.Core/Outbounds/OutboundTester.cs) thay `TestOutboundAsync` static; đăng ký DI ở [ProxyDivertServiceCollectionExtensions.cs:70](../src/ProxyDivert.Core/DependencyInjection/ProxyDivertServiceCollectionExtensions.cs#L70), WPF gọi qua `_services.OutboundTester`. Gỡ luôn mục Nice ở E4.7 ("static tự dựng factory, bỏ qua DI").
- **Sửa thêm** (không nằm trong kế hoạch, lộ ra lúc dời):
  - Run được công bố **trước** `redirector.Start()`. Code cũ gọi `Start()` rồi mới gán `_hostNames`/`_udpForwarder`, để hở đúng cái cửa sổ mà một kết nối vào ngay lúc đó sẽ được định tuyến khi chưa có cả hai.
  - `Start` ném (thường là driver từ chối vì process chưa elevated) thì run dở dang bị dọn ([:151-163](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L151-L163)). Trước đây nó nằm lại trong field với handle vẫn mở, và lần bấm Start sau dựng thêm một cái nữa bên cạnh.
  - `RebuildResolver` khi không còn run thì thôi ([:438](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L438)), thay vì công bố một bảng dựng từ policy map rỗng mà không ai đọc.
- **Khác dự kiến**:
  - **Không** thêm `IProcessRuleTrackerFactory`/`IUdpForwarderFactory`. Hai lớp đó vốn đã dựng được trong test: `ProcessRuleTracker` chỉ cần `ProcessInventory` trên `FakeProcessMachine` (đang dùng ở `ProcessRuleTrackerTests`), `UdpProxyForwarder` chỉ cần một `IProcessRedirector` giả — nay có `FakeProcessRedirector`. Thứ thật sự chặn test là logic định tuyến nằm trong method private của một class không tồn tại nếu chưa nạp driver, và đó chính là phần đã tách. Thêm hai interface ~15 member để thay được cái vốn đã thay được là mua thêm bề mặt mà không được gì.
  - Resolver không nằm thẳng trong `EngineRun` mà sau [ResolverSlot](../src/ProxyDivert.Core/Engine/ResolverSlot.cs): router cần **đọc** bảng mà engine liên tục hoán, còn run lại **giữ** router. Trỏ cả hai vào một slot nhỏ cắt vòng tròn đó mà không phải cho ai một tham chiếu ngược sửa được.
  - `TcpConnectionRouter` nhận `Func<uint, string?>` để hỏi tên tiến trình chứ không nhận tracker: đường định tuyến chỉ cần một nhãn cho bảng connection, lấy cả tracker là buộc routing vào bộ máy tiến trình vì một chuỗi.
  - `OutboundTester` dựng từ **factory** chứ không phải `OutboundTester(registry)` như dòng ghi ở E4.7. Registry là **chủ sở hữu** instance: lấy từ đó thì bấm Test một VPN sẽ chạm đúng tunnel đang chạy, và một lần test hỏng kéo luôn nó xuống.
- **Không đổi hành vi**: thứ tự `Connections.Open/Update/Close` và `_liveConnections.Register/Unregister` giữ nguyên (đã có test khoá lại); mọi câu log giữ nguyên chữ.
- **Còn hở, ghi ra cho rõ**: `RedirectEngine` vẫn 496 dòng và vẫn `new` tracker/forwarder trong `BuildRun` ([:187](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L187)). Phần còn lại là vòng đời + cầu tracker↔redirector, và hai việc gọt tiếp nó đã có tên: E7.1 (`Channel` attach/detach) và E3.3 (detection strategy).
- **Test**: +16 (357 → 373). `UdpFlowRouterTests` 7 fact — trong đó hai cái mà nếu hỏng thì **rò** chứ không phải fail: gói bị Block và gói IPv6 qua outbound không có đường IPv6 đều bị bỏ, không bao giờ trả lại cho relay; cộng một cái khoá đúng ý nghĩa của `IResolverSource` (sửa cấu hình đổi được đường đi của gói **kế tiếp**). `TcpConnectionRouterTests` 5 — kết nối bị chặn không hỏi tới đường ra, IPv6 không tên bị từ chối ngay còn IPv6 **có** tên vẫn được giao cho outbound, và một đường ra mở không được thì để lại lý do trên dòng rồi buông sạch. `OutboundTesterTests` 4 — cái đáng giá khoá đúng thứ từng rò: bấm Test dựng gì thì dọn nấy. Thêm `FakeProcessRedirector` để test định tuyến không cần driver và không cần chạy elevated.

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

- [x] **Vị trí**: cache + signature [OutboundSourceFactory.cs:51-98](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L51-L98); switch IPv6 theo kiểu bê tông [:113-126](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L113-L126); switch theo kind [:154-200](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L154-L200); VPN hai engine [:209-258](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L209-L258); URL parse + DNS đồng bộ [:263-295](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L263-L295). Lớp `sealed`, không interface, `KeptVpnTunnel` nhận thẳng ([KeptVpnTunnel.cs:50](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L50)) nên `VpnConnectionKeeperTests` chỉ test được nhánh thất bại.
- **Cách sửa**: `IOutboundSourceBuilder { OutboundKind Kind; string Signature(Outbound, BuildContext); IOutboundInstance Build(Outbound, BuildContext); }` với `Direct/Http/Socks4/Socks5/VpnSourceBuilder`; `OutboundSourceRegistry` (DI `IEnumerable<IOutboundSourceBuilder>`) chọn builder; cache tách riêng (E1.1). Thứ tự: rút builder sau façade (test `OutboundSourceFactoryTests` vẫn xanh) → tách cache → đổi DI ở [ProxyDivertServiceCollectionExtensions.cs:68](../src/ProxyDivert.Core/DependencyInjection/ProxyDivertServiceCollectionExtensions.cs#L68). Thêm kind SshNet (lib đã có) khi đó chỉ là thêm một builder.
- **Đã sửa**: cùng 2 commit với E1.1. Sáu builder trong [Outbounds/Builders/](../src/ProxyDivert.Core/Outbounds/Builders/), chọn theo `Kind` tại [BuilderFor:87](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L87); factory còn 91 dòng và **không giữ gì**, nên nút Test dựng bản dùng một lần mà không thể chạm vào bản đang chạy. Đăng ký DI ở [ProxyDivertServiceCollectionExtensions.cs:63](../src/ProxyDivert.Core/DependencyInjection/ProxyDivertServiceCollectionExtensions.cs#L63) — thêm kind SshNet giờ đúng nghĩa là thêm một class.
- **Khác dự kiến**:
  - Gộp `OutboundSourceRegistry` (chọn builder) **vào chính** `OutboundSourceFactory` thay vì đẻ thêm một class. Có `OutboundRegistry` (chủ sở hữu instance) rồi, để cạnh nó một `OutboundSourceRegistry` làm việc khác hẳn thì tên gọi tự nó gây hiểu nhầm.
  - Thêm `BlockOutboundBuilder` — kind duy nhất có câu trả lời là "không có gì". Nhờ vậy mọi giá trị của enum đều có một class đứng tên, và lỗi khi ai đó route (thay vì đóng) một connection Block nói đúng chuyện đó, không phải "unknown outbound kind" nghe như thiếu tính năng.
  - `switch` IPv6 theo kiểu bê tông biến mất hẳn: builder giao thẳng `Action<bool>` mà nó biết tên kiểu, nên **compiler kiểm** — `switch` cũ chỉ liệt kê 4 lớp và sẽ im lặng bỏ sót lớp thứ năm. SOCKS4 và wireproxy cố ý không có setter (giao thức không có IPv6 / subprocess chỉ nhận lúc cấu hình).

#### E2.6 `AppServices` chứa chính sách miền, CLI phải chép lại — Should (app)

- [ ] **Vị trí**: [AppServices.cs:29](../src/ProxyDivert.Wpf/Services/AppServices.cs#L29): container, load config, chính sách file log [:92-105](../src/ProxyDivert.Wpf/Services/AppServices.cs#L92-L105), hàng đợi [:259-268](../src/ProxyDivert.Wpf/Services/AppServices.cs#L259-L268), bật engine = `ConnectRoutedVpns` + save + `Start` [:204-213](../src/ProxyDivert.Wpf/Services/AppServices.cs#L204-L213), map detection mode [:133-134](../src/ProxyDivert.Wpf/Services/AppServices.cs#L133-L134). CLI chép [Program.cs:189-192](../src/ProxyDivert.Cli/Program.cs#L189-L192), [Program.cs:210](../src/ProxyDivert.Cli/Program.cs#L210); WPF `Vpn.Sync` lúc khởi động còn CLI không.
- **Cách sửa**: `ProxyDivert.Core/Hosting/ProxyDivertSession : IAsyncDisposable` gom `ProcessInventory` start + chọn event source theo config, `VpnConnectionKeeper.Sync`, `StartAsync/StopAsync/ApplyAsync(config)` với hàng đợi tuần tự bên trong. `AppServices` còn `ConfigStore`, `Config` đang sửa, log path. `Program.cs` dùng cùng session. Cùng lúc chuyển `LaunchSuspended` ([ProcessesViewModel.cs:229-285](../src/ProxyDivert.Wpf/ViewModels/ProcessesViewModel.cs#L229-L285)) thành `SuspendedLaunchService` trong Core (CLI hiện làm cùng việc bằng `AttachProcessId`).

### E3. Đa hình thay cho `switch`

Bảng phán quyết (giữ nguyên: `UdpMode`, `HostMatcherType`, `ProcessMatcherType`, `ArgumentMatcherType`, `ProcessEventSourceKind`, `DnsMode`, `Ipv6Support` vì mỗi enum chỉ switch một chỗ, đầy đủ nhánh, có `default: throw`).

#### E3.1 Loại điều kiện tiến trình: seam đã hứa "một class một dòng" nhưng cần sửa 6 chỗ — Must (app)

- [x] **Vị trí**: lời hứa [ProcessCondition.cs:9-10](../src/ProxyDivert.Core/Routing/Models/Conditions/ProcessCondition.cs#L9-L10); thực tế switch ở [ProcessRuleMatcher.cs:83-90](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L83-L90), [ProcessRuleMatcher.cs:43-55](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L43-L55), [ConditionNodeViewModel.cs:54-61](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L54-L61), [ConditionLeafViewModel.cs:82-94](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionLeafViewModel.cs#L82-L94), [ConditionTextBuilder.cs:69-76](../src/ProxyDivert.Wpf/Helpers/ConditionTextBuilder.cs#L69-L76), enum [ConditionSubject.cs:6-8](../src/ProxyDivert.Core/Routing/Enums/ConditionSubject.cs#L6-L8).
- **Cách sửa**: các class đã có `Clone()` ảo nên model đã mang hành vi; thêm `abstract ConditionResult Evaluate(in ConditionSubject)` vào `ProcessCondition` (giữ [logic ba trạng thái](Glossary-vi.md#L195) trong `ConditionGroup`), `RegexBudgetMs` chung nằm ở `ConditionSubject`. Cho UI: `abstract LeafCondition.Descriptor` (`Subject`, `MatcherValues`, `DefaultMatcher`, `Create`) thay enum `ConditionSubject` + `Matchers`; `ConditionLeafViewModel`/`ConditionTextBuilder` chỉ đọc descriptor. Thứ tự: `Evaluate` trước (29 fact `ProcessRuleMatcherTests` chuyển gần nguyên), descriptor UI sau.
- **Đã sửa** (2026-09-11, nhánh `refactor/condition-evaluate`, 3 commit `3588ec7`, `378c92e`, `c6138e4`). Sáu chỗ phải sửa khi thêm một loại điều kiện nay còn **ba, nằm cạnh nhau**: class lá, một dòng `[JsonDerivedType]`, một mục trong `ConditionSubject` — và test giữ hai danh sách sau khớp nhau.
  - **Đánh giá vào model**: [`ProcessCondition.Evaluate`](../src/ProxyDivert.Core/Routing/Models/Conditions/ProcessCondition.cs#L49) lo độ sâu và `Negate` một lần, rồi gọi [`Answer`](../src/ProxyDivert.Core/Routing/Models/Conditions/ProcessCondition.cs#L74) (`private protected abstract` — tập kiểu nút đóng theo định dạng file, kiểu khai ở assembly khác không bao giờ lưu/nạp được). [`ConditionGroup.Answer`](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionGroup.cs#L28) giữ [logic ba trạng thái](Glossary-vi.md#L195); [`LeafCondition.Answer`](../src/ProxyDivert.Core/Routing/Models/Conditions/LeafCondition.cs#L51) (sealed) giữ luật "ô rỗng = `Ignored`" chung cho mọi lá rồi gọi [`Compare`](../src/ProxyDivert.Core/Routing/Models/Conditions/LeafCondition.cs#L58); các phép so khớp (`Contains`, `StartsWith`, wildcard, regex, chuẩn hoá đường dẫn) là `private protected static` trên `LeafCondition` để lá mới dùng lại.
  - [`ConditionContext`](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionContext.cs#L19) giữ tên/đường dẫn/command line, `RegexBudget` và bộ đếm độ sâu. **Class chứ không struct** như đề xuất `in ConditionSubject`: struct có giá trị `default` không mang ngân sách nào, lọt tới regex là ném giữa lượt quét.
  - [`ProcessRuleMatcher`](../src/ProxyDivert.Core/Processes/ProcessRuleMatcher.cs#L18) chỉ còn `IsMatch` — đúng luật "trong bốn câu trả lời, chỉ `Match` mới bật bộ lọc".
  - **Descriptor cho UI**: enum `ConditionSubject` xoá, thay bằng class [`ConditionSubject`](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionSubject.cs#L22) (xem [descriptor](Glossary-vi.md#L563)) mang `Name`, `Matchers`, `DefaultMatcher`, `Offers`, [`Create`](../src/ProxyDivert.Core/Routing/Models/Conditions/ConditionSubject.cs#L78). [`ConditionLeafViewModel`](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionLeafViewModel.cs#L46), [`ConditionNodeViewModel.FromModel`](../src/ProxyDivert.Wpf/ViewModels/Conditions/ConditionNodeViewModel.cs#L120), [`ConditionTextBuilder`](../src/ProxyDivert.Wpf/Helpers/ConditionTextBuilder.cs#L75) và [`LeafCondition.Clone`](../src/ProxyDivert.Core/Routing/Models/Conditions/LeafCondition.cs#L47) chỉ hỏi subject — hết `switch`.
  - Chữ của subject và gợi ý trong ô nhập tra theo `Name`: key `Enum.ConditionSubject.*` → `Str.Cond.Subject.*`, `Str.Process.TargetHint/ArgumentHint` → `Str.Cond.Hint.*`, qua [`ConditionSubjectTextConverter`](../src/ProxyDivert.Wpf/Converters/ConditionConverters.cs#L24). DataTrigger so với tên enum `"CommandLine"` trong XAML cũng bỏ.
- **Khác dự kiến**:
  - **Tên**: đề xuất gọi struct ngữ cảnh là `ConditionSubject` và descriptor là `LeafCondition.Descriptor`. Làm ngược lại: `ConditionSubject` là **descriptor** — nó chính là khái niệm enum cũ ("dòng này nhìn vào cái gì"), giữ tên nên code đọc y như trước (`ConditionSubject.CommandLine`); ngữ cảnh đánh giá là `ConditionContext`.
  - **`Subject` và `MatcherValue` KHÔNG virtual.** Bản đầu làm `abstract` + `[JsonIgnore]` ở lớp gốc; test round-trip mới bắt được file config bị ghi thêm `"Subject": {...}` trong **mọi** lá — `[JsonIgnore]` trên khai báo abstract không tới được override. Giờ `Subject` nhận qua constructor, matcher đi qua `private protected abstract BoxedMatcher` — serializer không bao giờ đọc member non-public.
  - **Xoá `ProcessRuleMatcher.NeedsCommandLine`** (commit `3588ec7`) thay vì chuyển nó sang đa hình: không ai gọi ngoài test từ khi reader native đọc command line cho mọi tiến trình trong cùng một lời gọi. Kèm sửa remark cũ trên `CommandLineCondition` ("tốn một query WMI").
  - `ProcessRuleMatcher.Evaluate` (public) xoá: remark của nó nói editor dùng để tô màu từng dòng khi thử bộ lọc — **chưa từng có** chỗ nào gọi.
  - `Clone` của lá viết **một lần** trên `LeafCondition` qua `Subject.Create`. Lá nào sau này có thêm field riêng phải tự override, remark ghi rõ.
- **Sửa thêm** — một lỗi thật, ghi ở [A20](#L185): `null` trong cây điều kiện sửa tay làm editor không mở được.
- **Test** (510 → 525: bỏ 2 test chỉ khẳng định `NeedsCommandLine`, thêm 17): `ProcessRuleMatcherTests` +3 (ranh giới độ sâu đúng như cũ — `MaxDepth` khớp, `MaxDepth+1` thành `Unknown`; `null` trong nhóm bị bỏ qua); `ConditionSubjectTests` 11 ca mới (hai danh sách file/editor khớp nhau, `Create`/`Clone` giữ đủ, matcher của subject khác thành mặc định, **`Subject` không bị ghi vào file**, đổi subject trên dòng editor, từ chối `null`, mở-rồi-lưu không đổi); `DataGridColumnBindingTests.Each_condition_row_names_its_subject_and_hints_for_it` mở `ProcessFilterWindow` thật — **đã kiểm ngược**: converter trả cùng một gợi ý cho mọi dòng thì đỏ; `WpfResourceSmokeTests` kiểm hai key `Str.Cond.*` của mọi subject ở cả hai ngôn ngữ.

#### E3.2 `VpnProtocol`: thêm một giao thức = sửa 6 chỗ trong 3 file — Should (app)

- [ ] **Vị trí**: [VpnClientProxySource.cs:181-221](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L181-L221) (switch dial, `Required()` kiểm lúc dial thay vì lúc đọc), [VpnProfileReader.cs:83-94](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L83-L94), [VpnProfileReader.cs:262-279](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L262-L279), [VpnProfileReader.cs:310-332](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L310-L332), [VpnProfile.cs:11-14](../src/ProxyDivert.Core/Vpn/Models/VpnProfile.cs#L11-L14) (union-bag tự nhận), [OutboundSignature.cs:43-49](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L43-L49).
- **Cách sửa**: `abstract class VpnProfile { Protocol; CarriesUdp; Signature(); Task<VpnTunnel> DialAsync(VpnTunnelOptions, ct) }` với `WireGuardFileProfile { Engine = WireProxy|InProcess }`, `OpenVpnFileProfile`, `SstpProfile`, `L2tpIpsecProfile`, `Ikev2Profile`, `SoftEtherProfile`; ctor bắt buộc tham số nên validate lúc `Read`. `VpnProfileReader.Read` chỉ chọn subclass; `RunsOnWireProxy(protocol, url)` giữ static (không đụng đĩa). `VpnClientProxySource` nhận `Func<CancellationToken, Task<VpnTunnel>>` = seam test. Cùng lúc giải quyết C6.

#### E3.3 `ProcessDetectionMode`: một mode = 3 nút vặn ở 3 nơi — Should (app)

- [ ] **Vị trí**: [RedirectEngine.cs:208-210](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L208-L210), [RedirectEngine.cs:158](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L158), [AppServices.cs:136-137](../src/ProxyDivert.Wpf/Services/AppServices.cs#L136-L137), [Program.cs:190-191](../src/ProxyDivert.Cli/Program.cs#L190-L191), `SettingsViewModel.ApplyEventSource`.
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

- [x] **Vị trí**: [KeptVpnTunnel.cs:133-147](../src/ProxyDivert.Core/Vpn/KeptVpnTunnel.cs#L133-L147) pattern-match `WireGuardProxySource` để bọc `WireProxyKeptTunnel` ([WireProxyKeptTunnel.cs:8-13](../src/ProxyDivert.Core/Vpn/WireProxyKeptTunnel.cs#L8-L13)), trong khi lib đã cố ý phơi `IsRunning/StartAsync/Exited/Socks5Endpoint` ([WireGuardProxySource.cs:46-83](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L46-L83)); `OpenSshProxySource` (ControlMaster) và `SshNetProxySource` (`EnsureConnectedAsync`) cũng là tunnel giữ được nhưng keeper lọc `Kind != Vpn` ([VpnConnectionKeeper.cs:85](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L85)). Bốn bộ [backoff](Glossary-vi.md#L109) chồng nhau: keeper, driver VpnClient (vô hạn), `WireGuardOptions.AutoRestart`, Reverse.
- **Cách sửa**: lib core thêm `IManagedProxySource : IProxySource { bool IsRunning; string Endpoint; Task StartAsync(ct); Task<string> WaitUntilDownAsync(ct); }` với hợp đồng "source tự reconnect, host chỉ retry lần dial đầu + hiển thị trạng thái" (giữ ranh giới đúng của `IKeptTunnel` hiện tại, xem [driver tự kết nối lại](Glossary-vi.md#L125)); `WireGuardProxySource`, `OpenSshProxySource`, `SshNetProxySource` implement. App xoá `WireProxyKeptTunnel`, `Resolve()` = `source as IManagedProxySource ?? throw`, keeper lọc theo interface thay vì `Kind`. Đi cùng C2 (thoát khỏi retry vô hạn).
- **Đã sửa**: 2 commit (submodule Proxy `feat(sources): make "a way out that stays up" a library contract`; app `refactor(outbounds): let a way out say for itself that it stays up`).
  - Lib: [IManagedProxySource.cs:27](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IManagedProxySource.cs#L27). `WaitUntilDownAsync` của wireproxy là đoạn code **chuyển từ app xuống** — sự kiện `Exited` + poll 15s dự phòng + dòng stderr cuối làm lý do.
  - App: xoá hẳn `IKeptTunnel.cs` và `WireProxyKeptTunnel.cs`. [`IOutboundInstance.Tunnel`](../src/ProxyDivert.Core/Outbounds/IOutboundInstance.cs#L43) giờ **tính ra** (`Source as IManagedProxySource`) chứ builder không truyền vào nữa ⇒ instance wireproxy **chính là** tunnel, test khẳng định bằng `Assert.Same`.
  - Keeper hết lọc `Kind == Vpn`: [`IOutboundSourceBuilder.BuildsManagedSource`](../src/ProxyDivert.Core/Outbounds/Builders/IOutboundSourceBuilder.cs#L30) → [`OutboundRegistry.CanBeKeptConnected`](../src/ProxyDivert.Core/Outbounds/OutboundRegistry.cs#L91). Phải trả lời **không cần build**, vì build một VPN chính là dial nó — nên hỏi builder chứ không hỏi instance. Không có `default` implementation: tác giả builder mới **buộc** phải trả lời.
  - Nút Connect ở tab Outbounds cũng hỏi cùng câu đó qua [`VpnConnectionKeeper.CanKeep`](../src/ProxyDivert.Core/Vpn/VpnConnectionKeeper.cs#L71) (làm ở bước E4.2).
- **Khác dự kiến**:
  - **`OpenSshProxySource` cố ý KHÔNG implement.** `ControlMasterEnabled = UseControlMaster && !IsWindows` ⇒ trên Windows luôn false, và ngay cả nơi khác cũng là tuỳ chọn; khi tắt thì mỗi tunnel là một tiến trình `ssh -W` riêng, **không có gì đang chạy giữa các request**. Source trả lời "tôi đang chạy" chỉ vì không có gì có thể chết sẽ khiến host đi giám sát một thứ không tồn tại và hiện "Connected" trong UI.
  - `SshNetProxySource` thì có: nó giữ một session đã xác thực thật. `WaitUntilDownAsync` **poll trạng thái** chứ không chờ sự kiện (SSH.NET không bắn gì khi peer im lặng), và canh **trạng thái của source** chứ không canh một `SshClient` cụ thể — request tới giữa chừng sẽ reconnect, báo hỏng lúc đó là bắt host dựng lại một thứ đã tự lành.
  - C2 không phải làm lại: đã `[x]` từ đợt trước.
- **Không đổi hành vi**: `VpnOutboundBuilder` là builder duy nhất trả `true`, đúng bằng tập mà `Kind == Vpn` chọn.
- **Test**: `OutboundInstanceTests.AVpnOnWireProxy_ComesWithATunnelToHoldOpen` đổi thành `Assert.Same(instance.Source, instance.Tunnel)`; `OutboundRegistryTests` thêm 2 (hỏi được mà không build gì — khẳng định `builder.Builds` rỗng; kind lạ trả `false` chứ không ném). Test double đổi hình: `FakeKeptTunnel` → `FakeManagedProxySource : FakeProxySource, IManagedProxySource`, và ctor nhận managed source **tự bật** `BuildsManagedSource` nên hai câu trả lời không lệch nhau được.

#### E4.2 Cờ `IsSupportUdp/Ipv6/Bind` thay cho interface; hai nguồn sự thật cho khả năng — Should (lib Proxy + app)

- [x] **Vị trí**: [IProxySource.cs:5-33](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IProxySource.cs#L5-L33); server chỉ đọc 2/3 cờ; `IsSupportIpv6` chỉ có tác dụng ở `LocalProxySource` (D5); app switch trên kiểu bê tông [OutboundSourceFactory.cs:113-126](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L113-L126) nên SshNet/OpenSsh/Reverse rơi vào no-op; model đoán lại khả năng từ `Kind`+URL ở [Outbound.cs:69-79](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L69-L79) và gọi ngược `VpnProfileReader` (model routing phụ thuộc Vpn); Reverse khai `IsSupportUdp` từ Hello nhưng `ReverseUdpAssociateSource` ném `NotImplementedException` ([ReverseUdpAssociateSource.cs:31-38](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse/Server/ReverseUdpAssociateSource.cs#L31-L38)).
- **Cách sửa**: lib tách `IBindCapable`, `IUdpCapable` (server kiểm `is`), `IAddressFamilyPolicy { bool AllowIpv6 {get;set;} }` chỉ ở source thật sự lọc (Local, VpnClient); cờ cũ `[Obsolete]` một phiên bản. App: `OutboundCapabilities` do builder trả về cùng instance (E1.1), resolver hỏi `registry.Find(id)?.Caps ?? outbound.SupportsUdp`. Đồng thời gọn 6 phép `Kind ==` trong engine thành `RouteDecision.IsDirect/IsBlocked/UsesTunnel`.
- **Đã sửa**: 2 commit (submodule Proxy `feat(sources): ask a way out only what it can actually answer`; app `refactor(outbounds): stop pretending a proxy can be told to skip IPv6`). Xem [capability interface](Glossary-vi.md#L452).
  - Lib: [`IProxySource`](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IProxySource.cs#L31) còn đúng `GetConnectSourceAsync`. [`IUdpCapable`](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IUdpCapable.cs#L18) và [`IBindCapable`](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IBindCapable.cs#L13) mang **cả cờ lẫn hàm factory** nên không thể cài cái này thiếu cái kia; bên gọi phải qua hai tầng (`is` = giao thức có, cờ = upstream cụ thể có chịu). [`IAddressFamilyPolicy.AllowIpv6`](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Interfaces/IAddressFamilyPolicy.cs#L23) thay `IsSupportIpv6`, chỉ ở `LocalProxySource` và `VpnClientProxySource`. Sáu source bỏ được cặp "cờ false + hàm ném".
  - **Bug thật lòi ra**: công tắc IPv6 mà builder HTTP/SOCKS5 trao cho instance **chưa bao giờ có tác dụng** — không source nào đọc `IsSupportIpv6` ngoài `LocalProxySource`. Qua proxy thì đích được gửi đi dưới dạng tên và upstream mới phân giải, nên phía này không có gì để lọc. Hai builder đó nay **không trao công tắc** và nói rõ lý do; `WireGuardOptions.IsSupportIpv6` cũng xoá vì chỉ chạy tới đúng cái property chết đó.
  - `VpnClientProxySource.IsSupportIpv6` có getter và setter **nghĩa khác nhau** (đọc ra "có route IPv6", ghi vào "user cho phép IPv6") ⇒ tách thành [`AllowIpv6`](../src/ProxyDivert.Core/Vpn/Client/VpnClientProxySource.cs#L65) (chính sách, public) và `CarriesIpv6` (sự thật, private).
  - `UdpProxyForwarder` gọi `GetUdpAssociateSourceAsync` trên `IProxySource` trần; nay kiểm `IUdpCapable` trước và ném thông báo **gọi tên đúng mâu thuẫn** thay vì để `NotSupportedException` chui ra từ trong source.
  - [`RouteDecision.IsDirect/IsBlocked/UsesTunnel`](../src/ProxyDivert.Core/Routing/Models/RouteDecision.cs#L25) + `Outbound.IsDirect/IsBlocked` thay 8 phép so `Kind` ở engine, resolver và nút Connect.
- **Khác dự kiến** (3 điểm):
  - **KHÔNG** để resolver hỏi `registry.Find(id)?.Caps ?? outbound.SupportsUdp`. Câu trả lời khi đó **đổi theo việc instance tình cờ đã dựng hay chưa**, nên cùng một datagram định tuyến khác nhau trước và sau kết nối TCP đầu tiên — tệ hơn hiện trạng, và không sửa được gì vì C6 đã làm hai bên khớp. Thay vào đó: giữ hai câu trả lời nhưng **chốt bằng test** duyệt mọi `Kind` (xem [hai nguồn sự thật](Glossary-vi.md#L456)). `IOutboundInstance.SupportsUdp` vẫn thêm, cho ai đã cầm sẵn instance.
  - **Không** `[Obsolete]` cờ cũ. Trên `IProxySource` thì không được: netstandard2.0 không có default interface member, nên giữ cờ nghĩa là mọi implement vẫn phải viết nó **và** ăn warning — đúng thứ cần bỏ. Còn trên lớp cụ thể thì tên không đổi (`IsSupportUdp`/`IsSupportBind` vẫn nguyên, nay đến từ interface khả năng), nên chẳng có gì để đánh dấu.
  - **Chưa** sửa `ReverseUdpAssociateSource` ném `NotImplementedException`. Đó là tính năng chưa viết, không phải hình dáng interface; `ReverseClientSession` nay implement `IUdpCapable` và vẫn trả lời theo Hello của client. Để lại cho lúc làm Reverse.
- **Còn hở, ghi ra cho rõ** (có từ trước, không phải do đợt này): `Ipv6Support=Disabled` trên outbound proxy/VPN chỉ chặn được **IPv6 literal** (engine từ chối ở `OutboundIpv6Capability`). Khi kết nối có host name thì tên được giao cho upstream và upstream vẫn có thể chọn AAAA — phía này không có tiếng nói. Trước đây điều này bị che vì cái cờ chết trông như đã lọc.
- **Test**: `OutboundInstanceTests` thêm 1 theory 5 ca (Direct/Http/Socks4/Socks5/VPN in-process) + 1 fact (VPN trên wireproxy) khẳng định `Outbound.SupportsUdp == instance.SupportsUdp`. Tổng 357 xanh.

#### E4.3 `RedirectOptions` là túi delegate thay cho interface host — Should (WinDivert)

- [ ] **Vị trí**: 4 delegate [RedirectOptions.cs:33](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L33), [:36](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L36), [:78](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L78), [:121](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L121); consumer implement cả 4 bằng method private trên một class ([RedirectEngine.cs:201-210](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L201-L210)); Demo phải tạo `redirectorRef` gán sau `Create` để handler gọi ngược ([ProxyRedirectorRunner.cs:50-70](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/ProxyRedirectorRunner.cs#L50-L70)); mode chọn bằng null-ness của `Func` ([ISocketTrackerFactory.cs:25-26](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/Interfaces/ISocketTrackerFactory.cs#L25-L26)); sentinel `ProcessId = 0` ([RedirectOptions.cs:23](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/RedirectOptions.cs#L23)) làm `NatEntry.ProcessId = 0` bị stamp lặng lẽ khi tracker miss ([NatRedirectMiddleware.cs:194-196](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L194-L196)).
- **Cách sửa**: `IRedirectHost { HandleTcpAsync(session, conn, ct); UdpVerdict HandleUdp(dg); bool ShouldRedirectUdp(target); }` + `IProcessSelector` + `ProcessScopeKind` enum tường minh; `RedirectOptions` chỉ còn dữ liệu; `InitialProcessIds` thay sentinel 0, bỏ fallback root pid. Giữ overload extension nhận lambda cho Demo.

#### E4.4 Packet model: `WinDivertAddress` rò lên middleware; chữ ký async mời làm sai; `MarkModified` tách rời — Should (WinDivert)

- [ ] **Vị trí**: `PacketContext.Address` là struct native public ([PacketContext.cs:37](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/Models/PacketContext.cs#L37)), `IPacketInjector.Inject(..., in WinDivertAddress)`, magic `IfIdx = 1` ([NatRedirectMiddleware.cs:236-238](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L236-L238)), dựng address inbound trùng ở [NatRedirectMiddleware.cs:362-368](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/NatRedirectMiddleware.cs#L362-L368) và [DnsOverHttpsMiddleware.cs:172-182](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.SecureDns/DnsOverHttpsMiddleware.cs#L172-L182). `IPacketMiddleware.InvokeAsync` trả `Task` nhưng pump `GetAwaiter().GetResult()` ([PacketPump.cs:73](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Pipeline/PacketPump.cs#L73)) và cấm await I/O: consumer NuGet viết `await http.GetAsync()` sẽ treo mạng cả máy. `SetSource/SetDestination` ghi buffer nhưng phải nhớ gọi `MarkModified` riêng ([ParsedPacket.cs:59-69](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/ParsedPacket.cs#L59-L69)); setter trên header view struct không có tác dụng ([TcpHeader.cs:14-18](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Packet/Models/TcpHeader.cs#L14-L18)).
- **Cách sửa**: value type `PacketRoute { Direction; Loopback; InterfaceRef; Family }` với factory `LoopbackOutbound(family)`/`InboundOn(iface)`; `PacketPump` là nơi duy nhất map sang `WinDivertAddress`; struct native `internal`. `void Invoke(PacketContext, PacketDelegate next)` đồng bộ (6 middleware built-in đều đã đồng bộ nên đổi cơ học), `IPacketInjector` là đường duy nhất cho việc chậm. Bỏ setter trên view; ghi qua `ctx.Rewrite(...)` tự đặt `Modified`.

#### E4.5 Redirect phụ thuộc cứng SecureDns/Inspection; không thêm được feature thứ 6 — Should (WinDivert)

- [ ] **Vị trí**: `IReverseDnsTable` trên interface session ([IProcessRedirector.cs:31](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L31)); `PeekableStream` (kiểu Inspection) trên model ([RedirectedTcpConnection.cs:33](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L33)); `ProcessRedirector` new thẳng hai middleware DNS ([ProcessRedirector.cs:253-256](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L253-L256)); `CapturesUdp` hard-code nhu cầu từng feature ([ProcessRedirector.cs:228-229](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L228-L229)) nên middleware user cần UDP khi `Protocols = Tcp` không bao giờ thấy gói UDP; `ConfigureNetworkPipeline` chỉ chèn sau NAT; doc NAT "must run first" mâu thuẫn thứ tự đăng ký thực ([ProcessRedirector.cs:243-259](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/ProcessRedirector.cs#L243-L259)). `DnsCacheLookup` (spawn `ipconfig`) nằm ở core `Flow/` với interface khác tên `IReverseDnsTable` dù cùng mục đích IP→tên.
- **Cách sửa**: `IRedirectFeature { NeedsUdpCapture; Families; Contribute(builder, PipelineStage, ctx) }` với `DnsSniffFeature`, `DohFeature`, `BlockTargetUdpFeature`; `ProcessRedirector` lặp `options.Features` và ghép filter driver từ `NeedsUdpCapture`; `PipelineStage` enum (PreNat/Nat/PostNat) làm ràng buộc thứ tự thành kiểu; `ReverseDns` rời khỏi interface session; `IPeekableStream` ở core. `IAddressNameLookup` chung cho `ReverseDnsTable` và `DnsCacheLookup` (dời sang SecureDns), `CompositeNameLookup`. Hai middleware chỉ phụ thuộc `ISocketTracker` (`Ipv6BlockMiddleware`, `BlockTargetUdpMiddleware`) dời về core làm "per-process firewall". Mẫu để nhân bản: `IHostNameParser` strategy list của Inspection.

#### E4.6 Exception là API nhưng không nhất quán — Should (lib Proxy)

- [ ] **Vị trí**: server chỉ bắt `InitConnectSourceFailedException` ([HttpProxyServer.cs:111](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L111)); Socks5 auth fail ném `Exception` trần ([Socks5ProxySource.BaseTunnel.cs:117](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/Socks5ProxySource.BaseTunnel.cs#L117)); Http ném không message, mất status ([HttpProxySource.ConnectTunnel.cs:44-47](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/HttpProxySource.ConnectTunnel.cs#L44-L47)); `WireGuardException : Exception` ngoài cây `ProxySourceException`; `InitConnectSourceFailedException` không có ctor `(message, inner)` nên app bọc mất inner ([VpnClientConnectSource.cs:54-58](../src/ProxyDivert.Core/Vpn/Client/VpnClientConnectSource.cs#L54-L58)). Hệ quả: `catch (Exception) when (isIpv6 ...)` ([TcpConnectionRouter.cs:186-195](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs#L186-L195)) đánh dấu outbound "IPv4-only" kể cả khi lỗi là 407/auth/proxy chết.
- **Cách sửa**: cây `ProxySourceException` → `UpstreamUnreachableException`, `ProxyHandshakeException { Status }`, `ProxyAuthenticationException`, `DestinationUnreachableException`; mọi lớp có `(message, inner)`; `WireGuardException` vào cây. App: `NoteIpv6Failure` chỉ với `DestinationUnreachableException`/timeout; C8 (dial timeout thành "cancelled") sửa cùng.

#### E4.7 Nhóm Nice (abstraction)

- [ ] `IProcessRedirector` phơi `Nat`, `TcpConnectionOpened/Closed`, `TrackedProcessIds` không consumer nào dùng ([IProcessRedirector.cs:16](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L16), [:48-49](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Interfaces/IProcessRedirector.cs#L48-L49)). Tách `IRedirectDiagnostics`.
- [ ] Marker interface rỗng và dán nhãn sai trong Proxy: `IHttpProxy/ISocks4Proxy/ISocks5Proxy/ISsh/IVpn/IAuthentication`; `LocalProxySource : IHttpProxy`, `WireGuardProxySource : ISocks5Proxy` lộ chi tiết cài đặt ([LocalProxySource.cs:8](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxySources/LocalProxySource.cs#L8), [WireGuardProxySource.cs:15](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Vpn.WireProxyCli/WireGuardProxySource.cs#L15)). Xoá hoặc cho một member thật.
- [ ] `LiveTcpConnectionRegistry.CloseWhereRouteChanged` nhận kiểu bê tông `RoutingPolicyResolver` ([LiveTcpConnectionRegistry.cs:52](../src/ProxyDivert.Core/Engine/LiveTcpConnectionRegistry.cs#L52)); chỉ cần `Func<RouteTarget, RouteDecision>`.
- [x] `RedirectEngine.TestOutboundAsync` static tự dựng factory, bỏ qua DI. **Đã sửa cùng E2.1**: [OutboundTester.cs](../src/ProxyDivert.Core/Outbounds/OutboundTester.cs) lấy factory từ container. Dựng từ **factory** chứ không phải `registry` như dòng này ghi — registry là chủ sở hữu instance, lấy từ đó thì bấm Test một VPN sẽ chạm đúng tunnel đang chạy.
- [ ] Reverse: `event Func<…,Task>` chỉ await delegate cuối ([RawTcpReverseTransportServer.cs:116-118](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.RawTcp/RawTcpReverseTransportServer.cs#L116-L118)); AspNetCore phải poll `socket.State` mỗi giây vì `IControlChannel` thiếu `Task Closed` ([AspNetCoreReverseTransportServer.cs:42-50](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy.Reverse.Transport.AspNetCore/AspNetCoreReverseTransportServer.cs#L42-L50)).
- [ ] `Singleton` limit toàn process trong Proxy ([Singleton.cs:3-7](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/Singleton.cs#L3-L7)); `LogCritical` cho mọi tunnel fail và header ở `Information` ([ProxyServer.cs:248-251](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServer.cs#L248-L251), [HttpProxyServer.cs:63](../libs/TqkLibrary.Proxy/src/TqkLibrary.Proxy/ProxyServers/HttpProxyServer.cs#L63)) là noise với hàng trăm tunnel/phút.
- [ ] Naming trong WinDivert: 4 "tracker" (`SocketTracker`/`ProcessRuleTracker`/`ConnectionTracker`/`IProcessTreeMonitor`), `AddTrackedProcessId` vs `AddProcess`, file `Ipv4Header.cs` chứa `Ipv4HeaderView`, consumer phải `using` 7 namespace con `.Interfaces/.Enums/.Models` ([RedirectEngine.cs:23-29](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L23-L29)). NuGet nên phẳng namespace.

### E5. Đóng gói và mô hình cấu hình

#### E5.1 Toàn vẹn tham chiếu của cấu hình vá ở 4 nơi — Must (app)

- [x] **Vị trí**: xoá policy sửa `ProcessRule.PolicyIds` tại [RulesViewModel.cs:157-168](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L157-L168); xoá outbound trỏ về Block tại [OutboundsViewModel.cs:174-175](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L174-L175); built-in bị sửa bậy vá lúc load tại [ConfigStore.cs:94-108](../src/ProxyDivert.Core/Configuration/ConfigStore.cs#L94-L108); policy đã xoá nhưng filter còn trỏ thì resolver bỏ qua tại [RoutingPolicyResolver.cs:60-66](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L60-L66). Grid sửa thẳng `Kind/Url/Username` qua TwoWay ([OutboundsView.xaml:149-166](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml#L149-L166)), chặn built-in ở code-behind ([OutboundsView.xaml.cs:17-20](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml.cs#L17-L20)). CLI dựng `AppConfig` tay ([Program.cs:143-150](../src/ProxyDivert.Cli/Program.cs#L143-L150)) không qua phép vá nào.
- **Cách sửa**: `AppConfig` thành [aggregate](Glossary-vi.md#L428): `RemovePolicy(id)`, `RemoveOutbound(id)`, `Normalize()` (gọi từ `Load` và `Clone`); `Outbound.IsBuiltIn` bảo vệ setter hoặc `BuiltInOutbound : Outbound` sealed. VM chỉ gọi aggregate. Cùng lúc giải quyết A10 bằng một `ObservableCollection` chung. Thứ tự: thêm method → chuyển 2 VM → `RestoreBuiltInOutbounds` thành `Normalize` → đổi target 8 fact `ConfigStoreTests`.
- **Đã sửa** (2026-09-10, nhánh `refactor/config-aggregate`, commit `c89edc0`):
  - [AppConfig.RemovePolicy](../src/ProxyDivert.Core/Configuration/Models/AppConfig.cs#L113), [RemoveOutbound](../src/ProxyDivert.Core/Configuration/Models/AppConfig.cs#L132), [Normalize](../src/ProxyDivert.Core/Configuration/Models/AppConfig.cs#L158) — luật toàn vẹn nằm ở object sở hữu **cả hai đầu** của mọi tham chiếu. `Load` và `Clone` gọi `Normalize`; CLI gọi sau khi dựng tay.
  - Hai VM chỉ còn hỏi aggregate rồi làm theo ([RulesViewModel.cs:178](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L178), [OutboundsViewModel.cs:153](../src/ProxyDivert.Wpf/ViewModels/OutboundsViewModel.cs#L153)). `ConfigStore.RestoreBuiltInOutbounds` **xoá hẳn**, chuyển vào `Normalize`.
  - `Normalize` sửa được: built-in bị sửa bậy, built-in **mất khỏi list**, policy trỏ outbound không tồn tại → Block, filter trỏ policy đã xoá, không còn policy nào, và **id trùng** (giữ bản sau — cùng câu trả lời mà mọi bảng dựng-bằng-phép-gán đưa ra).
- **Sửa thêm (không có trong kế hoạch)** — hai lỗi thật:
  - **A16 gạch đầu dòng 1** (`RoutingPolicyResolver` ném khi outbound trùng id) — đã tick, xem A16.
  - Filter mất policy cuối cùng thì **vẫn bắt tiến trình** nhưng không có luật nào ⇒ đi thẳng dưới địa chỉ thật của user. Trước đây chỉ `RulesViewModel` biết luật này; giờ `Normalize` áp cho cả file sửa tay và CLI.
- **Khác dự kiến**:
  - **Không** bảo vệ setter của `Outbound` theo `IsBuiltIn`. Deserializer JSON gán `Id` không đảm bảo trước `Kind`, nên setter tự chặn sẽ chặn nhầm lúc nạp. Chốt chặn đặt ở hai chỗ đúng: `Normalize` sửa file, và [OutboundRowViewModel](../src/ProxyDivert.Wpf/ViewModels/OutboundRowViewModel.cs) từ chối ô nhập (xem E8.1).
  - **Không** làm `ObservableCollection` dùng chung cho A10. A10 đã `[x]` bằng `ReloadAll` lúc đổi tab, và phần *đúng đắn* của nó (xoá policy/outbound để lại tham chiếu treo) chính là thứ aggregate vừa bịt. Danh sách dùng chung chỉ còn giải quyết phần *hiển thị*, mà `ReloadAll` đang làm đủ.
  - `Clone` normalize **bản sao**, không normalize bản gốc: đây là cửa cuối trước engine, còn instance cửa sổ đang sửa thì để nguyên — vá dưới tay user giữa lúc sửa sẽ dời hàng họ đang nhìn.
- **Test**: `AppConfigIntegrityTests` 15 fact/theory (xoá policy/outbound, từ chối policy cuối và built-in, khôi phục built-in, id trùng, filter treo, idempotent, bản engine được vá còn bản cửa sổ thì không). 438 (từ 422).

#### E5.2 `Outbound.Url` là chuỗi 4 nghĩa, parse ở 4 nơi — Should (app)

- [x] **Vị trí**: [Outbound.cs:22-27](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L22-L27); parse tại [OutboundSourceFactory.cs:263-275](../src/ProxyDivert.Core/Outbounds/OutboundSourceFactory.cs#L263-L275), [VpnProfileReader.cs:39-62](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L39-L62), [OutboundSignature.cs:54-70](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L54-L70) (`Expand` viết lại), [Program.cs:291-301](../src/ProxyDivert.Cli/Program.cs#L291-L301) (`KindFromUrl`); `ProxyUriParser` vẫn `internal` trong Demo dù plan ghi "nâng lên thư viện" ([ProxyUriParser.cs:10](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Parsing/ProxyUriParser.cs#L10)).
- **Cách sửa**: [value object](Glossary-vi.md#L432) `OutboundAddress` (`ProxyEndpoint | VpnFile | VpnEndpoint | VpnIniFile`) với `TryParse(kind, raw, out address, out error)`; model giữ string để JSON, thêm `[JsonIgnore] Address` lazy; VM gọi `TryParse` lúc rời ô; factory/signature/CLI/Demo dùng chung.
- **Đã sửa** (2026-09-10, nhánh `refactor/config-aggregate`, commit `7d01a6d`):
  - [OutboundAddress](../src/ProxyDivert.Core/Outbounds/Models/OutboundAddress.cs) + [OutboundAddressKind](../src/ProxyDivert.Core/Outbounds/Enums/OutboundAddressKind.cs) (`ProxyEndpoint | VpnEndpoint | VpnConfigFile | VpnIniFile`). `TryParse(kind, url, protocol, out address, out error)` — có `protocol` vì đúng một ca cần nó: `219.100.37.1:443` là **server** khi user đã chọn giao thức quay số, là **đường dẫn file** khi chưa.
  - `Outbound.Address` cache một lần; setter của `Kind`/`Url`/`VpnProtocol` **vứt cache** ([Outbound.cs:53](../src/ProxyDivert.Core/Routing/Models/Outbound.cs#L53)). Cần cache vì `SupportsUdp` hỏi mỗi kết nối. Cache là **một reference** giữ câu trả lời, không phải "giá trị + cờ đã-đọc": hai field có thể được công bố theo thứ tự bất kỳ.
  - Bốn chỗ đọc cũ giờ đọc chung: [OutboundUrl.Parse](../src/ProxyDivert.Core/Outbounds/Builders/OutboundUrl.cs#L21), [VpnProfileReader.Read](../src/ProxyDivert.Core/Vpn/VpnProfileReader.cs#L42), [OutboundSignature](../src/ProxyDivert.Core/Outbounds/OutboundSignature.cs#L58), `KindFromUrl` của CLI. `VpnProfileReader.Expand` xoá, `RunsOnWireProxy` có thêm overload nhận `OutboundAddress` để đường định tuyến không parse lại.
- **Sửa thêm (không có trong kế hoạch)** — hai thứ nhỏ nhưng thật:
  - Proxy SOCKS **thiếu cổng** trước đây nhận ở ô nhập rồi mới từ chối ở kết nối đầu tiên, lúc user đã đang đợi. Giờ từ chối ngay tại ô.
  - `OutboundSignature` stamp file cho **mọi** outbound VPN, kể cả loại ô nhập là địa chỉ (`sstp://...`) và không nêu file nào — `new FileInfo("sstp://…")` ném rồi bị nuốt thành `"?"` mỗi lần lưu.
- **Khác dự kiến**:
  - `OutboundAddress` chỉ trả lời **ô nhập LÀ cái gì**. Quyết định *làm gì với nó* (scheme nào là giao thức nào, file nào wireproxy được chạy, host name thành endpoint ra sao) ở nguyên chỗ cũ — nếu chuyển vào đây thì lại có hai nơi cùng quyết định.
  - **Không chạm đĩa** trong `TryParse`. `SupportsUdp` hỏi mỗi kết nối; kiểm file có tồn tại không là việc của reader lúc mở, và của row VM lúc sửa ô (E8.1).
  - `ProxyUriParser` trong Demo của WinDivert **chưa** nâng lên thư viện: đó là submodule khác, chỉ Demo dùng, không ảnh hưởng app. Để lại cho đợt 4.
- **Test**: `OutboundAddressTests` 29 ca (scheme suy từ kind, giữ scheme đã gõ, IPv6 bỏ ngoặc, SOCKS thiếu cổng, http lấy cổng mặc định, hub SoftEther, đường dẫn thắng giao thức quay số, `.vpn` vs `.conf`, biến môi trường, dấu nháy, cache theo `Url`/`Kind`/`VpnProtocol`, `TryReadProxyKind`). 438 → 467.

#### E5.3 `RoutingRule.Pattern` parse lại mỗi kết nối; resolver trộn phần tĩnh với phần động — Should (app)

- [x] **Vị trí**: [HostMatcher.cs:102-108](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L102-L108) (`Split('/')` + `IPAddress.TryParse` mỗi lần), [HostMatcher.cs:138-150](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L138-L150), `_regexCache` tĩnh không xoá [HostMatcher.cs:25](../src/ProxyDivert.Core/Routing/HostMatcher.cs#L25); `Resolve` chạy `Where().OrderBy()` mỗi kết nối [RoutingPolicyResolver.cs:92](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L92); resolver dựng lại mỗi attach/detach pid ([RedirectEngine.cs:406](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L406), [RedirectEngine.cs:420](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L420)) vì ôm cả `policiesByProcessId`: 60 tab Chrome = 60 lần copy dictionary.
- **Cách sửa**: `CompiledRuleSet` (bất biến, dựng một lần mỗi config: rule đã parse thành `IHostPredicate`, sắp theo Order) + `ProcessPolicyMap` (ConcurrentDictionary do tracker sở hữu); `RoutingPolicyResolver(ruleSet, policyMap)` không cần rebuild khi pid đổi (giải quyết A4 và A8). Pattern sai phát hiện lúc compile, báo ở tab Rules. Giữ thứ tự "policy đầu tiên quyết UDP". 11 test `HostMatcherTests` giữ qua adapter.
- **Đã sửa** (2026-09-09, nhánh `refactor/compiled-rules`, commit `1eb59bf` + `f338518` + `d0d8983`):
  - Thư mục mới `src/ProxyDivert.Core/Routing/Compiled/`: [IHostPredicate](../src/ProxyDivert.Core/Routing/Compiled/IHostPredicate.cs), [HostPredicate.Compile:34](../src/ProxyDivert.Core/Routing/Compiled/HostPredicate.cs#L34) (5 predicate: name / regex / cidr / port / protocol, cộng `Never`), [CompiledRule](../src/ProxyDivert.Core/Routing/Compiled/CompiledRule.cs), [CompiledPolicy](../src/ProxyDivert.Core/Routing/Compiled/CompiledPolicy.cs), [CompiledRuleSet.Compile:41](../src/ProxyDivert.Core/Routing/Compiled/CompiledRuleSet.cs#L41), [RulePatternError](../src/ProxyDivert.Core/Routing/Compiled/RulePatternError.cs).
  - Rule `IsEnabled=false` bị **loại lúc compile**, phần còn lại sắp theo `Order` một lần. `Resolve` chỉ còn duyệt mảng ([RoutingPolicyResolver.cs:89](../src/ProxyDivert.Core/Routing/RoutingPolicyResolver.cs#L89)).
  - CIDR **mask ngay lúc compile**, nên `10.1.2.3/8` nghĩa đúng như `10.0.0.0/8` chứ không phụ thuộc bên nào được mask lúc so.
  - `_regexCache` tĩnh không xoá **đã bỏ hẳn**. `HostMatcher` còn lại đúng một hàm adapter compile-rồi-so, không nằm trên đường kết nối nữa — 11 test cũ giữ nguyên.
- **Sửa thêm (không có trong kế hoạch)** — hai lỗi thật, đã tách thành mục riêng ở phần A để tra được từ danh sách bug:
  - **A18**: `ResolveUdp` đọc bảng pid hai lần ⇒ thiết lập của policy này ghép rule của policy kia. Lỗi do chính thay đổi này sinh ra (bảng pid chuyển từ chụp đông cứng sang đọc sống).
  - **A19**: hai policy trùng `Id` làm `ToDictionary` ném ngay trong `StartAsync` ⇒ máy không có redirect nào.
  - (Dọn, không phải lỗi) `RedirectEngine._config` sau khi bỏ `RebuildResolver` **chỉ còn được ghi, không ai đọc**. Đã xoá field và cả hai lệnh gán, bớt luôn một lần `lock (_stateLock)`.
- **Khác dự kiến**:
  - **Không** có `ProcessPolicyMap` do tracker sở hữu như kế hoạch ghi. Một `ConcurrentDictionary` thứ hai đặt cạnh `_tracked` là thêm một thứ phải giữ đồng bộ ở **sáu** chỗ `_tracked` thay đổi, trong đó hai chỗ (`ReconcileTrackedWithRules` sửa tại chỗ, `FollowParents` dời con theo cha) không raise event nào. Thay vào đó `ProcessRuleTracker` **tự** implement [IProcessPolicySource.TryGetPolicyIds:87](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L87) đọc thẳng `_tracked` ⇒ không có bản sao nào để lệch. `BuildPolicyMap()` xoá hẳn.
  - `ProcessPolicyMap` vẫn tồn tại nhưng là bản đứng riêng cho test và cho ctor nhận dictionary ([ProcessPolicyMap.cs](../src/ProxyDivert.Core/Routing/ProcessPolicyMap.cs)); engine chạy thật không dùng.
  - Outbound **không** chuyển vào `CompiledRuleSet`: nó là tập hợp luật, còn outbound là thứ khác. Ctor là `(ruleSet, outbounds, policySource, fallback?)`.
  - **Cố ý không** đặt `matchTimeout` cho regex người dùng. Timeout biến một pattern chậm thành luật **âm thầm ngừng áp dụng khi máy bận** — đúng kiểu hỏng mà ngân sách regex của luật tiến trình đã trả giá một lần (xem A17).
- **Thứ tự đổi ở `ApplyConfigAsync`**: bảng định tuyến publish **trước** `ApplyRules` ([:273](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L273)). `ApplyRules` phát cho từng tiến trình các policy id của cấu hình đang lưu, nên kết nối rơi vào giữa chừng phải gặp bảng đã biết các id đó; làm ngược lại thì nó đi theo policy chưa có trong bảng ⇒ phân giải ra rỗng ⇒ đi thẳng.
- **Báo ở tab Rules**: `RulesViewModel` chạy **đúng** `CompiledRuleSet.Compile` mà engine chạy, lúc nạp và sau mỗi lệnh có lưu ([RulesViewModel.cs:84](../src/ProxyDivert.Wpf/ViewModels/RulesViewModel.cs#L84)); khối cảnh báo ở [RulesView.xaml:96-105](../src/ProxyDivert.Wpf/Views/RulesView.xaml#L96-L105), chuỗi `Str.Rules.PatternProblems` có cả en lẫn vi. Engine cũng log một lần mỗi lần dựng bảng ([:455](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L455)).
- **Không đổi hành vi**: thứ tự "policy đầu tiên quyết UDP", "rule khớp đầu tiên thắng", policy bị xoá thì bỏ qua chứ không giả, hai built-in luôn phân giải được.
- **Còn hở, ghi ra cho rõ**: luật có `IsNot` mà pattern hỏng **claim tất cả** (predicate `Never` bị đảo). Đây là hành vi cũ, giữ nguyên và **chốt bằng test** `AnInvertedRuleWithAnUnusablePattern_ClaimsEverything` — lý do nó chấp nhận được là giờ đã có chỗ báo. Ô nhập vẫn chưa validate lúc rời ô (việc của E5.2).
- **Test**: `CompiledRuleSetTests` 13 fact/theory (pattern hỏng từng loại, disabled bị loại, sắp theo Order, CIDR lệch biên, dải cổng viết ngược, trùng Id policy) và `RoutingFollowsProcessesTests` 4 fact (bảng dựng một lần vẫn theo kịp attach / sửa bộ lọc / tiến trình thoát / con thừa kế cha). 373 → 390.

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

- [ ] **Vị trí**: [Demo/UdpProxyForwarder.cs:19-171](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/UdpProxyForwarder.cs#L19-L171) vs [UdpProxyForwarder.cs:21-188](../src/ProxyDivert.Core/Engine/UdpProxyForwarder.cs#L21-L188) (cùng `PortTunnel`, cùng `GetAwaiter().GetResult()` khi inject); [ProxyRedirectorRunner.cs:186-198](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Demo/Running/ProxyRedirectorRunner.cs#L186-L198) vs [TcpConnectionRouter.cs:140-212](../src/ProxyDivert.Core/Engine/TcpConnectionRouter.cs#L140-L212); `RelayDirectAsync` I/O nằm trong model ([RedirectedTcpConnection.cs:71-108](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert.Redirect/Models/RedirectedTcpConnection.cs#L71-L108)).
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

- [x] **Vị trí**: [ProcessInventory.cs:413-424](../src/ProxyDivert.Core/Processes/ProcessInventory.cs#L413-L424) `Raise` trên luồng pump → [ProcessRuleTracker.cs:361-367](../src/ProxyDivert.Core/Processes/ProcessRuleTracker.cs#L361-L367) → [RedirectEngine.cs:401-413](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L401-L413) mở handle driver + `RebuildResolver`; `IProcessEventSource` yêu cầu "handlers must be short" ([IProcessEventSource.cs:38-39](../src/ProxyDivert.Core/Processes/Interfaces/IProcessEventSource.cs#L38-L39)) nhưng chuỗi này vi phạm; `ApplyConfig` giữ `_stateLock` khi `_tracker.ApplyRules` raise event.
- **Cách sửa**: tracker đẩy `(pid, attach/detach)` vào `Channel<T>`, một consumer làm việc với redirector ([single-writer](Glossary-vi.md#L444)); E5.3 xoá `RebuildResolver`; `EngineRun` bất biến (E2.1) cho phép thu hẹp lock xuống chỉ phép swap `_run`. Giải quyết A3, A4, A8.
- **Đã sửa** (2026-09-09, nhánh `refactor/compiled-rules`, commit `b51d8ee`):
  - [TrackedPidQueue](../src/ProxyDivert.Core/Engine/TrackedPidQueue.cs) — `Channel` unbounded, `SingleReader = true`, một consumer ([:81](../src/ProxyDivert.Core/Engine/TrackedPidQueue.cs#L81)). `AddTrackedProcessId` **không phải** ghi sổ: ngoài chế độ machine-wide nó mở một handle WinDivert SOCKET cho tiến trình đó và bật một pump riêng.
  - Hai handler còn đúng hai dòng ([RedirectEngine.cs:441](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L441), [:447](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L447)): đẩy pid vào hàng đợi rồi forward event ra app. Không `try/catch` nữa — pid nào hỏng thì consumer log, vì lúc đó không còn ai để ném.
  - Thứ tự được giữ: hàng đợi FIFO một reader ⇒ attach rồi detach cùng một pid không thể tới ngược.
- **Khác dự kiến — hàng đợi thuộc engine, không thuộc tracker**: sự kiện của tracker là hợp đồng của nó, và thứ phải rời luồng pump là công việc **với driver**, tức việc của engine. Để ở tracker thì `Start()` phải thành async chỉ để giữ đúng thứ tự lúc khởi động.
- **Cái giá phải trả, và ba chỗ phải trả**: dời việc khỏi một luồng thì ai cần nó xong trước khi đi tiếp phải nói ra. `DrainAsync` bỏ một mốc vào hàng đợi rồi chờ reader chạm tới ([:55](../src/ProxyDivert.Core/Engine/TrackedPidQueue.cs#L55)):
  - `StartAsync` ([:161](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L161)) — trình duyệt đang mở sẵn trước khi bật redirect, nếu không chờ thì nó cứ đi thẳng suốt quãng 60 handle mở xong.
  - `ApplyConfigAsync` ([:279](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L279)) — `ResetEscapedFlows` ngay sau đó đọc bảng flow của chính driver, mà tiến trình vừa được nhận trong lần lưu này thì chưa có flow nào trong đó cho tới khi handle mở.
  - Luồng launch-suspended: `AttachProcessId` → `AttachProcessIdAsync` ([:322](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L322)) và `ForceProcessScan` → `ForceProcessScanAsync` ([:337](../src/ProxyDivert.Core/Engine/RedirectEngine.cs#L337)). Dòng ngay sau lời gọi ở cả CLI lẫn WPF là `Resume()`, và một tiến trình chạy trước khi handle của nó mở sẽ bắn SYN đầu tiên lọt ra ngoài.
- **Vòng đời**: `EngineRun.DisposeAsync` dispose hàng đợi **giữa** tracker và redirector ([EngineRun.cs:103](../src/ProxyDivert.Core/Engine/EngineRun.cs#L103)) — pid còn trên đường không được rơi vào handle đã đóng.
- **Chưa làm, cố ý**: lock **chưa** thu hẹp thêm. `_stateLock` giờ chỉ còn bọc lúc publish/thu hồi `_run` (Start/Stop) — `ApplyConfig` đã không giữ nó khi `ApplyRules` raise event nữa, đó chính là phần E7.1 nhắm tới; phần còn lại của `_stateLock` đúng như E2.1 để lại. Sự kiện `ProcessAttached`/`ProcessDetached` **vẫn** bắn ra app trên luồng pump: hai subscriber đều rẻ (`RateGate.Request` ở WPF, `Console.WriteLine` ở CLI).
- **A8 chỉ xong một nửa** — xem ghi chú ở A8.
- **Test**: `TrackedPidQueueTests` 6 fact (đúng thứ tự; `Attach` trả về ngay khi driver còn đang kẹt; `DrainAsync` chờ tới driver chứ không chỉ tới hàng đợi; một pid hỏng không chặn pid sau; thứ đã xếp hàng vẫn tới driver lúc dừng; drain một hàng đợi đã dừng không treo). 390 → 396.

#### E7.2 Sự kiện `ISocketTracker` bắn từ 3 ngữ cảnh thread mà interface không nói — Should (WinDivert)

- [ ] **Vị trí**: từ socket pump [SocketTracker.cs:517](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L517), từ thread gọi `RemoveProcess` (UI) [SocketTracker.cs:334](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L334), từ NETWORK pump qua reconcile [SocketTracker.cs:374](../libs/TqkLibrary.WinDivert/src/TqkLibrary.WinDivert/Flow/SocketTracker.cs#L374); `ISocketTracker` không có ghi chú thread trong khi `ITcpRelayServer` có; `ProcessRedirector` forward event ra host nên host chậm sẽ chặn pump.
- **Cách sửa**: sau E2.2, mọi mutation đi qua một `Channel<FlowMutation>` do thread sở hữu `FlowTable`; event bắn từ đúng một thread; doc một dòng. Lợi ích chính là hợp đồng rõ, không phải hiệu năng.

### E8. Tầng MVVM

#### E8.1 Model POCO bind thẳng vào DataGrid, VM giữ hai bản danh sách — Should (app)

- [x] **Vị trí**: model không có `INotifyPropertyChanged` → mẹo re-insert, `OnPropertyChanged(nameof(IsRunning))` thủ công; grid ghi trực tiếp vào model mà engine sẽ clone ([OutboundsView.xaml:149-166](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml#L149-L166)) nên không có chỗ validate (E5.2) hay chặn built-in (E5.1); 3 `MultiBinding` tra `VpnTunnels` theo `Id` ([VpnConverters.cs:21-59](../src/ProxyDivert.Wpf/Converters/VpnConverters.cs#L21-L59)) là dấu hiệu thiếu row VM.
- **Cách sửa**: `OutboundRowViewModel`/`RuleRowViewModel`/`FilterRowViewModel` (ObservableObject bọc model, `Commit()` ghi về aggregate, `Validate()`); `Rows.Sync(config.X, m => new RowVm(m))` helper chung; `OutboundRowViewModel.Tunnel` observable thay converter. [BindingProxy](Glossary-vi.md#L141) là giới hạn WPF thật, giữ.
- **Đã sửa** (2026-09-10, nhánh `refactor/config-aggregate`, commit `69e1ebb` + `64be057` + `3ba1d95`): **bốn** row VM, không phải ba.
  - [OutboundRowViewModel](../src/ProxyDivert.Wpf/ViewModels/OutboundRowViewModel.cs) — ghi xuyên xuống model (cấu hình vẫn là danh sách duy nhất, Save vẫn đưa engine một bản chụp), **từ chối** mọi ô của Direct/Block, và báo `AddressProblem` ngay trên ô đã gõ ([OutboundsView.xaml:76](../src/ProxyDivert.Wpf/Views/OutboundsView.xaml#L76)). `Tunnel` chuyển lên hàng ⇒ `VpnTunnelForOutboundConverter` **xoá hẳn**, `VpnConnectButtonTextConverter` còn nhận đúng một `bool`.
  - [PolicyRowViewModel](../src/ProxyDivert.Wpf/ViewModels/PolicyRowViewModel.cs) + [RuleRowViewModel](../src/ProxyDivert.Wpf/ViewModels/RuleRowViewModel.cs) — tab Rules. Hàng luật tự trả lời `PatternProblem` bằng **đúng** `HostPredicate.Compile` mà engine chạy.
  - [ProcessFilterRowViewModel](../src/ProxyDivert.Wpf/ViewModels/ProcessFilterRowViewModel.cs) — tab Processes; `Refresh()` thay mẹo gỡ-ra-cắm-lại sau khi đóng cửa sổ sửa điều kiện.
- **Sửa thêm (không có trong kế hoạch)** — một lỗi thật, chính là gạch đầu dòng "Đổi tên policy làm mất `SelectedRule`" trong A16 (đã tick ở đó): gỡ hàng ra khỏi collection làm **ListBox** bỏ chọn nó, bỏ chọn policy thì bảng luật bên dưới bị xoá, và luật user đang chọn biến mất trước khi tên mới hiện ra. Test `Renaming_a_policy_leaves_the_rule_the_user_had_picked_alone` phải dựng ListBox **thật** mới tái hiện được — collection không ai nhìn thì không bỏ chọn gì cả; **đã kiểm ngược** bằng cách dựng lại cách cũ, test đỏ.
- **Khác dự kiến**:
  - **Không** có `Commit()`/`Validate()` như kế hoạch ghi. Hàng ghi thẳng xuống model chứ không giữ bản nháp: cấu hình đã có sẵn ba tầng (cửa sổ / bản chụp engine / file), thêm tầng thứ tư trong từng hàng là thêm một chỗ có thể lệch. `Validate()` thành hai property đọc được (`AddressProblem`/`HasAddressProblem`, `PatternProblem`/`HasPatternProblem`) vì cái UI cần là *hiển thị*, không phải *chặn*.
  - **Không** làm `Rows.Sync`. Hàng không giữ trạng thái nào đáng cứu qua một lần nạp lại — trừ `Tunnel`, mà cái đó lấy lại từ keeper — nên helper chỉ đổi một lần dựng lại rẻ tiền lấy câu hỏi "hàng có nhận ra không" ở mọi chỗ gọi.
  - Tên built-in **vẫn không sửa được** trên lưới (code-behind huỷ edit như cũ). `Normalize` không đụng tên, nên tên đặt tay trong JSON vẫn sống — hai chuyện khác nhau, giữ nguyên cả hai.
- **Test**: `OutboundRowViewModelTests` 11, `ProcessFilterRowViewModelTests` 3, `PolicyRenameTests` +1; `DataGridColumnBindingTests`/`PolicyRenameTests`/`ProcessRuleDragTests` đổi sang bind row VM. 467 → 482.

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
- [x] `RedirectEngine` `new` tracker/forwarder trong `Start` → E2.1 factory. **Không làm factory**: cả hai vốn đã dựng được trong test (`ProcessRuleTracker` cần `ProcessInventory` trên `FakeProcessMachine`, `UdpProxyForwarder` cần một `IProcessRedirector` giả — nay có [FakeProcessRedirector.cs](../src/ProxyDivert.Core.Tests/FakeProcessRedirector.cs)). Thứ chặn test là logic định tuyến nằm trong method private của engine, và nó đã ra thành hai router có test riêng. Xem E2.1 **Khác dự kiến**.
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
| 1 ✅ | E1.2 `IAsyncDisposable` xuyên chuỗi (lib Proxy → app) | Mọi refactor vòng đời sau đó đều dựa vào nó; sửa lớp lỗi "Save đơ" |
| 2 ✅ | E1.1 + E2.5 `IOutboundInstance` + `OutboundRegistry` + builder per kind | Một chủ cho `IProxySource`; seam để test keeper |
| 3 ✅ | E4.1 `IManagedProxySource` vào lib, xoá `WireProxyKeptTunnel`; E4.2 capability interface | Keeper và engine hết đoán kiểu bê tông; cùng lúc chốt C2 |
| 4 ✅ | E2.1 `EngineRun` + hai router + `OutboundTester` | Chạm cùng vùng với 2-3, làm rời sẽ conflict |
| 5 ✅ | E5.3 `CompiledRuleSet` + `ProcessPolicyMap`; E7.1 `Channel` attach/detach | Xoá `RebuildResolver`, hết A4/A8, thu hẹp lock |
| 6 ✅ | E5.1 aggregate `AppConfig`; E8.1 row ViewModel; E5.2 `OutboundAddress` | Bịt gốc A10 và toàn vẹn cấu hình; validate tại ô nhập |
| 7 ✅ | E3.1 `ProcessCondition.Evaluate` + descriptor | Seam đã hứa, độc lập với 1-6 |
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

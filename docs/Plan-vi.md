# Kế hoạch ProxyDivert

Tool WPF chuyển hướng gói tin của tiến trình được chọn sang proxy (HTTP / SOCKS4 / SOCKS5) theo luật domain hoặc IP; đích không khớp luật thì đi thẳng. Giai đoạn sau cắm thêm VPN dưới dạng một loại đường ra.

Thuật ngữ: [docs/Glossary-vi.md](Glossary-vi.md). Ngày lập: 2026-09-03.

## 1. Phạm vi đã chốt

- **Giai đoạn 1 chỉ làm proxy.** VPN (TqkLibrary.VpnClient) làm sau, dưới dạng một [IProxySource](Glossary-vi.md#L41) như mọi proxy khác. Được phép thiết kế lại VpnClient nếu kiến trúc khó dùng.
- **Ba thư viện nhúng dạng [git submodule](Glossary-vi.md#L45)** chỉ để tiện sửa tại chỗ khi cần, không cần cơ chế đặc biệt:
  - `https://github.com/tqk2811/TqkLibrary.WinDivert` (bản local `D:\IT\Csharp\Libraries\TqkLibrary.WinDivert`)
  - `https://github.com/tqk2811/TqkLibrary.Proxy` (bản local `D:\IT\Csharp\Libraries\TqkLibrary.Proxy`)
  - `https://github.com/tqk2811/TqkLibrary.VpnClient` (bản local `D:\IT\Csharp\Libraries\TqkLibrary.Vpn`)
- Yêu cầu chức năng:
  1. Theo dõi tiến trình mở/đóng, lọc theo tên exe/đường dẫn, tự áp [WinDivert](Glossary-vi.md#L5) lên tiến trình khớp.
  2. Chuyển hướng gói tin của tiến trình đó sang proxy theo luật domain hoặc IP. Không khớp luật thì direct.

## 2. Kết quả khảo sát ba thư viện

### 2.1 TqkLibrary.WinDivert (lõi chuyển hướng, gần đủ)

- TFM `net462;net6.0-windows;net8.0-windows`, gói duy nhất `Native.WinDivert` (chỉ có native **win-x64**). Không nuspec, không GitVersion, dùng `src/ProjectBuildProperties.targets` import tay và [CPM](Glossary-vi.md#L53) qua `src/Directory.Packages.props`.
- Lọc theo tiến trình bằng WinDivert tầng SOCKET, filter `processId == {pid}`, một handle cho mỗi PID:
  `D:\IT\Csharp\Libraries\TqkLibrary.WinDivert\src\TqkLibrary.WinDivert\Flow\SocketTracker.cs:93`. Có pre-populate từ bảng socket kernel (dòng 129) và reconcile chống [race SYN](Glossary-vi.md#L37) (dòng 164).
- Chuyển hướng bằng [NAT loopback](Glossary-vi.md#L9) về relay local:
  `...\Redirect\NatRedirectMiddleware.cs:49`, relay `...\Redirect\TcpRelayServer.cs:12`, `...\Redirect\UdpRelayServer.cs:15`.
- Điểm cắm quyết định: hai delegate trong `...\Redirect\RedirectOptions.cs:15` (`TcpConnectionHandler`) và `:18` (`UdpDatagramHandler`, trả `null` = drop). Handler nhận `RedirectedTcpConnection` có `ProcessId`, `OriginalDestination`, `ClientStream`.
- Orchestrator `...\Redirect\ProcessRedirector.cs:18`, đã có `AddTrackedProcessId(uint)` (dòng 51) nên **một instance theo dõi được nhiều PID**.
- Có sẵn [DoH](Glossary-vi.md#L25) middleware, chặn IPv6 chống rò, chặn UDP chưa xử lý, [pipeline middleware](Glossary-vi.md#L69) để chèn logic tự viết (`RedirectOptions.ConfigureNetworkPipeline`, dòng 46).
- Demo (`src/TqkLibrary.WinDivert.Demo`) là bản CLI của đúng tool này: `ProxyCommandModule`, `ProxyUriParser` (URI → IProxySource), `ProxyRedirectorRunner`, `UdpProxyForwarder`, `ProcessFinder`, `ProcessTreeMonitor`, `SuspendedProcessLauncher`. Tất cả `internal`.

**Thiếu / phải sửa:**

| # | Vấn đề | Vị trí |
|---|---|---|
| W1 | Không có luật theo domain. Không đọc [SNI](Glossary-vi.md#L13), không học [bảng DNS ngược](Glossary-vi.md#L17); `DnsCacheLookup` chỉ để in log, `DohResolver` không parse answer. | `Flow\DnsCacheLookup.cs`, `SecureDns\DohResolver.cs:53` |
| W2 | Không có rule engine, chỉ whitelist port đích. | `Redirect\RedirectOptions.cs:39` |
| W3 | Relay **luôn connect thẳng tới đích thật trước** khi gọi handler → rò IP thật + tốn socket kể cả khi đi proxy. | `Redirect\TcpRelayServer.cs:75-87` |
| W4 | `SocketTracker` có `AddProcess` nhưng **không có `RemoveProcess`**; `ProcessRedirector` không có Stop, chỉ Dispose. | `Flow\SocketTracker.cs:93`, `Redirect\ProcessRedirector.cs:160` |
| W5 | Khoá `NatTable` là `(protocol, họ địa chỉ, srcPort)`, không có PID. Đủ dùng vì cổng nguồn là duy nhất toàn máy **trong từng họ địa chỉ** ([dual-stack](Glossary-vi.md#L85)), nhưng cần ghi rõ giả định. | `Redirect\NatTable.cs` |
| W6 | Demo gọi `ConnectAsync` bằng **IP literal**, proxy không bao giờ thấy hostname → không remote-DNS. | `Demo\Running\ProxyRedirectorRunner.cs:180` |
| W7 | Không có bộ đếm byte, không có danh sách kết nối observable; log ghi file qua `DiagnosticLogger` static toàn cục, `Configure` xoá file cũ. | `Redirect\DiagnosticLogger.cs:22` |
| W8 | UDP qua SOCKS5 map reply chỉ theo endpoint server, hai socket cùng server sẽ nhầm. | `Demo\Running\UdpProxyForwarder.cs:12-17` |
| W9 | Helper process và parser proxy URI nằm ở Demo, `internal`, không tái dùng được. | `Demo\Process\*`, `Demo\Parsing\ProxyUriParser.cs` |
| W10 | ~~Chỉ IPv4; IPv6 bị chặn chứ không chuyển hướng.~~ **Đã xử lý 03/09/2026**: `Ipv6Mode.Redirect` NAT cả IPv6 qua listener `[::1]` riêng; khoá `NatTable` thêm họ địa chỉ. `RedirectOptions.SocketPriority` giờ đã được dùng. | `Redirect\ProcessRedirector.cs`, `Redirect\NatTable.cs` |

### 2.2 TqkLibrary.Proxy (đường ra, dùng gần như nguyên)

- TFM `netstandard2.0;net6.0;net8.0`. Có `GitVersion.yml`, `.nuspec`, CPM, và **submodule lồng** `src/CsharpNugetPush`.
- `IProxySource` (`D:\IT\Csharp\Libraries\TqkLibrary.Proxy\src\TqkLibrary.Proxy\Interfaces\IProxySource.cs`) với các implementation: `LocalProxySource` (direct, có UDP), `HttpProxySource`, `Socks4ProxySource`, `Socks5ProxySource` (có [UDP ASSOCIATE](Glossary-vi.md#L21)), `SshNetProxySource`, `OpenSshProxySource`, `WireGuardProxySource` (bọc wireproxy.exe), `GlobalUnicastProxySource`, `ReverseClientSession`.
- `IConnectSource.ConnectAsync(Uri)` nhận hostname, nên remote-DNS làm được ngay khi sửa W6.
- Throttling từ gói `TqkLibrary.Streams` (`ThrottlingStream`), mẫu ở `src\DemoProxyThrottling\Program.cs`.

**Thiếu:** không có bộ đếm byte hay event mở/đóng tunnel; `StreamTransferHelper` chỉ LogTrace. Tool tự bọc `Stream` đếm byte ở relay của mình nên không cần sửa Proxy trong giai đoạn 1.

### 2.3 TqkLibrary.VpnClient (giai đoạn 2)

- Thuần [userspace TCP/IP](Glossary-vi.md#L73), 19 driver, `netstandard2.0;net8.0`, chưa publish NuGet, 46 project trong `src/`, có `src/Directory.Build.props` riêng (cách ly tốt).
- Cầu nối VPN → proxy **đã tồn tại nhưng chỉ trong demo**: `D:\IT\Csharp\Libraries\TqkLibrary.Vpn\demo\Vpn2ProxyDemo\VpnProxySource.cs:18` (`IProxySource` bọc `TcpIpStack`, có UDP, không BIND). Roadmap của thư viện đã ghi việc tách thành `TqkLibrary.VpnClient.Proxy` (`.docs\11-todo-roadmap.md:211`).
- Vấn đề khi dùng: adapter resolve DNS bằng DNS của máy thật (rò DNS, `VpnProxySource.VpnConnectSource.cs:78-95`); façade `TqkLibrary.VpnClient.csproj` kéo 28 project driver; parse URI/config VPN (`VpnTarget`, `VpnTunnel` với 17 hàm connect) cũng nằm trong demo.
- Đánh giá: API lõi (`VpnClientBuilder.Use*().Build()` → `ConnectAsync` → `Sessions[0].CreateTcpStack()`) dùng được, **không cần thiết kế lại lõi**. Phần cần làm là nâng adapter + parser từ demo lên thư viện. Quyết định cuối để giai đoạn 2, khi đã có tool chạy với proxy.

### 2.4 ProxyRouterWpf (nguồn tái dùng UI)

`d:\IT\Github\tqk2811\ProxyRouterWpf\src\ProxyRouterWpf`: `net8.0-windows`, [CommunityToolkit.Mvvm](Glossary-vi.md#L61) 8.4.0, theme tự làm (`Themes\ThemeManager.cs`, swap `Colors.Dark.xaml`/`Colors.Light.xaml`, theo registry hệ thống), localization swap ResourceDictionary (`Localization\LocalizationManager.cs`, `Strings.vi.xaml`/`Strings.en.xaml`), title bar `WindowChrome` tự vẽ, log FIFO trong RAM (`Proxy\EventLogs\InMemoryTunnelLogStore.cs`), biểu đồ băng thông vẽ Canvas, kéo thả DataGrid (`Views\DropAdorner.cs`). Logic khớp host (Wildcard/Equals/StartsWith/EndsWith/Contains/[CIDR](Glossary-vi.md#L33)/Regex, AND/OR, IsNot) nằm dưới dạng local function trong `Proxy\ProxySession.MyProxyServerHandler.cs:287-360`, cần tách ra class riêng để dùng lại.

## 3. Kiến trúc đề xuất

### 3.1 Luồng chạy

```
ProcessWatcher (WMI start/stop + quét ban đầu)
   │ tiến trình khớp ProcessRule
   ▼
RedirectEngine  ── 1 ProcessRedirector chung ── AddTrackedProcessId / RemoveTrackedProcessId
   │                    │
   │                    ├─ pipeline: DoH (tuỳ chọn) → DnsSniff (học IP→domain) → NAT → chặn QUIC/UDP theo policy
   │                    ▼
   │             TcpRelayServer ──► TcpConnectionHandler(conn)
   │                                   1. domain = SNI/Host peek từ conn.ClientStream, không có thì tra bảng DNS ngược theo IP
   │                                   2. outbound = RoutingPolicy(conn.ProcessId).Resolve(domain, ip, port)
   │                                   3. Direct → LocalProxySource; Proxy → IProxySource tương ứng; Block → đóng
   │                                   4. ConnectAsync(host hoặc ip, port) rồi bơm 2 chiều qua CountingStream
   ▼
ConnectionTracker → sự kiện Opened/Updated/Closed cho UI (PID, domain, đích, outbound, bytes, thời gian)
```

- **Quyết định định tuyến ở tầng kết nối** (trong `TcpConnectionHandler`), không ở tầng gói, vì domain chỉ biết sau khi TCP mở (SNI). Mọi kết nối của tiến trình mục tiêu đều đi qua relay; "direct" nghĩa là relay nối thẳng bằng `LocalProxySource`. Ưu điểm: đếm được byte, ghi log, đổi luật không cần attach lại. Nhược: thêm một bước copy trong tool. Chấp nhận ở giai đoạn 1; sau này có thể thêm tầng "pass-through theo IP/CIDR" ở middleware nếu cần hiệu năng.
- **Domain lấy theo thứ tự**: SNI (TLS) → header Host (HTTP) → bảng DNS ngược → chỉ IP. Với UDP chỉ có bảng DNS ngược hoặc IP.
- **UDP**: DNS (53) xử lý riêng (DoH hoặc chuyển qua outbound mặc định nếu là SOCKS5, hoặc direct); [QUIC](Glossary-vi.md#L29) UDP/443 **chặn mặc định** để trình duyệt lùi về TCP; UDP khác theo policy (direct / SOCKS5 / chặn). Proxy HTTP và SOCKS4 không chở được UDP nên khi outbound không hỗ trợ thì chặn, không để rò.
- **IPv6**: chuyển hướng như IPv4 (mặc định `Ipv6Mode.Redirect`) — pump NETWORK IPv6 chạy đúng pipeline NAT của IPv4 nhưng trỏ vào cặp listener `[::1]` riêng của relay. Vẫn giữ được hai chế độ cũ: `Block` (chặn để ứng dụng lùi về IPv4) và `Ignore`. Đường ra không có tuyến IPv6 thì đích có tên miền đi bằng tên (đường ra tự phân giải sang A), đích chỉ có IP trần thì từ chối ngay để ứng dụng lùi IPv4 ([Happy Eyeballs](Glossary-vi.md#L81)); mỗi outbound có `Ipv6Support` = Auto/Enabled/Disabled, Auto tự học từ lần hỏng đầu tiên.
- **Nhiều tiến trình, mỗi tiến trình một policy**: một `ProcessRedirector` duy nhất, handler tra policy theo `conn.ProcessId`. Không tạo nhiều redirector vì mỗi cái mở thêm một handle NETWORK cùng filter.

### 3.2 Mô hình dữ liệu (ProxyDivert.Core)

- `Outbound`: `Id`, `Name`, `Kind` (Direct | Block | HttpProxy | Socks4 | Socks5 | về sau Vpn/Ssh), `Uri`, `Credential`, `IsEnabled`. Factory `OutboundSourceFactory` → `IProxySource` (chuyển từ `ProxyUriParser` của Demo).
- `RoutingRule`: `Matcher` (DomainSuffix | DomainWildcard | DomainRegex | DomainEquals | IpCidr | Port), `Pattern`, `IsNot`, `OutboundId`, `Order`.
- `RoutingPolicy`: `Name`, danh sách `RoutingRule` theo thứ tự, `DefaultOutboundId` (mặc định Direct), `UdpMode`, `BlockQuic`.
- `ProcessRule`: `Matcher` (ExeName | FullPath | Wildcard), `Pattern`, `IncludeChildren`, `PolicyId`, `IsEnabled`.
- `AppConfig`: các danh sách trên + tuỳ chọn DNS (SystemSniff | DoH + endpoint) + log. Lưu JSON cạnh exe như ProxyRouterWpf; credential mã hoá DPAPI.

### 3.3 Cấu trúc repo

```
ProxyDivert/
├── ProxyDivert.sln
├── .gitmodules                  3 submodule dưới libs/
├── Directory.Build.rsp          (gitignore) -maxCpuCount:2 -nodeReuse:false
├── libs/
│   ├── TqkLibrary.WinDivert/    ProjectReference → src/TqkLibrary.WinDivert
│   ├── TqkLibrary.Proxy/        ProjectReference → src/TqkLibrary.Proxy (+ SshNet khi cần)
│   └── TqkLibrary.VpnClient/    giai đoạn 2
├── src/
│   ├── Directory.Build.props    chỉ áp cho src/ của tool (đặt trong src/ để không lan sang libs/)
│   ├── ProxyDivert.Core/        net8.0-windows, x64: engine, models, services, không WPF
│   ├── ProxyDivert.Wpf/         net8.0-windows, x64, WinExe, app.manifest requireAdministrator
│   └── ProxyDivert.Core.Tests/  xUnit: rule matcher, SNI parser, DNS parser, config
└── docs/
    ├── Plan-vi.md, Glossary-vi.md
```

- [TFM](Glossary-vi.md#L57) `net8.0-windows`, `PlatformTarget x64` (native WinDivert chỉ có x64). WinDivert.dll + WinDivert64.sys phải nằm cạnh exe.
- `src/Directory.Build.props` đặt trong `src/` chứ không ở gốc repo, vì repo WinDivert và Proxy **không có** `Directory.Build.props` riêng, file ở gốc sẽ lan sang project của họ.
- Bẫy GitVersion của Proxy: `ProjectBuildProperties.targets` bật GitVersion ở Release khi có `.nuspec`; submodule ở detached HEAD hoặc clone nông có thể vỡ. Xử lý: clone submodule đủ history + tag, hoặc set `EnableGitVersion=false` cho project trong `libs/` từ `.csproj` của tool. Kiểm chứng ở bước 0.
- Namespace theo quy ước trong `~/.claude/csharp.md`: feature gốc + `.Interfaces/.Enums/.Models/.Helpers/.Extensions`, mỗi type một file.

## 4. Các bước thực hiện

### Bước 0. Dựng khung repo (không có logic) — ĐÃ XONG 2026-09-03

1. `git init`, `.gitignore`, `Directory.Build.rsp` (gitignore), README.
2. Thêm 2 submodule WinDivert và Proxy vào `libs/` (`--recursive` vì Proxy có submodule lồng). VpnClient thêm ở giai đoạn 2.
3. Tạo solution, 3 project trong `src/`, ProjectReference tới 2 thư viện. Build Debug và Release phải xanh; kiểm chứng GitVersion không vỡ.
4. Copy WinDivert native ra output; app.manifest requireAdministrator.

### Bước 1. Sửa TqkLibrary.WinDivert (trong submodule, commit về repo thư viện) — ĐÃ XONG 2026-09-03

1. **W3** Relay không tự connect đích; handler nhận `RedirectedTcpConnection` chỉ có `ClientStream` + thông tin đích, tự quyết mở upstream. Thêm helper `RelayDirectAsync` cho ai vẫn muốn hành vi cũ (Demo `attach`/`launch`).
2. **W4** `SocketTracker.RemoveProcess(pid)` đóng handle SOCKET của PID và xoá flow; `ProcessRedirector.RemoveTrackedProcessId(pid)`.
3. **W9** Nâng từ Demo lên thư viện, đổi thành `public`: `ProcessFinder`, `ProcessTreeMonitor`, `SuspendedProcessLauncher`, `ProxyUriParser` (đặt ở namespace phù hợp, ví dụ `Process.*`, `Redirect.Helpers`), `ConnectSourceExtensions.ForwardAsync`.
4. **W1** Middleware `DnsAnswerSniffMiddleware` parse gói trả lời DNS UDP/53 (và bổ sung parse answer trong `DnsOverHttpsMiddleware`) → `ReverseDnsTable` (IP → domain, TTL). Kèm `TlsClientHelloParser`/`HttpHostParser` đọc SNI/Host từ `ClientStream` bằng `PreReadStream` (đã có trong TqkLibrary.Proxy) để không mất byte đã peek.
5. **W7** Thay `DiagnosticLogger` static bằng `ILogger` truyền vào; thêm `ConnectionStatistics` (bytes up/down per connection) và sự kiện Opened/Closed có PID + đích + thời gian.
6. **W8** Sửa map reply UDP theo `(clientPort, serverEndpoint)`.
7. Cập nhật Demo theo API mới để nó vẫn chạy (Demo là bộ test thủ công nhanh nhất).
8. Bỏ hoặc dùng `RedirectOptions.SocketPriority`.

### Bước 2. ProxyDivert.Core — ĐÃ XONG 2026-09-03

1. Models + `AppConfig` + `ConfigStore` (JSON, DPAPI cho mật khẩu).
2. `HostMatcher` tách từ ProxyRouterWpf (Wildcard/Suffix/Regex/Equals/CIDR, IsNot) + unit test.
3. `RoutingPolicyResolver`: (pid, domain?, ip, port, proto) → `Outbound`.
4. `OutboundSourceFactory` + cache `IProxySource` theo `OutboundId` (một instance mỗi outbound, dispose khi xoá/sửa).
5. `ProcessWatcher`: quét ban đầu + [WMI](Glossary-vi.md#L65) `Win32_ProcessStartTrace/StopTrace`, fallback poll khi WMI lỗi; khớp `ProcessRule`; nuôi cây tiến trình con khi `IncludeChildren`.
6. `RedirectEngine`: giữ một `ProcessRedirector`, đăng ký handler TCP/UDP theo mục 3.1, `Start/Stop/ApplyConfig` không cần attach lại khi đổi luật.
7. `ConnectionTracker` + `TrafficLog` FIFO (mượn `InMemoryTunnelLogStore`).
8. Test: matcher, SNI parser, DNS parser, resolver với policy mẫu.

### Bước 3. ProxyDivert.Wpf — ĐÃ XONG 2026-09-03 (trừ title bar tự vẽ, kéo-thả rule, biểu đồ băng thông; chưa kiểm tra thủ công)

- Nền: theme, localization vi/en, title bar, `AppServices` composition root, mượn từ ProxyRouterWpf.
- Tab **Tiến trình**: danh sách đang chạy (tên, PID, đường dẫn, trạng thái đã attach), ProcessRule CRUD, nút Launch suspended (né race SYN).
- Tab **Đường ra**: Outbound CRUD, nút Test (mở kết nối thử qua IProxySource).
- Tab **Luật**: Policy CRUD, kéo thả thứ tự rule, chọn outbound mặc định, UdpMode, BlockQuic.
- Tab **Kết nối**: bảng sống (PID, tiến trình, domain, đích, outbound, bytes, thời gian), lọc.
- Tab **Log** và **Cài đặt** (DNS mode, DoH endpoint, ngôn ngữ, theme, chạy cùng Windows).
- Kiểm tra thủ công: attach Chrome/curl, luật `*.google.com → SOCKS5`, còn lại direct; xác nhận bằng trang checkip qua hai đường.

### Giai đoạn 2. VPN dưới dạng đường ra

**WireGuard ĐÃ XONG (03/09/2026)** — và không cần tới submodule VpnClient: `TqkLibrary.Proxy.Vpn.WireProxyCli` trong chính submodule Proxy đã bọc [wireproxy](Glossary-vi.md#L93) thành `IProxySource`, tức là đúng hình dạng mà kế hoạch dự tính đi tới. Đã làm:

1. `OutboundKind.Vpn` → `WireGuardProxySource`; ô `Outbound.Url` là đường dẫn file `.conf`.
2. `Vpn/WireGuardConfigParser` đọc file `.conf` **nguyên bản của nhà cung cấp** (chỉ `[Interface]`/`[Peer]`) rồi để runner sinh bản có `[Socks5]` trên cổng loopback ngẫu nhiên + mật khẩu ngẫu nhiên; file nào đã có sẵn `[Socks5]` thì dùng nguyên trạng (đọc `BindAddress` để biết chỗ nối).
3. `AppConfig.WireProxyPath` (một thiết lập cho cả máy) + ô chọn file trong tab Cài đặt; bỏ trống thì tìm cạnh exe rồi tới PATH.
4. `Outbound.SupportsUdp` KHÔNG còn gồm Vpn — SOCKS5 của wireproxy chỉ có TCP, nên UDP qua đường ra VPN bị hạ xuống Block thay vì rò ra ngoài.
5. `ProcessWatcher` từ chối attach chính tiến trình tool và `wireproxy.exe`: luật rộng kiểu `*.exe` mà tóm phải chúng thì mọi kết nối quay vòng lại relay.

Còn lại cho các loại VPN khác (OpenVPN/SSTP/L2TP qua TqkLibrary.VpnClient): giữ nguyên hướng cũ — thêm submodule VpnClient, tách `VpnProxySource` + `VpnTarget`/`VpnTunnel` từ demo lên thư viện (resolve DNS trong tunnel), rồi cắm vào cùng chỗ `OutboundSourceFactory.CreateVpn` đang đứng. Trạng thái kết nối/reconnect hiện lên UI cũng chưa làm.

## 5. Rủi ro và điểm cần quyết định

- **Direct qua relay hay pass-through**: giai đoạn 1 mọi kết nối qua relay (đơn giản, đếm được byte). Nếu cần hiệu năng cao cho game/stream thì thêm luật pass-through theo IP ở tầng gói sau.
- **Race SYN**: kết nối mở trước khi attach sẽ rò. Giảm bằng WMI event + pre-populate; nút Launch suspended cho trường hợp cần tuyệt đối.
- **UDP qua proxy** chỉ có với SOCKS5. Mặc định UDP không phải DNS thì direct hay chặn cần chọn trong Cài đặt; đề xuất mặc định direct, QUIC chặn.
- **Anti-cheat**: `NtSuspendProcess` và driver WinDivert có thể bị game chặn. Ghi cảnh báo trong UI.
- **x64 only**, cần Administrator, Windows 10/11.
- **GitVersion trong submodule Proxy** khi build Release: kiểm chứng ở bước 0.
- **Xung đột `Directory.Packages.props`**: mỗi submodule có file riêng trong `src/`, tool đặt file của mình trong `src/` của tool; không đặt ở gốc.

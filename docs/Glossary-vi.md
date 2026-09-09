# Thuật ngữ dùng trong ProxyDivert

Mỗi mục là một heading `##`. Tài liệu khác dẫn link tới đây theo **số dòng** (`#Lxx`), nên khi chèn mục mới hãy thêm vào **cuối file** để không lệch link cũ.

## WinDivert

Driver kernel mã nguồn mở cho Windows (WinDivert.dll + WinDivert64.sys) cho phép chương trình user-mode **bắt, sửa, thả hoặc bơm lại** gói tin IP ở nhiều tầng (NETWORK, SOCKET, FLOW...). Cần quyền Administrator. Ở tầng SOCKET nó báo sự kiện connect/bind/close kèm `processId`, nên lọc được theo tiến trình.

## NAT loopback (relay)

Kỹ thuật TqkLibrary.WinDivert dùng để chuyển hướng: gói TCP/UDP đi ra của tiến trình mục tiêu bị **ghi lại địa chỉ đích** thành `127.0.0.1:<cổng relay>` và bơm lại; một server relay trong tool nhận kết nối, tra bảng NAT (khoá theo cổng nguồn) để biết đích thật rồi tự quyết định nối trực tiếp hay qua proxy. Gói trả về được ghi ngược địa chỉ để tiến trình không biết gì.

## SNI (Server Name Indication)

Phần mở rộng của TLS: client gửi **tên miền đích ở dạng chữ thường** trong gói ClientHello đầu tiên. Đọc trộm (peek) vài byte đầu của kết nối TCP cổng 443 là biết domain mà không cần giải mã. Với HTTP thường thì đọc header `Host`.

## Bảng DNS ngược (IP → domain)

Bảng do tool tự học bằng cách **đọc gói trả lời DNS** (UDP/53 hoặc DoH) mà tiến trình nhận được: mỗi bản ghi A/AAAA cho biết IP này ứng với domain nào. Dùng để suy ra domain cho kết nối không có SNI (UDP, TCP không TLS, cổng lạ). Có TTL, hết hạn thì xoá.

## SOCKS5 và UDP ASSOCIATE

SOCKS5 là giao thức proxy tầng TCP hỗ trợ CONNECT (mở TCP), BIND (chờ kết nối ngược) và **UDP ASSOCIATE** (chuyển tiếp gói UDP qua proxy). HTTP proxy (CONNECT) và SOCKS4 **không** chuyển được UDP, nên UDP của tiến trình chỉ đi được qua proxy SOCKS5, hoặc phải direct/chặn.

## DoH (DNS over HTTPS)

Phân giải DNS bằng request HTTPS tới máy chủ như `https://dns.google/dns-query` thay vì UDP/53. Tránh rò rỉ truy vấn DNS ra mạng thường và tránh bị ISP chặn. TqkLibrary.WinDivert có sẵn middleware DoH.

## QUIC (HTTP/3)

Giao thức HTTP/3 chạy trên **UDP/443**. Trình duyệt ưu tiên QUIC nếu được; khi đó không có kết nối TCP nào để đọc SNI. Tool chặn UDP/443 của tiến trình mục tiêu thì trình duyệt tự lùi về TCP+TLS, nhờ vậy định tuyến theo SNI hoạt động.

## CIDR

Cách viết dải IP `địa chỉ/độ dài tiền tố`, ví dụ `192.168.0.0/16` là mọi IP bắt đầu bằng `192.168`. Dùng cho luật định tuyến theo IP.

## Rò rỉ SYN khi attach (race)

Khoảng trống thời gian giữa lúc tiến trình khởi động và lúc tool gắn bộ lọc WinDivert: kết nối mở trong khoảng đó **đi thẳng** ra ngoài, không bị chuyển hướng. Giảm bằng cách đọc bảng socket kernel ngay khi attach (pre-populate), hoặc khởi chạy tiến trình ở trạng thái suspended rồi mới attach.

## IProxySource

Interface của TqkLibrary.Proxy đại diện cho **một đường ra (upstream)**: HTTP proxy, SOCKS4/5, SSH, direct (`LocalProxySource`), và sau này VPN. Cung cấp `GetConnectSourceAsync` (mở TCP), `GetUdpAssociateSourceAsync` (UDP), `GetBindSourceAsync`. Tool chọn một `IProxySource` cho mỗi kết nối theo luật.

## Git submodule

Cách nhúng một repo git khác vào thư mục con của repo hiện tại, ghim theo commit. Cho phép sửa thư viện tại chỗ và commit về repo gốc của thư viện. Clone phải kèm `--recurse-submodules`; repo TqkLibrary.Proxy lại có submodule lồng (`src/CsharpNugetPush`).

## ProjectReference và PackageReference

`ProjectReference` tham chiếu thẳng file `.csproj` (build từ source, sửa được ngay); `PackageReference` tham chiếu gói NuGet đã đóng gói. Dùng submodule thì đi kèm `ProjectReference`.

## CPM (Central Package Management)

Cơ chế MSBuild khai báo phiên bản NuGet tập trung trong `Directory.Packages.props`, các `.csproj` chỉ ghi tên gói không ghi `Version`. Mỗi cây thư mục tìm file gần nhất đi lên; hai repo có file riêng thì không đụng nhau.

## TFM (Target Framework Moniker)

Chuỗi định danh nền tảng đích như `net8.0`, `net8.0-windows`, `netstandard2.0`. Project WPF phải dùng `-windows`; nó tham chiếu được project `net8.0` và `netstandard2.0`, nhưng không ngược lại.

## MVVM và CommunityToolkit.Mvvm

Mẫu tách giao diện WPF (View) khỏi trạng thái/logic (ViewModel) qua data-binding. `CommunityToolkit.Mvvm` là thư viện sinh mã cho `[ObservableProperty]`, `[RelayCommand]` để bớt code lặp.

## WMI process event

Truy vấn `Win32_ProcessStartTrace` / `Win32_ProcessStopTrace` qua WMI để nhận **sự kiện** tiến trình khởi động/kết thúc (cần Administrator). Thay thế cho việc poll danh sách tiến trình theo chu kỳ.

## Middleware pipeline gói tin

Cách TqkLibrary.WinDivert xử lý gói: mỗi gói đi qua chuỗi middleware kiểu ASP.NET (`InvokeAsync(ctx, next)`), mỗi bước có thể Pass, sửa rồi đánh dấu Modified, hoặc Drop. Chạy **đồng bộ** trên thread bơm gói nên middleware không được chờ I/O.

## Userspace TCP/IP stack

TqkLibrary.VpnClient tự hiện thực TCP/IP ở tầng ứng dụng thay vì dùng card mạng ảo TUN/TAP: gói IP trong đường hầm VPN được đưa cho `TcpIpStack`, và app mở socket "bên trong" đường hầm. Không đụng bảng route của Windows, phần lớn không cần Administrator.

## DPAPI (Data Protection API)

API mã hoá sẵn có của Windows. Với `DataProtectionScope.CurrentUser`, chuỗi mã hoá ra chỉ giải mã lại được bằng đúng tài khoản Windows đã mã hoá nó, không cần tự quản khoá. Trong ProxyDivert dùng để mã hoá mật khẩu proxy trước khi ghi vào `proxydivert.config.json`. Lưu ý phạm vi bảo vệ: nó chống việc chép file sang máy/tài khoản khác, KHÔNG chống mã độc chạy dưới chính tài khoản đó.

## Happy Eyeballs (RFC 8305)

Cách trình duyệt/thư viện mạng xử lý host có cả bản ghi A (IPv4) và AAAA (IPv6): thử IPv6 trước, nếu không xong trong khoảng vài trăm mili-giây thì mở song song một kết nối IPv4 và dùng cái nào xong trước. Hệ quả cho ProxyDivert: **từ chối nhanh** một kết nối IPv6 không phục vụ được thì ứng dụng tự lùi sang IPv4 gần như tức thì, còn để nó treo tới lúc timeout thì người dùng thấy trang đứng hình. Vì vậy khi đường ra không có tuyến IPv6, tool đóng kết nối ngay chứ không chờ.

## Dual-stack và hai không gian cổng

Windows chạy song song hai chồng giao thức IPv4 và IPv6, và **cổng nguồn của hai họ là độc lập**: cùng lúc có thể có socket TCP/50000 trên IPv4 và một socket TCP/50000 khác trên IPv6. Mọi bảng tra theo cổng nguồn trong tool (bảng NAT, bảng đường hầm UDP, socket upstream) vì thế phải lấy khoá gồm cả họ địa chỉ, nếu không hai luồng khác nhau sẽ ghi đè nhau và gói trả về đi nhầm chỗ.

## Ipv6Mode và Ipv6Support

Hai thiết lập khác nhau, dễ nhầm. `Ipv6Mode` (Cài đặt, phạm vi toàn engine) nói **làm gì với IPv6 của tiến trình đích**: `Redirect` đưa qua relay và luật định tuyến như IPv4, `Block` chặn để ứng dụng lùi về IPv4, `Ignore` thả đi thẳng (lọt ra ngoài proxy). `Ipv6Support` (theo từng đường ra) nói **đường ra đó có tuyến IPv6 hay không**: `Auto` thử rồi tự nhớ khi thất bại, `Enabled`/`Disabled` do người dùng khẳng định.

## wireproxy (VPN ở tầng ứng dụng)

Chương trình mã nguồn mở chạy đường hầm WireGuard hoàn toàn trong **không gian người dùng** rồi phơi ra một listener SOCKS5 trên loopback. Không tạo card mạng ảo TUN/TAP, không sửa bảng route Windows — nên chỉ tiến trình nào được ProxyDivert chuyển hướng vào listener đó mới đi qua VPN, phần còn lại của máy giữ nguyên mạng thường. ProxyDivert bọc nó qua `WireGuardProxySource` (submodule TqkLibrary.Proxy), đọc file `.conf` của nhà cung cấp rồi sinh bản cấu hình có mục `[Socks5]` mà wireproxy cần. Hạn chế: SOCKS5 của wireproxy **chỉ có TCP**, không có UDP ASSOCIATE.

## DI (Dependency Injection) và IServiceCollection

Cách dựng đối tượng bằng cách **đưa phụ thuộc vào từ ngoài** qua constructor thay vì để chính lớp đó tự `new` ra thứ nó cần. `IServiceCollection` là bảng khai báo "interface này thì dùng lớp nào", `IServiceProvider` là chỗ lấy ra một đối tượng đã dựng sẵn cùng toàn bộ phụ thuộc của nó. Thư viện tự khai báo phần của mình qua các extension `AddWinDivert*()`, nên host chỉ gọi một dòng thay vì phải biết từng factory bên trong; đổi lại, đổi một mảnh (ví dụ thay `IWinDivertHandleFactory` bằng bản giả lập khi test) chỉ là đăng ký lớp khác, không phải sửa lớp đang dùng nó.

## ILogger, ILoggerProvider và ILoggerFactory

Bộ giao diện ghi log chuẩn của .NET. `ILogger<T>` là thứ **lớp nghiệp vụ** dùng để ghi; nó không biết dòng log đi đâu. `ILoggerProvider` là **host** cắm vào để quyết định đích đến (file, console, khung log trong cửa sổ); `ILoggerFactory` gom các provider lại và tạo `ILogger` theo từng category. Thư viện WinDivert cố ý không kèm provider nào: nó chỉ ghi qua `ILogger<T>`, còn nơi đổ log là quyết định của ứng dụng — trong ProxyDivert là `AppLoggerProvider`, vừa ghi file trace vừa đẩy vào khung Log.

## PersistentKeepalive (WireGuard)

Số giây WireGuard tự gửi một gói rỗng khi đường hầm đang rỗi. Không có nó, phiên WireGuard nghỉ đủ lâu sẽ bị đầu kia quên và bản ghi NAT ở router cũng hết hạn; gói tiếp theo phải **bắt tay lại** — tốn một vòng đi-về tới máy chủ VPN, rơi đúng vào request xui xẻo đi đầu. File `.conf` nhà cung cấp phát ra thường bỏ trống mục này vì với client chính thức thì đường hầm rỗi chẳng tốn gì. ProxyDivert điền mặc định 25 giây (giá trị WireGuard tự khuyến nghị, nằm dưới thời gian giữ NAT ngắn nhất thường gặp) cho peer nào không tự khai; peer đã tự khai thì giữ nguyên.

## Backoff luỹ tiến (exponential backoff)

Cách thử lại một việc vừa hỏng với khoảng chờ **dài dần** thay vì thử lại ngay: 1s → 2s → 5s → 10s → 30s rồi giữ nguyên. Mục đích là phân biệt hai tình huống nhìn giống nhau — trục trặc thoáng qua (thử lại sau 1 giây là xong) và hỏng thật (sai file cấu hình, mất mạng, máy chủ VPN chết). Không có backoff thì trường hợp thứ hai biến thành vòng lặp sinh tiến trình liên tục, đốt CPU mà không bao giờ thành công. ProxyDivert đếm lại từ đầu khi đường hầm đã đứng vững đủ lâu (60 giây), để một lần rớt sau nhiều giờ chạy tốt không bị coi là phần tiếp theo của một chuỗi hỏng cũ.

## Rò rỉ DNS (DNS leak)

Tình trạng lưu lượng đi qua VPN nhưng **việc tra tên miền thì không**. Trình duyệt muốn vào `example.com` thì trước hết phải hỏi một máy chủ DNS xem tên đó ứng với IP nào; nếu câu hỏi ấy đi ra bằng đường mạng thường tới resolver của nhà mạng, thì nhà mạng vẫn có đủ danh sách những nơi bạn truy cập, dù nội dung đã được mã hoá trong đường hầm. Bản demo của TqkLibrary.VpnClient dùng `Dns.GetHostAddressesAsync` của máy thật nên đúng vào lỗi này; ProxyDivert tự hỏi DNS **bên trong đường hầm** qua socket UDP của stack, tới máy chủ DNS mà VPN cấp (không có thì 1.1.1.1 / 8.8.8.8, vẫn gửi trong đường hầm).

## PSK (Pre-Shared Key) của IPsec

Một chuỗi bí mật **dùng chung cho cả nhóm**, dùng ở pha 1 của IPsec để hai đầu tin nhau trước khi hỏi tới tài khoản/mật khẩu của từng người. L2TP/IPsec và IKEv2 cần nó; SSTP, SoftEther, OpenVPN, WireGuard thì không. Nó là bí mật thật (ai có nó đều bắt đầu bắt tay được) nên ProxyDivert để riêng một ô và mã hoá bằng [DPAPI](#L77) như mật khẩu, thay vì nhét vào ô URL nơi nó sẽ nằm thô trong file cấu hình.

## Watermark của SoftEther

Một khối chữ ký nhị phân mà client SoftEther chính thức gửi kèm lúc đăng nhập; máy chủ SoftEther thật kiểm khối này và trả **HTTP 403** nếu thiếu. Khối đó là dữ liệu GPL nên không nằm trong repo TqkLibrary.VpnClient, và cũng không thể tự sinh. Muốn dùng đường ra SoftEther với máy chủ thật thì phải tự lấy file blob rồi khai bằng dòng `Watermark = <đường dẫn>` trong file `.vpn` — đây là lý do SoftEther là giao thức duy nhất trong sáu giao thức không dùng được nếu chỉ điền URL.

## Driver VPN tự kết nối lại

Mọi driver của TqkLibrary.VpnClient đều kế thừa `ReconnectingVpnConnection`: **bản thân driver** đã theo dõi link, phát hiện rớt, và bắt tay lại với backoff kèm jitter. Nên phần giám sát của ProxyDivert cố ý đứng ngoài — nó chỉ dựng đường hầm mới khi driver **bỏ cuộc hẳn** (trạng thái `Disconnected`), chứ không can thiệp lúc driver đang tự chữa. Dựng lại giữa chừng chỉ là đua với driver: hai bên cùng quay số tới một máy chủ, và cái đang gần xong thì bị vứt đi.

## Chế độ binding (OneWay / TwoWay)

Hướng chảy dữ liệu của một data-binding WPF: `OneWay` chỉ đọc từ ViewModel ra giao diện, `TwoWay` ghi ngược lại. Mỗi dependency property có mặc định riêng — `TextBlock.Text` mặc định OneWay, còn `CheckBox.IsChecked` mặc định **TwoWay**, nên `DataGridCheckBoxColumn` trỏ vào một property chỉ có `get` sẽ ném `InvalidOperationException` ngay lúc gắn binding. Sửa bằng `Mode=OneWay` tường minh.

## Converter và MultiBinding

`IValueConverter` biến giá trị nguồn thành thứ hiển thị được (vd giá trị enum → chuỗi đã dịch). Khác `DynamicResource`, kết quả của converter KHÔNG tự cập nhật khi từ điển chuỗi bị hoán đổi, vì nó không phải tham chiếu tài nguyên. `MultiBinding` gộp nhiều nguồn vào một binding qua `IMultiValueConverter`; cắm thêm một nguồn "phiên bản ngôn ngữ" vào đó là cách bắt WPF chạy lại converter mà không phải đụng vào `ItemsSource`.

## WindowChrome (thanh tiêu đề tự vẽ)

Lớp WPF cho phép thay thanh tiêu đề hệ thống bằng nội dung của chính cửa sổ, mà vẫn giữ nguyên hành vi cửa sổ thật: kéo, snap, bấm đúp phóng to, kéo cạnh đổi kích thước, và không đè lên thanh tác vụ khi maximize. `CaptionHeight` là chiều cao dải được coi là "thanh tiêu đề" — phải khớp đúng chiều cao hàng bạn vẽ. Mọi control có thể bấm nằm trong dải đó phải đặt `WindowChrome.IsHitTestVisibleInChrome="True"`, nếu không cú bấm bị hiểu thành kéo cửa sổ.

## DataGridColumn nằm ngoài visual tree / BindingProxy

Cột của `DataGrid` (`DataGridComboBoxColumn`, `DataGridTextColumn`...) không phải con visual cũng không phải con logical của lưới, nên binding đặt trên chính thuộc tính của cột không có tổ tiên nào để đi ngược lên: `RelativeSource AncestorType=UserControl` không bao giờ phân giải, `ItemsSource` lặng lẽ ở lại `null` và danh sách xổ xuống rỗng — build không báo, log không báo. `BindingProxy` là một `Freezable` đặt trong `Resources` của phần tử: WPF cấp cho nó ngữ cảnh kế thừa của phần tử đó, gồm cả `DataContext`, nên cột lấy được view model qua `{Binding Data.Xxx, Source={StaticResource Vm}}` mà không cần cây nào cả.

## Bộ luật (policy) và thứ tự ưu tiên

Một **bộ luật** (`RoutingPolicy`) là danh sách đích có tên, cộng với **một** đường ra dùng chung (`OutboundId`) và hai thiết lập UDP / Block QUIC. Nó là khung trái của tab Rules; lưới bên phải là luật của bộ luật đang chọn. Luật (`RoutingRule`) chỉ nói "đích này thuộc về bộ luật này" — nó **không** có đường ra riêng, vì hai đường ra khác nhau nghĩa là hai bộ luật khác nhau, mà bộ lọc thì xếp được chúng theo thứ tự nó muốn.

Một **bộ lọc tiến trình** giữ một **danh sách** bộ luật theo thứ tự ưu tiên (`ProcessRule.PolicyIds`). Lúc định tuyến, luật được duyệt hết bộ luật thứ nhất (theo `Order` của chính nó) rồi mới sang bộ luật thứ hai; khớp cái nào trước thì kết nối đi theo đường ra của **bộ luật chứa luật đó**. Cố ý KHÔNG trộn rồi sắp lại theo `Order` chung: hai bộ luật viết riêng có `Order` trùng nhau, trộn lại thì thứ tự do con số tình cờ nhỏ hơn quyết định chứ không do người dùng.

Không bộ luật nào khớp = không ai nhận kết nối đó ⇒ **đi thẳng**. Không còn khái niệm "đường ra mặc định" nữa; muốn bắt hết thì thêm một bộ luật có luật `*` và đặt nó cuối danh sách.

Hai thiết lập UDP và Block QUIC lấy theo **bộ luật đứng đầu**, vì một kết nối không thể chọn chúng theo từng luật.

## Edit mode của DataGrid và IsReadOnly

`DataGrid` chỉ cho gõ vào một ô khi ô đó **vào edit mode**: lưới đổi phần tử hiển thị (một `TextBlock`) lấy phần tử soạn thảo (một `TextBox`) rồi ghi ngược về nguồn lúc commit. `IsReadOnly=True` chặn đúng bước đó, nên mọi `DataGridTextColumn` và `DataGridCheckBoxColumn` đứng im — không có con trỏ, không có gì báo là bị khoá.

Riêng `DataGridComboBoxColumn` **không** đi qua đường đó: phần tử hiển thị của nó đã là một `ComboBox` thật, chọn phát nào ghi thẳng vào nguồn phát ấy. Vì thế một lưới read-only trông như hỏng lỗ chỗ — combo vẫn đổi được, ô chữ thì không — chứ không trông như bảng chỉ đọc.

Muốn khoá riêng một **hàng** (vd Direct/Block) thì đừng đặt `IsEnabled=False` cho cả `DataGridRow`: khi những hàng đó là toàn bộ nội dung lúc mới cài, cả lưới trông như bị tắt. Cách dùng ở đây gồm ba mảnh — huỷ sự kiện `BeginningEdit` trong code-behind (chặn ô chữ và checkbox), `DataGrid.CellStyle` làm mờ chữ để báo hàng đó không phải của mình, và style riêng cho `ComboBox` của cột combo vì nó không đi qua edit mode. Lưu ý `DataGridCell` trong theme có setter `Foreground` riêng, nên đặt màu ở `RowStyle` không tới được chữ.

`IsReadOnly` là **hành vi**, không phải hình thức, nên nó không được đặt trong style implicit của theme (style không có `x:Key`, áp cho mọi `DataGrid` trong ứng dụng). Trong ProxyDivert: Outbounds/Rules/Processes cho sửa, Connections tự khai `IsReadOnly="True"` tại chỗ vì nó là bảng theo dõi.

## Kiểu so khớp của luật tiến trình

Một điều kiện gồm ba phần: combo **đối tượng** (soi cái gì), combo **kiểu so khớp**, và ô giá trị. Combo đối tượng quyết định combo so khớp hiển thị bộ nào — vì hai bộ thật sự khác nhau, command line không phải đường dẫn. Bộ kiểu lấy theo mẫu ProxyRouterWpf (`ProxySourceGroupFilterType`), bỏ hai kiểu chỉ dành cho nguồn proxy là `CidrIp` và `TotalBytes`.

Đối tượng `Tiến trình`: `Tên tệp chạy` (bỏ đuôi .exe hai bên), `Đường dẫn đầy đủ`, `Ký tự đại diện`, `Bắt đầu bằng`, `Kết thúc bằng`, `Có chứa`, `Regex`. Ba kiểu đầu chỉ soi đúng thứ chúng nói; bốn kiểu sau soi CẢ đường dẫn đầy đủ LẪN tên tệp chạy và khớp cái nào cũng tính — nếu chỉ soi đường dẫn thì `Có chứa chrome` vô dụng với mọi tiến trình Windows không cho đọc đường dẫn, còn nếu chỉ soi tên thì không viết nổi `Bắt đầu bằng C:Games`.

Đối tượng `Argument`: `Có chứa` (mặc định), `Ký tự đại diện`, `Khớp chính xác`, `Bắt đầu bằng`, `Kết thúc bằng`, `Regex` — tất cả trên toàn bộ command line.

Cả `Ký tự đại diện` lẫn `Regex` đều chạy qua `Regex.IsMatch` với hạn 100ms **tính cho cả lần đánh giá một bộ lọc**, không phải cho từng mẫu: cây hai chục lá regex mà mỗi lá được 100ms là hai giây cho mỗi tiến trình mỗi lượt quét. Mẫu do người dùng gõ, chạy trên mọi tiến trình mỗi lượt quét, nên một biểu thức backtrack thảm hoạ sẽ treo watcher. Mẫu hỏng hoặc quá hạn cho kết quả `Unknown` (xem mục logic ba trạng thái), không ném lỗi.

## Luật theo argument (command line)

Điều kiện soi toàn bộ command line, một loại lá trong cây điều kiện của bộ lọc. Dùng để tách một chương trình trong nhiều cái cùng chạy từ một tệp — `java.exe` thì có nhiều, nhưng chỉ cái có `minecraft` trong command line mới là game.

Command line không đổi trong đời một tiến trình, nên nó được đọc đúng một lần lúc tiến trình xuất hiện rồi nằm sẵn trong [bảng process](#L218) — bộ lọc theo argument vì thế không tốn một lời gọi hệ điều hành nào, kể cả khi sửa bộ lọc rồi Save. Command line không đọc được (tiến trình được bảo vệ, hoặc thiếu [SeDebugPrivilege](#L248)) cho kết quả `Unknown`, và bộ lọc không áp dụng — hướng an toàn.

## Bộ lọc tiến trình (tên → điều kiện → hành động)

Hình dạng của `ProcessRule` từ config v3: một cái **tên** người dùng tự đặt, một **cây điều kiện**, và **hành động** (policy nào + có bám theo tiến trình con không). Tên tồn tại vì một cây điều kiện không còn đọc lướt qua trong ô lưới được nữa — "Minecraft" thì được, còn cây thì nằm sau một cú bấm.

Lưới ở tab Tiến trình vì thế còn: bật/tắt, tên, câu tóm tắt điều kiện, policy, nút Sửa. Câu tóm tắt do `ConditionTextBuilder` dựng, có dịch, nên cột đó phải bind qua `LocalizationScope.Version` mới đổi theo ngôn ngữ.

## Cây điều kiện (ConditionGroup / LeafCondition)

Cấu trúc đệ quy thay cho hai ô cố định của v2. Nút gốc trừu tượng `ProcessCondition` mang cờ `Negate`; `ConditionGroup` có `Operator` (`All`/`Any`) và danh sách con; các lá (`ProcessNameCondition`, `CommandLineCondition`) mang kiểu so khớp và giá trị. Lưu bằng `[JsonPolymorphic]` với khoá phân biệt `kind` — chuỗi `group`/`process`/`commandLine` là **định dạng file**, không đổi tên được.

Toán tử nằm ở nhóm chứ không nằm giữa hai dòng: đó là lý do giao diện không phải một ô gõ biểu thức trá hình, vì không có toán tử nào để đặt giữa hai dòng thì cũng không có độ ưu tiên nào để nhầm. Thêm loại đối tượng mới (tiến trình cha, tài khoản, tiêu đề cửa sổ) = thêm một lớp lá và một dòng `[JsonDerivedType]`.

Migration v2→v3 ở `ConfigStore.Migrate`: hai ô cũ thành một nhóm `All` gồm lá tiến trình, cộng lá argument nếu ô argument có điền. Ô argument rỗng KHÔNG sinh ra dòng nào — nó vốn không được xét, và một bộ lọc chưa ai đụng tới thì không nên mở ra trông như đang sửa dở.

## Logic ba trạng thái khi đánh giá điều kiện (ConditionResult)

`ConditionResult` có bốn giá trị chứ không phải bool, và hai trong bốn là thứ giữ cho bộ lọc không tóm cả máy:

* `Unknown` — dữ liệu không đọc được (command line của tiến trình hệ thống, đường dẫn của tiến trình 64-bit nhìn từ host 32-bit), hoặc mẫu regex hỏng/quá hạn. `NOT` **không** đảo nó. Nếu đảo thì `argument KHÔNG chứa X` sẽ đúng với mọi tiến trình không đọc được command line — tức gần như toàn bộ hệ thống.
* `Ignored` — dòng chưa điền giá trị. Không kéo nhóm xuống "không" lúc người dùng đang gõ dở, cũng không nâng nhóm lên "có".
* Nhóm gộp theo kiểu Kleene: `All` gặp một `NoMatch` là chốt không; còn lại hễ có `Unknown` thì cả nhóm `Unknown`. `Any` gặp một `Match` là chốt có, kể cả khi bên cạnh có `Unknown`.
* Ở gốc, chỉ `Match` mới là áp dụng bộ lọc. `Ignored` (chưa điền gì) và `Unknown` đều là không áp dụng — cùng hướng an toàn mà bản hai ô đã chọn.

Độ sâu cây bị chặn ở `ProcessRuleMatcher.MaxDepth` (16) khi nạp: giao diện không dựng nổi cây sâu vậy, nhưng file config sửa tay thì có, và đệ quy phải dừng trước khi stack dừng. Quá sâu trả `Unknown`, không phải `Match`.

## DNF (dạng chuẩn tuyển, disjunctive normal form)

Biểu thức boolean viết thành "OR của các nhóm AND": `(a AND b) OR (c AND d) OR e`. Mọi biểu thức boolean đều quy được về dạng này, nên UI chỉ cần đúng hai tầng — danh sách nhóm, trong mỗi nhóm là danh sách điều kiện — mà vẫn diễn đạt được mọi thứ, không cần cấu trúc đệ quy. Giá phải trả là có biểu thức nở ra dài hơn khi khai triển: `a AND (b OR c)` phải viết thành `(a AND b) OR (a AND c)`.

## Query builder (bộ dựng điều kiện lồng nhau)

Kiểu giao diện cho biểu thức boolean tuỳ ý: mỗi nút là một nhóm có toán tử AND/OR của riêng nó, con của nhóm là điều kiện lá hoặc nhóm con, kèm nút "+ điều kiện" / "+ nhóm". Quen thuộc từ luật của Outlook, bộ lọc Notion, JQL builder của Jira. Diễn đạt được mọi biểu thức kể cả lồng sâu, nhưng model, phần lưu file, bộ so khớp và cả UI đều phải viết đệ quy.

## Cây biểu thức (AST) và parser điều kiện

Cách thứ ba: cho gõ thẳng chuỗi điều kiện (`exe = "java.exe" and args contains "minecraft" or exe = "chrome.exe"`), rồi parser dựng cây biểu thức (AST — abstract syntax tree) để so khớp. Gọn và mạnh nhất cho người dùng thạo, nhưng phải tự viết parser, tự báo lỗi cú pháp ở đúng vị trí, và người dùng phải học cú pháp — thường làm thêm ở "chế độ nâng cao" chứ không thay cho giao diện bấm chọn.

## Bảng process (`ProcessInventory`)

Bảng `pid → {pid, path, argument, parent_pid}` giữ trong RAM, **chạy từ lúc mở app chứ không đợi bật engine**, và tự cập nhật suốt đời ứng dụng. Đây là nguồn duy nhất mà tầng khớp luật ([`ProcessRuleTracker`](#L254)) đọc — nó không bao giờ hỏi lại hệ điều hành.

Ba nguồn dữ liệu, dựng theo đúng thứ tự này:

1. **Sự kiện [WMI](#L65)** `Win32_ProcessStartTrace` / `StopTrace`, hook TRƯỚC — tiến trình sinh ra trong lúc quét lượt đầu không được rơi vào khe giữa hai bước.
2. **Một lượt quét đầy đủ**, đồng bộ ngay trong `Start()`.
3. **Đối chiếu định kỳ 5 giây**: so bảng với danh sách tươi. Đây là thứ làm bảng **tự lành** — một sự kiện WMI bị rơi, hay tiến trình sinh ra lúc máy ngủ, chỉ sai tối đa 5 giây thay vì sai vĩnh viễn. WMI hỏng hẳn thì vòng này chạy mỗi 750ms và thành nguồn duy nhất.

Ba bất biến giữ cho nó đúng:

* **Có khoá = đã hỏi rồi**, không hỏi lại. Command line không bao giờ đổi sau khi tiến trình khởi động, nên lý do duy nhất phải đọc lại là đã quên.
* **Giá trị `null` là một CÂU TRẢ LỜI** ("đã hỏi, không đọc được"), không phải chỗ trống — trừ khi handle không mở được lần nào, thường là tiến trình còn non hơn cả sự kiện báo nó, và lượt đối chiếu sau thử lại.
* **`(pid, StartedUtc)` mới là danh tính.** Windows cấp lại pid trong vài giây; hai tiến trình không thể cùng pid ở cùng một thời điểm, nên cặp này trả lời dứt khoát "còn đúng tiến trình cũ không?", chỗ mà so tên chỉ trả lời "có vẻ đúng".

## API Windows dùng để đọc tiến trình

Ba lời gọi, chọn vì rẻ nhất cho đúng một dữ kiện. Đo thật trên máy 640 tiến trình:

| Dữ kiện | API | Chi phí | Vì sao không dùng cách quen thuộc |
|---|---|---|---|
| pid + **parent_pid** + tên + session + thời điểm tạo, cho cả máy | `NtQuerySystemInformation(SystemProcessInformation)` | **11.7ms** | `Process.GetProcesses()` gọi đúng syscall này bên dưới rồi cấp phát một object cho từng tiến trình — và **không hề lộ parent_pid** ra ngoài |
| full path | `QueryFullProcessImageNameW` | ~0.05ms | `Process.MainModule` cần quyền `PROCESS_VM_READ` và phải duyệt danh sách module để trả lời cùng câu hỏi |
| **argument** (command line) | `NtQueryInformationProcess(ProcessCommandLineInformation)` | ~0.03ms | WMI `Win32_Process.CommandLine` mất **210ms cho MỘT tiến trình**; đọc PEB (Process Environment Block — vùng nhớ trong chính tiến trình đích) thủ công thì cần `PROCESS_VM_READ`, 3–4 lượt `ReadProcessMemory`, và một layout struct thứ hai cho tiến trình 32-bit |

Đọc path + argument cho **toàn bộ** 640 tiến trình hết 19.2ms, tức quét đầy đủ cả máy đủ 4 giá trị ≈ **31ms**. Chính con số này cho phép quét lượt đầu chạy đồng bộ ngay trong `Start()` thay vì phải đẩy sang luồng nền như trước.

`ProcessCommandLineInformation` có từ Windows 8.1; .NET 8 tối thiểu Windows 10 1607 nên luôn khả dụng. Hai information class này không có trong tài liệu chính thức nhưng ổn định suốt hai thập kỷ (ProcessHacker, Sysinternals dùng chúng) — dù vậy mọi lỗi ở đây đều được coi là "không đọc được", không bao giờ ném.

## SeDebugPrivilege

Chạy quyền administrator **không đồng nghĩa** với có đặc quyền này: nó nằm sẵn trong token nhưng ở trạng thái **DISABLED**, mà đặc quyền disabled thì không được tính khi kiểm tra quyền truy cập. Không bật nó thì `OpenProcess` trượt với mọi tiến trình ở **session 0** (SYSTEM / LOCAL SERVICE / NETWORK SERVICE) và của user khác ⇒ path và argument của chúng đọc ra `null`, và bộ lọc theo argument im lặng ngừng khớp với chúng.

Bật bằng `OpenProcessToken` + `LookupPrivilegeValue` + `AdjustTokenPrivileges`, một lần lúc bảng process khởi động. Bẫy: `AdjustTokenPrivileges` trả về **thành công kể cả khi không bật được gì** — token không có đặc quyền thì nó vẫn `true` kèm `ERROR_NOT_ALL_ASSIGNED` (1300) ở `GetLastError`; đó là cách duy nhất phân biệt hai trường hợp.

## Tách bảng process khỏi khớp luật (`ProcessInventory` / `ProcessRuleTracker`)

Hai việc có **vòng đời khác hẳn nhau**, nên tách làm hai lớp:

| | `ProcessInventory` | `ProcessRuleTracker` |
|---|---|---|
| Việc | thu thập: máy này đang chạy gì | quyết định: cái nào cần redirect |
| Sống theo | **ứng dụng** (mở app là chạy) | **engine** (bật tool mới chạy) |
| Phát sự kiện | `ProcessStarted` / `ProcessStopped` | `ProcessAttached` / `ProcessDetached` |
| Nguồn đọc | hệ điều hành | bảng RAM của inventory |

Cái được:

* **Bật engine không phải đi khám phá lại cả máy** — chỉ đọc bảng đã có sẵn.
* **Sửa bộ lọc rồi Save tốn một lượt tra từ điển cho mỗi tiến trình**, thay vì một truy vấn WMI 210ms cho mỗi tiến trình đang redirect (30 tiến trình = 5–10 giây đơ giao diện).
* **Bỏ được `ProcessTreeMonitor`** (mỗi tiến trình gốc một luồng poll BFS 500ms): bảng biết parent_pid của **mọi** tiến trình, kể cả tiến trình đã chạy từ trước. Nhờ vậy mở app khi Chrome đang mở sẵn thì 30 tab của nó **được nhận làm con** — điều mà poller cũ không bao giờ làm được, vì nó chỉ thấy tiến trình sinh ra sau khi nó bắt đầu canh.
* **Cha giả bị loại**: Windows không xoá parent_pid khi cha chết, nên một pid đã được cấp lại có thể bị nhận nhầm làm cha. Cha mà `StartedUtc` **muộn hơn** con thì chắc chắn không phải cha.

## Quét bù khi sự kiện dồn (`ProcessEventBacklog`, ĐÃ GỠ 07/09/2026)

WMI giao sự kiện của **một** watcher lần lượt từng cái, không bao giờ song song — đo thật: 60 handler trong một đợt, không cặp nào chồng nhau. Hồi bảng process còn đọc command line **bằng WMI** (~210ms mỗi tiến trình), một trình duyệt mở 30 tiến trình con làm cái cuối chờ vài giây mới được redirect. Cơ chế chữa cháy là đo **tuổi của chính sự kiện** (`TIME_CREATED`, thuộc tính mọi lớp sự kiện WMI đều có): trong một đợt burst, tuổi tăng đúng bằng chi phí handler — 5ms → 263 → 528 → 790 → … → 15.459ms — tức nó nói thẳng chiều dài hàng chờ bằng mili giây thật. Quá ngưỡng thì đọc cả máy một lượt thay vì từng cái.

Đã gỡ cùng ô `Quét bù khi trễ` ở tab Cài đặt vì lý do sinh ra nó không còn: WMI nay **chỉ báo sự kiện** start/stop, còn số liệu (đường dẫn, command line, thời điểm khởi động) đọc bằng WinAPI trong `ProcessInventory.WithDetails` — dưới một mili giây mỗi tiến trình — nên hàng chờ không kịp hình thành. Lưới an toàn còn lại là vòng reconcile 5 giây, vốn cũng chữa được sự kiện WMI bị rơi.

Bài học giữ lại: khi nghi một hàng đợi sự kiện bị dồn, **tuổi của sự kiện** là thước đo sẵn có và trung thực hơn mọi bộ đếm tự chế.

## Chặng đi và chặng về của UDP relay (egress leg / reply leg)

Một luồng UDP bị chuyển hướng có hai nửa độc lập, khác hẳn TCP. **Chặng đi**: gói của tiến trình bị NAT về `127.0.0.1:<cổng relay>`, relay đọc được rồi gửi ra ngoài bằng **socket của chính tool** (cổng ephemeral hoàn toàn khác cổng gốc). **Chặng về**: gói trả lời từ đích thật quay về **cổng ephemeral đó**, không phải cổng của tiến trình — bảng NAT khoá theo cổng nguồn gốc nên tra không ra. Vì vậy chặng về **bắt buộc phải do phần mềm tự dựng lại**: đọc phản hồi trên socket upstream rồi bơm ngược vào tiến trình (`InjectReplyToProcessAsync`) để middleware NAT ghi lại địa chỉ thành `đích gốc → nguồn gốc`. Thiếu vòng đọc này thì UDP thành **một chiều**: câu hỏi đi được, câu trả lời mất hẳn — biểu hiện rõ nhất là DNS (UDP/53) không bao giờ có đáp án.

## Đi thẳng không chuyển hướng (UDP passthrough)

Với UDP, "Direct" **không** thể làm bằng cách cho gói qua relay rồi relay gửi hộ — xem [chặng đi và chặng về](#L280). Nên khi một luồng UDP được định tuyến ra `Direct`, công cụ quyết định **ngay trên đường gói tin**, trước khi NAT chạm vào: trả `PacketDisposition.Pass` để gói đi ra nguyên trạng từ chính socket của tiến trình, y như khi tiến trình không bị theo dõi. Trả lời quay về đúng socket đó, không cần bảng NAT.

Đổi lại, luồng đó mang **địa chỉ thật** của máy. Với DNS thì đây đúng bằng mức phơi bày mà chế độ `SystemSniff` đã tuyên bố sẵn. Muốn DNS đi qua outbound thì thêm một luật khớp được nó — luật `Protocol` = `udp`, hoặc `Port` = `53` — khi đó luồng ra một outbound thật chứ không còn là `Direct`, và nó quay lại đi qua relay như cũ.

Câu hỏi chỉ được đặt **một lần cho mỗi luồng**, lúc luồng chưa có bản ghi NAT. Luồng đã chuyển hướng thì giữ nguyên chuyển hướng kể cả khi câu trả lời đổi (bảng DNS ngược vừa học được tên mới): nửa luồng qua relay nửa luồng đi thẳng sẽ chờ trả lời ở hai nơi khác nhau. Riêng `Block` vẫn đi qua relay để bị thả ở đó — thả bằng cách "cho đi thẳng" thì đúng là làm rò rỉ thứ cần chặn.

## Matcher `Protocol` (lọc theo tcp/udp)

Kiểu so khớp luật nhìn vào **giao thức tầng vận chuyển**, pattern là `tcp` hoặc `udp`. Khác mọi matcher theo tên ở chỗ nó **luôn có dữ liệu để so**: không cần SNI, không cần bảng DNS ngược, không cần bắt tay xong. Đây là cách duy nhất viết được luật kiểu "toàn bộ UDP của tiến trình này", gồm cả DNS — thứ mà `Wildcard *` cũng bỏ sót vì gói DNS không mang tên miền nào cả. Pattern không phải `tcp`/`udp` thì **không khớp gì hết** (chứ không phải khớp tất cả): gõ sai một luật phải mất tác dụng, không được âm thầm ôm luôn traffic mà luật đó sinh ra để loại trừ.

## Luồng thoát (escaped flow)

Kết nối TCP mà bắt tay (SYN) đã diễn ra **trước khi** công cụ kịp giành lấy nó — tiến trình được attach khi đã có socket mở, hoặc sự kiện tầng SOCKET thua cuộc đua với gói SYN (xem [rò rỉ SYN](#L37)). Luồng như vậy **không bao giờ được chuyển hướng giữa chừng**: nửa kết nối qua relay, nửa đi thẳng là chết kết nối. `NatRedirectMiddleware` chỉ có hai lựa chọn cho nó: **cho qua** (mặc định — kết nối sống nhưng lộ IP thật tới đích đó, ghi cảnh báo "passing escaped flow" một lần mỗi luồng) hoặc **thả** (`RedirectOptions.BlockEscapedFlows` = true — không rò rỉ gì, ứng dụng thấy kết nối chết và mở kết nối mới, kết nối mới được bắt từ SYN).

## Ba tầng cấu hình (giao diện → snapshot RAM → file json)

Cấu hình sống ở ba nơi, và chỉ đi theo một chiều. **Tầng giao diện** là `AppServices.Config` — thứ các ViewModel bind và sửa thẳng, kể cả khi đang gõ dở. **Tầng snapshot** là bản deep-copy (`ConfigStore.Clone`, JSON round-trip) được lấy đúng lúc bấm Save và giao cho engine (`RedirectEngine.Start/ApplyConfig`); engine, `ProcessRuleTracker` và `RoutingPolicyResolver` chỉ nhìn bản này, không chia sẻ một `List` nào với giao diện. **Tầng file** là `proxydivert.config.json`, ghi từ cùng snapshot đó (mật khẩu được DPAPI bọc lại trên một bản copy nữa). Lúc mở app: file → tầng giao diện; snapshot chỉ xuất hiện khi bật tool hoặc Save.

Lý do tách: trước đây engine giữ CÙNG tham chiếu với giao diện, nên một luật vừa thêm vào lưới đã được so khớp với tiến trình mới trước khi bấm Save, và luồng WMI duyệt `_rules` đúng lúc lưới đang `Add` vào cùng danh sách. Save, ghi file và `ApplyConfig` chạy trên thread pool, xếp hàng tuần tự (`AppServices.Enqueue`), không bao giờ trên UI thread.

## Save áp lên kết nối đang chạy (đóng kết nối lệch outbound, reset luồng thoát)

Sau khi engine nhận snapshot mới, ba việc xảy ra với những gì ĐANG chạy, ngoài việc tiến trình/kết nối mới đi theo snapshot:

1. **Tiến trình đã track**: luật vẫn khớp thì entry được thay tại chỗ (`TrackedProcess.WithRule`) — không Detach/Attach, không đóng handle SOCKET, con theo cha (`ProcessRuleTracker.ReconcileTrackedWithRules`). Luật hết khớp mới Detach.
2. **Kết nối TCP đang qua relay** (`LiveTcpConnectionRegistry`): resolve lại từng kết nối bằng resolver mới; outbound KHÁC (kể cả Block, kể cả "không policy nào nhận nữa" ⇒ Direct) thì **đóng** — huỷ token forward và đóng socket phía tiến trình, ứng dụng thấy đứt và tự mở lại, kết nối mới được bắt từ SYN. Cùng outbound thì giữ nguyên.
3. **Luồng thoát** (xem [Luồng thoát](#L296)): `IProcessRedirector.ResetEscapedFlows` đánh dấu mọi flow TCP đang track mà không có bản ghi NAT; gói tiếp theo của flow đó bị thả và tiến trình được bơm một gói **RST** (`TcpResetPacketBuilder`: seq = ack-number của gói vừa gửi, tức đúng byte tiến trình đang chờ, nên stack nhận ngay theo RFC 5961). Chỉ làm lúc Save; lúc bật tool luồng thoát vẫn được cho qua như cũ.

## BlockQuic và UDP đi theo quyết định TCP

`RoutingPolicy.BlockQuic` (mặc định bật) sinh ra để trình duyệt không lách qua proxy bằng QUIC (UDP/443) trong khi TCP của nó đang bị tunnel. `ResolveUdp` vì thế **resolve mục tiêu như TCP trước**: TCP ra Direct (policy Direct, hoặc không luật nào nhận) ⇒ UDP ra Direct luôn, kể cả QUIC — không có proxy nào để lách. TCP ra Block ⇒ Block. TCP qua proxy/VPN thì `UdpMode` mới lên tiếng: `ThroughOutbound` + outbound chở được UDP ⇒ đi cùng tunnel (QUIC cũng vậy); còn lại QUIC bị chặn nếu BlockQuic, UDP thường theo `UdpMode.Direct`/`Block`.

Bài học đứng sau: chặn QUIC bằng cách **thả gói im lặng** khiến Chrome gửi lại Initial theo lũy thừa (đo trên log: 0,13 → 0,26 → 0,5 → 1 → 2 → 5 → 10 giây) và chỉ rơi về TCP sau vài giây. Với policy Direct, việc đó biến "mở trang mất 3–10 giây" thành triệu chứng của… một cờ mặc định. Hướng tốt hơn cho chế độ proxy (chưa làm): trả ICMP Port Unreachable để QUIC thất bại tức thì.

## Hàng đợi gói của driver WinDivert (QueueLength / QueueTime / QueueSize)

Gói driver đã bắt nằm trong hàng đợi chờ pump `WinDivertRecv`. Mặc định 4096 gói / 2000 ms / 4 MB; gói nằm quá lâu **bị thả**. Pump NETWORK là một luồng duy nhất, nên một cú nghẽn (đọc bảng kernel cho một loạt SYN, hay bất kỳ việc đồng bộ nào trong middleware) dài hơn hạn thời gian là mất SYN hoặc SYN-ACK của relay ⇒ tiến trình chỉ bắt tay xong sau khi bộ đếm phát lại nổ: 1 s, rồi 3 s, rồi 7 s. `ProcessRedirector.OpenNetworkHandle` đặt 16384 gói / 8000 ms / 16 MB (tối đa driver cho phép là 16384 / 16000 / 32 MB) để cú nghẽn thành **chậm** chứ không thành **mất**. Muốn biết pump có nghẽn không: dòng log `accepted {ms}ms after its SYN` của `TcpRelayServer` và `handshake …ms` trong dòng `tcp pid=… up via …` của engine.

## MTU / MSS của tunnel và hố đen MTU (MTU black hole)

**MTU** là kích thước gói lớn nhất một chặng mạng chở được; **MSS** là phần dữ liệu TCP lớn nhất trong một gói, bằng `MTU − 40` (IPv4) hoặc `MTU − 60` (IPv6). Userspace TCP/IP stack của tunnel lấy MTU từ kênh (`TcpIpStack` → `TcpConnection(linkMtu: _channel.Mtu)`), suy ra `_localMss` và quảng cáo nó trong SYN để phía kia không gửi segment to hơn.

MTU **bên trong** tunnel phải nhỏ hơn MTU đường truyền thật đủ để chứa toàn bộ vỏ ngoài. Với L2TP/IPsec NAT‑T, vỏ ngoài là IP(20) + UDP(8) + ESP header/IV(24) + padding/trailer + ICV(12) + L2TP(6–8) + PPP(4) ≈ 80–95 byte. PPP mặc định MTU 1400 ⇒ gói ngoài ≈ 1480–1495 byte: vừa đủ trên Ethernet 1500, nhưng **vượt** trên đường PPPoE (1492) hay bất kỳ chặng nào nhỏ hơn.

Khi đó sinh **hố đen MTU**: gói nhỏ (SYN, SYN‑ACK, ACK) qua được nên `connect` báo thành công, còn gói lớn đầu tiên (thường là ServerHello + chuỗi chứng chỉ TLS) bị router thả im lặng. Phát lại cũng đúng kích thước đó nên **thả tiếp** — kết nối treo tới khi ứng dụng bỏ cuộc, không bao giờ tự khỏi. Dấu vân tay trên log: `TcpRelayServer: connection srcPort=… closed, up=~1900B down=0B 30.0s` — gửi đi đúng một ClientHello, nhận về **0 byte**.

Trên lý thuyết ICMP "fragmentation needed" sẽ báo path MTU thật và `TcpConnection.OnIcmpPacketTooBig` hạ `_sendMss` (PMTUD, RFC 1191); thực tế nhiều mạng chặn sạch ICMP nên không bao giờ có tin báo — vì thế cách chữa là **hạ MTU của tunnel** cho chắc, không trông vào PMTUD.

## Cửa sổ gửi (SND.WND) và luật cập nhật cửa sổ theo thứ tự

Bên nhận quảng cáo **cửa sổ nhận** trong mỗi gói TCP: "tôi còn chừng này chỗ trống". Bên gửi lưu con số đó thành **cửa sổ gửi** (`SND.WND`) và không bao giờ để số byte đang bay vượt quá nó. Cửa sổ về 0 nghĩa là "đừng gửi nữa"; bên gửi chuyển sang **zero-window persist** — mỗi lần chỉ bắn **1 byte** thăm dò, giãn dần theo cấp số nhân, cho tới khi bên kia báo còn chỗ.

Gói tới có thể **đến sai thứ tự**, nên không phải gói nào cũng được quyền sửa cửa sổ: RFC 9293 giữ thêm `SND.WL1` (số thứ tự của gói đã cập nhật cửa sổ lần cuối) và `SND.WL2` (số ack của nó), chỉ nhận cập nhật khi gói **mới hơn**. So sánh số thứ tự TCP là so sánh **có dấu 32 bit** (`(int)(a - b) > 0`) vì không gian số thứ tự quay vòng.

**Bẫy đã vấp** (`TcpConnection`, sửa 2026-09-07): SYN-ACK phải **gán thẳng** `SND.WND = SEG.WND`, `SND.WL1 = SEG.SEQ`, `SND.WL2 = SEG.ACK` (RFC 9293 §3.10.7.3, bước 5 của SYN-SENT), KHÔNG đi qua luật "mới hơn" ở trên. Nếu để nó đi qua, `SND.WL1` lúc đó vẫn là 0, mà so sánh có dấu với 0 thì **mọi ISN từ 2^31 trở lên đều bị coi là cũ** — và bị coi là cũ ở mọi gói sau đó luôn, vì WL1 không bao giờ được gán. Cửa sổ gửi kẹt ở 0 suốt đời kết nối: bắt tay xong, bên kia vẫn ACK, nhưng request chỉ rỉ ra 1 byte mỗi lần thăm dò rồi server bỏ cuộc. Server chọn ISN **ngẫu nhiên** ⇒ hỏng như tung đồng xu, mỗi kết nối một lần — nhìn từ ngoài y hệt "mạng phập phù" chứ không giống bug. Dấu vân tay trong log: dòng `ipstack` báo `sent=5 acked=5 rcvd=0 sndwnd=0` (5 = số lần thăm dò), còn `TcpRelayServer` báo `up≈1930B down=0B`.

Bài học đo đạc: mọi test bắt tay sẵn có đều dùng ISN nhỏ (9000) nên không bao giờ chạm nửa còn lại của không gian số thứ tự. Test cho giao thức có so sánh quay vòng phải phủ **cả hai nửa** và hai mép của phép so sánh.

## Vòng đời kết nối VPN tách khỏi engine WinDivert

Đường hầm VPN **không** thuộc một lần chạy chuyển hướng. Một outbound VPN có instance chính là đường hầm (tiến trình `wireproxy`, hoặc phiên của driver chạy trong process), tức là **phiên của người dùng với nhà cung cấp VPN** — bật/tắt WinDivert không phải lý do để dựng lên hay giật xuống.

Cách chia trách nhiệm (sửa 2026-09-07):

- `OutboundSourceFactory` và `VpnConnectionKeeper` là **singleton** trong container [DI](Glossary-vi.md#L97), sống bằng đời ứng dụng; `RedirectEngine` chỉ mượn. `Engine.Start` chỉ gọi `ApplyOutbounds` để đối chiếu instance với cấu hình, `Engine.Stop` **không** dispose cái nào.
- Đường hầm nào được giữ là do cờ `Outbound.KeepConnected`, lưu trong file cấu hình ⇒ mở lại app thì các đường hầm đang bật tự dựng lại. Nút Connect/Disconnect ở tab Outbounds chỉ lật cờ này rồi gọi `Sync`, không đụng tới engine.
- **Bật** WinDivert thì `ConnectRoutedVpns` bật cờ cho mọi VPN mà [bộ lọc tiến trình](Glossary-vi.md#L181) đang bật định tuyến qua (bộ lọc → các [policy](Glossary-vi.md#L145) → outbound, xem `OutboundUsage.RoutedOutboundIds`); **tắt** WinDivert thì không ngắt gì cả.
- Ngược lại, đường hầm đang có bộ lọc chạy qua thì nút Disconnect bị khoá: ngắt nó sẽ làm mọi kết nối bộ lọc đó bắt được rơi vào lỗi mà trên màn hình không có gì giải thích.

**Bẫy đi kèm**: `KeptVpnTunnel.Dispose` phải gọi `_factory.Invalidate(outboundId)`. Trước đây `Engine.Stop` dispose cả factory nên không ai để ý; bỏ chỗ đó đi mà không invalidate thì "Disconnect" chỉ dừng vòng giám sát, còn tiến trình `wireproxy` vẫn chạy và vẫn nói chuyện với máy chủ VPN.

## Hai cách phát hiện tiến trình (sự kiện tiến trình / nghe socket)

Câu hỏi công cụ phải trả lời là "traffic này có thuộc luật nào không", mà luật thì mô tả **tiến trình**. Có hai đường đi tới câu trả lời, người dùng chọn ở tab Cài đặt và **khoá khi engine đang bật**.

**1. Sự kiện tiến trình** (mặc định). Đợi hệ điều hành báo có tiến trình mới, đối chiếu bộ lọc, khớp thì attach. Nguồn sự kiện lại có hai lựa chọn con, đứng sau `IProcessEventSource`:
- **ETW** — đọc thẳng provider `Microsoft-Windows-Kernel-Process` (keyword `0x0010`, EventId 1/2) bằng một phiên trace riêng tên `ProxyDivert-Process`. Sự kiện tới trong khoảng 1ms.
- **WMI** — `Win32_ProcessStartTrace`/`StopTrace`, chính là sự kiện ETW đó sau khi dịch vụ WMI gói lại; thêm một dịch vụ và một chặng COM, và sự kiện của một watcher được giao **lần lượt từng cái**.
Nguồn nào không khởi động được thì tự lùi sang nguồn kia, rồi mới tới quét định kỳ 750ms.

**2. Nghe socket** (`ProcessDetectionMode.NetworkSniff`). Mở **một** handle WinDivert lớp SOCKET cho cả máy (filter `tcp or udp`, bắt buộc `Sniff | RecvOnly` — xem `SocketTracker.OpenMachineWideHandle`), mỗi sự kiện đã mang sẵn `ProcessId`. Với pid chưa gặp, `RedirectOptions.ShouldTrackProcess` hỏi ngược lên `ProcessRuleTracker.ShouldRedirect`: đọc thông tin tiến trình (`ProcessInventory.EnsureKnown`, quét máy có tiết chế 50ms), khớp bộ lọc, không khớp thì **lần ngược chuỗi cha** tới tổ tiên đang được theo dõi. Câu trả lời được cache theo pid; trả về `null` nghĩa là "chưa đọc được" và **không** cache, nếu không một tiến trình vừa sinh sẽ bị loại vĩnh viễn. Mode này không cần sự kiện tiến trình nên WMI/ETW đều tắt, bảng process chỉ còn quét định kỳ.

**Connection có sẵn**: lớp SOCKET chỉ nói khi có thao tác socket, nên kết nối mở từ trước khi handle tồn tại sẽ im lặng mãi mãi. Hai chỗ bù: (1) ngay khi một pid được xét là "của ta", `AcceptPid` gọi `PrePopulateForPid` đọc bảng kernel để nạp các flow sẵn có của nó; (2) lúc `ProcessRuleTracker.Start`, **cả hai mode** đều chạy `MatchEverything()` một lượt để nhận các tiến trình đang chạy — nếu chỉ chờ socket mới thì chương trình đã kết nối từ trước và cứ dùng kết nối cũ sẽ không bao giờ bị chuyển hướng.

Đánh đổi: cách 1 attach sớm hơn (ngay khi tiến trình sinh ra, trước cả kết nối đầu) nhưng phụ thuộc sự kiện tới kịp; cách 2 xét đúng lúc mở kết nối và chỉ tốn 1 handle thay vì mỗi pid một handle, nhưng lớp SOCKET chỉ nghe được chứ không giữ được nên SYN vẫn có thể ra trước quyết định — lúc đó `TryReconcileFromKernel` tra bảng kernel để bắt lại.

**Bẫy đã sửa cùng đợt**: `AttachChild` tạo tiến trình con với `includeChildren = false`, nên **cháu không bao giờ được nhận** — cây con dừng đúng một tầng, dù `AdoptChildren` tự mô tả là đi hết cây. Con nay kế thừa `IncludeChildren` của cha.

## Tái dùng PID (PID reuse)

Windows cấp lại số PID của tiến trình vừa thoát cho một tiến trình mới, nhiều khi chỉ sau vài giây. Mọi bảng cache **khoá theo pid trần** (verdict "nên track hay không", cây cha–con, danh sách con đã thấy) vì thế có thể trả lời đúng cho tiến trình cũ mà sai cho tiến trình mới cùng số. Cách phòng chuẩn: khoá theo cặp `(pid, thời điểm start)` hoặc xoá cache khi nhận sự kiện tiến trình thoát. `ProcessInventory.IsDifferentProcess` đã làm vậy; các cache trong `SocketTracker._pidDecisions` và `ProcessTreeMonitor._knownDescendants` thì chưa.

## SafeHandle và P/Invoke

`P/Invoke` là cách C# gọi hàm native (DLL của WinDivert, iphlpapi, ntdll). `SafeHandle` là lớp bọc handle native có đếm tham chiếu: khi tham số P/Invoke khai báo kiểu `SafeHandle`, marshaller tự `DangerousAddRef` trước lúc gọi và `Release` sau, nên `ReleaseHandle` (đóng handle thật) bị hoãn tới khi không còn lời gọi native nào đang dùng nó. Nếu code lấy `DangerousGetHandle()` ra `IntPtr` rồi truyền đi, lớp bảo vệ này mất tác dụng: một thread vẫn đang `WinDivertRecv` trên số handle mà thread khác đã đóng, và kernel có thể đã cấp số đó cho object khác.

## FIN-WAIT-2 và half-close

Đóng TCP là hai chiều: mỗi bên gửi FIN riêng. Bên gửi FIN trước, sau khi FIN được ACK, vào trạng thái **FIN-WAIT-2** và chờ FIN của bên kia; đây là "half-close" — chiều gửi đã đóng nhưng chiều nhận vẫn mở. Nếu peer không bao giờ gửi FIN, kết nối treo ở FIN-WAIT-2 mãi. Kernel thật có timeout cho trạng thái này; [userspace stack](#L73) của TqkLibrary.VpnClient chỉ có timer ở TIME-WAIT, nên `Dispose` của `VpnNetworkStream` (chỉ `CloseSend`, tức chỉ gửi FIN) có thể để lại kết nối sống mãi trong bảng của stack. Muốn dứt điểm phải gửi RST (abort) thay vì FIN.

## Khay hệ thống (system tray / notification area)

Vùng biểu tượng nhỏ cạnh đồng hồ. Một tiến trình đăng ký biểu tượng của mình bằng `Shell_NotifyIcon`; WPF không có sẵn thứ này, ở đây dùng gói `Hardcodet.NotifyIcon.Wpf` (`TaskbarIcon`). Điểm phải nhớ: **`TaskbarIcon` gắn tay vào sự kiện của `Application` ngay trong constructor**, nên nếu đặt nó vào `Application.Resources` thì bất kỳ ai duyệt qua tài nguyên của ứng dụng — kể cả một test chạy trên luồng khác — cũng dựng ra một biểu tượng khay thật và ném `InvalidOperationException` vì sai luồng dispatcher. Vì thế `Views/TrayIcon.xaml` KHÔNG merge vào `App.xaml`, mà `App.OnStartup` tự nạp đúng một lần. Menu chuột phải là `ContextMenu` WPF thường: nó không nằm trong cây trực quan nào nên `DynamicResource` và style ngầm tự rơi về `Application.Resources`, tức là ăn sẵn theme và bản dịch.

Windows 11 mặc định giấu biểu tượng của ứng dụng mới vào ngăn tràn ("Show Hidden Icons"); không thấy biểu tượng ngay không có nghĩa là nó chưa được đăng ký.

## Scheduled Task và RunLevel HighestAvailable

Cách cho một chương trình **cần quyền Administrator** tự chạy lúc đăng nhập mà không hiện UAC. Khoá `Run` trong registry KHÔNG làm được: Windows **bỏ qua im lặng** mọi entry `Run` trỏ tới file thi hành có manifest `requireAdministrator` — không lỗi, không log, chỉ là không chạy. Thay bằng một task của Task Scheduler với `<LogonTrigger>` và `<Principal><RunLevel>HighestAvailable</RunLevel><LogonType>InteractiveToken</LogonType></Principal>`.

`ProxyDivert` đăng ký task qua `schtasks.exe /Create /TN … /XML … /F`. Bẫy: file XML phải là **UTF-16 có BOM**; đưa UTF-8 vào thì schtasks báo "The task XML contains a value which is incorrectly formatted or out of range", một thông điệp không hề nhắc tới encoding. Muốn kiểm XML mà không đụng vào máy: `$svc = New-Object -ComObject Schedule.Service; $svc.Connect(); $t = $svc.NewTask(0); $t.XmlText = $xml` — gán mà không ném là XML hợp lệ.

Các mặc định `DisallowStartIfOnBatteries`, `StopIfGoingOnBatteries`, `RunOnlyIfIdle`, `RunOnlyIfNetworkAvailable` đều phải đặt `false`: chúng sinh ra để hoãn việc có thể hoãn, còn một bộ lọc mạng thì không — traffic đi ra trước khi engine kịp bật là traffic không được chuyển hướng.

## ICO đa kích thước

File `.ico` là một bảng mục lục (`ICONDIR` 6 byte + mỗi ảnh một `ICONDIRENTRY` 16 byte) rồi tới dữ liệu từng ảnh. Mỗi ảnh có thể ở **hai dạng**: DIB (BITMAPINFOHEADER + pixel BGRA xếp từ dưới lên + mặt nạ AND, và `biHeight` phải ghi gấp đôi chiều cao thật vì tính cả mặt nạ), hoặc PNG nhúng nguyên khối (hợp lệ từ Vista).

`tools/logo/Generate-AppIcon.ps1` ghi cỡ nhỏ (16–48) dạng DIB và cỡ lớn (từ 64) dạng PNG. Lý do không dùng PNG cho tất cả: `System.Drawing.Icon.ToBitmap()` ném `ArgumentOutOfRangeException` trên mục PNG, mà đó chính là đường mà thư viện khay đi qua để lấy `HICON`. Ngược lại không dùng DIB cho tất cả vì cỡ 256 dạng DIB tốn 270 KB một mình.

Một bẫy nữa khi render: `RenderTargetBitmap` chỉ dựng được `Pbgra32` (alpha đã nhân trước), còn ICO cần alpha thẳng — phải đi qua `FormatConvertedBitmap` sang `Bgra32`, bỏ bước này thì mọi pixel bán trong suốt bị tối đi.

## Cờ dòng lệnh khi khởi động (argument flag)

Cùng một file thi hành, hai cách vào: người dùng bấm thì hiện cửa sổ, task đăng nhập chạy thì không. Phân biệt bằng một tham số dòng lệnh — ở đây là `--minimized`, do `AppArguments.Parse` đọc từ `StartupEventArgs.Args`. Khác với `CliOptions` của bản console, parser này **bỏ qua tham số lạ thay vì báo lỗi**: một cửa sổ chạy lúc đăng nhập không có chỗ nào để in lời than, và từ chối chạy vì một chữ gõ sai thì trông y hệt như công cụ bị hỏng.

Kéo theo: khi cửa sổ có thể không bao giờ được `Show()`, `ShutdownMode` mặc định `OnLastWindowClose` là sai — phải chuyển sang `OnExplicitShutdown` và tự gọi `Shutdown()` ở đúng hai chỗ (mục Thoát trên menu khay, và nút X khi người dùng chọn "đóng là thoát").

## UIPI và thử nghiệm ứng dụng chạy quyền Administrator

**UIPI** (User Interface Privilege Isolation) chặn tiến trình có mức toàn vẹn thấp gửi thông điệp cửa sổ hoặc input tổng hợp vào tiến trình mức cao hơn. Hệ quả khi tự động kiểm thử `ProxyDivert` (manifest `requireAdministrator`) từ một shell **không** nâng quyền: `SendMessage(WM_CLOSE)` không có tác dụng gì, `SetCursorPos`/`mouse_event` không di chuyển được con trỏ vào cửa sổ của nó, cây UI Automation của nó không đọc được, và `Stop-Process` trả về "Access is denied".

Việc vẫn làm được: đọc danh sách biểu tượng khay bằng UI Automation (chúng thuộc `explorer.exe`, mức trung bình), và **bấm chuột vào chính biểu tượng khay đó** — cú double-click được shell chuyển tiếp nên đây là đường duy nhất điều khiển được ứng dụng elevated từ ngoài. Muốn kiểm phần còn lại thì phải chạy shell nâng quyền, hoặc bấm tay.

## God class (lớp ôm đồm)

Một lớp gánh nhiều mối quan tâm không liên quan (vòng đời, định tuyến, đo thời gian, chính sách IPv6, test tĩnh...) nên mỗi lần sửa một việc phải đọc lại và có nguy cơ làm hỏng các việc còn lại. Dấu hiệu: hàng trăm dòng, nhiều field nullable cùng bật/tắt theo trạng thái, constructor tự `new` các cộng sự nên không thay bằng bản giả để test được. Trong repo: `RedirectEngine` (641 dòng, 7 mối quan tâm), `SocketTracker` (578 dòng, 8 mối quan tâm), `NatRedirectMiddleware`, `ProcessRedirector`, `OutboundSourceFactory`.

## Strategy (mẫu chiến lược) thay cho switch

Khi một enum (`OutboundKind`, `VpnProtocol`, `ProcessDetectionMode`, address family v4/v6, tcp/udp) bị `switch`/`if` ở nhiều file, thêm một giá trị mới nghĩa là phải sửa tất cả các chỗ đó và dễ sót. Mẫu chiến lược đưa mỗi giá trị thành một lớp implement chung một interface (`IOutboundSourceBuilder`, `IVpnProtocolDescriptor`, `IProcessDetectionStrategy`, `ITransportFlowPolicy`), chọn một lần qua registry/dictionary, phần còn lại của code chỉ gọi interface. Chỉ đáng làm khi switch xuất hiện ở từ 2 nơi trở lên hoặc chủ dự án đã dự định mở rộng; một switch duy nhất, đầy đủ nhánh, có `default: throw` thì giữ nguyên.

## Aggregate (gốc tập hợp) cho cấu hình

Một đối tượng gốc sở hữu các đối tượng con và là nơi **duy nhất** được sửa chúng, để mọi ràng buộc toàn vẹn (xoá policy thì gỡ khỏi mọi filter, xoá outbound thì policy trỏ về Block, built-in không được đổi `Kind/Url`) nằm ở một chỗ thay vì được vá ở từng ViewModel, `ConfigStore.Load` và resolver. Với ProxyDivert đó là `AppConfig` với các method `RemovePolicy`, `RemoveOutbound`, `Normalize`. Hệ quả: CLI, import/export, menu tray đều đi qua cùng luật.

## Value object (đối tượng giá trị)

Kiểu nhỏ, bất biến, so sánh theo giá trị, dùng để thay chuỗi/số thô đa nghĩa (primitive obsession). Ví dụ `Outbound.Url` hiện là một `string` mang 4 hình dạng (proxy URL, file `.conf`, endpoint `sstp://`, ini `.vpn`) và bị parse ở 4 nơi; một `OutboundAddress` với `TryParse` cho phép báo lỗi ngay trong ô nhập thay vì lúc kết nối đầu tiên trên luồng relay. Tương tự `RelayEndpoint(protocol, family, port)` thay cho 4 int + 2 bool trong WinDivert.

## IAsyncDisposable và chuỗi dispose đồng bộ

`IDisposable.Dispose()` là đồng bộ; một thành phần bên dưới chỉ có `DisposeAsync()` (như `VpnTunnel`) buộc tầng trên phải `.Wait(timeout)`. Khi chuỗi có nhiều tầng (`VpnClientProxySource` → `OutboundSourceFactory` → `KeptVpnTunnel` → `VpnConnectionKeeper` → `AppServices`), các timeout cộng dồn (5s + 2s + ...) và block đúng luồng đang Save. Cách đúng: `IAsyncDisposable` từ dưới lên trên, chỉ chấp nhận `Wait` ở đúng một chỗ ngoài cùng (lúc app thoát), hoặc giao việc drop cho thread pool và chỉ await lúc shutdown.

## Job Object (Windows)

Đối tượng kernel gom nhiều tiến trình; với cờ `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, khi handle job đóng (kể cả vì tiến trình cha bị kill hoặc crash) mọi tiến trình con trong job bị kết thúc theo. Không có nó, `wireproxy.exe`/`ssh.exe` do app spawn sẽ sống mãi sau khi app chết vì `Dispose` không kịp chạy. Tạo bằng `CreateJobObject` + `SetInformationJobObject` + `AssignProcessToJobObject` ngay sau `Process.Start()`.

## Single-writer và Channel<T>

Mô hình một thread duy nhất được phép ghi vào một bảng trạng thái; mọi nguồn sự kiện (socket pump, reconcile từ bảng kernel, Add/Remove từ UI) chỉ đẩy yêu cầu vào `Channel<T>`, consumer đọc tuần tự và áp dụng. Lợi ích chính là **hợp đồng rõ** (sự kiện bắn ra từ đúng một thread, không cần `Interlocked` throttle hay cặp `TryAdd/else overwrite`), không hẳn là hiệu năng; đọc từ thread khác vẫn cần `ConcurrentDictionary`. Áp dụng cho `FlowTable` tách từ `SocketTracker` và cho chuỗi attach/detach của `ProcessRuleTracker`.

## Builder theo kind và registry sở hữu instance

Hai vai tách rời cho cùng một thứ. **Builder** trả lời "loại này dựng ra sao": một `IOutboundSourceBuilder` cho mỗi `OutboundKind`, chọn theo `Kind` thay cho `switch`, nên thêm một cách đi ra là thêm một class chứ không phải sửa một hàm dài (cùng họ với [Strategy](#L424)). **Registry** trả lời "ai sở hữu cái đã dựng": giữ một instance cho mỗi outbound, và là chỗ DUY NHẤT gọi dispose. Khi cache nằm chung trong factory thì mỗi bên cần huỷ instance lại tự gọi `Invalidate`, không có gì trong code nói bên nào được phép — registry biến việc đó thành hai động từ khác nhau (`Reconcile` cho cấu hình đổi, `Discard` cho người giám sát) và bắn một sự kiện để mọi thứ khoá theo outbound (tunnel UDP, kết luận IPv6 đã học) biết mà bỏ theo.

## Capability interface (interface theo khả năng)

Thay vì bắt mọi lớp trả lời "có làm được X không" bằng một cờ `bool` trên interface chung, tách riêng một interface nhỏ cho từng khả năng và bên gọi kiểm bằng `is`. Lợi ích không phải là bớt code lặp mà là **compiler kiểm được**: lớp không làm được X thì không có cả cờ lẫn hàm, nên "khai báo hỗ trợ" và "thật sự cài đặt" không thể lệch nhau. Ở `IProxySource`, ba cờ cũ (`IsSupportUdp/Ipv6/Bind`) tách thành `IUdpCapable`, `IBindCapable`, `IAddressFamilyPolicy`. Hai cái đầu vẫn **giữ cờ bên trong interface** vì "làm được" có hai tầng: giao thức có tính năng đó không (interface) và upstream cụ thể có chịu không (cờ) — SOCKS5 có UDP ASSOCIATE trong giao thức nhưng một server cụ thể có thể không bật. Bên gọi phải qua cả hai: `source is IUdpCapable u && u.IsSupportUdp`.

## Hai nguồn sự thật cho một câu hỏi

Cùng một câu hỏi ("outbound này có tải được UDP không") được trả lời ở hai chỗ bằng hai cách: từ **cấu hình** (`Outbound.SupportsUdp`, suy từ `Kind` + URL) và từ **thứ đã dựng** (`IOutboundInstance.SupportsUdp`, hỏi source thật). Đây không phải lúc nào cũng là lỗi cần gộp: đường routing trả lời mỗi datagram một lần và **trước khi** có instance — dựng instance của VPN chính là dial nó — nên nó buộc phải suy từ cấu hình. Cách xử lý đúng là **giữ cả hai nhưng chốt bằng test** duyệt mọi `Kind` và khẳng định hai bên bằng nhau: lệch thì hỏng ở lúc build chứ không phải lúc chạy. Cách sai là để đường routing hỏi `registry.Find(id)?.Caps ?? model` — câu trả lời khi đó đổi theo việc instance tình cờ đã dựng hay chưa, nên cùng một datagram định tuyến khác nhau trước và sau kết nối TCP đầu tiên.

## Đối tượng theo lượt chạy (per-run object)

Thay vì để N field nullable trên một lớp dài, cùng được gán lúc `Start` và cùng bị xoá lúc `Stop`, gom chúng vào MỘT đối tượng bất biến và giữ đúng một tham chiếu tới nó. Cái mua được không phải là bớt `?.` mà là bớt một lớp lỗi: mỗi handler lấy tham chiếu đó **một lần ở đầu hàm** rồi làm việc với một lượt chạy nguyên vẹn, nên không còn cảnh đọc được tracker của lượt đang tắt và forwarder của con số không. `IsRunning` cũng thôi là cờ riêng — "đang chạy" chính là "run khác null", nên không có hai nguồn sự thật để lệch. Ở ProxyDivert là `EngineRun` (redirector, tracker, host-name resolver, UDP forwarder, `CancellationTokenSource`, hai router).

## Slot (ô giữ giá trị hoán được)

Một đối tượng nhỏ chỉ để **giữ chỗ** cho một giá trị bị thay nguyên khối, và được truyền đi thay cho chính giá trị đó. Dùng khi bên đọc cần thấy giá trị MỚI NHẤT nhưng không được sở hữu hay sửa nó: bên đọc nhận `IResolverSource` (chỉ có getter), bên ghi giữ `ResolverSlot` và gọi `Use(...)`. Nó cũng cắt được vòng phụ thuộc lúc dựng — router cần đọc bảng định tuyến mà [đối tượng theo lượt chạy](#L460) lại giữ router — mà không phải cho ai một tham chiếu ngược sửa được. Khác với việc truyền `Func<T>`: slot có một chủ ghi rõ ràng và đọc được tên trong stack trace.

## Luật đã biên dịch (compiled rule set)

Tách một bảng quyết định thành **phần đổi chậm** (cấu hình người dùng lưu) và **phần đổi nhanh** (trạng thái chạy), rồi chỉ dựng lại phần đổi chậm khi nó thật sự đổi. Ở ProxyDivert phần đổi chậm là `CompiledRuleSet`: mỗi `Pattern` (chuỗi người dùng gõ) được **parse một lần lúc lưu** thành một `IHostPredicate` — CIDR đã mask sẵn, regex đã compile, dải cổng đã tách — rule tắt bị loại và phần còn lại sắp theo `Order`. Phần đổi nhanh là "pid nào đang thuộc policy nào", đọc sống qua `IProcessPolicySource`.

Trộn hai phần vào một snapshot là cái bẫy: mỗi lần một tiến trình được nhận hay bỏ (một lần mở trình duyệt là sáu chục lần trong vài giây) lại phải dựng lại **cả** bảng, tức parse lại mọi pattern của mọi policy. Cái mua được thứ hai quan trọng không kém: có một **thời điểm biên dịch** thì mới có chỗ nói "pattern này không dùng được" — trước đó nó chỉ lặng lẽ không khớp gì trên mọi kết nối, mà nếu luật đó bật `IsNot` thì lại **khớp tất cả**.

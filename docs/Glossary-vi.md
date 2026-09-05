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

Đọc command line của tiến trình khác tốn một truy vấn WMI (`Win32_Process.CommandLine`), nên engine chỉ trả phí đó khi có ít nhất một bộ lọc đang bật chứa một lá loại này có điền giá trị — `ProcessRuleMatcher.NeedsCommandLine` duyệt cả cây để biết. Command line không đọc được (tiến trình hệ thống, tiến trình của tài khoản khác) cho kết quả `Unknown`, và bộ lọc không áp dụng — hướng an toàn.

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

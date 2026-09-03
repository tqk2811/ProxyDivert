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

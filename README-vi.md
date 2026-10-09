# ProxyDivert

*English: [README.md](README.md)*

Tool WPF chuyển hướng gói tin của tiến trình được chọn sang proxy (HTTP / SOCKS4 / SOCKS5) theo luật
domain hoặc IP; đích không khớp luật thì đi thẳng (direct). Giai đoạn sau cắm thêm VPN dưới dạng một
loại đường ra.

- Kiến trúc: [docs/Architecture-vi.md](docs/Architecture-vi.md)

## Yêu cầu

- Windows 10/11 **x64** (native WinDivert chỉ có bản win-x64).
- .NET SDK 8.0 trở lên.
- Chạy tool với quyền **Administrator** (WinDivert nạp driver kernel).

## Lấy mã nguồn

```
git clone --recursive https://github.com/tqk2811/ProxyDivert.git
```

Đã clone sẵn thì: `git submodule update --init --recursive`.

## Build

```
dotnet build ProxyDivert.sln -c Debug
```

Sản phẩm: `src/ProxyDivert.Wpf/bin/x64/<Config>/net8.0-windows/ProxyDivert.exe`
(kèm `WinDivert.dll` + `WinDivert64.sys` copy sẵn cạnh exe).

Phiên bản lấy từ git: `M.N` theo tag `vM.N.0` gần nhất, số thứ ba là số commit kể từ tag đó (chưa có
tag thì `0.0.<tổng số commit>`). Mỗi lần push lên `master`,
[.github/workflows/release.yml](.github/workflows/release.yml) build và gắn file zip vào GitHub Release
`vM.N.0`; muốn mở dòng phiên bản mới thì gắn tag `vM.N.0` mới lên `master`.

## Cấu trúc

Repo gồm ứng dụng WPF (`src/`) và ba submodule trong `libs/`. Cấu trúc từng project nằm ở
[docs/Architecture-vi.md](docs/Architecture-vi.md#L5).

## Cách dùng nhanh

1. Chạy `ProxyDivert.exe` **bằng quyền Administrator**.
2. Tab **Đường ra**: thêm proxy (`socks5://host:port`, `http://host:port`), bấm **Thử** để kiểm tra.
3. Tab **Luật**: một bộ luật là danh sách đích có tên, cộng với **một** đường ra dùng chung. Thêm
   luật — ví dụ `Wildcard` + `*.google.com` — rồi chọn **Đường ra** ở dưới; mọi thứ khớp các luật đó
   sẽ đi lối này. Thứ không khớp rơi xuống bộ luật kế tiếp mà bộ lọc chỉ định, không bộ luật nào
   nhận thì đi thẳng. Thêm bao nhiêu bộ luật tuỳ ý, bấm đúp vào một cái để đổi tên ngay tại chỗ —
   mất focus hoặc Enter là xong, Escape thì huỷ.
4. Tab **Tiến trình**: bấm **Thêm**, cửa sổ bộ lọc mở ra. Một bộ lọc gồm tên, các điều kiện, và
   hành động áp dụng cho thứ khớp được. Mỗi dòng điều kiện chọn soi cái gì (**Tiến trình** hay
   **Argument**), so khớp kiểu nào (tên tệp chạy, đường dẫn đầy đủ, ký tự đại diện, bắt đầu bằng,
   kết thúc bằng, có chứa, regex — bốn kiểu cuối soi cả đường dẫn lẫn tên tệp chạy, khớp cái nào
   cũng được, nên `có chứa chrome` vẫn bắt được tiến trình mà Windows không cho đọc đường dẫn), và
   giá trị. Các dòng nối với nhau bằng đúng một combo ở đầu nhóm — **Khớp TẤT CẢ** hoặc **Khớp BẤT
   KỲ** — nên không phải đặt toán tử giữa hai dòng, cũng không có độ ưu tiên nào để nhầm. Nút
   **KHÔNG** trên mỗi dòng hoặc mỗi nhóm để đảo ngược, **+ Nhóm** để lồng thêm một lớp ngoặc. Muốn
   sắp lại hay đưa một dòng đã viết vào ngoặc thì kéo nó bằng tay cầm ở đầu dòng: thả vào mép trên
   hay mép dưới của một dòng là chèn ngay trên hoặc ngay dưới dòng đó, thả vào nửa dưới của dòng
   nhóm là vào bên trong ngoặc, còn dải trống dưới dòng cuối của một nhóm là ra sau cả nhóm. Kéo nốt
   dòng cuối ra khỏi một ngoặc thì ngoặc rỗng đó biến mất theo. Câu ở trên cùng
   nói bộ lọc hiện đang có nghĩa gì. `java.exe VÀ (minecraft HOẶC forge)` viết ra như vậy. Cột bên
   phải phần điều kiện là **Hành động**: tích các bộ luật mà bộ lọc áp dụng, luật được xét từ bộ
   luật trên xuống, khớp cái nào trước thì cái đó quyết định, nên kéo thả hoặc dùng ▲▼ để sắp thứ
   tự ưu tiên.
   Thứ tự đó giữ nguyên kể cả với những bộ luật đang không tích, nên bỏ tích một cái rồi tích lại
   không làm xáo chỗ. Bộ luật đứng đầu cũng là cái quyết định chế độ UDP và Block QUIC. Đóng cửa sổ
   khi còn thay đổi chưa lưu thì được hỏi lưu, không lưu, hay quay lại. Bảng bộ lọc cũng xét từ trên
   xuống: một tiến trình khớp bộ lọc nào trước thì theo bộ lọc đó và không xét tiếp, nên kéo tay cầm
   ở đầu dòng để sắp lại thứ tự (bảng này cố tình không cho sắp xếp theo cột — thứ tự dòng chính là
   thứ tự ưu tiên). Hoặc bấm **Chạy ở trạng thái tạm dừng…** để không lọt kết nối nào lúc khởi động.
5. Gạt công tắc trên thanh tiêu đề. Nửa dưới tab **Tiến trình** khi đó hiện dạng cây mọi tiến trình
   engine đang thực sự áp dụng, tiến trình con nhận theo **Cả tiến trình con** nằm lồng dưới tiến
   trình đã sinh ra nó. Mỗi dòng nói bộ lọc nào đã bắt được tiến trình đó — tiến trình con hiện bộ
   lọc của cha nó kèm nhãn *kế thừa*, vì đó mới là bộ lọc cần sửa; tiến trình do **Chạy ở trạng thái
   tạm dừng…** khởi động thì không bộ lọc nào tả nó nên để dấu `—`. Kéo mép phải của tiêu đề cột để
   nới rộng cột. Tab **Kết nối** hiện từng kết nối kèm tên miền, đường ra và số byte.

Cấu hình lưu ở `proxydivert.config.json` cạnh exe. File là JSON thuần, kể cả mật khẩu: sửa tay được
và chép sang máy khác dùng luôn được, nên hãy để nó ở chỗ chỉ mình bạn đọc được.

## Ngôn ngữ và giao diện

Cửa sổ tự vẽ thanh tiêu đề thay vì dùng thanh của hệ thống, nên dải đó chở luôn tên các tab, công tắc
engine và hai công tắc giao diện thay vì bỏ trống. Nó vẫn kéo được, bấm đúp để phóng to, kéo cạnh để
đổi kích thước y như cửa sổ thường; ba nút thu nhỏ / phóng to / đóng vẫn nằm ở góc phải như mọi khi.

Bản thân engine giờ là một công tắc trên đó, thay cho cặp nút Chạy/Dừng kèm huy hiệu báo cái nào đang
đúng. Gạt là bật hoặc tắt chuyển hướng; nếu bật hỏng — hay gặp nhất là thiếu quyền Administrator —
thì knob quay về chỗ cũ và lý do hiện ngay dưới thanh tiêu đề.

Cửa sổ có tiếng Việt và tiếng Anh, có nền sáng, nền tối, hoặc theo đúng thiết lập của Windows. Cả hai
công tắc nằm ngay trên thanh tiêu đề đó — ô chọn ngôn ngữ, và cạnh nó là nút xoay vòng Theo hệ thống
→ Sáng → Tối — đồng thời cũng có trong tab **Cài đặt**. Đổi là ăn ngay và được ghi thẳng vào file cấu
hình, nên lần chạy sau vẫn giữ nguyên; nút Lưu dành cho những thiết lập đi vào engine, không phải cho
hai thứ này.

`Theo hệ thống` nghĩa là để máy quyết định: ngôn ngữ đọc theo UI culture của Windows (tiếng Việt nếu
Windows đang tiếng Việt, còn lại là tiếng Anh), còn giao diện bám theo màu ứng dụng của Windows và
đổi ngay khi bạn đổi bên Windows.

Mọi chữ đều được dịch, kể cả giá trị bên trong các ô chọn: kiểu khớp hiện "Hậu tố tên miền" chứ không
phải định danh `DomainSuffix`. Riêng tên giao thức (SOCKS5, IKEv2, WireGuard) giữ nguyên, vì đó là
tên riêng chứ không phải từ để dịch.

## Tên miền của một kết nối lấy từ đâu

Luật theo domain cần một cái tên mà gói tin không mang sẵn: tool đọc SNI
hoặc header `Host` ở đầu kết nối, không được thì dùng bảng học từ các gói trả lời DNS. UDP/QUIC,
giao thức server nói trước và ECH không để lại tên nào để đọc. Chi tiết ở
[docs/Architecture-vi.md](docs/Architecture-vi.md#L16).

## Độ trễ với traffic không chuyển hướng

Khi engine chạy, mọi gói TCP/UDP đi ra của cả máy, dù có chuyển hướng hay không, đều đi qua engine
(trung bình mỗi gói chờ khoảng 0,1–0,3 ms). Cách giữ chi phí đó thấp và số đo:
[docs/Architecture-vi.md](docs/Architecture-vi.md#L37).

## Giới hạn hiện tại

- **Mọi gói đi ra của cả máy đều qua engine**, không riêng tiến trình bị chuyển hướng (xem mục
  trên). Trung bình chỉ tốn chưa tới 1 ms, nhưng khi CPU bị vắt hết thì thỉnh thoảng một gói phải
  chờ thread pump vài tới vài chục ms — game giật ping thoáng qua dù game không bị chuyển hướng.
  Muốn hết hẳn thì tắt engine khi chơi game ([vì sao](docs/Architecture-vi.md#L55)).

- IPv6 được chuyển hướng như IPv4 (mặc định `Redirect`, xem Ipv6Mode trong Cài đặt).
  Chọn `Block` nếu muốn hành vi cũ — chặn để ứng dụng lùi về IPv4; `Ignore` thì IPv6 đi thẳng, **lọt ra ngoài proxy**.
- Đường ra không có tuyến IPv6 (VPN/proxy chỉ IPv4): đích có tên miền vẫn đi được bằng cách lùi về IPv4;
  đích chỉ có địa chỉ IPv6 trần thì bị đóng kết nối để ứng dụng tự chuyển sang IPv4. Mỗi đường ra có thiết lập
  Ipv6Support: `Auto` (thử một lần rồi tự nhớ), `Enabled`, `Disabled`.
  Cách lùi hoạt động: [docs/Architecture-vi.md](docs/Architecture-vi.md#L55).
- Secure DNS (xem mục [DNS bảo mật theo policy](#dns-bảo-mật-theo-policy)) xử lý DNS/53 qua UDP, cả IPv4 lẫn IPv6; DNS qua TCP/53 (hỏi lại khi câu trả lời quá dài) vẫn đi DNS thường.
- Kết nối IPv6 đang mở sẵn lúc bật engine cũng rơi vào luật "kết nối đã mở trước" bên dưới.
- Kết nối đã mở TRƯỚC khi tiến trình được gắn sẽ **đi thẳng** (không chuyển hướng) và ghi rõ trong log:
  chuyển hướng nửa chừng một kết nối đang chạy sẽ làm hỏng hẳn kết nối đó. Muốn không lọt gói nào
  thì dùng "Chạy ở trạng thái tạm dừng".
- UDP chỉ qua proxy được với **SOCKS5**, và chỉ qua VPN được khi VPN đó chạy trong chính tiến trình
  này (tức là mọi loại trừ file `.conf` WireGuard chạy bằng wireproxy — xem mục dưới). Đường ra khác
  — kể cả SSH — thì UDP bị chặn chứ không rò ra ngoài. QUIC (UDP/443) chặn mặc định để trình duyệt lùi về TCP.
- Game có anti-cheat kernel có thể coi việc chuyển hướng gói tin là can thiệp.
- **SoftEther** cần đúng khối watermark thật mới nói chuyện được với máy
  chủ thật; khối đó là dữ liệu GPL nên repo này không kèm — thiếu nó máy chủ trả HTTP 403. Bấm
  **Tải** ở mục *Watermark SoftEther* trong tab Cài đặt (hoặc nút **Tải watermark** hiện ngay trên
  dòng đường ra SoftEther) là tool tải khối đó từ mã nguồn chính thức về, lưu cạnh `ProxyDivert.exe`
  rồi tự dùng — không phải khai đường dẫn ở đâu cả. Tool KHÔNG bao giờ tự tải: phải bấm.
- Đã chạy thật với VPN Gate: **SSTP**, **L2TP/IPsec** và **SoftEther** (SoftEther cần khối watermark, xem mục trên). OpenVPN và WireGuard chưa kiểm chứng với máy chủ thật trên máy này.

## Đường ra VPN

Chọn loại đường ra **Vpn**. Dù là giao thức nào, đường hầm cũng chạy ở **tầng ứng dụng**: không tạo
card mạng ảo, không đụng bảng route, nên **chỉ tiến trình bị chuyển hướng đi qua VPN**, phần còn lại
của máy vẫn dùng mạng bình thường.

Ô URL điền gì thì tuỳ giao thức, vì bản thân sáu giao thức không giống nhau: hai loại cấu hình bằng
file nhà cung cấp đưa, bốn loại còn lại **không có định dạng file client chuẩn nào** nên phải quay số
bằng địa chỉ máy chủ.

| Ô URL | Giao thức | Ô khác nó dùng |
|---|---|---|
| `D:\vpn\wg0.conf` | WireGuard, chạy bằng `wireproxy.exe` | — |
| `D:\vpn\jp.ovpn` | OpenVPN, chạy trong tiến trình này | Tài khoản, Mật khẩu (nếu profile đòi) |
| `sstp://vpn.example.com:443` | SSTP | Tài khoản, Mật khẩu |
| `l2tp://vpn.example.com` | L2TP/IPsec | Tài khoản, Mật khẩu, Khoá chung |
| `ikev2://vpn.example.com` | IKEv2 | Khoá chung; Tài khoản/Mật khẩu chỉ khi dùng EAP |
| `softether://vpn.example.com:443/HUB` | SoftEther SSL-VPN | Tài khoản, Mật khẩu |
| `D:\vpn\office.vpn` | bất kỳ loại nào ở trên, khai trong file ini nhỏ | xem bên dưới |

Cột **Giao thức VPN** để `Auto` là tool tự đoán từ ô URL — có scheme thì scheme nói thẳng ra giao
thức, là file thì nhận theo đuôi và nội dung. Thứ duy nhất nó **không** đoán được là file `.conf`
WireGuard nên chạy bằng engine nào; cột đó thật ra sinh ra vì lý do này, xem mục kế tiếp. Việc tra
tên miền của engine chạy trong tiến trình luôn nằm trong đường hầm, không bao giờ tới resolver của
máy ([chi tiết](docs/Architecture-vi.md#L98)).

Mật khẩu và khoá chung nằm ở ô riêng chứ không nhét vào URL, để sửa
riêng và che được trên màn hình. Trong file cấu hình chúng nằm thô đúng như bạn gõ.

### Hai engine, và khi nào dùng cái nào

Chỉ file `.conf` WireGuard chạy được bằng một trong hai engine, `wireproxy.exe` hoặc tiến trình này;
các giao thức khác luôn chạy trong tiến trình này. File `.conf` WireGuard **mặc định vẫn đi
wireproxy**, đúng như từ trước tới nay — cấu hình cũ của bạn không đổi hành vi một chút nào. Muốn
chạy chính file đó trong tiến trình này thì đặt cột **Giao thức VPN** thành `WireGuard`: khi đó không
cần `wireproxy.exe` nữa, và UDP đi qua được đường hầm (SOCKS5 của wireproxy chỉ có TCP).

Với engine wireproxy, cần tải `wireproxy.exe` để cạnh `ProxyDivert.exe` (hoặc trong PATH, hoặc trỏ
đường dẫn ở tab **Cài đặt**). Bảng so sánh hai engine và cách wireproxy nhận cấu hình (bản sao tạm
trong `%TEMP%` **chứa private key dạng rõ** trong lúc chạy):
[docs/Architecture-vi.md](docs/Architecture-vi.md#L77).

### File `.vpn`

Muốn giữ thông tin máy chủ trong file thay vì trên dòng đường ra thì trỏ ô URL vào một file ini nhỏ.
Ô nào bạn điền ở dòng đường ra sẽ **thắng** giá trị trong file, vì dòng đường ra mới là thứ tool lưu lại.

```ini
[Vpn]
Protocol  = l2tp          ; sstp | l2tp | ikev2 | softether | openvpn | wireguard
Host      = vpn.example.com
Port      = 443           ; chỉ SSTP và SoftEther
Hub       = VPN           ; chỉ SoftEther
User      = nam
Pass      = ...
Psk       = ...           ; l2tp và ikev2
Watermark = D:\vpn\se.dat ; chỉ SoftEther — xem mục Giới hạn hiện tại
Config    = jp.ovpn       ; openvpn/wireguard thì dùng dòng này thay cho Host; tương đối so với file .vpn
```

File này **không được** khai wireproxy: việc một đường ra có chở được UDP hay không phải trả lời từ ô
URL, mỗi kết nối một lần, nên một lời khai nằm trong file mà đường đó không bao giờ đọc sẽ là lời nói
dối mà bộ định tuyến tin theo. Muốn wireproxy thì trỏ thẳng ô URL vào file `.conf`.

### Đường hầm được giữ chạy liên tục

Đường hầm dựng ngay khi bấm Start chứ không đợi request đầu tiên, và được giữ cho tới khi dừng
engine, rớt thì tự dựng lại. Độ giãn cách khi thử lại, keepalive và ai giám sát cái gì:
[docs/Architecture-vi.md](docs/Architecture-vi.md#L105).

Trạng thái hiện ngay trên tab **Đường ra**: chấm xanh là đang chạy, chấm vàng kèm lý do là đang kết
nối hoặc kết nối lại. Bấm Lưu **không** làm rớt đường hầm — chỉ đường ra nào thật sự bị sửa mới dựng
lại, và sửa chính file cấu hình cũng tính là sửa.

## Đường ra SSH

Chọn loại đường ra **Ssh**. Tool giữ **một** phiên SSH tới máy chủ, và mỗi kết nối được chuyển hướng
là một channel direct-tcpip trên phiên đó — giống `ssh -D` nhưng không
cần listener SOCKS cục bộ ở giữa. Tên miền đích được gửi sang máy chủ và phân giải ở đó, nên tra tên
không đi qua DNS của máy này. Máy chủ không cần gì đặc biệt: `sshd` bình thường với
`AllowTcpForwarding` (mặc định đã bật). Chạy ngay trong tiến trình này (SSH.NET), không cần cài
`ssh.exe`.

| Ô | Điền gì |
|---|---|
| URL | `ssh://user@host:22`, hoặc chỉ `user@host` — không ghi cổng thì là 22 |
| Tài khoản | tên đăng nhập; thắng tên viết trong URL |
| Mật khẩu | mật khẩu — hoặc, khi có file khoá, là passphrase của khoá (vẫn được thử làm mật khẩu) |
| File khoá | private key: OpenSSH, PuTTY `.ppk` hoặc PEM (RSA, ECDSA, Ed25519) |

**Host key được tin ở lần đầu** (TOFU). Lần đầu nối tới một máy chủ,
host key của nó được ghi vào `%LOCALAPPDATA%\ProxyDivert\known_hosts` — đúng định dạng của OpenSSH
nên đọc và sửa tay được — và từ đó chỉ khoá này được chấp nhận. Máy chủ đưa khoá khác thì bị từ chối,
thông báo lỗi nêu rõ file và dòng: xoá dòng đó nếu máy chủ thật sự vừa cài lại, còn nếu không giải
thích được vì sao khoá đổi thì đừng xoá.

Phiên được giữ như đường hầm VPN: nút **Kết nối** trên dòng, chấm xanh/vàng, giữ chạy khi có bộ lọc
định tuyến qua và tự dựng lại khi rớt.

Giới hạn:

- **Chỉ TCP.** SSH không có channel nào chở datagram, nên UDP định tuyến vào đường ra SSH bị chặn
  (kể cả QUIC — trình duyệt tự lùi về TCP).
- Mọi tunnel dùng chung một kết nối TCP tới máy chủ, và mỗi tunnel đang mở giữ một luồng, nên trình
  duyệt mở một trăm kết nối là tốn một trăm luồng ([chi tiết](docs/Architecture-vi.md#L121)).
- Đã thử với `sshd` OpenSSH 9.5 có sẵn của Windows trên chính máy này, bằng khoá Ed25519 có và không
  có passphrase (xem `LiveSshOutboundTests`). Chưa thử đăng nhập bằng mật khẩu và máy chủ Linux ở xa.
  Chưa hỗ trợ keyboard-interactive và ssh-agent.

## Chống DPI

Có mạng đọc tên miền đích ngay trên đường truyền — SNI trong ClientHello
của TLS, hoặc tên máy trong dòng `CONNECT` gửi tới proxy — rồi chặn hay bóp băng thông theo đó
(DPI). Các công tắc chống DPI gửi phần tên miền thành nhiều mẩu nhỏ, để
thiết bị chỉ soi từng gói hoặc từng record không thấy được trọn tên (ý tưởng của GoodbyeDPI).

Ở tab **Outbounds**:

| Cột | Làm gì | Áp dụng cho |
|---|---|---|
| **Chống DPI TLS** | ClientHello được dựng lại thành nhiều TLS record quanh tên miền trong SNI, riêng tên miền cắt thành record mỗi cái *Số byte DPI* byte. Server bắt buộc phải ráp lại (RFC 8446 §5.1). | Direct (cả hàng dựng sẵn), HTTP, SOCKS4, SOCKS5 |
| **Chống DPI CONNECT** | Tên miền trong lệnh `CONNECT` gửi tới proxy được gửi mỗi lần *Số byte DPI* byte. Vài proxy xử lý kém. | HTTP, SOCKS4, SOCKS5 |
| **Số byte DPI** | Số byte tên miền mỗi mảnh; mặc định 2. | |

Chỉ phần bắt tay chứa tên miền bị cắt, nên kết nối đã lên thì không tốn thêm gì
([chi tiết](docs/Architecture-vi.md#L130)). Đường ra VPN và SSH không có tuỳ chọn này — dữ liệu của chúng vốn đã mã hoá suốt đường.

**Theo từng policy.** Ở tab **Rules**, mỗi policy có hai công tắc tương tự cạnh Block QUIC, dạng ô
3 trạng thái: tick là bật chống DPI cho những gì luật của policy khớp, trống là tắt, ô vuông đặc là
theo outbound. Ô *Số byte DPI* riêng của policy để trống thì dùng của outbound. Policy thắng
outbound, nên cùng một proxy có thể chỉ cắt cho những trang cần. Các ô bị mờ khi outbound của policy
không hỗ trợ. UDP không bao giờ bị cắt.

## DNS bảo mật theo policy

Mỗi policy ở tab **Rules** có ba công tắc đưa DNS qua DoH bằng chính đường ra của policy, để việc
tra tên đi cùng đường với dữ liệu. Máy chủ DoH là endpoint trong **Settings** (*DoH endpoint*),
trừ khi policy tự chọn máy chủ riêng ở ô *DoH server* cạnh các công tắc (để trống = dùng máy chủ trong Settings).

| Công tắc | Tác dụng |
|---|---|
| **Secure DNS: app's own DNS** | Các truy vấn DNS do chính ứng dụng khớp tự gửi đi qua DoH bằng đường ra của policy. |
| **Secure DNS: system DNS** | DNS do Windows hỏi thay cho ứng dụng (dịch vụ DNS Client): nếu tên được hỏi khớp một luật tên miền của policy thì phân giải qua DoH bằng đường ra của policy. Chỉ tính luật tên miền dạng khẳng định; luật IP, cổng, giao thức, Any và luật NOT bị bỏ qua. Xét theo thứ tự bộ lọc trong danh sách, rồi các policy được tick trong mỗi bộ lọc theo thứ tự của bộ lọc đó. Không dùng được với policy Default dựng sẵn. |
| **Allow plain DNS if DoH fails** | Khi DoH lỗi, truy vấn đi ra như DNS thường thay vì trả lỗi. Cách này làm lộ tên miền. Chỉ có nghĩa khi một trong hai công tắc trên đang bật. |

Các ô bị mờ khi đường ra của policy là Block. Chúng chỉ có tác dụng khi engine đang chạy, và đổi
endpoint DoH thì có hiệu lực ngay khi bấm Apply & Save. Tên máy chủ của chính các outbound (proxy, VPN,
SSH) luôn được phân giải bằng DNS thường ([vì sao](docs/Architecture-vi.md#L137)).

Giới hạn:
- Phần mềm có driver lọc mạng riêng có thể lấy DNS/53 trước khi WinDivert thấy, khi đó không truy vấn
  nào bị chuyển hướng. ExitLag ở chế độ mặc định (WFP) là một ví dụ; chuyển ExitLag sang chế độ driver NDIS thì truy vấn lại tới được ProxyDivert.
- Trình duyệt bật DNS bảo mật riêng (*Use secure DNS* của Chrome, DNS over HTTPS của Firefox) không hỏi
  DNS của Windows, nên các công tắc này không thấy truy vấn của chúng. Tắt cài đặt đó để chúng đi qua đây.

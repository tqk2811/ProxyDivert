# ProxyDivert

*English: [README.md](README.md)*

Tool WPF chuyển hướng gói tin của tiến trình được chọn sang proxy (HTTP / SOCKS4 / SOCKS5) theo luật
domain hoặc IP; đích không khớp luật thì đi thẳng (direct). Giai đoạn sau cắm thêm VPN dưới dạng một
loại đường ra.

- Kế hoạch: [docs/Plan-vi.md](docs/Plan-vi.md)
- Thuật ngữ: [docs/Glossary-vi.md](docs/Glossary-vi.md)

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

## Cấu trúc

| Đường dẫn | Nội dung |
|---|---|
| `libs/TqkLibrary.WinDivert` | submodule — lõi chuyển hướng gói tin theo tiến trình (5 project: core, `.Redirect`, `.SecureDns`, `.Inspection`, `.ProcessControl`) |
| `libs/TqkLibrary.Proxy` | submodule — `IProxySource` cho HTTP/SOCKS4/SOCKS5/SSH/WireGuard |
| `libs/TqkLibrary.VpnClient` | submodule — [stack TCP/IP userspace](docs/Glossary-vi.md#L73) và driver các giao thức VPN. Project `TqkLibrary.VpnClient.Tunnels` trong đó quay số sáu giao thức bên dưới và trả về đường hầm đã lên. |
| `src/ProxyDivert.Core` | engine, model, service (không phụ thuộc WPF) |
| `src/ProxyDivert.Wpf` | giao diện |
| `src/ProxyDivert.Core.Tests` | unit test |

## Cách dùng nhanh

1. Chạy `ProxyDivert.exe` **bằng quyền Administrator**.
2. Tab **Đường ra**: thêm proxy (`socks5://host:port`, `http://host:port`), bấm **Thử** để kiểm tra.
3. Tab **Luật**: thêm luật cho bộ luật mặc định, ví dụ `Wildcard` + `*.google.com` → proxy vừa tạo.
   Đích không khớp luật nào sẽ đi thẳng (đường ra mặc định).
4. Tab **Tiến trình**: chọn tiến trình trong danh sách rồi bấm **Tạo luật từ dòng đang chọn**
   (hoặc **Chạy ở trạng thái tạm dừng…** để không lọt kết nối nào lúc khởi động).
5. Bấm **Chạy** trên thanh trên cùng. Tab **Kết nối** hiện từng kết nối kèm tên miền, đường ra và số byte.

Cấu hình lưu ở `proxydivert.config.json` cạnh exe; mật khẩu proxy được mã hoá bằng DPAPI
(chỉ tài khoản Windows đã lưu mới đọc lại được).

## Giới hạn hiện tại

- IPv6 được chuyển hướng như IPv4 (mặc định `Redirect`, xem [Ipv6Mode](docs/Glossary-vi.md#L89) trong Cài đặt).
  Chọn `Block` nếu muốn hành vi cũ — chặn để ứng dụng lùi về IPv4; `Ignore` thì IPv6 đi thẳng, **lọt ra ngoài proxy**.
  Bộ phân tích gói không đi qua IPv6 extension header, nên gói IPv6 có extension header sẽ được cho đi thẳng
  thay vì bị hiểu sai (hiếm gặp với traffic ứng dụng thông thường).
- Đường ra không có tuyến IPv6 (VPN/proxy chỉ IPv4): đích **có tên miền** vẫn đi được — tool đưa tên cho đường ra
  tự phân giải sang IPv4. Đích chỉ có **địa chỉ IPv6 trần** thì không còn gì để lùi, tool đóng kết nối ngay để ứng dụng
  tự chuyển sang IPv4 ([Happy Eyeballs](docs/Glossary-vi.md#L81)). Mỗi đường ra có thiết lập
  [Ipv6Support](docs/Glossary-vi.md#L89): `Auto` (thử một lần rồi tự nhớ), `Enabled`, `Disabled`.
  SOCKS4 không có IPv6 trong giao thức nên luôn coi là không hỗ trợ.
- DoH chỉ xử lý DNS/53 trên IPv4; DNS/53 IPv6 của tiến trình đích đi theo luật UDP thông thường.
- Kết nối IPv6 đang mở sẵn lúc bật engine cũng rơi vào luật "kết nối đã mở trước" bên dưới.
- Kết nối đã mở TRƯỚC khi tiến trình được gắn sẽ **đi thẳng** (không chuyển hướng) và ghi rõ trong log:
  chuyển hướng nửa chừng một kết nối đang chạy sẽ làm hỏng hẳn kết nối đó. Muốn không lọt gói nào
  thì dùng "Chạy ở trạng thái tạm dừng".
- UDP chỉ qua proxy được với **SOCKS5**, và chỉ qua VPN được khi VPN đó chạy trong chính tiến trình
  này (tức là mọi loại trừ file `.conf` WireGuard chạy bằng wireproxy — xem mục dưới). Đường ra khác
  thì UDP bị chặn chứ không rò ra ngoài. QUIC (UDP/443) chặn mặc định để trình duyệt lùi về TCP.
- Game có anti-cheat kernel có thể coi việc chuyển hướng gói tin là can thiệp.
- **SoftEther** cần đúng khối [watermark](docs/Glossary-vi.md#L121) thật mới nói chuyện được với máy
  chủ thật; khối đó là dữ liệu GPL nên repo này không kèm — thiếu nó máy chủ trả HTTP 403. Khai bằng
  dòng `Watermark =` trong file `.vpn`.
- Chưa đường ra VPN nào được kiểm chứng với máy chủ thật trên máy này.

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
| `l2tp://vpn.example.com` | L2TP/IPsec | Tài khoản, Mật khẩu, [Khoá chung](docs/Glossary-vi.md#L117) |
| `ikev2://vpn.example.com` | IKEv2 | Khoá chung; Tài khoản/Mật khẩu chỉ khi dùng EAP |
| `softether://vpn.example.com:443/HUB` | SoftEther SSL-VPN | Tài khoản, Mật khẩu |
| `D:\vpn\office.vpn` | bất kỳ loại nào ở trên, khai trong file ini nhỏ | xem bên dưới |

Cột **Giao thức VPN** để `Auto` là tool tự đoán từ ô URL — có scheme thì scheme nói thẳng ra giao
thức, là file thì nhận theo đuôi và nội dung. Thứ duy nhất nó **không** đoán được là file `.conf`
WireGuard nên chạy bằng engine nào; cột đó thật ra sinh ra vì lý do này, xem mục kế tiếp.

Mật khẩu và khoá chung nằm ở ô riêng chứ không nhét vào URL, để chúng được mã hoá
[DPAPI](docs/Glossary-vi.md#L77) như mọi mật khẩu khác thay vì nằm thô trong file cấu hình.

### Hai engine, và khi nào dùng cái nào

| | `wireproxy.exe` | Trong tiến trình này |
|---|---|---|
| Giao thức | file `.conf` WireGuard | OpenVPN, SSTP, L2TP/IPsec, IKEv2, SoftEther, `.conf` WireGuard |
| File exe rời | bắt buộc | không cần |
| UDP qua đường hầm | không (SOCKS5 của nó chỉ có TCP) | có |
| IPv6 qua đường hầm | không | có, khi máy chủ cấp IPv6 global |
| DNS | wireproxy tự hỏi trong đường hầm | hỏi trong đường hầm |

File `.conf` WireGuard **mặc định vẫn đi wireproxy**, đúng như từ trước tới nay — cấu hình cũ của bạn
không đổi hành vi một chút nào. Muốn chạy chính file đó trong tiến trình này thì đặt cột **Giao thức
VPN** thành `WireGuard`: khi đó không cần `wireproxy.exe` nữa, và UDP đi qua được đường hầm.

Với engine wireproxy, cần tải `wireproxy.exe` để cạnh `ProxyDivert.exe` (hoặc trong PATH, hoặc trỏ
đường dẫn ở tab **Cài đặt**). File `.conf` đã có sẵn mục `[Socks5]` thì dùng nguyên trạng; file
thường sẽ được sinh bản sao tạm có `[Socks5]` trên cổng loopback ngẫu nhiên **kèm mật khẩu ngẫu
nhiên**, để tiến trình khác trên máy không dùng ké được đường hầm. Bản sao tạm đó nằm trong `%TEMP%`
và **chứa private key dạng rõ** trong lúc wireproxy chạy (bị xoá khi dừng) — đúng như cách wireproxy
vốn nhận cấu hình.

### Tra tên miền nằm trong đường hầm

VPN chở lưu lượng của bạn nhưng để việc tra tên miền đi ra resolver của nhà mạng thì coi như đã đưa
nguyên danh sách những nơi bạn vào ([rò rỉ DNS](docs/Glossary-vi.md#L113)). Nên engine chạy trong
tiến trình tự hỏi DNS **bên trong đường hầm**, qua socket UDP của stack, tới máy chủ DNS mà VPN cấp —
không cấp thì 1.1.1.1 rồi 8.8.8.8, vẫn gửi trong đường hầm. Không bao giờ hỏi resolver của máy.

### File `.vpn`

Muốn giữ thông tin máy chủ trong file thay vì trên dòng đường ra thì trỏ ô URL vào một file ini nhỏ.
Ô nào bạn điền ở dòng đường ra sẽ **thắng** giá trị trong file, vì ô đó được mã hoá còn file thì không.

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
engine: tiến trình `wireproxy` chết thì được dựng lại ngay, kết nối lại giãn dần 1 → 2 → 5 → 10 → 30
giây để một cấu hình sai không biến thành vòng lặp sinh tiến trình. Phiên WireGuard rỗi được giữ sống
bằng `PersistentKeepalive` — file nhà cung cấp thường không khai mục này nên tool tự điền 25 giây;
file nào đã tự khai thì giữ nguyên.

Driver chạy trong tiến trình thì [tự giám sát và tự kết nối lại](docs/Glossary-vi.md#L125) với backoff
riêng của nó, nên tool cố ý đứng ngoài: đường hầm đang tự dựng lại thì chỉ được báo trạng thái chứ
không bị đụng vào, chỉ khi driver bỏ cuộc hẳn mới bị thay. Dựng lại giữa chừng chỉ là hai lượt quay
số cùng đua tới một máy chủ.

Trạng thái hiện ngay trên tab **Đường ra**: chấm xanh là đang chạy, chấm vàng kèm lý do là đang kết
nối hoặc kết nối lại. Bấm Lưu **không** làm rớt đường hầm — chỉ đường ra nào thật sự bị sửa mới dựng
lại, và sửa chính file cấu hình cũng tính là sửa.

Ngoại lệ: file `.conf` do bạn tự viết (đã có sẵn `[Socks5]`) được giao cho wireproxy nguyên trạng,
nên `PersistentKeepalive` trong đó là việc của bạn.

## Công cụ dòng lệnh (`ProxyDivert.Cli`)

Bản console để thử engine mà không cần giao diện: mọi thứ truyền bằng argument, không đọc file cấu hình.

```
ProxyDivert.Cli --selfhost 18080 --pid 2372 --matcher DomainSuffix --rule facebook.com --duration 40
ProxyDivert.Cli --proxy socks5://127.0.0.1:1080 --launch "C:\Windows\System32\curl.exe" ^
                --launch-args "-4 https://example.com"
```

`--selfhost <port>` dựng luôn một HTTP proxy trong chính tiến trình đó (đường ra là direct), nên có
thể kiểm chứng cả đường proxy lẫn đường direct mà không cần proxy thật. `--help` liệt kê đủ tham số.

Hai tham số cho IPv6: `--ipv6 Redirect|Block|Ignore` (mặc định `Redirect`) và
`--outbound-ipv6 Auto|Enabled|Disabled` để giả lập đường ra không có tuyến IPv6:

```
ProxyDivert.Cli --selfhost 18080 --pid 2372 --rule "*" --duration 40 --ipv6 Redirect
ProxyDivert.Cli --selfhost 18080 --pid 2372 --rule "*" --outbound-ipv6 Disabled
```

`--vpn` nhận đúng thứ mà ô URL nhận, thông tin đăng nhập truyền bằng cờ — cách nhanh nhất để thử một
đường ra VPN mà không đụng vào cấu hình đã lưu:

```
ProxyDivert.Cli --vpn sstp://219.100.37.1:443 --vpn-user vpn --vpn-pass vpn --pid 2372 --rule "*"
ProxyDivert.Cli --vpn D:\vpn\wg0.conf --vpn-protocol WireGuard --pid 2372 --rule "*"
```

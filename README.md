# ProxyDivert

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
| `libs/TqkLibrary.WinDivert` | submodule — lõi chuyển hướng gói tin theo tiến trình |
| `libs/TqkLibrary.Proxy` | submodule — `IProxySource` cho HTTP/SOCKS4/SOCKS5/SSH/WireGuard |
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
- UDP chỉ đi qua proxy được với **SOCKS5**; đường ra khác thì UDP bị chặn chứ không rò ra ngoài.
  QUIC (UDP/443) chặn mặc định để trình duyệt lùi về TCP.
- Game có anti-cheat kernel có thể coi việc chuyển hướng gói tin là can thiệp.
- VPN dưới dạng đường ra là giai đoạn 2 (xem [docs/Plan-vi.md](docs/Plan-vi.md)).

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

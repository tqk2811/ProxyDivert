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

- Chỉ IPv4. IPv6 của tiến trình đích bị **chặn** (nếu không sẽ đi vòng qua proxy) — tắt được trong Cài đặt.
- UDP chỉ đi qua proxy được với **SOCKS5**; đường ra khác thì UDP bị chặn chứ không rò ra ngoài.
  QUIC (UDP/443) chặn mặc định để trình duyệt lùi về TCP.
- Game có anti-cheat kernel có thể coi việc chuyển hướng gói tin là can thiệp.
- VPN dưới dạng đường ra là giai đoạn 2 (xem [docs/Plan-vi.md](docs/Plan-vi.md)).

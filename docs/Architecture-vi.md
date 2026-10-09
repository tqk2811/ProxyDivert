# Kiến trúc

ProxyDivert được ghép từ những phần nào và các cơ chế chạy ra sao; cách cài và dùng xem ở [README-vi](../README-vi.md).

## Cấu trúc

| Đường dẫn | Nội dung |
|---|---|
| `libs/TqkLibrary.WinDivert` | submodule — lõi chuyển hướng gói tin theo tiến trình (5 project: core, `.Redirect`, `.SecureDns`, `.Inspection`, `.ProcessControl`) |
| `libs/TqkLibrary.Proxy` | submodule — `IProxySource` cho HTTP/SOCKS4/SOCKS5/SSH/WireGuard |
| `libs/TqkLibrary.VpnClient` | submodule — [stack TCP/IP userspace](Glossary-vi.md#L73) và driver các giao thức VPN. Project `TqkLibrary.VpnClient.Tunnels` trong đó quay số sáu giao thức bên dưới và trả về đường hầm đã lên. |
| `src/ProxyDivert.Core` | engine, model, service (không phụ thuộc WPF) |
| `src/ProxyDivert.Wpf` | giao diện |
| `src/ProxyDivert.Core.Tests` | unit test |

## Tên miền của một kết nối lấy từ đâu

Luật định tuyến so khớp theo domain, nhưng gói tin không mang sẵn tên miền — tool phải tự tìm, theo
đúng thứ tự đáng tin này:

1. **[SNI](Glossary-vi.md#L13) hoặc header `Host`** — đọc trộm (peek) vài byte đầu của kết nối,
   bytes vẫn giữ nguyên cho chặng sau. Đây là cái tên chính ứng dụng gõ ra, nên vẫn đúng khi nhiều
   domain **dùng chung một IP** như Cloudflare và các CDN khác
   ([IP dùng chung](Glossary-vi.md#L512)).
2. **[Bảng DNS ngược](Glossary-vi.md#L17)** — tool nghe gói trả lời DNS/53 (hoặc
   [DoH](Glossary-vi.md#L25)) để tự học IP → domain; nó KHÔNG đọc DNS cache của Windows. Bảng
   khoá theo IP nên với IP dùng chung chỉ giữ được cái tên học **sau cùng**: đây là phỏng đoán chứ
   không phải sự thật.
3. Không ra tên → luật theo domain không khớp, chỉ còn luật theo IP, cổng và giao thức.

Bước 1 không dùng được trong mấy trường hợp, lúc đó phải chịu phỏng đoán của bước 2:

- **UDP và QUIC** — không có ClientHello để đọc.
- **Giao thức server nói trước** (SMTP, FTP, SSH) — chờ 3 giây không thấy gì thì bỏ qua.
- **[ECH](Glossary-vi.md#L518)** — trình duyệt mã hoá luôn ClientHello, SNI biến mất.

## Độ trễ với traffic không chuyển hướng

WinDivert ở tầng NETWORK không biết gói thuộc tiến trình nào, nên khi engine chạy thì **mọi** gói
TCP/UDP đi ra của cả máy đều phải vòng lên user mode, kể cả gói của game không dính luật nào. Mỗi gói
như vậy chờ một thread của ProxyDivert thả đi. Để thời gian chờ ngắn nhất:

- Traffic chia ra **sáu handle**, mỗi handle một thread riêng: TCP đi ra, UDP đi ra và chiều về từ
  relay, cho cả IPv4 lẫn IPv6. Một đợt QUIC của trình duyệt hay một lượt tải lớn qua proxy không
  còn bắt gói TCP của game xếp hàng phía sau.
- Gói không stage nào cần xử lý được thả theo **đường tắt**, trước khi parse hay cấp phát gì.
- Thread pump chạy ở mức `TimeCritical` và đăng ký [MMCSS](Glossary-vi.md#L635) ("Pro Audio"),
  bên trên mức ưu tiên tiến trình chọn ở mục *Ưu tiên CPU* trong Cài đặt (mặc định High).
- Mức log Debug ghi độ trễ pump mỗi 10 giây (`capture-to-release fast/full … avg p99 max`) để tự
  xem engine tốn bao nhiêu trên máy mình.

Đo trên máy 32 lõi (bản Debug): trung bình mỗi gói chờ khoảng 0,1–0,3 ms. Khi vắt hết CPU, thỉnh
thoảng một gói TCP vẫn chờ vài ms, gói UDP có lúc tới ~30 ms.

## Chi tiết cơ chế của các giới hạn hiện tại

Danh sách dành cho người dùng là mục "Giới hạn hiện tại" trong [README-vi](../README-vi.md#L117); đây là phần chi
tiết ở mức cơ chế đằng sau một số mục của nó.

- Code ở user mode không thể nhanh bằng đường xử lý gói của kernel, nên gói phải chờ thread pump lúc
  CPU bị vắt hết không thể nhanh bằng khi xử lý trong kernel.
- Thread pump đăng ký MMCSS suốt lúc engine chạy. Cơ chế `NetworkThrottlingIndex` của Windows bóp
  xử lý mạng của ứng dụng không phải đa phương tiện khi có tác vụ MMCSS; chưa đo xem nó có ảnh hưởng
  tới các gói này không.
- Bộ phân tích gói không đi qua IPv6 extension header, nên gói IPv6 có extension header sẽ được cho đi thẳng
  thay vì bị hiểu sai (hiếm gặp với traffic ứng dụng thông thường).
- Đường ra không có tuyến IPv6 (VPN/proxy chỉ IPv4): đích **có tên miền** vẫn đi được — tool đưa tên cho đường ra
  tự phân giải sang IPv4. Đích chỉ có **địa chỉ IPv6 trần** thì không còn gì để lùi, tool đóng kết nối ngay để ứng dụng
  tự chuyển sang IPv4 ([Happy Eyeballs](Glossary-vi.md#L81)). Mỗi đường ra có thiết lập
  [Ipv6Support](Glossary-vi.md#L89): `Auto` (thử một lần rồi tự nhớ), `Enabled`, `Disabled`.
  SOCKS4 không có IPv6 trong giao thức nên luôn coi là không hỗ trợ.

## Đường ra VPN

Cách cấu hình dành cho người dùng (ô URL, file `.vpn`) nằm ở [README-vi](../README-vi.md#L146).

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
nguyên danh sách những nơi bạn vào ([rò rỉ DNS](Glossary-vi.md#L113)). Nên engine chạy trong
tiến trình tự hỏi DNS **bên trong đường hầm**, qua socket UDP của stack, tới máy chủ DNS mà VPN cấp —
không cấp thì 1.1.1.1 rồi 8.8.8.8, vẫn gửi trong đường hầm. Không bao giờ hỏi resolver của máy.

### Đường hầm được giữ chạy liên tục

Đường hầm dựng ngay khi bấm Start chứ không đợi request đầu tiên, và được giữ cho tới khi dừng
engine: tiến trình `wireproxy` chết thì được dựng lại ngay, kết nối lại giãn dần 1 → 2 → 5 → 10 → 30
giây để một cấu hình sai không biến thành vòng lặp sinh tiến trình. Phiên WireGuard rỗi được giữ sống
bằng `PersistentKeepalive` — file nhà cung cấp thường không khai mục này nên tool tự điền 25 giây;
file nào đã tự khai thì giữ nguyên.

Driver chạy trong tiến trình thì [tự giám sát và tự kết nối lại](Glossary-vi.md#L125) với backoff
riêng của nó, nên tool cố ý đứng ngoài: đường hầm đang tự dựng lại thì chỉ được báo trạng thái chứ
không bị đụng vào, chỉ khi driver bỏ cuộc hẳn mới bị thay. Dựng lại giữa chừng chỉ là hai lượt quay
số cùng đua tới một máy chủ.

Ngoại lệ: file `.conf` do bạn tự viết (đã có sẵn `[Socks5]`) được giao cho wireproxy nguyên trạng,
nên `PersistentKeepalive` trong đó là việc của bạn.

## Đường ra SSH

Cách cấu hình dành cho người dùng (các ô, host key) nằm ở [README-vi](../README-vi.md#L220).

- Mọi tunnel dùng chung một kết nối TCP tới máy chủ, nên mất một gói là tất cả khựng lại một chút.
- Mỗi tunnel đang mở giữ một luồng: SSH.NET chuyển tiếp bằng một vòng lặp chặn cho mỗi kết nối. Tool
  giữ chỗ các luồng đó khi tunnel mở để phần còn lại của ứng dụng không bao giờ bị thiếu luồng, nhưng
  trình duyệt mở một trăm kết nối là tốn một trăm luồng.

## Chống DPI

Các công tắc dành cho người dùng nằm ở [README-vi](../README-vi.md#L255).

Chỉ phần bắt tay chứa tên miền bị cắt; mọi thứ sau đó đi thẳng, nên kết nối đã lên thì không tốn
thêm gì.

## DNS bảo mật theo policy

Các công tắc dành cho người dùng nằm ở [README-vi](../README-vi.md#L279).

Tên máy chủ của chính các outbound (proxy, VPN, SSH) luôn được phân giải bằng DNS thường, vì bản
thân yêu cầu DoH cần chúng.

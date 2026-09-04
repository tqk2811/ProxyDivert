# ProxyDivert

*Tiếng Việt: [README-vi.md](README-vi.md)*

A WPF tool that pushes a chosen process's traffic through a proxy (HTTP / SOCKS4 / SOCKS5) according
to domain or IP rules; anything no rule matches goes out direct. VPN is available as another kind of
outbound.

- Plan: [docs/Plan-vi.md](docs/Plan-vi.md) (Vietnamese)
- Glossary: [docs/Glossary-vi.md](docs/Glossary-vi.md) (Vietnamese)

## Requirements

- Windows 10/11 **x64** (the native WinDivert build is win-x64 only).
- .NET SDK 8.0 or later.
- Run the tool **as Administrator** — WinDivert loads a kernel driver.

## Getting the source

```
git clone --recursive https://github.com/tqk2811/ProxyDivert.git
```

Already cloned without it: `git submodule update --init --recursive`.

## Build

```
dotnet build ProxyDivert.sln -c Debug
```

Output: `src/ProxyDivert.Wpf/bin/x64/<Config>/net8.0-windows/ProxyDivert.exe`, with `WinDivert.dll`
and `WinDivert64.sys` copied next to it.

## Layout

| Path | What it is |
|---|---|
| `libs/TqkLibrary.WinDivert` | submodule — per-process packet redirection (5 packages: core, `.Redirect`, `.SecureDns`, `.Inspection`, `.ProcessControl`) |
| `libs/TqkLibrary.Proxy` | submodule — `IProxySource` for HTTP/SOCKS4/SOCKS5/SSH/WireGuard |
| `libs/TqkLibrary.VpnClient` | submodule — userspace TCP/IP stack and VPN protocol drivers (OpenVPN, SSTP, L2TP/IPsec, IKEv2, SoftEther, …). Added for the next VPN outbounds; nothing references it yet. |
| `src/ProxyDivert.Core` | engine, models, services (no WPF dependency) |
| `src/ProxyDivert.Wpf` | the window |
| `src/ProxyDivert.Core.Tests` | unit tests |

## Quick start

1. Run `ProxyDivert.exe` **as Administrator**.
2. **Outbounds** tab: add a proxy (`socks5://host:port`, `http://host:port`) and press **Test**.
3. **Rules** tab: add a rule to the default policy — say `Wildcard` + `*.google.com` → the proxy you
   just made. Destinations matching no rule take the policy's default outbound (Direct).
4. **Processes** tab: pick a process and press **Create a rule from the selected row**, or **Launch
   suspended…** so not one connection escapes while the process starts.
5. Press **Start** in the top bar. The **Connections** tab lists every connection with its host name,
   outbound and byte counts.

Configuration lives in `proxydivert.config.json` beside the executable; proxy passwords are encrypted
with DPAPI, so only the Windows account that saved them can read them back.

## Current limits

- IPv6 is redirected like IPv4 (default `Redirect`, see [Ipv6Mode](docs/Glossary-vi.md#L89) in
  Settings). `Block` gives the old behaviour — drop it so the application falls back to IPv4;
  `Ignore` lets IPv6 go out **unproxied**. The packet parser does not walk IPv6 extension headers, so
  an IPv6 packet carrying one is passed through rather than misread (rare in ordinary application
  traffic).
- An outbound with no IPv6 route (an IPv4-only VPN or proxy) still reaches a destination that has a
  **host name**: the name is handed to the outbound, which resolves it to IPv4 itself. A **bare IPv6
  literal** has nothing left to fall back to, so the connection is closed immediately and the
  application retries over IPv4 ([Happy Eyeballs](docs/Glossary-vi.md#L81)). Each outbound carries an
  [Ipv6Support](docs/Glossary-vi.md#L89) setting: `Auto` (try once, then remember), `Enabled`,
  `Disabled`. SOCKS4 has no IPv6 in the protocol at all, so it is always treated as unsupported.
- DoH only handles DNS/53 over IPv4; the target's IPv6 DNS/53 follows the ordinary UDP rules.
- IPv6 connections already open when the engine starts fall under the "connections that started
  first" rule below.
- A connection opened **before** its process was attached goes out **direct**, and says so in the log:
  redirecting the second half of a live connection would break that connection outright. Use "Launch
  suspended" if you want nothing to escape.
- UDP only goes through a proxy with **SOCKS5**; other outbounds block UDP rather than leaking it.
  QUIC (UDP/443) is blocked by default so browsers fall back to TCP.
- Games with a kernel anti-cheat may treat packet redirection as interference.
- The **VPN (WireGuard)** outbound needs `wireproxy.exe` — see below. UDP cannot go through it
  (wireproxy's SOCKS5 is TCP-only), so UDP over a VPN outbound is blocked rather than leaked.

## The VPN (WireGuard) outbound

Pick the **Vpn** outbound kind and point the URL box at a WireGuard `.conf` — exactly the file your
VPN provider gave you, unedited. The tunnel runs at the **application layer** through
[wireproxy](https://github.com/pufferffish/wireproxy): no virtual adapter, no route table changes, so
**only the redirected process goes through the VPN** while the rest of the machine keeps its normal
network.

Download `wireproxy.exe` and put it next to `ProxyDivert.exe`, or on PATH, or point the **Settings**
tab at it. A `.conf` that already has a `[Socks5]` section is used as-is; an ordinary one gets a
temporary copy with `[Socks5]` on a random loopback port **and a random password**, so no other
process on the machine can help itself to the tunnel.

Note: that temporary copy lives in `%TEMP%` and **holds the private key in clear text** while
wireproxy runs (it is deleted on stop) — which is simply how wireproxy takes its configuration.

### The tunnel is held up, not dialled per request

The tunnel comes up the moment you press Start rather than when the first request needs it, and it is
held until the engine stops: a dead `wireproxy` process is rebuilt immediately, with the retry delay
growing 1 → 2 → 5 → 10 → 30 seconds so a broken configuration cannot become a process-spawning loop.
An idle WireGuard session is kept alive by `PersistentKeepalive` — provider files usually omit it, so
the tool fills in 25 seconds; a file that sets its own value is left alone.

The state shows on the **Outbounds** tab: a green dot means up, an amber one means connecting or
reconnecting and carries the reason. Pressing Save does **not** drop a tunnel — only outbounds that
actually changed are rebuilt, and editing the `.conf` itself counts as a change.

One exception: a `.conf` you wrote yourself (one that already has `[Socks5]`) is handed to wireproxy
untouched, so the `PersistentKeepalive` in it is your business.

## Command line (`ProxyDivert.Cli`)

A console build for exercising the engine without the window: everything is passed as arguments, and
no configuration file is read.

```
ProxyDivert.Cli --selfhost 18080 --pid 2372 --matcher DomainSuffix --rule facebook.com --duration 40
ProxyDivert.Cli --proxy socks5://127.0.0.1:1080 --launch "C:\Windows\System32\curl.exe" ^
                --launch-args "-4 https://example.com"
```

`--selfhost <port>` stands up an HTTP proxy inside that same process (going out direct), so both the
proxied path and the direct path can be checked without a real proxy anywhere. `--help` lists every
argument.

Two of them are about IPv6: `--ipv6 Redirect|Block|Ignore` (default `Redirect`) and
`--outbound-ipv6 Auto|Enabled|Disabled`, which fakes an outbound with no IPv6 route:

```
ProxyDivert.Cli --selfhost 18080 --pid 2372 --rule "*" --duration 40 --ipv6 Redirect
ProxyDivert.Cli --selfhost 18080 --pid 2372 --rule "*" --outbound-ipv6 Disabled
```

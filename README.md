# ProxyDivert

*Tiếng Việt: [README-vi.md](README-vi.md)*

A WPF tool that pushes a chosen process's traffic through a proxy (HTTP / SOCKS4 / SOCKS5) according
to domain or IP rules; anything no rule matches goes out direct. VPN is available as another kind of
outbound.

- Architecture: [docs/Architecture.md](docs/Architecture.md)

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

The version comes from git: `M.N` from the nearest tag `vM.N.0`, the third number is the count of
commits since that tag (no tag yet: `0.0.<all commits>`). Every push to `master` is built by
[.github/workflows/release.yml](.github/workflows/release.yml) and the zip is attached to the GitHub
Release `vM.N.0`; tag `master` with a new `vM.N.0` to start a new line.

## Layout

The repository is the WPF app (`src/`) plus three submodules under `libs/`. The project-by-project
layout is in [docs/Architecture.md](docs/Architecture.md#L5).

## Quick start

1. Run `ProxyDivert.exe` **as Administrator**.
2. **Outbounds** tab: add a proxy (`socks5://host:port`, `http://host:port`) and press **Test**.
3. **Rules** tab: a policy is a named list of destinations and the one way out they share. Add a
   rule — say `Wildcard` + `*.google.com` — and pick the **Outbound** under the list; that is where
   everything the rules match goes. Anything they do not match falls to the next policy the filter
   names, and to Direct if none of them claims it. Add as many policies as you like, and
   double-click one to rename it in place — the name lands when the box loses focus or on Enter,
   and Escape drops it.
4. **Processes** tab: press **Add** and a filter window opens. A filter is a name, a set of
   conditions, and what to do with whatever matches. Each condition row picks what to look at
   (**Process** or **Argument**), how to compare it (executable name, full path, wildcard, starts
   with, ends with, contains, regex — the last four read the full path and the executable name
   both and match if either does, so `contains chrome` still finds a process whose path Windows
   will not hand over), and the value. Rows join by the one picker at the top of their group —
   **match ALL of** or **match ANY of** — so there is no operator to place between two rows and no
   precedence to get wrong. **NOT** on any row or group inverts it and **+ Group** nests a bracket.
   Rows are rearranged, and rows already written are put into a bracket, by dragging them by the
   grip at their left edge: the top or bottom edge of a row means just above or just below it, the
   lower half of a group row means inside that bracket, and the strip under a bracket's last row
   means after the whole thing. Dragging the last row out of a bracket takes the empty bracket with
   it. The sentence above the rows says what the filter currently means. That is how
   `java.exe AND (minecraft OR forge)` gets written. The column beside the conditions is **Then**:
   tick the policies the filter applies, whose rules are tried from the top policy down so that the
   first rule that matches decides — drag them, or use ▲▼, to say which comes first. That arrangement is kept for
   the unticked policies too, so unticking one and ticking it again does not move it. The top policy
   is also the one whose UDP mode and Block QUIC apply. Closing the window with unsaved changes asks
   whether to save them, drop them, or go back. The filter list is tried from the top down as well:
   a process is caught by the first filter that matches it and by no other, so drag a row by its
   grip to say which comes first — the grid deliberately does not sort by column, because the row
   order is the priority. Or press **Launch suspended…** so not one connection escapes while the
   process starts.
5. Throw the switch in the title bar. The lower half of the **Processes** tab then shows, as a tree,
   every process the engine is actually holding, with anything adopted through **Children** nested
   under the process that spawned it. Each row names the filter that caught it — an adopted child
   shows its parent's, marked *inherited*, because that is the filter to go and edit, while a
   process started by **Launch suspended…** is described by no filter and says `—`. Drag the right
   edge of a heading to widen its column. The **Connections** tab lists every connection with its
   host name, outbound and byte counts.

Configuration lives in `proxydivert.config.json` beside the executable. It is plain JSON, passwords
included: the file can be edited by hand and carried to another machine as it is, so keep it
somewhere only you can read.

## Language and theme

The window draws its own title bar rather than using the system one, so that strip carries the tab
headers, the engine switch and the appearance switches instead of sitting empty. It drags,
double-clicks to maximize and resizes from the edges exactly as a normal window does; minimize,
maximize and close are at the far right where they always were.

The engine itself is one switch there rather than a Start button, a Stop button and a badge saying
which of them applies. Throwing it starts or stops the redirect; if the start fails — no
Administrator rights, most often — the knob goes back and the reason appears under the title bar.

The window speaks English or Vietnamese, and comes in light, dark, or whatever Windows is set to.
Both switches sit in that title bar — a drop-down for the language, and next to it a button that
cycles System → Light → Dark — and both are on the **Settings** tab as well. A change applies
immediately and is written to the configuration file there and then, so it is still there the next
time you start; the Save button is for the settings that go into the engine, not for these.

`System` means the machine decides. The language follows the Windows UI culture — Vietnamese when
that is what it says, English otherwise — and the theme follows the Windows app colour, changing
live when you change it in Windows.

Every piece of text is translated, the values inside the drop-downs included: a matcher type reads
"Domain suffix" or "Hậu tố tên miền" rather than the identifier `DomainSuffix`. Protocol names
(SOCKS5, IKEv2, WireGuard) stay as they are, because they are names rather than words.

## Where a connection's host name comes from

Domain rules need a name, which a packet does not carry: the tool reads the
SNI or `Host` header from the start of the connection, and falls back to a
table learned from DNS answers. UDP/QUIC, server-speaks-first protocols and
ECH leave no name to read. Details in
[docs/Architecture.md](docs/Architecture.md#L16).

## Latency for traffic that is not redirected

While the engine runs, every outbound TCP/UDP packet of the machine, redirected or not, passes
through the engine (average wait about 0.1–0.3 ms per packet). How the cost is kept down, and the
measurements: [docs/Architecture.md](docs/Architecture.md#L37).

## Current limits

- **Every outbound packet of the machine passes through the engine**, not only the redirected
  processes' (see above). Average cost is a fraction of a millisecond, but when the CPU is
  saturated the occasional packet waits several to tens of ms for the pump thread — a short ping
  spike in a game even if the game is not redirected. The only complete cure is not running the
  engine while playing ([why](docs/Architecture.md#L58)).

- IPv6 is redirected like IPv4 (default `Redirect`, see Ipv6Mode in
  Settings). `Block` gives the old behaviour — drop it so the application falls back to IPv4;
  `Ignore` lets IPv6 go out **unproxied**.
- An outbound with no IPv6 route (an IPv4-only VPN or proxy) falls back to IPv4 for destinations that
  have a host name; a bare IPv6 address is refused so the application retries over IPv4. Each
  outbound carries an Ipv6Support setting: `Auto` (try once, then
  remember), `Enabled`, `Disabled`. How the fallback works: [docs/Architecture.md](docs/Architecture.md#L58).
- Secure DNS (see [Secure DNS per policy](#secure-dns-per-policy)) takes over DNS/53 over UDP, IPv4 and IPv6 alike; DNS over TCP/53 (asked again when an answer is too long) still goes out as plain DNS.
- IPv6 connections already open when the engine starts fall under the "connections that started
  first" rule below.
- A connection opened **before** its process was attached goes out **direct**, and says so in the log:
  redirecting the second half of a live connection would break that connection outright. Use "Launch
  suspended" if you want nothing to escape.
- UDP goes through a proxy only with **SOCKS5**, and through a VPN only when that VPN runs inside
  this process (everything except a WireGuard `.conf` on wireproxy — see below). Every other
  outbound — SSH included — blocks UDP rather than leaking it. QUIC (UDP/443) is blocked by default so browsers fall
  back to TCP.
- Games with a kernel anti-cheat may treat packet redirection as interference.
- **SoftEther** needs the genuine watermark blob to reach a real server, which is GPL data this
  repository cannot ship — without it the server answers HTTP 403. Press **Download** under
  *SoftEther watermark* on the Settings tab (or **Get watermark**, which appears on a SoftEther row
  itself) and the blob is fetched from the official sources, saved next to `ProxyDivert.exe` and
  used from there — no path to configure. Nothing is ever downloaded unless you press it.
- Verified against live VPN Gate servers: **SSTP**, **L2TP/IPsec** and **SoftEther** (the last one needs the watermark blob, above). OpenVPN and WireGuard have not been tried against a live server on this machine.

## The VPN outbound

Pick the **Vpn** outbound kind. Whatever the protocol, the tunnel runs at the **application layer**:
no virtual adapter and no route table changes, so **only the redirected process goes through the
VPN** while the rest of the machine keeps its normal network.

What goes in the URL box depends on the protocol, because the protocols themselves differ — two of
them are configured by a file the provider gives you, and the other four have no standard client
file at all and are dialled with a server address instead.

| URL box | Protocol | Other boxes it uses |
|---|---|---|
| `D:\vpn\wg0.conf` | WireGuard, run by `wireproxy.exe` | — |
| `D:\vpn\jp.ovpn` | OpenVPN, in this process | Username, Password (when the profile asks) |
| `sstp://vpn.example.com:443` | SSTP | Username, Password |
| `l2tp://vpn.example.com` | L2TP/IPsec | Username, Password, Pre-shared key |
| `ikev2://vpn.example.com` | IKEv2 | Pre-shared key; Username/Password only for EAP |
| `softether://vpn.example.com:443/HUB` | SoftEther SSL-VPN | Username, Password |
| `D:\vpn\office.vpn` | any of the above, from a small ini | see below |

The **VPN protocol** column is `Auto` unless you say otherwise, and the guess is read off the URL —
a scheme names the protocol outright, and a file is recognised by its extension and contents. The
one thing it cannot guess is which of two engines should run a WireGuard `.conf`, so that is what
the column is really for; see the next section. Name lookups of the in-process engine stay inside the
tunnel and never reach the machine's resolver
([details](docs/Architecture.md#L103)).

Passwords and pre-shared keys go in their own boxes rather than into the URL, so they can be edited
and hidden on screen on their own. They are written to the configuration file as typed, like
everything else here.

### Two engines, and which one you get

Only a WireGuard `.conf` can run on either engine, `wireproxy.exe` or this process; the other
protocols always run in this process. A WireGuard `.conf` goes to **wireproxy by default**, which is
what it has always done — an existing configuration behaves exactly as it did before the other
protocols existed. To run the same file in this process instead, set the **VPN protocol** column to
`WireGuard`; you then need no `wireproxy.exe`, and UDP goes through the tunnel (wireproxy's SOCKS5
is TCP-only).

For the wireproxy engine, download `wireproxy.exe` and put it next to `ProxyDivert.exe`, or on PATH,
or point the **Settings** tab at it. The comparison of the two engines and how wireproxy takes its
configuration (a temporary copy in `%TEMP%` that **holds the private key in clear text** while it
runs): [docs/Architecture.md](docs/Architecture.md#L81).

### A `.vpn` file

If you would rather keep a server in a file than in the outbound row, point the URL box at a small
ini. Anything you also put in the outbound's own boxes wins over the file, because the row is what
the tool actually saves.

```ini
[Vpn]
Protocol  = l2tp          ; sstp | l2tp | ikev2 | softether | openvpn | wireguard
Host      = vpn.example.com
Port      = 443           ; SSTP and SoftEther only
Hub       = VPN           ; SoftEther only
User      = nam
Pass      = ...
Psk       = ...           ; l2tp and ikev2
Watermark = D:\vpn\se.dat ; SoftEther only — see Current limits
Config    = jp.ovpn       ; openvpn/wireguard instead of Host; relative to this file
```

It cannot ask for wireproxy: whether an outbound can carry UDP is decided from the URL alone, once
per connection, and a claim buried in a file that is never read on that path would be a lie the
router would act on. Point the URL box straight at the `.conf` for that.

### The tunnel is held up, not dialled per request

The tunnel comes up the moment you press Start rather than when the first request needs it, and it is
held until the engine stops, re-established automatically when it drops. Retry delays, keepalive and
who supervises what: [docs/Architecture.md](docs/Architecture.md#L110).

The state shows on the **Outbounds** tab: a green dot means up, an amber one means connecting or
reconnecting and carries the reason. Pressing Save does **not** drop a tunnel — only outbounds that
actually changed are rebuilt, and editing the configuration file itself counts as a change.

## The SSH outbound

Pick the **Ssh** outbound kind. One SSH session to the server is held open and every redirected
connection becomes a direct-tcpip channel on it — what `ssh -D` gives
you, without a local SOCKS listener in between. The destination's name is sent to the server and
resolved there, so name lookups never touch this machine's DNS. The server needs nothing special: a
stock `sshd` with `AllowTcpForwarding` (the default). It runs inside this process (SSH.NET), so there
is no `ssh.exe` to install.

| Box | What goes in it |
|---|---|
| URL | `ssh://user@host:22`, or just `user@host` — the port defaults to 22 |
| Username | the login name; wins over a user written in the URL |
| Password | the password — or, when a key file is set, the key's passphrase (it is still offered as a password as well) |
| Key file | a private key: OpenSSH, PuTTY `.ppk` or PEM (RSA, ECDSA, Ed25519) |

**Host keys are trusted on first use** (TOFU). The first time a server
is reached, its host key is written to `%LOCALAPPDATA%\ProxyDivert\known_hosts` — OpenSSH's own
format, so you can read and edit it — and from then on only that key is accepted. A server that
shows a different key is refused, and the error names the file and the line: delete that line if the
server really was reinstalled, and do not if you cannot say why it changed.

The session is held like a VPN tunnel: **Connect** on the row, the green/amber dot, kept up while a
filter routes through it and re-established when it drops.

Limits:

- **TCP only.** SSH has no channel for datagrams, so UDP routed to an SSH outbound is blocked
  (QUIC included — browsers fall back to TCP).
- Every tunnel shares one TCP connection to the server, and each open tunnel holds a thread, so a
  browser with a hundred connections open costs a hundred threads
  ([details](docs/Architecture.md#L126)).
- Tried against Windows' own OpenSSH 9.5 `sshd` on this machine, with an Ed25519 key with and without
  a passphrase (see `LiveSshOutboundTests`). Password logins and a remote Linux server have not been
  tried yet. Keyboard-interactive and ssh-agent are not supported.

## Anti-DPI

Some networks read the destination's name off the wire — the SNI in a
TLS ClientHello, or the host in a proxy's `CONNECT` line — and block or throttle by it
(DPI). The anti-DPI switches send that name in small pieces, so a box
that looks at one packet or one record at a time never sees it whole (the GoodbyeDPI idea).

On the **Outbounds** tab:

| Column | What it does | Applies to |
|---|---|---|
| **Anti-DPI TLS** | The ClientHello is rebuilt as several TLS records around the SNI name, the name itself cut into records of *DPI bytes* each. Servers must reassemble them (RFC 8446 §5.1). | Direct (the built-in row too), HTTP, SOCKS4, SOCKS5 |
| **Anti-DPI CONNECT** | The name in the `CONNECT` request to the proxy goes out *DPI bytes* at a time. Some proxies handle this badly. | HTTP, SOCKS4, SOCKS5 |
| **DPI bytes** | Bytes of the name per piece; defaults to 2. | |

Only the part of the handshake that carries the name is split, so there is no cost once the
connection is up ([details](docs/Architecture.md#L135)). VPN and SSH outbounds do not offer it —
their traffic is already encrypted end to end.

**Per policy.** On the **Rules** tab each policy has the same two switches beside Block QUIC, as
three-state boxes: ticked turns anti-DPI on for whatever the policy's rules match, empty turns it
off, and the filled square follows the outbound. Its own *DPI bytes* box, left empty, uses the
outbound's. The policy wins over the outbound, so one proxy can split only the sites that need it.
The boxes are greyed out when the policy's outbound cannot do it. UDP is never split.

## Secure DNS per policy

Each policy on the **Rules** tab has three switches that send DNS over DoH through the policy's own
outbound, so the name lookups follow the same road as the traffic. The DoH server is the
endpoint in **Settings** (*DoH endpoint*), unless the policy names its own in the *DoH server* box
beside the switches (empty = the one in Settings).

| Switch | What it does |
|---|---|
| **Secure DNS: app's own DNS** | The DNS queries the matched app sends itself go over DoH through this policy's outbound. |
| **Secure DNS: system DNS** | DNS that Windows asks on behalf of apps (the DNS Client service): when the queried name matches one of this policy's domain rules, it is resolved over DoH through this policy's outbound. Only positive domain rules count; IP, port, protocol, Any and NOT rules are ignored. Tried in the filters' list order, then the policies ticked in each filter in that filter's order. Not available for the built-in Default policy. |
| **Allow plain DNS if DoH fails** | When DoH fails the query goes out as normal DNS instead of an error answer. This leaks the name. Only meaningful while one of the two above is on. |

The boxes are greyed out when the policy's outbound is Block. They only act while the engine is
running; ticking a box or changing the DoH endpoint takes effect on Apply & Save. The names of the
outbounds' own servers (proxy, VPN and SSH endpoints) are always resolved with normal DNS
([why](docs/Architecture.md#L142)).

Limits:
- Software with its own network filter driver can take DNS/53 before WinDivert sees it, and then
  nothing is taken over. ExitLag does this in its default (WFP) mode; switching ExitLag to its NDIS driver mode lets the queries through again.
- Browsers with their own secure DNS (Chrome's *Use secure DNS*, Firefox's DNS over HTTPS) never ask
  the system resolver, so these switches do not see their lookups. Turn that setting off to route
  them here.

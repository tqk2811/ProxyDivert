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
| `libs/TqkLibrary.VpnClient` | submodule — userspace TCP/IP stack and VPN protocol drivers. Its `TqkLibrary.VpnClient.Tunnels` project dials the six protocols below and hands back a live tunnel. |
| `src/ProxyDivert.Core` | engine, models, services (no WPF dependency) |
| `src/ProxyDivert.Wpf` | the window |
| `src/ProxyDivert.Core.Tests` | unit tests |

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

Routing rules match on domains, but a packet does not carry one — the tool has to find it, in this
order of trust:

1. **[SNI](docs/Glossary-vi.md#L13) or the `Host` header** — peeked from the first bytes of the
   connection, which are left in place for the forwarding leg. This is the name the application
   itself asked for, so it stays correct when many domains **share one IP**, as they do behind
   Cloudflare and other CDNs ([shared IP](docs/Glossary-vi.md#L512)).
2. **The [reverse-DNS table](docs/Glossary-vi.md#L17)** — built by listening to the DNS/53 (or
   [DoH](docs/Glossary-vi.md#L25)) answers the target receives; it does NOT read the Windows DNS
   cache. The table is keyed by IP, so for a shared IP it holds only the name learned **last**: a
   guess, not a fact.
3. No name at all — domain rules cannot match, leaving the IP, port and protocol rules.

Step 1 is unavailable in a few cases, and step 2's guess is what remains:

- **UDP and QUIC** — there is no ClientHello to read.
- **Server-speaks-first protocols** (SMTP, FTP, SSH) — the peek gives up after 3 seconds.
- **[ECH](docs/Glossary-vi.md#L518)** — the browser encrypts the ClientHello and the SNI is gone.

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
the column is really for; see the next section.

Passwords and pre-shared keys go in their own boxes rather than into the URL, so they can be edited
and hidden on screen on their own. They are written to the configuration file as typed, like
everything else here.

### Two engines, and which one you get

| | `wireproxy.exe` | In this process |
|---|---|---|
| Protocols | WireGuard `.conf` | OpenVPN, SSTP, L2TP/IPsec, IKEv2, SoftEther, WireGuard `.conf` |
| External binary | required | none |
| UDP through the tunnel | no (its SOCKS5 is TCP-only) | yes |
| IPv6 through the tunnel | no | when the server assigns a global IPv6 |
| DNS | resolved by wireproxy inside the tunnel | resolved inside the tunnel |

A WireGuard `.conf` goes to **wireproxy by default**, which is what it has always done — an existing
configuration behaves exactly as it did before the other protocols existed. To run the same file in
this process instead, set the **VPN protocol** column to `WireGuard`; you then need no
`wireproxy.exe`, and UDP goes through the tunnel.

For the wireproxy engine, download `wireproxy.exe` and put it next to `ProxyDivert.exe`, or on PATH,
or point the **Settings** tab at it. A `.conf` that already has a `[Socks5]` section is used as-is;
an ordinary one gets a temporary copy with `[Socks5]` on a random loopback port **and a random
password**, so no other process on the machine can help itself to the tunnel. That temporary copy
lives in `%TEMP%` and **holds the private key in clear text** while wireproxy runs (it is deleted on
stop) — which is simply how wireproxy takes its configuration.

### Name lookups stay inside the tunnel

A VPN that carries your traffic but lets the name lookups go out to your ISP's resolver has given
away the list of everywhere you went. So the in-process engine resolves through the tunnel, over its
own UDP socket, asking the DNS server the VPN assigned — or 1.1.1.1 and then 8.8.8.8 when it
assigned none, still inside the tunnel. The machine's own resolver is never asked.

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
held until the engine stops: a dead `wireproxy` process is rebuilt immediately, with the retry delay
growing 1 → 2 → 5 → 10 → 30 seconds so a broken configuration cannot become a process-spawning loop.
An idle WireGuard session is kept alive by `PersistentKeepalive` — provider files usually omit it, so
the tool fills in 25 seconds; a file that sets its own value is left alone.

The in-process drivers already supervise their own link and re-establish it with their own backoff,
so the tool stays out of their way: a tunnel that is re-establishing is reported as such but left
alone, and only a driver that has given up entirely gets replaced. Rebuilding one mid-repair would
just be two dials racing each other to the same server.

The state shows on the **Outbounds** tab: a green dot means up, an amber one means connecting or
reconnecting and carries the reason. Pressing Save does **not** drop a tunnel — only outbounds that
actually changed are rebuilt, and editing the configuration file itself counts as a change.

One exception: a `.conf` you wrote yourself (one that already has `[Socks5]`) is handed to wireproxy
untouched, so the `PersistentKeepalive` in it is your business.

## The SSH outbound

Pick the **Ssh** outbound kind. One SSH session to the server is held open and every redirected
connection becomes a [direct-tcpip](docs/Glossary-vi.md#L587) channel on it — what `ssh -D` gives
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

**Host keys are trusted on first use** ([TOFU](docs/Glossary-vi.md#L591)). The first time a server
is reached, its host key is written to `%LOCALAPPDATA%\ProxyDivert\known_hosts` — OpenSSH's own
format, so you can read and edit it — and from then on only that key is accepted. A server that
shows a different key is refused, and the error names the file and the line: delete that line if the
server really was reinstalled, and do not if you cannot say why it changed.

The session is held like a VPN tunnel: **Connect** on the row, the green/amber dot, kept up while a
filter routes through it and re-established when it drops.

Limits:

- **TCP only.** SSH has no channel for datagrams, so UDP routed to an SSH outbound is blocked
  (QUIC included — browsers fall back to TCP).
- Every tunnel shares one TCP connection to the server, so a lost packet briefly stalls all of them.
- Each open tunnel holds a thread: SSH.NET forwards with a blocking loop per connection. The tool
  reserves those threads as tunnels open so the rest of the application is never starved of them,
  but a browser with a hundred connections open costs a hundred threads.
- Tried against Windows' own OpenSSH 9.5 `sshd` on this machine, with an Ed25519 key with and without
  a passphrase (see `LiveSshOutboundTests`). Password logins and a remote Linux server have not been
  tried yet. Keyboard-interactive and ssh-agent are not supported.

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

`--vpn` takes the same thing the URL box does, with the credentials as flags — which is the quickest
way to try a VPN outbound without touching the saved configuration:

```
ProxyDivert.Cli --vpn sstp://219.100.37.1:443 --vpn-user vpn --vpn-pass vpn --pid 2372 --rule "*"
ProxyDivert.Cli --vpn D:\vpn\wg0.conf --vpn-protocol WireGuard --pid 2372 --rule "*"
```

An SSH server goes in `--proxy`, with the login as flags:

```
ProxyDivert.Cli --proxy ssh://me@ssh.example.com --ssh-key C:\Users\me\.ssh\id_ed25519 --pid 2372 --rule "*"
```

# Architecture

How ProxyDivert is put together and how its mechanisms work; for installing and using it, see the [README](../README.md).

## Layout

| Path | What it is |
|---|---|
| `libs/TqkLibrary.WinDivert` | submodule — per-process packet redirection (5 packages: core, `.Redirect`, `.SecureDns`, `.Inspection`, `.ProcessControl`) |
| `libs/TqkLibrary.Proxy` | submodule — `IProxySource` for HTTP/SOCKS4/SOCKS5/SSH/WireGuard |
| `libs/TqkLibrary.VpnClient` | submodule — userspace TCP/IP stack and VPN protocol drivers. Its `TqkLibrary.VpnClient.Tunnels` project dials the six protocols below and hands back a live tunnel. |
| `src/ProxyDivert.Core` | engine, models, services (no WPF dependency) |
| `src/ProxyDivert.Wpf` | the window |
| `src/ProxyDivert.Core.Tests` | unit tests |

## Where a connection's host name comes from

Routing rules match on domains, but a packet does not carry one — the tool has to find it, in this
order of trust:

1. **SNI or the `Host` header** — peeked from the first bytes of the
   connection, which are left in place for the forwarding leg. This is the name the application
   itself asked for, so it stays correct when many domains **share one IP**, as they do behind
   Cloudflare and other CDNs (shared IP).
2. **The reverse-DNS table** — built by listening to the DNS/53 (or
   DoH) answers the target receives; it does NOT read the Windows DNS
   cache. The table is keyed by IP, so for a shared IP it holds only the name learned **last**: a
   guess, not a fact.
3. No name at all — domain rules cannot match, leaving the IP, port and protocol rules.

Step 1 is unavailable in a few cases, and step 2's guess is what remains:

- **UDP and QUIC** — there is no ClientHello to read.
- **Server-speaks-first protocols** (SMTP, FTP, SSH) — the peek gives up after 3 seconds.
- **ECH** — the browser encrypts the ClientHello and the SNI is gone.

## Latency for traffic that is not redirected

WinDivert cannot tell which process a packet at the NETWORK layer belongs to, so while the engine runs
**every** outbound TCP/UDP packet of the machine makes a round trip through user mode — including a
game's that no rule touches. Each such packet waits for a ProxyDivert thread to release it. To keep
that wait short:

- Traffic is split over **six handles**, each with its own thread: TCP egress, UDP egress and relay
  replies, for IPv4 and IPv6. A browser's QUIC burst or a large redirected download no longer
  queues a game's TCP packets behind it.
- A packet no stage would act on is released on a **fast path** before anything is parsed or
  allocated.
- The pump threads run at `TimeCritical` and are registered with
  MMCSS ("Pro Audio"), on top of the process priority chosen under *CPU
  priority* in Settings (default High).
- Log level Debug writes the pump latency every 10 s (`capture-to-release fast/full … avg p99 max`),
  to check what the engine costs on your machine.

Measured on a 32-core machine (Debug build): the average wait is about 0.1–0.3 ms per packet. With
every core busy, a TCP packet now and then still waits a few ms, and a UDP one up to ~30 ms.

## Mechanism notes on the current limits

The user-facing list is "Current limits" in the [README](../README.md#L123); these are the
mechanism-level details behind some of its entries.

- Nothing in user mode can match the kernel's own packet path, so a packet that waits for the pump
  thread under a saturated CPU cannot be made as fast as in the kernel.
- The pump threads stay registered with MMCSS for the life of the engine. Windows'
  `NetworkThrottlingIndex` throttles non-multimedia network processing while an MMCSS task is
  active; whether it affects these packets has not been measured.
- The packet parser does not walk IPv6 extension headers, so an IPv6 packet carrying one is passed
  through rather than misread (rare in ordinary application traffic).
- An outbound with no IPv6 route (an IPv4-only VPN or proxy) still reaches a destination that has a
  **host name**: the name is handed to the outbound, which resolves it to IPv4 itself. A **bare IPv6
  literal** has nothing left to fall back to, so the connection is closed immediately and the
  application retries over IPv4 (Happy Eyeballs). Each outbound carries an
  Ipv6Support setting: `Auto` (try once, then remember), `Enabled`,
  `Disabled`. SOCKS4 has no IPv6 in the protocol at all, so it is always treated as unsupported.

## VPN outbound

User-facing configuration (URL box, `.vpn` file) is in the [README](../README.md#L156).

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

One exception: a `.conf` you wrote yourself (one that already has `[Socks5]`) is handed to wireproxy
untouched, so the `PersistentKeepalive` in it is your business.

## SSH outbound

User-facing configuration (boxes, host keys) is in the [README](../README.md#L234).

- Every tunnel shares one TCP connection to the server, so a lost packet briefly stalls all of them.
- Each open tunnel holds a thread: SSH.NET forwards with a blocking loop per connection. The tool
  reserves those threads as tunnels open so the rest of the application is never starved of them,
  but a browser with a hundred connections open costs a hundred threads.

## Anti-DPI

User-facing switches are in the [README](../README.md#L270).

Only the part of the handshake that carries the name is split; everything after it passes through
untouched, so there is no cost once the connection is up.

## Secure DNS per policy

User-facing switches are in the [README](../README.md#L295).

The names of the outbounds' own servers (proxy, VPN and SSH endpoints) are always resolved with
normal DNS, since the DoH request itself needs them.

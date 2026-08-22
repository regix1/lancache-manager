# Client Hostnames { #client-hostnames }

**Management → Clients** has a **Client hostnames** setting. Turn on **Show client hostnames** and, wherever the page would show a raw address like `172.16.2.111`, it shows a name instead, if your network's DNS server can supply one.

This page explains what the setting actually asks for, why a DNS rewrite alone usually isn't enough to satisfy it, and how to make your DNS server answer the question it's asking.

## What the toggle does

When the setting is on, the app asks your network's DNS server one question for each client address: "who is this address?" If the server answers with a name, the page shows that name instead of the number. If the server has no answer, the page shows the address, exactly as before. The setting is off by default.

A saved nickname always wins over a DNS-supplied name, and a DNS-supplied name always wins over the raw address.

On a very large network, only the most recently active clients are looked up at one time, so that a single page load can't turn into a flood of questions for your DNS server. When that limit is reached, the Clients page says so, and the clients past it keep showing their addresses until a later refresh.

## Why a DNS rewrite alone isn't enough

Say you added a rewrite in your DNS server that points `test.lan` at `172.16.2.111`. That rewrite answers the question "what address does `test.lan` point to?" It's a **forward** record.

The Client hostnames setting asks the opposite question: "what name belongs to `172.16.2.111`?" That's a **reverse** record, also called a **PTR record** (PTR is short for "pointer"). A forward record and a reverse record are two separate pieces of data. Creating one does not create the other. Most DNS servers, including the `lancache-dns` container itself, only ever create forward records unless you specifically ask for a reverse one.

So a working `test.lan → 172.16.2.111` rewrite, on its own, does nothing for this setting. You need a PTR record for `172.16.2.111` that points back to `test.lan`.

## Building the reverse zone name

A PTR record doesn't live under `test.lan`. It lives under a special zone built from the address itself, called `in-addr.arpa`. To build the name, reverse the four numbers in the address and add `.in-addr.arpa`.

For `172.16.2.111`:

```text
Address:              172.16.2.111
Reverse the numbers:   111.2.16.172
Add the suffix:        111.2.16.172.in-addr.arpa
```

`111.2.16.172.in-addr.arpa` is the name you create a PTR record for, and the answer you put there is `test.lan`.

## Adding the reverse record

The reverse record has to live on your network's DNS server, not in this app. Routers that run dnsmasq - OpenWrt and many vendor firmwares - create one automatically for every device they hand out an address to over DHCP, alongside the forward name. If yours doesn't do this, or the address is static rather than DHCP-assigned, check your DNS server's own documentation for "reverse DNS" or "PTR records" to find out how to add one by hand.

## dnsmasq, OpenWrt, and Pi-hole

These all run on the same underlying software (`dnsmasq`), and the fix is the same line change.

Use `host-record`, not `address`:

```text
host-record=test.lan,172.16.2.111
```

`host-record` creates the forward record **and** the reverse (PTR) record together, in one line.

A plain rewrite line only creates the forward record:

```text
address=/test.lan/172.16.2.111
```

That single difference, `host-record` versus `address`, is the whole bug in one line. If your rewrite currently uses `address=`, switch it to `host-record=` and the PTR record appears alongside it.

## Checking it yourself

Two commands, run from any machine on your network, tell you which half of this is working.

Check the forward direction:

```bash
nslookup test.lan
```

Check the reverse direction, which is the one this feature actually needs:

```bash
nslookup -type=PTR 111.2.16.172.in-addr.arpa
```

If the first command returns an address and the second returns something like "Non-existent domain" or "can't find … NXDOMAIN", that's exactly the situation this page fixes: the forward record exists, the reverse (PTR) record doesn't. Add the PTR record using the guidance above and try the second command again.

## The port-53 redirect trap

Some routers redirect every DNS request on the network to one server, no matter which server a device is actually configured to use. If your router does this, pointing a device at a different DNS server changes nothing: every device gets identical answers from the same hijacked resolver regardless of its settings.

A quick way to check: query a DNS server address that doesn't exist anywhere on your network.

```bash
nslookup test.lan 172.16.99.99
```

If you still get an answer, even though `172.16.99.99` isn't a real server, your traffic is being redirected to whatever DNS server the router has chosen. On a network like this, changing lancache-manager's own DNS-related settings won't help either, because every query leaves your machine and ends up at the same place regardless.

## If you don't see a name for anything

The app asks more than one DNS server for each address: the nameservers configured on this host (whatever DHCP advertised — AdGuard, lancache-dns, unbound), then the lancache DNS it already found, then the router addresses on the same subnet as the client. A forward chain such as AdGuard → lancache-dns → unbound usually still has no reverse records, because those live on the DHCP server, which is not in that chain. If the router redirects every port-53 query to one resolver, every hop is that resolver and the DHCP names still have to be published there.

If **nothing** on your network shows a name after that walk, the usual cause is that none of those servers has a reverse record. Routers that run dnsmasq already name every device they hand an address to over DHCP. Confirm the router itself answers a reverse lookup (see above); if it does, the names should appear across the whole network at once.

Some devices don't answer a name query by any method at all. If a machine has no reverse record and also doesn't respond to a direct name query, there's no way for anything to discover its name automatically, and giving it a manual nickname is the only option.

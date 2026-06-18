#!/usr/bin/env python3
"""
verify_cache_key.py — THROWAWAY verification gate for the eviction false-positive fix.

WHY THIS EXISTS
---------------
lancache-manager decides whether a Download is on-disk-or-evicted by computing an md5 cache key
and checking whether the derived filename exists on disk. That key is suspected to DIVERGE from
the key nginx (the monolithic lancache image) actually used to write the file, so real on-disk
files probe to the wrong filename and the download is wrongly flagged Evicted.

nginx writes the file at:
    md5( $cacheidentifier + $uri + $slice_range )
where:
    $cacheidentifier = lowercase service name (steam/epicgames/blizzard/riot/wsus/...) for mapped
                       hosts, or the raw Host header for unmapped hosts.
    $uri             = URL-DECODED, '//'-collapsed, dot-segment-resolved path. NO query string.
    $slice_range     = "bytes=START-END", slice size 1 MiB (1048576). Always present (slice on).
On disk (levels=2:2):
    <cache_root>/<md5[-2:]>/<md5[-4:-2]>/<md5>

This script takes a sample of (service, url) pairs (from the DB, a JSON file, or CLI args) and,
for EACH pair, computes candidate md5s for the cross-product of:
    URL variants    : {as-is, query-stripped, percent-decoded, query-stripped+decoded}
    service variants: {lowercase, as-stored}
    range keys      : {no-range, bytes=0-1048575, and a few more chunk offsets}
It then reports WHICH variant produced a filename that ACTUALLY EXISTS under <cache_root>.

INTERPRETATION
--------------
  * The variant whose file EXISTS = the confirmed nginx derivation. If it differs from the
    "as-is / lowercase" current derivation, that is the false-evict root cause and tells the Rust
    fix exactly which transform to apply.
  * If NO variant matches for a row, that row is a LIKELY GENUINE (LRU) eviction — the file really
    is gone, not a false positive.
A final tally prints which variant won most often.

HOW TO RUN (inside the lancache-manager container, where /data/cache is mounted)
--------------------------------------------------------------------------------
    # Sample 25 currently-flagged-evicted rows straight from the DB (needs psycopg2; falls back
    # gracefully if it is missing — see --input / --service/--url below):
    docker exec -it <lancache-manager-container> \
        python3 /app/rust-processor/scripts/verify_cache_key.py --sample-evicted 25

    # Or point at a non-default cache root:
    ... verify_cache_key.py --sample-evicted 25 --cache-root /data/cache/cache

    # Or, with NO DB driver, feed pairs from a JSON file: [{"service":"steam","url":"/depot/..?x=1"}, ...]
    ... verify_cache_key.py --input /tmp/pairs.json

    # Or a single pair on the command line:
    ... verify_cache_key.py --service steam --url '/depot/123/chunk/abc?token=xyz'

The DB connection string is read from $ConnectionStrings__DefaultConnection or $DATABASE_URL
(or pass --dsn). Pure stdlib except for the OPTIONAL psycopg2 DB path.
"""

import argparse
import hashlib
import json
import os
import re
import sys

SLICE_SIZE = 1048576  # 1 MiB, matches nginx `slice 1m;` and Rust DEFAULT_SLICE_SIZE


# --------------------------------------------------------------------------------------------
# URL transforms — these MIRROR rust-processor/src/cache_utils.rs::nginx_cache_uri EXACTLY.
# --------------------------------------------------------------------------------------------
def strip_query(url):
    i = url.find("?")
    return url if i < 0 else url[:i]


_PCT = re.compile(r"%([0-9A-Fa-f]{2})")


def percent_decode(s):
    # Decode %XX as raw bytes then latin-1 round-trip so any byte is preserved (paths are ASCII
    # in practice; cache keys are hashed as bytes so this is faithful to nginx's $uri decode).
    return _PCT.sub(lambda m: chr(int(m.group(1), 16)), s)


def collapse_slashes(s):
    # nginx merge_slashes on → collapse runs of '/'. Idempotent.
    return re.sub(r"/{2,}", "/", s)


def resolve_dot_segments(s):
    if "." not in s:
        return s
    leading = s.startswith("/")
    trailing = len(s) > 1 and s.endswith("/")
    stack = []
    for seg in s.split("/"):
        if seg in ("", "."):
            continue
        if seg == "..":
            if stack:
                stack.pop()
            continue
        stack.append(seg)
    out = ("/" if leading else "") + "/".join(stack)
    if trailing and not out.endswith("/"):
        out += "/"
    return out


def nginx_uri(url):
    """Full nginx $uri reproduction (query-stripped + decoded + collapsed + dot-resolved)."""
    return resolve_dot_segments(collapse_slashes(percent_decode(strip_query(url))))


def url_variants(url):
    """Ordered dict of {label: transformed_url} covering the divergence vectors."""
    return {
        "as-is": url,
        "query-stripped": strip_query(url),
        "percent-decoded": percent_decode(url),
        "query-stripped+decoded": nginx_uri(url),  # the SHIPPED Rust transform
    }


def service_variants(service):
    out = {"lowercase": service.lower()}
    if service != service.lower():
        out["as-stored"] = service
    return out


# --------------------------------------------------------------------------------------------
# md5 / on-disk path — MIRRORS calculate_md5 + the levels=2:2 layout in cache_utils.rs.
# --------------------------------------------------------------------------------------------
def md5_hex(key):
    return hashlib.md5(key.encode("utf-8", "surrogatepass")).hexdigest()


def disk_path(cache_root, h):
    return os.path.join(cache_root, h[-2:], h[-4:-2], h)


def range_keys(max_chunks):
    """('no-range', None) plus ('bytes=S-E', (S,E)) for the first `max_chunks` slices."""
    yield ("no-range", None)
    for c in range(max_chunks):
        s = c * SLICE_SIZE
        e = s + SLICE_SIZE - 1
        yield ("bytes=%d-%d" % (s, e), (s, e))


def build_key(service, url, rng):
    if rng is None:
        return "%s%s" % (service, url)
    return "%s%sbytes=%d-%d" % (service, url, rng[0], rng[1])


# --------------------------------------------------------------------------------------------
# Probe one (service, url): test every variant cross-product, report the matching variant(s).
# --------------------------------------------------------------------------------------------
def probe_pair(service, url, cache_root, max_chunks):
    matches = []  # list of (url_label, svc_label, range_label, md5, path)
    for u_label, u in url_variants(url).items():
        for s_label, svc in service_variants(service).items():
            for r_label, rng in range_keys(max_chunks):
                h = md5_hex(build_key(svc, u, rng))
                p = disk_path(cache_root, h)
                if os.path.exists(p):
                    matches.append((u_label, s_label, r_label, h, p))
    return matches


def main():
    ap = argparse.ArgumentParser(description="Verify nginx cache-key derivation against on-disk files.")
    ap.add_argument("--cache-root", default="/data/cache/cache",
                    help="cache root dir (default: /data/cache/cache)")
    ap.add_argument("--sample-evicted", type=int, default=0,
                    help="sample N currently-Evicted (service,url) rows from the DB")
    ap.add_argument("--input", help="JSON file: [{\"service\":..,\"url\":..}, ...]")
    ap.add_argument("--service", help="single service (with --url)")
    ap.add_argument("--url", help="single url (with --service)")
    ap.add_argument("--dsn", help="Postgres DSN (else $ConnectionStrings__DefaultConnection / $DATABASE_URL)")
    ap.add_argument("--max-chunks", type=int, default=8,
                    help="how many leading 1 MiB slice keys to test per pair (default: 8)")
    args = ap.parse_args()

    pairs = []  # list of (service, url)

    if args.service and args.url:
        pairs.append((args.service, args.url))

    if args.input:
        with open(args.input, "r", encoding="utf-8") as f:
            for row in json.load(f):
                pairs.append((row["service"], row["url"]))

    if args.sample_evicted > 0:
        pairs.extend(sample_from_db(args.dsn, args.sample_evicted))

    if not pairs:
        print("No (service, url) pairs to test. Use --sample-evicted N, --input FILE, or --service/--url.",
              file=sys.stderr)
        return 2

    if not os.path.isdir(args.cache_root):
        print("WARNING: cache root %r does not exist — every row will look 'evicted'."
              % args.cache_root, file=sys.stderr)

    print("Testing %d (service, url) pair(s) against cache root %s\n" % (len(pairs), args.cache_root))

    winning_variant = {}  # (url_label, range_label) -> count
    genuine_evictions = 0

    for idx, (service, url) in enumerate(pairs, 1):
        matches = probe_pair(service, url, args.cache_root, args.max_chunks)
        print("[%d] service=%r url=%r" % (idx, service, url))
        if not matches:
            genuine_evictions += 1
            print("    NO VARIANT MATCHES → likely GENUINE (LRU) eviction (file is really gone).")
        else:
            for (u_label, s_label, r_label, h, _p) in matches:
                key = (u_label, r_label)
                winning_variant[key] = winning_variant.get(key, 0) + 1
                flag = "  <-- nginx-correct derivation" if u_label != "as-is" else ""
                print("    MATCH url=%-22s service=%-9s range=%-20s md5=%s%s"
                      % (u_label, s_label, r_label, h, flag))
        print()

    print("=" * 78)
    print("TALLY — which (url-variant, range) located on-disk files:")
    if winning_variant:
        for (u_label, r_label), count in sorted(winning_variant.items(), key=lambda kv: -kv[1]):
            print("    %5d  url=%-22s range=%s" % (count, u_label, r_label))
    else:
        print("    (none — all rows look like genuine evictions)")
    print("    genuine-eviction rows (no variant matched): %d / %d" % (genuine_evictions, len(pairs)))
    print("=" * 78)
    print("\nThe winning URL variant is the transform the Rust fix must apply. If 'as-is' wins,")
    print("the key already matches and the false-evict is elsewhere (e.g. chunk cap / service token).")
    return 0


def sample_from_db(dsn, limit):
    """Pull `limit` (service, url) rows from LogEntries for currently-Evicted Downloads.
    Requires psycopg2; returns [] with a clear message if it is unavailable or the query fails."""
    dsn = dsn or os.environ.get("ConnectionStrings__DefaultConnection") or os.environ.get("DATABASE_URL")
    if not dsn:
        print("--sample-evicted needs a DSN ($ConnectionStrings__DefaultConnection / $DATABASE_URL "
              "or --dsn).", file=sys.stderr)
        return []
    try:
        import psycopg2  # optional dependency
    except ImportError:
        print("psycopg2 not installed — cannot --sample-evicted. Use --input or --service/--url, "
              "or `pip install psycopg2-binary`.", file=sys.stderr)
        return []

    # psycopg2 wants key=value or postg:// form; .NET DSNs use 'Host=..;Database=..;..'. Translate
    # the common .NET shape to libpq keywords if needed.
    libpq_dsn = _to_libpq(dsn)
    rows = []
    try:
        conn = psycopg2.connect(libpq_dsn)
        try:
            cur = conn.cursor()
            cur.execute(
                """
                SELECT le."Service", le."Url"
                FROM "LogEntries" le
                INNER JOIN "Downloads" d ON le."DownloadId" = d."Id"
                WHERE d."IsEvicted" = true
                  AND le."Service" IS NOT NULL AND le."Url" IS NOT NULL
                LIMIT %s
                """,
                (limit,),
            )
            rows = [(r[0], r[1]) for r in cur.fetchall()]
        finally:
            conn.close()
    except Exception as e:  # noqa: BLE001 — throwaway diagnostic, surface any DB error plainly
        print("DB sample failed: %s" % e, file=sys.stderr)
        return []
    if not rows:
        print("No currently-Evicted rows found in the DB.", file=sys.stderr)
    return rows


def _to_libpq(dsn):
    """Best-effort translate a .NET 'Host=..;Port=..;Database=..;Username=..;Password=..' DSN to
    libpq keywords. Pass through anything already in URL or key=value libpq form."""
    if "://" in dsn or "Host=" not in dsn:
        return dsn
    mapping = {"host": "host", "port": "port", "database": "dbname",
               "username": "user", "user id": "user", "userid": "user", "password": "password"}
    parts = []
    for kv in dsn.split(";"):
        kv = kv.strip()
        if not kv or "=" not in kv:
            continue
        k, v = kv.split("=", 1)
        libpq_key = mapping.get(k.strip().lower())
        if libpq_key:
            parts.append("%s=%s" % (libpq_key, v.strip()))
    return " ".join(parts) if parts else dsn


if __name__ == "__main__":
    sys.exit(main())

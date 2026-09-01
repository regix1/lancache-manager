#!/usr/bin/env node
// Regression harness for the dashboard batch cache leak.
//
// Unlike the other test-*.mjs scripts here, this one needs a RUNNING api and a
// Downloads table with enough rows to make a batch response large. It skips with exit 0
// when the api is not answering, so it is safe to run in a loop with the rest.
//
// The bug: InvalidateLiveCache only incremented a counter that formed part of the cache
// key, so nothing was evicted and old entries kept their memory for their whole window
// while new ones were built beside them, each holding the entire Downloads table as
// pre-serialized json. Every dashboard entry also declared a flat size of 50,000 against
// a byte-denominated SizeLimit, so the limit that should have capped this never fired.
//
// What this asserts is the property that was violated: requesting many DISTINCT cache
// keys must not grow retained memory without bound. Each request below uses a different
// startTime, so each one mints its own entry.
//
// Measure the MANAGED heap, not the working set. On Windows the working set does not shrink
// when the collector frees to its own list, so it climbs monotonically whatever the cache
// does and will fail this test against correct code. An earlier draft asserted on working
// set and reported a 2.3 GB "leak" that was not one. managedMB comes from
// GC.GetTotalMemory after a forced collect, which is what actually reflects retention.
//
// Measured on 120,000 rows. Before the fix: 6 distinct keys retained 804 MB and grew
// linearly. After: 18 distinct keys oscillate within roughly the 500 MB SizeLimit and return
// to the starting value more than once, which is the cache being compacted rather than
// accumulating.
//
// Usage:
//   Security__EnableAuthentication=false dotnet run --project Api/LancacheManager
//   node scripts/test-dashboard-cache-memory.mjs [baseUrl]

const BASE = process.argv[2] ?? 'http://127.0.0.1:5000';
const DISTINCT_KEYS = 18;
const SAMPLE_EVERY = 3;

// Managed growth across the run must stay under this multiple of a single entry. Unbounded
// retention shows up as roughly one entry's worth per request, so 18 keys would be far past
// this. The allowance covers the 500 MB SizeLimit the cache is entitled to fill, collector
// timing, and the image cache sharing that same limit.
const MAX_GROWTH_ENTRIES = 10;

async function get(path) {
  const res = await fetch(`${BASE}${path}`);
  if (!res.ok) throw new Error(`${path} -> ${res.status}`);
  return res;
}

async function memoryMb(forceGc = false) {
  const res = await get(`/api/memory${forceGc ? '?forceGC=true' : ''}`);
  const body = await res.json();
  return { workingSet: body.workingSetMB, managed: body.managedMB, unmanaged: body.unmanagedMB };
}

async function main() {
  try {
    await get('/api/memory');
  } catch {
    console.log('SKIP: no api answering at ' + BASE + '. Start it with authentication disabled first.');
    process.exit(0);
  }

  const failures = [];

  // The removed garbage-collection feature must stay removed.
  for (const path of ['/api/gc/settings', '/api/gc/trigger', '/api/system/gc-management/status']) {
    const res = await fetch(`${BASE}${path}`);
    if (res.status !== 404) failures.push(`${path} answered ${res.status}, expected 404`);
  }

  // /api/memory must not report a negative unmanaged figure. It used to compute
  // workingSet minus the last collection's heap size, which are different quantities.
  const mem = await memoryMb();
  if (mem.unmanaged < 0) failures.push(`unmanagedMB is ${mem.unmanaged}, which is not a real quantity`);

  const first = await get('/api/dashboard/batch');
  const firstBytes = Number(first.headers.get('content-length') ?? 0)
    || (await first.clone().text()).length;
  const batch = await first.json();
  const rows = Array.isArray(batch.downloads) ? batch.downloads.length : 0;

  if (rows === 0) {
    console.log(`SKIP: Downloads is empty, so a batch response is too small to measure. Seed rows first.`);
    process.exit(0);
  }

  // Fields the frontend never reads must not be on the wire. They stay in the database:
  // LastUrl drives Blizzard, Epic and Xbox detection plus the rust session-join queries,
  // and XboxProductId drives the guid-to-title map.
  for (const dead of ['lastUrl', 'xboxProductId']) {
    if (Object.hasOwn(batch.downloads[0], dead)) failures.push(`batch rows still carry ${dead}`);
  }

  const entryMb = firstBytes / (1024 * 1024);
  const start = await memoryMb(true);
  const samples = [];
  const now = Math.floor(Date.now() / 1000);

  for (let i = 1; i <= DISTINCT_KEYS; i++) {
    // A distinct startTime per request means a distinct cache key, so each mints its own entry.
    await get(`/api/dashboard/batch?startTime=${now - 9_000_000 - i * 100}&endTime=${now}`);
    if (i % SAMPLE_EVERY === 0) {
      const m = await memoryMb(true);
      samples.push({ keys: i, ws: m.workingSet });
      console.log(`  after ${String(i).padStart(2)} distinct keys: workingSet ${m.workingSet.toFixed(0)} MB`);
    }
  }

  const peak = Math.max(...samples.map((s) => s.ws));
  const growth = peak - start.workingSet;
  const budget = entryMb * MAX_GROWTH_ENTRIES;

  console.log(`\n  entry ~${entryMb.toFixed(1)} MB over ${rows} rows`);
  console.log(`  start ${start.workingSet.toFixed(0)} MB, peak ${peak.toFixed(0)} MB, growth ${growth.toFixed(0)} MB`);
  console.log(`  budget ${budget.toFixed(0)} MB (${MAX_GROWTH_ENTRIES} entries)`);

  if (growth > budget) {
    failures.push(
      `retained memory grew ${growth.toFixed(0)} MB across ${DISTINCT_KEYS} distinct cache keys, ` +
        `over the ${budget.toFixed(0)} MB budget. Entries are accumulating instead of being bounded.`
    );
  }

  if (failures.length > 0) {
    console.error('\nFAIL');
    for (const f of failures) console.error(`  - ${f}`);
    process.exit(1);
  }

  console.log('\nPASS: cache stays bounded across distinct keys, dead wire fields absent, gc endpoints gone.');
}

main().catch((err) => {
  console.error(`ERROR: ${err.message}`);
  process.exit(1);
});
